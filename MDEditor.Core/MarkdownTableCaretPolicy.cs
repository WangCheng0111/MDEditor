using MDEditor.Core.Text;

namespace MDEditor.Core.Markdown;

public readonly record struct MarkdownTablePointerHit(bool InRow, int Column);

/// <summary>Table borders and source padding have explicit caret ownership.</summary>
public static class MarkdownTableCaretPolicy
{
    /// <summary>
    /// A pointer beside a table row is consumed without selecting a cell. Only a
    /// pointer above or below the row may fall through to ordinary text hit testing.
    /// </summary>
    public static MarkdownTablePointerHit HitTest(double x, double y, double left,
        double top, double bottom, IReadOnlyList<double> widths) =>
        !double.IsFinite(y) || !double.IsFinite(top) || !double.IsFinite(bottom) ||
        y < top || y > bottom
            ? new(false, -1)
            : new(true, ColumnAt(x, left, widths));

    public static int ColumnAt(double x, double left, IReadOnlyList<double> widths)
    {
        ArgumentNullException.ThrowIfNull(widths);
        if (widths.Count == 0 || !double.IsFinite(x) || !double.IsFinite(left) || x < left)
            return -1;
        var edge = left;
        for (var column = 0; column < widths.Count; column++)
        {
            if (!double.IsFinite(widths[column]) || widths[column] <= 0)
                throw new ArgumentOutOfRangeException(nameof(widths));
            edge += widths[column];
            if (x < edge || column == widths.Count - 1 && x <= edge) return column;
        }
        return -1;
    }

    public static int EditableOffset(SourceTextSnapshot source, SourceRange cell, int candidate)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.FullRange.Contains(cell)) throw new ArgumentOutOfRangeException(nameof(cell));
        var start = cell.Start;
        var end = cell.End;
        while (start < end && char.IsWhiteSpace(source.Text[start])) start++;
        while (end > start && char.IsWhiteSpace(source.Text[end - 1])) end--;
        if (start == end && cell.Length > 0)
            start = end = cell.Start + cell.Length / 2;
        return Math.Clamp(candidate, start, end);
    }
}
