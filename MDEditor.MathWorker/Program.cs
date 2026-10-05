using System.Text.Json;
using System.Text.Json.Serialization;
using MDEditor.Typesetting.Mathematics;

namespace MDEditor.MathWorker;

internal static class Program
{
    private const int CacheCapacity = 128;
    private const int CacheCostLimit = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static int Main(string[] args) => args.Contains("--server", StringComparer.Ordinal)
        ? RunServer()
        : RunOneShot();

    private static int RunOneShot()
    {
        using var service = new MathLayoutService(CacheCapacity, CacheCostLimit);
        var response = HandleRequest(Console.In.ReadToEnd(), service, "", 1);
        Console.Out.Write(JsonSerializer.Serialize(response, Json));
        return response.Success ? 0 : 2;
    }

    private static int RunServer()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var handshake = Negotiate(Console.In.ReadLine(), sessionId);
        WriteLine(handshake);
        if (!handshake.Success) return 3;

        using var service = new MathLayoutService(CacheCapacity, CacheCostLimit);
        long sequence = 0;
        string? payload;
        while ((payload = Console.In.ReadLine()) is not null)
            WriteLine(HandleRequest(payload, service, sessionId, checked(++sequence)));
        return 0;
    }

    private static MathWorkerHandshakeResponse Negotiate(string? payload, string sessionId)
    {
        try
        {
            if (payload is null) throw new EndOfStreamException("The client closed before the handshake.");
            var request = JsonSerializer.Deserialize<MathWorkerHandshakeRequest>(payload, Json)
                ?? throw new JsonException("The handshake payload is null.");
            request.Validate();
            var selected = Math.Min(request.MaximumProtocolVersion, MathLayoutRequest.CurrentProtocolVersion);
            if (selected < request.MinimumProtocolVersion)
                return HandshakeFailure(sessionId, "protocol-mismatch",
                    $"Worker protocol {MathLayoutRequest.CurrentProtocolVersion} does not overlap " +
                    $"client range {request.MinimumProtocolVersion}-{request.MaximumProtocolVersion}.");
            return new()
            {
                Success = true, SelectedProtocolVersion = selected,
                ProcessId = Environment.ProcessId, SessionId = sessionId,
                CacheCapacity = CacheCapacity
            };
        }
        catch (Exception error)
        {
            return HandshakeFailure(sessionId,
                error is JsonException ? "invalid-json" : "invalid-handshake", error.Message);
        }
    }

    private static MathWorkerHandshakeResponse HandshakeFailure(string sessionId, string code, string message) => new()
    {
        Success = false, ProcessId = Environment.ProcessId, SessionId = sessionId,
        CacheCapacity = CacheCapacity, ErrorCode = code, ErrorMessage = message
    };

    private static MathWorkerResponse HandleRequest(string payload, MathLayoutService service,
        string sessionId, long sequence)
    {
        MathLayoutRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<MathLayoutRequest>(payload, Json)
                ?? throw new JsonException("The request payload is null.");
            request.Validate();
            var (layout, cacheHit) = service.Layout(request);
            return new()
            {
                RequestId = request.RequestId, Success = true, Layout = layout,
                SessionId = sessionId, Sequence = sequence, CacheHit = cacheHit
            };
        }
        catch (Exception error)
        {
            return new()
            {
                RequestId = request?.RequestId ?? "", Success = false,
                SessionId = sessionId, Sequence = sequence,
                ErrorCode = ErrorCode(error), ErrorMessage = error.Message,
                ErrorPosition = (error as MathParseException)?.Position
            };
        }
    }

    private static void WriteLine<T>(T value)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(value, Json));
        Console.Out.Flush();
    }

    private static string ErrorCode(Exception error) => error switch
    {
        JsonException => "invalid-json",
        MathParseException => "invalid-formula",
        ArgumentException => "invalid-request",
        NotSupportedException => "unsupported-glyph",
        _ => "worker-failure"
    };

    private sealed class MathLayoutService(int capacity, int costLimit) : IDisposable
    {
        private readonly Dictionary<string, SystemDirectWriteMathGlyphMetricsProvider> _metrics =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _cache = [];
        private readonly LinkedList<CacheEntry> _lru = [];
        private int _cost;

        public (MathLayoutResult Layout, bool CacheHit) Layout(MathLayoutRequest request)
        {
            var key = CacheKey.Create(request);
            if (_cache.TryGetValue(key, out var cached))
            {
                _lru.Remove(cached); _lru.AddFirst(cached);
                return (WithRequestId(cached.Value.Layout, request.RequestId), true);
            }

            if (!_metrics.TryGetValue(request.FontFamily, out var metrics))
            {
                metrics = new SystemDirectWriteMathGlyphMetricsProvider(request.FontFamily);
                _metrics.Add(request.FontFamily, metrics);
            }
            var layout = new TeXMathLayoutEngine(metrics).Layout(request);
            Add(key, layout);
            return (layout, false);
        }

        private void Add(CacheKey key, MathLayoutResult layout)
        {
            var cost = checked(layout.Source.Length * 2 + layout.Glyphs.Length * 96 + layout.Rules.Length * 48 + 256);
            if (cost > costLimit) return;
            while (_cache.Count >= capacity || _cost + cost > costLimit)
            {
                var last = _lru.Last;
                if (last is null) break;
                _lru.RemoveLast(); _cache.Remove(last.Value.Key); _cost -= last.Value.Cost;
            }
            var entry = new CacheEntry(key, layout, cost);
            var node = _lru.AddFirst(entry); _cache.Add(key, node); _cost += cost;
        }

        private static MathLayoutResult WithRequestId(MathLayoutResult layout, string requestId) =>
            new(requestId, layout.Source, layout.Width, layout.Height, layout.Depth, layout.Glyphs, layout.Rules);

        public void Dispose()
        {
            foreach (var metrics in _metrics.Values) metrics.Dispose();
            _metrics.Clear(); _cache.Clear(); _lru.Clear(); _cost = 0;
        }

        private readonly record struct CacheKey(string Source, MathLayoutStyle Style, long EmSizeBits, string FontFamily)
        {
            public static CacheKey Create(MathLayoutRequest request) => new(request.Source, request.Style,
                BitConverter.DoubleToInt64Bits(request.EmSize), request.FontFamily.ToUpperInvariant());
        }
        private sealed record CacheEntry(CacheKey Key, MathLayoutResult Layout, int Cost);
    }
}
