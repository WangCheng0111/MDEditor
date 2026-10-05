using MDEditor.Typesetting.Layout;

namespace MDEditor.Typesetting.LineBreaking;

/// <summary>One physical code line: no whitespace trimming, wrapping or justification.</summary>
public static class LiteralLineBreaker
{
    /// <summary>Source-mode soft wrap at shaped clusters; keeps every space and never inserts hyphens.</summary>
    public static LineBreakResult BreakWrapped(ParagraphItemMap map, double width,
        Func<LineMeasureRequest, LineMeasurement?> measure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map); ArgumentNullException.ThrowIfNull(measure);
        LayoutValidation.Positive(width, nameof(width));
        if (map.Typography.Enabled || map.Hyphenation.Enabled)
            throw new ArgumentException("Literal wrapping requires unmodified glyph spacing.", nameof(map));
        var lines = new List<LineBreakLine>();
        var itemStart = 0; var clusterStart = 0;
        while (clusterStart < map.Clusters.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clusterEnd = clusterStart; double advance = 0;
            do
            {
                advance += map.Clusters[clusterEnd++].Advance;
            } while (clusterEnd < map.Clusters.Length && advance + map.Clusters[clusterEnd].Advance <= width);
            int ItemEnd(int end)
            {
                var sourceEnd = map.Clusters[end - 1].Source.End;
                var low = itemStart; var high = map.Items.Length;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if (map.ItemSources[middle].Start < sourceEnd) low = middle + 1;
                    else high = middle;
                }
                return low;
            }
            var itemEnd = ItemEnd(clusterEnd);
            var measurement = measure(new(itemStart, itemEnd, itemEnd, 0, clusterEnd == map.Clusters.Length)) ??
                throw new InvalidOperationException("Literal source line could not be measured.");
            while (measurement.NaturalWidth > width && clusterEnd > clusterStart + 1)
            {
                itemEnd = ItemEnd(--clusterEnd);
                measurement = measure(new(itemStart, itemEnd, itemEnd, 0, false)) ??
                    throw new InvalidOperationException("Literal source line could not be measured.");
            }
            lines.Add(new(itemStart, itemEnd, itemEnd, itemEnd, width,
                measurement.NaturalWidth, measurement.Stretch, measurement.Shrink, 0, 0,
                measurement.NaturalWidth, 0, LineFitness.Decent, 0, false, true,
                clusterEnd == map.Clusters.Length, 0));
            itemStart = itemEnd; clusterStart = clusterEnd;
        }
        return new(LineBreakStatus.Success, map.Items, lines, 0);
    }

    public static LineBreakResult Break(ParagraphItemMap map, double width, LineMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(map);
        LayoutValidation.Positive(width, nameof(width));
        if (measurement.NaturalWidth > width)
            return new(LineBreakStatus.NoFeasibleBreaks, map.Items, [], null);
        if (map.Paragraph.Length == 0)
            return new(LineBreakStatus.Success, map.Items, [], 0);
        var end = map.Items.Length;
        var line = new LineBreakLine(0, end, end, end, width,
            measurement.NaturalWidth, measurement.Stretch, measurement.Shrink,
            0, 0, measurement.NaturalWidth, 0, LineFitness.Decent,
            -LineBreakItem.InfinitePenalty, false, true, true, 0);
        return new(LineBreakStatus.Success, map.Items, [line], 0);
    }
}
