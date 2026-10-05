using System.Globalization;
using MDEditor.Core.Text;

namespace MDEditor.Typesetting.Layout;

public enum TextSurface { Body, MarkdownMath }
public enum CaretAffinity { Upstream, Downstream }

/// <summary>A source position survives layout replacement; no screen coordinate is stored as document state.</summary>
public readonly record struct TextCaret(TextSurface Surface, long SourceVersion, int Offset, CaretAffinity Affinity);

public readonly record struct TextSelection(TextCaret Anchor, TextCaret Focus)
{
    public bool IsEmpty => Anchor.Surface != Focus.Surface || Anchor.SourceVersion != Focus.SourceVersion || Anchor.Offset == Focus.Offset;
    public SourceRange Range => new(Math.Min(Anchor.Offset, Focus.Offset), Math.Abs(Anchor.Offset - Focus.Offset));
}

/// <summary>One grapheme or atomic formula in visual coordinates. StartX/EndX follow source order.</summary>
public readonly record struct TextInteractionSpan(SourceRange Source, double StartX, double EndX)
{
    public double Left => Math.Min(StartX, EndX);
    public double Right => Math.Max(StartX, EndX);
}

public sealed class TextInteractionLine
{
    public SourceRange Source { get; }
    public LayoutRect Bounds { get; }
    public IReadOnlyList<TextInteractionSpan> Spans { get; }

    public TextInteractionLine(SourceRange source, LayoutRect bounds, IEnumerable<TextInteractionSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var copy = spans.ToArray();
        foreach (var span in copy)
            if (!source.Contains(span.Source) || !double.IsFinite(span.StartX) || !double.IsFinite(span.EndX))
                throw new ArgumentException("A visual span must be finite and belong to its line.", nameof(spans));
        Source = source; Bounds = bounds; Spans = Array.AsReadOnly(copy);
    }
}

/// <summary>One immutable geometry view whose offsets refer to Source.</summary>
public interface ITextInteractionMap
{
    SourceTextSnapshot Source { get; }
    TextSurface Surface { get; }
    IReadOnlyList<TextInteractionLine> Lines { get; }
    double DistanceToY(double y);
    TextCaret? HitTest(LayoutPoint documentPoint);
    LayoutRect? Resolve(TextCaret caret);
    TextCaret? MoveHorizontal(TextCaret caret, int direction);
    IReadOnlyList<LayoutRect> SelectionRects(TextSelection selection);
}

/// <summary>Immutable hit geometry for one source. Formula contents remain atomic.</summary>
public sealed class TextInteractionMap : ITextInteractionMap
{
    private readonly TextInteractionLine[] _lines;
    public SourceTextSnapshot Source { get; }
    public TextSurface Surface { get; }
    public IReadOnlyList<TextInteractionLine> Lines { get; }

