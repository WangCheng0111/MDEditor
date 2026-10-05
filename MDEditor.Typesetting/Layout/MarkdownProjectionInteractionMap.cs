using MDEditor.Core.Markdown;
using MDEditor.Core.Text;

namespace MDEditor.Typesetting.Layout;

/// <summary>
/// Connects geometry measured in projected text to stable Markdown source carets.
/// The projected layout must be built from this exact projection's Display snapshot.
/// </summary>
public sealed class MarkdownProjectionInteractionMap : ITextInteractionMap
{
    private readonly TextInteractionMap _displayMap;
    public MarkdownEditProjection Projection { get; }
    public SourceTextSnapshot Source => Projection.Source;
    public TextSurface Surface => TextSurface.Body;
    public IReadOnlyList<TextInteractionLine> Lines { get; }

    public MarkdownProjectionInteractionMap(MarkdownEditProjection projection, TextInteractionMap displayMap)
    {
        Projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _displayMap = displayMap ?? throw new ArgumentNullException(nameof(displayMap));
        if (!ReferenceEquals(displayMap.Source, projection.Display) || displayMap.Surface != TextSurface.Body)
            throw new ArgumentException("Geometry must belong to this exact body projection.", nameof(displayMap));
        Lines = Array.AsReadOnly(displayMap.Lines.Select(line =>
        {
            var start = projection.ToNavigationSourceOffset(line.Source.Start, ProjectionBoundary.BeforeHidden);
            var end = projection.ToSourceOffset(line.Source.End, ProjectionBoundary.AfterHidden);
            var spans = line.Spans.Select(span =>
            {
                if (span.Source.Length == 0)
                {
                    // Empty table cells and alignment padding are caret stops,
                    // not selectable text. A collapsed separator has two source
                    // sides; using both would produce a negative source length.
                    var point = projection.ToSourceOffset(span.Source.Start,
                        ProjectionBoundary.AfterHidden);
                    return new TextInteractionSpan(new(point, 0), span.StartX, span.EndX);
                }
                // A rendered reference may have several visible glyphs backed by a
                // single, longer Markdown source token. Mapping a glyph's start to
                // AfterHidden and its end to BeforeHidden reverses the two source
                // edges for glyphs inside that replacement. Map the visible range
                // as a unit so any partial glyph span owns the complete token.
                var source = projection.ToSourceRange(span.Source);
                return new TextInteractionSpan(source, span.StartX, span.EndX);
            });
            return new TextInteractionLine(new(start, end - start), line.Bounds, spans);
        }).ToArray());
    }

    public double DistanceToY(double y) => _displayMap.DistanceToY(y);

    public TextCaret? HitTest(LayoutPoint point) => HitTest(point, null);

    /// <summary>Prefer the content side when a click lands beside a folded wrapper.</summary>
    public TextCaret? HitTestForEditing(LayoutPoint point)
    {
        if (_displayMap.HitTest(point) is not { } projected) return null;
        var before = Projection.ToSourceOffset(projected.Offset, ProjectionBoundary.BeforeHidden);
        var after = Projection.ToSourceOffset(projected.Offset, ProjectionBoundary.AfterHidden);
        var boundary = projected.Affinity == CaretAffinity.Upstream ?
            ProjectionBoundary.BeforeHidden : ProjectionBoundary.AfterHidden;
        if (before != after)
        {
            var opening = Projection.SyntaxUnits.Any(unit => unit.Delimiters.Any(delimiter =>
                delimiter.Start >= before && delimiter.End <= after &&
                delimiter.Start == unit.Source.Start));
            var closing = Projection.SyntaxUnits.Any(unit => unit.Delimiters.Any(delimiter =>
                delimiter.Start >= before && delimiter.End <= after &&
                delimiter.End == unit.Source.End));
            if (opening && !closing) boundary = ProjectionBoundary.AfterHidden;
            if (closing && !opening) boundary = ProjectionBoundary.BeforeHidden;
        }
        return new(TextSurface.Body, Projection.Source.Version,
            Projection.ToNavigationSourceOffset(projected.Offset, boundary), projected.Affinity);
    }

    public TextCaret? HitTest(LayoutPoint point, ProjectionBoundary? preferredBoundary)
    {
        if (_displayMap.HitTest(point) is not { } projected) return null;
        var boundary = preferredBoundary ?? (projected.Affinity == CaretAffinity.Upstream ?
            ProjectionBoundary.BeforeHidden : ProjectionBoundary.AfterHidden);
        return new(TextSurface.Body, Projection.Source.Version,
            preferredBoundary is null ? Projection.ToNavigationSourceOffset(projected.Offset, boundary) :
                Projection.ToSourceOffset(projected.Offset, boundary), projected.Affinity);
    }

    public LayoutRect? Resolve(TextCaret sourceCaret)
    {
        if (!Valid(sourceCaret)) return null;
        return _displayMap.Resolve(sourceCaret with { Offset = Projection.ToDisplayOffset(sourceCaret.Offset) });
    }

    public TextCaret? MoveHorizontal(TextCaret sourceCaret, int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (!Valid(sourceCaret)) return null;
        var projected = sourceCaret with { Offset = Projection.ToDisplayOffset(sourceCaret.Offset) };
        if (_displayMap.MoveHorizontal(projected, direction) is not { } moved) return null;
        return new(TextSurface.Body, Projection.Source.Version,
            Projection.ToNavigationSourceOffset(moved.Offset, direction < 0 ?
                ProjectionBoundary.AfterHidden : ProjectionBoundary.BeforeHidden), moved.Affinity);
    }

    public IReadOnlyList<LayoutRect> SelectionRects(TextSelection selection)
    {
        if (!Valid(selection.Anchor) || !Valid(selection.Focus)) return Array.Empty<LayoutRect>();
        var anchor = selection.Anchor with { Offset = Projection.ToDisplayOffset(selection.Anchor.Offset) };
        var focus = selection.Focus with { Offset = Projection.ToDisplayOffset(selection.Focus.Offset) };
        return _displayMap.SelectionRects(new(anchor, focus));
    }

    private bool Valid(TextCaret caret) => caret.Surface == TextSurface.Body &&
        caret.SourceVersion == Projection.Source.Version &&
        caret.Offset >= 0 && caret.Offset <= Projection.Source.Length;
}
