namespace MDEditor.Typesetting.Layout;

/// <summary>Immutable vertical interval index. A viewport query costs O(log n + visible candidates).</summary>
public sealed class ViewportIntervalIndex<T>
{
    private readonly (T Item, double Top, double Bottom)[] _items;
    private readonly double[] _maximumBottom;

    public int Count => _items.Length;

    public ViewportIntervalIndex(IEnumerable<(T Item, double Top, double Bottom)> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        _items = intervals.Select((entry, order) => (entry.Item, entry.Top, entry.Bottom, order))
            .OrderBy(entry => entry.Top).ThenBy(entry => entry.order)
            .Select(entry => (entry.Item, entry.Top, entry.Bottom)).ToArray();
        _maximumBottom = new double[_items.Length];
        var maximum = double.NegativeInfinity;
        for (var index = 0; index < _items.Length; index++)
        {
            var (_, top, bottom) = _items[index];
            if (!double.IsFinite(top) || !double.IsFinite(bottom) || bottom < top)
                throw new ArgumentOutOfRangeException(nameof(intervals));
            maximum = Math.Max(maximum, bottom);
            _maximumBottom[index] = maximum;
        }
    }

    /// <summary>Candidate range includes any overlapping long interval; test exact bounds in the caller.</summary>
    public (int Start, int End) CandidateRange(double top, double bottom)
    {
        if (!double.IsFinite(top) || !double.IsFinite(bottom) || bottom < top)
            throw new ArgumentOutOfRangeException(nameof(top));
        var low = 0; var high = _items.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_maximumBottom[middle] < top) low = middle + 1;
            else high = middle;
        }
        var start = low;
        high = _items.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_items[middle].Top <= bottom) low = middle + 1;
            else high = middle;
        }
        return (start, low);
    }

    public bool Intersects(int index, double top, double bottom) =>
        _items[index].Bottom >= top && _items[index].Top <= bottom;

    public T this[int index] => _items[index].Item;
}
