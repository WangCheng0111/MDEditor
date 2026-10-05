using MDEditor.Core.Text;

namespace MDEditor.Typesetting.Layout;

/// <summary>UIA geometry shares the exact interaction map and presented viewport, never reshapes text.</summary>
public sealed class AutomationTextGeometry
{
    private readonly ITextInteractionMap _map;
    private readonly ViewportIntervalIndex<TextInteractionLine> _lines;

    public AutomationTextGeometry(ITextInteractionMap map)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _lines = new(map.Lines.Select(line => (line, line.Bounds.Y, line.Bounds.Bottom)));
    }

    public IReadOnlyList<SourceRange> VisibleRanges(DocumentViewport viewport, double scroll)
    {
        var ranges = VisibleLines(viewport, scroll).Select(line => line.Source).OrderBy(range => range.Start);
        var result = new List<SourceRange>();
        foreach (var range in ranges)
        {
            if (result.Count > 0 && result[^1].End >= range.Start)
            {
                var previous = result[^1];
                result[^1] = new(previous.Start, Math.Max(previous.End, range.End) - previous.Start);
            }
            else result.Add(range);
        }
        return result;
    }

    public IReadOnlyList<LayoutRect> Rectangles(SourceRange range, DocumentViewport viewport, double scroll)
    {
        if (!_map.Source.FullRange.Contains(range)) throw new ArgumentOutOfRangeException(nameof(range));
        if (!viewport.IsVisible || range.Length == 0) return Array.Empty<LayoutRect>();
        var start = new TextCaret(_map.Surface, _map.Source.Version, range.Start, CaretAffinity.Downstream);
        var end = start with { Offset = range.End, Affinity = CaretAffinity.Upstream };
        var result = new List<LayoutRect>();
        foreach (var rect in _map.SelectionRects(new(start, end)))
        {
            var point = viewport.ToView(new(rect.X, rect.Y), scroll);
            var clipped = Clip(new(point.X, point.Y, rect.Width * viewport.Zoom, rect.Height * viewport.Zoom),
                new(0, 0, viewport.Width, viewport.Height));
            if (clipped is { Width: > 0, Height: > 0 } visible) result.Add(visible);
        }
        return result;
    }

    public LayoutRect? Caret(int offset, DocumentViewport viewport, double scroll)
    {
        var rect = _map.Resolve(new(_map.Surface, _map.Source.Version, offset, CaretAffinity.Downstream));
        if (rect is not { } resolved) return null;
        var point = viewport.ToView(new(resolved.X, resolved.Y), scroll);
        return Clip(new(point.X, point.Y, 1, resolved.Height * viewport.Zoom), new(0, 0, viewport.Width, viewport.Height));
    }

    public static LayoutRect? Clip(LayoutRect rect, LayoutRect viewport)
    {
        var left = Math.Max(rect.X, viewport.X); var top = Math.Max(rect.Y, viewport.Y);
        var right = Math.Min(rect.Right, viewport.Right); var bottom = Math.Min(rect.Bottom, viewport.Bottom);
        return right > left && bottom > top ? new(left, top, right - left, bottom - top) : null;
    }

    private IEnumerable<TextInteractionLine> VisibleLines(DocumentViewport viewport, double scroll)
    {
        if (!viewport.IsVisible) yield break;
        var top = viewport.ToDocument(new(0, 0), scroll).Y;
        var bottom = viewport.ToDocument(new(0, viewport.Height), scroll).Y;
        var (start, end) = _lines.CandidateRange(top, bottom);
        for (var index = start; index < end; index++)
        {
            var line = _lines[index];
            if (line.Bounds.Bottom > top && line.Bounds.Y < bottom) yield return line;
        }
    }
}