    public TextInteractionMap(SourceTextSnapshot source, TextSurface surface, IEnumerable<TextInteractionLine> lines)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentNullException.ThrowIfNull(lines);
        _lines = lines.ToArray();
        foreach (var line in _lines)
            if (!source.FullRange.Contains(line.Source))
                throw new ArgumentException("A line is outside its source.", nameof(lines));
        Surface = surface; Lines = Array.AsReadOnly(_lines);
    }

    public static TextInteractionMap FromSnapshot(LayoutSnapshot snapshot, TextSurface surface = TextSurface.Body)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var lines = snapshot.Blocks.SelectMany(block => block.Lines).Select(line =>
        {
            var spans = new List<TextInteractionSpan>();
            foreach (var run in line.Runs)
            {
                var origin = GlyphPaintCoordinates.Resolve(line, run, new(0, 0)).X;
                var advances = new double[run.Glyphs.Length + 1];
                for (var index = 0; index < run.Glyphs.Length; index++)
                    advances[index + 1] = advances[index] + run.Glyphs[index].Advance;
                var rtl = (run.BidiLevel & 1) != 0;
                foreach (var cluster in run.Clusters)
                {
                    if (cluster.Source.Length == 0) continue; // Generated discretionary hyphen.
                    var first = rtl ? origin - run.Advance + advances[cluster.GlyphStart] : origin + advances[cluster.GlyphStart];
                    var last = rtl ? origin - run.Advance + advances[cluster.GlyphStart + cluster.GlyphCount] :
                        origin + advances[cluster.GlyphStart + cluster.GlyphCount];
                    // RTL glyph indices advance left-to-right inside a run whose origin is its right edge.
                    // Source start is at the right edge of its cluster.
                    if (rtl) (first, last) = (last, first);
                    AddGraphemes(spans, snapshot.Source, cluster.Source, first, last);
                }
            }
            return new TextInteractionLine(line.Source, line.Bounds, spans);
        });
        return new(snapshot.Source, surface, lines);
    }

    public static void AddGraphemes(List<TextInteractionSpan> output, SourceTextSnapshot source,
        SourceRange range, double startX, double endX)
    {
        ArgumentNullException.ThrowIfNull(output); ArgumentNullException.ThrowIfNull(source);
        if (!source.FullRange.Contains(range)) throw new ArgumentOutOfRangeException(nameof(range));
        if (range.Length == 0) return;
        var text = source.GetText(range);
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        if (boundaries.Length == 0) throw new InvalidOperationException("Nonempty text has no grapheme boundary.");
        for (var index = 0; index < boundaries.Length; index++)
        {
            var start = boundaries[index];
            var end = index + 1 < boundaries.Length ? boundaries[index + 1] : text.Length;
            output.Add(new(new(range.Start + start, end - start),
                startX + (endX - startX) * index / boundaries.Length,
                startX + (endX - startX) * (index + 1) / boundaries.Length));
        }
    }

    public double DistanceToY(double y)
    {
        if (_lines.Length == 0) return double.PositiveInfinity;
        return _lines.Min(line => y < line.Bounds.Y ? line.Bounds.Y - y :
            y > line.Bounds.Bottom ? y - line.Bounds.Bottom : 0);
    }

    public TextCaret? HitTest(LayoutPoint documentPoint)
    {
        if (_lines.Length == 0) return null;
        var line = _lines.MinBy(line => documentPoint.Y < line.Bounds.Y ? line.Bounds.Y - documentPoint.Y :
            documentPoint.Y > line.Bounds.Bottom ? documentPoint.Y - line.Bounds.Bottom : 0)!;
        var stops = Stops(line).ToArray();
        if (stops.Length == 0) return new(Surface, Source.Version, line.Source.Start, CaretAffinity.Downstream);
        var stop = stops.MinBy(stop => Math.Abs(documentPoint.X - stop.X));
        return new(Surface, Source.Version, stop.Offset, stop.Affinity);
    }

    public LayoutRect? Resolve(TextCaret caret)
    {
        if (caret.Surface != Surface || caret.SourceVersion != Source.Version ||
            caret.Offset < 0 || caret.Offset > Source.Length) return null;
        var candidates = _lines.Where(line => line.Source.Start <= caret.Offset && caret.Offset <= line.Source.End).ToArray();
        if (candidates.Length == 0) return null; // A paragraph may be infeasible at this width.
        var line = caret.Affinity == CaretAffinity.Upstream ? candidates[0] : candidates[^1];
        var stops = Stops(line).Where(stop => stop.Offset == caret.Offset).ToArray();
        if (stops.Length == 0) return null;
        // Adjacent atoms can share a source offset but have different visual x positions
        // when justification inserts glue between them. Resolve the requested source side,
        // otherwise Right can repeatedly land on the same caret after a pasted formula.
        var preferred = caret.Affinity == CaretAffinity.Upstream
            ? Array.FindLastIndex(stops, stop => stop.Affinity == CaretAffinity.Upstream)
            : Array.FindIndex(stops, stop => stop.Affinity == CaretAffinity.Downstream);
        var stop = stops[preferred >= 0 ? preferred :
            caret.Affinity == CaretAffinity.Upstream ? stops.Length - 1 : 0];
        return new(stop.X, line.Bounds.Y, 0, line.Bounds.Height);
    }

    /// <summary>Follow visual x, including RTL runs and soft-line transitions.</summary>
    public TextCaret? MoveHorizontal(TextCaret caret, int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var position = Resolve(caret);
        if (position is null) return null;
        var lineIndex = Array.FindIndex(_lines, line => Math.Abs(line.Bounds.Y - position.Value.Y) < 1e-6);
        if (lineIndex < 0) return null;
        var line = _lines[lineIndex];
        var stops = Stops(line).OrderBy(stop => stop.X).ToArray();
        const double epsilon = 1e-6;
        bool IsMeaningfulStop((int Offset, double X, CaretAffinity Affinity) stop)
        {
            if (stop.Offset != caret.Offset) return true;
            // Justification/math glue has no source characters. Its two visual
            // edges must not create an extra arrow (or empty Shift+arrow) step.
            // A bidi boundary may also have two sides at one source offset, but
            // crossing a real source-backed glyph must remain navigable.
            var left = Math.Min(position.Value.X, stop.X);
            var right = Math.Max(position.Value.X, stop.X);
            return line.Spans.Any(span => span.Source.Length > 0 &&
                span.Left < right - epsilon && span.Right > left + epsilon);
        }
        var nextIndex = direction < 0
            ? Array.FindLastIndex(stops, stop => stop.X < position.Value.X - epsilon && IsMeaningfulStop(stop))
            : Array.FindIndex(stops, stop => stop.X > position.Value.X + epsilon && IsMeaningfulStop(stop));
        if (nextIndex >= 0)
            return new(Surface, Source.Version, stops[nextIndex].Offset, stops[nextIndex].Affinity);
        var adjacent = lineIndex + direction;
        if (adjacent < 0 || adjacent >= _lines.Length) return caret;
        var other = Stops(_lines[adjacent]).OrderBy(stop => stop.X).ToArray();
        if (other.Length == 0) return caret;
        var next = direction < 0 ? other[^1] : other[0];
        return new(Surface, Source.Version, next.Offset, next.Affinity);
    }

    public IReadOnlyList<LayoutRect> SelectionRects(TextSelection selection)
    {
        if (selection.IsEmpty || selection.Anchor.Surface != Surface || selection.Focus.Surface != Surface ||
            selection.Anchor.SourceVersion != Source.Version || selection.Focus.SourceVersion != Source.Version ||
            selection.Range.End > Source.Length) return Array.Empty<LayoutRect>();
        var range = selection.Range;
        var rectangles = new List<LayoutRect>();
        foreach (var line in _lines)
        {
            if (line.Source.End <= range.Start || line.Source.Start >= range.End) continue;
            LayoutRect? band = null;
            foreach (var span in line.Spans.OrderBy(span => span.Left))
            {
                if (span.Right <= span.Left) continue;
                var selected = span.Source.Length > 0 && span.Source.End > range.Start &&
                    span.Source.Start < range.End;
                if (!selected)
                {
                    if (band is { } complete) rectangles.Add(complete);
                    band = null;
                    continue;
                }
                // Consecutive selected glyphs share one visual band, including the
                // measured whitespace/justification gap between them. An unselected
                // visual span splits the band (important for bidirectional text).
                band = band is { } current
                    ? new(current.X, line.Bounds.Y, Math.Max(current.Right, span.Right) - current.X,
                        line.Bounds.Height)
                    : new(span.Left, line.Bounds.Y, span.Right - span.Left, line.Bounds.Height);
            }
            if (band is { } last) rectangles.Add(last);
        }
        return rectangles;
    }

    private static IEnumerable<(int Offset, double X, CaretAffinity Affinity)> Stops(TextInteractionLine line)
    {
        if (line.Spans.Count == 0)
            yield return (line.Source.Start, line.Bounds.X, CaretAffinity.Downstream);
        foreach (var span in line.Spans)
        {
            yield return (span.Source.Start, span.StartX, CaretAffinity.Downstream);
            yield return (span.Source.End, span.EndX, CaretAffinity.Upstream);
        }
    }
}
