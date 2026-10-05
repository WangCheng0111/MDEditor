namespace MDEditor.Native.Text;

/// <summary>A shaping cluster, not necessarily an editing grapheme. Offsets are UTF-16.</summary>
public sealed record GlyphCluster(int SourceStart, int SourceLength, int GlyphStart, int GlyphCount, double Advance);

internal static class GlyphClusterDecoder
{
    public static IReadOnlyList<GlyphCluster> Decode(int sourceStart, int[] map, float[] advances)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(advances);
        if (sourceStart < 0 || sourceStart > int.MaxValue - map.Length)
            throw new ArgumentOutOfRangeException(nameof(sourceStart));
        if (map.Length == 0 && advances.Length == 0) return Array.Empty<GlyphCluster>();
        if (map.Length == 0 || advances.Length == 0 || map.Min() != 0 ||
            map.Any(index => index < 0 || index >= advances.Length))
            throw new ArgumentException("Cluster map must cover the glyph array starting at glyph zero.");
        if (advances.Any(value => !float.IsFinite(value) || value < 0))
            throw new ArgumentException("Advances must be finite and nonnegative.", nameof(advances));

        // RTL maps can descend. Glyph spans are bounded in glyph order, not source order.
        var starts = map.Distinct().Order().ToArray();
        var spans = new Dictionary<int, (int Count, double Width)>();
        for (var i = 0; i < starts.Length; i++)
        {
            var end = i + 1 < starts.Length ? starts[i + 1] : advances.Length;
            double width = 0;
            for (var glyph = starts[i]; glyph < end; glyph++) width += advances[glyph];
            spans.Add(starts[i], (end - starts[i], width));
        }
        var result = new List<GlyphCluster>();
        var seen = new HashSet<int>();
        for (var start = 0; start < map.Length;)
        {
            var glyphStart = map[start];
            if (!seen.Add(glyphStart)) throw new ArgumentException("A source cluster must be contiguous.", nameof(map));
            var end = start + 1;
            while (end < map.Length && map[end] == glyphStart) end++;
            var span = spans[glyphStart];
            result.Add(new(sourceStart + start, end - start, glyphStart, span.Count, span.Width));
            start = end;
        }
        return result.AsReadOnly();
    }
}
