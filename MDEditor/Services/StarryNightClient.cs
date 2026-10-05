using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using MDEditor.Core.Markdown;
using MDEditor.Core.Text;

namespace MDEditor.Services;

/// <summary>Persistent offline JS/WASM tokenizer. Never reads editor state or paints.</summary>
internal sealed class StarryNightClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _processLock = new();
    private readonly Dictionary<string, CachedBlock> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _cacheOrder = new();
    private long _cacheBytes;
    private Process? _process;
    private SafeFileHandle? _job;
    private long _sequence;
    private bool _disposed;

    internal StarryNightClient(string? directory = null) =>
        _directory = directory ?? Path.Combine(AppContext.BaseDirectory, "Assets", "StarryNight");

    internal int? WorkerProcessId
    {
        get { lock (_processLock) return _process is { HasExited: false } process ? process.Id : null; }
    }

    public async Task<MarkdownCodeHighlight> HighlightAsync(MarkdownCodeInputs inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var available = new Dictionary<string, CachedBlock>(StringComparer.Ordinal);
            foreach (var block in inputs.Blocks)
                if (_cache.TryGetValue(Key(block), out var cached)) available[Key(block)] = cached;
            var missing = inputs.Blocks.Where(block => !_cache.ContainsKey(Key(block))).ToArray();
            if (missing.Length > 0)
            {
                // A caller may cancel while typing. Drain its reply before the next request;
                // cancellation must not tear down/restart the engine on every repeat key.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    var process = await EnsureProcessAsync(timeout.Token).ConfigureAwait(false);
                    var id = ++_sequence;
                    var request = JsonSerializer.Serialize(new { id,
                        blocks = missing.Select(block => new { block.Id, block.Language, block.Text }) }, JsonOptions);
                    await process.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                    var line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                    if (line is null || line.Length > 32 * 1024 * 1024)
                        throw new InvalidDataException("The code highlighter exited or returned an oversized reply.");
                    var response = JsonSerializer.Deserialize<Reply>(line, JsonOptions);
                    if (response is null || response.Id != id || response.Error is not null ||
                        response.Blocks is null || response.Blocks.Length != missing.Length)
                        throw new InvalidDataException("Invalid starry-night reply.");
                    for (var index = 0; index < missing.Length; index++)
                    {
                        var block = missing[index];
                        var result = response.Blocks[index];
                        if (result.Id != block.Id || result.Tokens is null || result.Tokens.Length > 131_072)
                            throw new InvalidDataException("Invalid starry-night code block.");
                        var tokens = result.Tokens.Select(token => new MarkdownCodeToken(
                            new SourceRange(token.Start, token.Length), (MarkdownCodeTokenKind)token.Kind)).ToImmutableArray();
                        // Validate before retaining tokens; worker failures cannot corrupt native geometry.
                        MarkdownCodeHighlighter.MapTokens(block, tokens);
                        available[Key(block)] = Remember(Key(block), tokens, result.Truncated);
                    }
                }
                catch
                {
                    StopProcess();
                    throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var mapped = ImmutableArray.CreateBuilder<MarkdownCodeToken>();
            var truncated = inputs.Truncated;
            foreach (var block in inputs.Blocks)
            {
                if (!available.TryGetValue(Key(block), out var cached))
                { truncated = true; continue; }
                if (_cache.ContainsKey(Key(block))) Touch(cached);
                mapped.AddRange(MarkdownCodeHighlighter.MapTokens(block, cached.Tokens));
                truncated |= cached.Truncated;
            }
            return new(inputs.Source, mapped.ToImmutable(), truncated);
        }
        finally { _serial.Release(); }
    }

    private async Task<Process> EnsureProcessAsync(CancellationToken cancellationToken)
    {
        lock (_processLock)
            if (_process is { HasExited: false } current) return current;
        StopProcess();
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64", Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64", _ => throw new PlatformNotSupportedException()
        };
        var executable = Path.Combine(_directory, "runtimes", rid, "node.exe");
        var worker = Path.Combine(_directory, "worker.mjs");
        if (!File.Exists(executable) || !File.Exists(worker) ||
            !File.Exists(Path.Combine(_directory, "onig.wasm")))
            throw new FileNotFoundException("The packaged starry-night runtime is incomplete.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _directory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment.Remove("NODE_OPTIONS");
        start.Environment.Remove("NODE_PATH");
        start.ArgumentList.Add("--max-old-space-size=192");
        start.ArgumentList.Add("--disable-proto=throw");
        start.ArgumentList.Add(worker);
        var process = new Process { StartInfo = start };
        lock (_processLock)
        {
            if (_disposed) { process.Dispose(); throw new ObjectDisposedException(nameof(StarryNightClient)); }
            if (!process.Start()) { process.Dispose(); throw new IOException("Unable to start starry-night."); }
            _process = process;
            _job = WorkerJob.Attach(process);
        }
        // Drain (do not store) stderr, so an unexpected runtime warning cannot block pipes.
        _ = DrainErrorsAsync(process);
        var hello = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (hello is null) throw new IOException("The code highlighter did not start.");
        var ready = JsonSerializer.Deserialize<Ready>(hello, JsonOptions);
        if (ready is not { Protocol: 1, Engine: "starry-night", Version: "3.11.0", Scopes: >= 600 })
            throw new InvalidDataException("Unexpected starry-night engine handshake.");
        return process;
    }

    private static async Task DrainErrorsAsync(Process process)
    {
        try
        {
            var buffer = new char[1024];
            while (await process.StandardError.ReadAsync(buffer).ConfigureAwait(false) != 0) { }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private static string Key(MarkdownCodeInput input) => input.Language + '\0' + input.Text;
    private void Touch(CachedBlock cached)
    { _cacheOrder.Remove(cached.Node); _cacheOrder.AddLast(cached.Node); }
    private CachedBlock Remember(string key, ImmutableArray<MarkdownCodeToken> tokens, bool truncated)
    {
        if (_cache.TryGetValue(key, out var existing)) { Touch(existing); return existing; }
        var bytes = key.Length * 2L + tokens.Length * 24L;
        var node = _cacheOrder.AddLast(key);
        var cached = new CachedBlock(tokens, truncated, node, bytes);
        _cache.Add(key, cached); _cacheBytes += bytes;
        while (_cache.Count > 64 || _cacheBytes > 16L * 1024 * 1024)
        {
            var oldest = _cacheOrder.First!;
            _cacheBytes -= _cache[oldest.Value].Bytes;
            _cache.Remove(oldest.Value); _cacheOrder.RemoveFirst();
        }
        return cached;
    }

    private void StopProcess()
    {
        Process? process;
        SafeFileHandle? job;
        lock (_processLock) { process = _process; _process = null; job = _job; _job = null; }
        job?.Dispose();
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }
    public void Dispose()
    {
        lock (_processLock)
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel();
        }
        StopProcess();
        // The in-flight task owns its semaphore/token handles until its finally completes.
    }

    private sealed record CachedBlock(ImmutableArray<MarkdownCodeToken> Tokens,
        bool Truncated, LinkedListNode<string> Node, long Bytes);
    private sealed record Ready(int Protocol, string Engine, string Version, int Scopes);
    private sealed record Reply(long Id, BlockReply[]? Blocks, string? Error);
    private sealed record BlockReply(int Id, TokenReply[]? Tokens, bool Truncated);
    private sealed record TokenReply(int Start, int Length, int Kind);

    // The OS terminates the child even when the editor crashes or VS stops debugging.
    private static class WorkerJob
    {
        internal static SafeFileHandle Attach(Process process)
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid) { job.Dispose(); throw new System.ComponentModel.Win32Exception(); }
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) ||
                !AssignProcessToJobObject(job, process.Handle))
            { job.Dispose(); throw new System.ComponentModel.Win32Exception(); }
            return job;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
            ref ExtendedLimits information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    }
}
