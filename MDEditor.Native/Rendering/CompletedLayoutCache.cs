using System.Diagnostics.CodeAnalysis;

namespace MDEditor.Native.Rendering;

/// <summary>UI-thread-only bounded ownership of completed frames. Borrowed hits must not be disposed by callers.</summary>
internal sealed class CompletedLayoutCache<T> : IDisposable where T : class, IDisposable
{
    private sealed record Entry(T Value, LinkedListNode<double> Node, long Cost);
    private readonly Dictionary<double, Entry> _entries = new();
    private readonly LinkedList<double> _recent = new();
    private readonly int _capacity;
    private readonly Func<T, long> _estimateCost;
    private readonly long _maximumCost;
    private long _cost;
    private bool _disposed;
    public int Count => _entries.Count;
    public long EstimatedCost => _cost;
    public CompletedLayoutCache(int capacity, Func<T, long>? estimateCost = null,
        long maximumCost = long.MaxValue)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maximumCost <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCost));
        _capacity = capacity; _estimateCost = estimateCost ?? (_ => 1); _maximumCost = maximumCost;
    }
    public bool TryGet(double width, [NotNullWhen(true)] out T? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entries.TryGetValue(width, out var entry)) { value = null; return false; }
        _recent.Remove(entry.Node); _recent.AddFirst(entry.Node);
        value = entry.Value; return true;
    }
    public void Remember(double width, T value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(value);
        if (!double.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (_entries.Any(e => e.Key != width && ReferenceEquals(e.Value.Value, value)))
            throw new ArgumentException("A resource may belong to only one cache key.", nameof(value));
        var cost = _estimateCost(value);
        if (cost <= 0) throw new ArgumentOutOfRangeException(nameof(value), "Frame cost must be positive.");
        if (_entries.TryGetValue(width, out var old))
        {
            _recent.Remove(old.Node); _entries.Remove(width); _cost -= old.Cost;
            if (!ReferenceEquals(old.Value, value)) old.Value.Dispose();
        }
        var node = _recent.AddFirst(width); _entries.Add(width, new(value, node, cost));
        _cost = cost > long.MaxValue - _cost ? long.MaxValue : _cost + cost;
        while (_entries.Count > _capacity || _cost > _maximumCost && _entries.Count > 1)
        {
            var key = _recent.Last!.Value; var evicted = _entries[key];
            _entries.Remove(key); _recent.RemoveLast(); _cost -= evicted.Cost;
            evicted.Value.Dispose();
        }
    }
    public void Clear()
    {
        var values = _entries.Values.Select(e => e.Value).ToArray();
        _entries.Clear(); _recent.Clear(); _cost = 0;
        foreach (var value in values) value.Dispose();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; Clear();
    }
}
