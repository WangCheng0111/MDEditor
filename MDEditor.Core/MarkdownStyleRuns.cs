using MDEditor.Core.Text;

namespace MDEditor.Core.Markdown;

/// <summary>Partitions projected text into non-overlapping runs, combining nested styles.</summary>
public static class MarkdownStyleRuns
{
    public static IReadOnlyList<MarkdownStyleSpan> Partition(SourceRange range,
        IReadOnlyList<MarkdownStyleSpan> styles)
    {
        ArgumentNullException.ThrowIfNull(styles);
        if (range.Length == 0) return [];
        var relevant = styles.Where(span => span.Display.Start < range.End &&
            range.Start < span.Display.End).ToArray();
        var boundaries = relevant.SelectMany(span => new[]
            { Math.Max(range.Start, span.Display.Start), Math.Min(range.End, span.Display.End) })
            .Append(range.Start).Append(range.End).Distinct().Order().ToArray();
        var runs = new List<MarkdownStyleSpan>();
        for (var index = 0; index + 1 < boundaries.Length; index++)
        {
            var start = boundaries[index];
            var end = boundaries[index + 1];
            var flags = MarkdownVisualStyle.None;
            foreach (var span in relevant)
                if (span.Display.Start <= start && span.Display.End >= end) flags |= span.Style;
            if (runs.Count > 0 && runs[^1].Style == flags)
            {
                var last = runs[^1];
                runs[^1] = last with { Display = new(last.Display.Start, end - last.Display.Start) };
            }
            else runs.Add(new(new(start, end - start), flags));
        }
        return runs;
    }
}
