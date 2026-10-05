using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MDEditor.Typesetting.Mathematics;

namespace MDEditor.Services;

internal sealed class MathWorkerRequestException : InvalidOperationException
{
    public MathWorkerResponse Response { get; }

    public MathWorkerRequestException(MathWorkerResponse response)
        : base($"MathWorker {response.ErrorCode}: {response.ErrorMessage}")
    {
        Response = response;
    }
}

internal sealed class MathWorkerTransportException(string message, Exception? inner = null)
    : IOException(message, inner);

/// <summary>
/// One negotiated, persistent MathWorker session. Calls may arrive concurrently but are intentionally
/// serialized because the DirectWrite font-face owner is session-affine. Cancellation invalidates the
/// stream so a late response can never be consumed by the next request.
/// </summary>
internal sealed class MathWorkerClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _stateGate = new();
    private Process? _process;
    private Task<string>? _stderr;
    private string? _sessionId;
    private bool _disposed;

    public async Task<MathWorkerResponse> LayoutAsync(MathLayoutRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); request.Validate();
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            for (var attempt = 0; ; attempt++)
            {
                try { return await LayoutCoreAsync(request, cancellationToken); }
                catch (MathWorkerTransportException) when (attempt == 0 && !cancellationToken.IsCancellationRequested)
                {
                    DiscardSession();
                }
                catch (MathWorkerTransportException)
                {
                    DiscardSession(); throw;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A response may still be in the pipe. Reusing this stream could associate it with
            // another request, so cancellation always revokes the complete session.
            DiscardSession();
            throw;
        }
        finally { _requestGate.Release(); }
    }

    private async Task<MathWorkerResponse> LayoutCoreAsync(MathLayoutRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureSessionAsync(cancellationToken);
        var process = CurrentProcess() ?? throw new MathWorkerTransportException("MathWorker session disappeared.");
        var sessionId = _sessionId ?? throw new MathWorkerTransportException("MathWorker session has no ID.");
        string? line;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Json).AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            line = await process.StandardOutput.ReadLineAsync(timeout.Token);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            DiscardSession();
            throw new TimeoutException($"MathWorker request exceeded {RequestTimeout.TotalSeconds:F0} seconds.", error);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException)
        {
            throw Transport("MathWorker IPC failed.", error);
        }
        if (line is null) throw Transport("MathWorker closed stdout before returning a response.");

        MathWorkerResponse response;
        try
        {
            response = JsonSerializer.Deserialize<MathWorkerResponse>(line, Json)
                ?? throw new JsonException("The response payload is null.");
        }
        catch (JsonException error) { throw Transport("MathWorker returned invalid JSON.", error); }
        if (response.ProtocolVersion != MathLayoutRequest.CurrentProtocolVersion ||
            response.RequestId != request.RequestId || response.SessionId != sessionId || response.Sequence <= 0)
            throw Transport("MathWorker response does not match the negotiated request.");
        if (!response.Success || response.Layout is null)
            throw new MathWorkerRequestException(response);
        return response;
    }

    private async Task EnsureSessionAsync(CancellationToken cancellationToken)
    {
        var current = CurrentProcess();
        if (current is not null)
        {
            if (!current.HasExited) return;
            DiscardSession();
        }

        var executable = ResolveExecutable();
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--server");
        var process = new Process { StartInfo = startInfo };
        Task<string>? stderr = null;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("MathWorker could not be started.");
            stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StartupTimeout);
            var hello = new MathWorkerHandshakeRequest { ClientId = $"MDEditor-{Environment.ProcessId}" };
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(hello, Json).AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token)
                ?? throw new EndOfStreamException("MathWorker closed before the handshake response.");
            var ready = JsonSerializer.Deserialize<MathWorkerHandshakeResponse>(line, Json)
                ?? throw new JsonException("The handshake response is null.");
            if (!ready.Success || ready.MessageType != "ready" ||
                ready.TransportVersion != MathWorkerHandshakeRequest.CurrentTransportVersion ||
                ready.SelectedProtocolVersion != MathLayoutRequest.CurrentProtocolVersion ||
                ready.ProcessId != process.Id || string.IsNullOrWhiteSpace(ready.SessionId))
                throw new InvalidDataException($"MathWorker handshake failed: {ready.ErrorCode} {ready.ErrorMessage}");
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_stateGate)
            {
                _process = process; _stderr = stderr; _sessionId = ready.SessionId;
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            Terminate(process, graceful: false);
            throw new MathWorkerTransportException("MathWorker handshake timed out.", error);
        }
        catch (OperationCanceledException)
        {
            Terminate(process, graceful: false); throw;
        }
        catch (Exception error)
        {
            Terminate(process, graceful: false);
            var details = CompletedStderr(stderr);
            throw new MathWorkerTransportException(
                string.IsNullOrWhiteSpace(details) ? "MathWorker handshake failed." : $"MathWorker handshake failed: {details}", error);
        }
    }

    private MathWorkerTransportException Transport(string message, Exception? error = null)
    {
        Task<string>? stderr;
        lock (_stateGate) stderr = _stderr;
        var details = CompletedStderr(stderr);
        return new(string.IsNullOrWhiteSpace(details) ? message : $"{message} {details}", error);
    }

    private Process? CurrentProcess() { lock (_stateGate) return _process; }

    private void DiscardSession()
    {
        Process? process;
        lock (_stateGate)
        {
            process = _process; _process = null; _stderr = null; _sessionId = null;
        }
        if (process is not null) Terminate(process, graceful: false);
    }

    private static string CompletedStderr(Task<string>? task) =>
        task is { IsCompletedSuccessfully: true } ? task.Result.Trim() : "";

    private static void Terminate(Process process, bool graceful)
    {
        try
        {
            if (graceful)
            {
                try { process.StandardInput.Close(); } catch { }
                if (!process.HasExited && process.WaitForExit(500)) return;
            }
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(1000);
        }
        catch { }
        finally { process.Dispose(); }
    }

    private static string ResolveExecutable()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "MDEditor.MathWorker.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("The packaged MathWorker executable is missing.", executable);
        return executable;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Process? process;
        lock (_stateGate)
        {
            process = _process; _process = null; _stderr = null; _sessionId = null;
        }
        if (process is not null) Terminate(process, graceful: true);
    }
}
