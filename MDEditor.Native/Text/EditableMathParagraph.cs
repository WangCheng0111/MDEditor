using System.Globalization;
using System.Numerics;
using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Native.Mathematics;
using MDEditor.Native.Rendering;
using MDEditor.Typesetting.Layout;
using MDEditor.Typesetting.LineBreaking;
using MDEditor.Typesetting.Markdown;
using MDEditor.Typesetting.Mathematics;
using MDEditor.Typesetting.Typography;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

namespace MDEditor.Native.Text;

/// <summary>
/// Binds one editable paragraph's source-preserving text and math atoms. Frames borrow these
/// native owners through SharedResource so an edit cannot dispose a glyph used by an older frame.
/// </summary>
internal sealed class EditableMathParagraphSession : IDisposable
{
    private readonly List<ShapedText> _textOwners = [];
    private readonly List<NativeMathFormula> _mathOwners = [];
    private readonly BoundAtom[] _atoms;
    private bool _disposed;
    internal ReflowContent Content { get; }
    internal int ParagraphIndex { get; }
    internal bool DisplayOnly { get; }
    internal IReadOnlyList<BoundAtom> Atoms => _atoms;

    internal EditableMathParagraphSession(CanvasDevice device, ReflowContent content, int paragraphIndex)
    {
        ArgumentNullException.ThrowIfNull(device);
        Content = content ?? throw new ArgumentNullException(nameof(content));
        ParagraphIndex = paragraphIndex;
        if (!content.HasVisualAtoms(paragraphIndex))
            throw new ArgumentException("The paragraph needs bound visual atoms.", nameof(content));
        var style = content.Paragraphs[paragraphIndex];
        var nodes = content.EditableMathByParagraph[paragraphIndex];
        var images = content.ImagesByParagraph[paragraphIndex];
        DisplayOnly = nodes.Count == 1 && images.Count == 0 &&
            nodes[0].Kind == MarkdownMathKind.Display && nodes[0].Source == style.Source ||
            images.Count == 1 && nodes.Count == 0 && images[0].Source == style.Source;
        var atoms = new List<BoundAtom>();
        var cursor = style.Source.Start;
        try
        {
            var visuals = nodes.Select(node => (Source: node.Source, Math: (MarkdownMathNode?)node,
                    Image: (MarkdownImageAtom?)null))
                .Concat(images.Select(image => (Source: image.Source, Math: (MarkdownMathNode?)null,
                    Image: (MarkdownImageAtom?)image))).OrderBy(item => item.Source.Start);
            foreach (var visual in visuals)
            {
                if (visual.Source.Start < cursor) continue;
                if (cursor < visual.Source.Start)
                    AddText(atoms, device, content.Source, new(cursor, visual.Source.Start - cursor), style);
                if (visual.Math is { } node)
                {
                    var layout = content.EditableMathLayouts[node.Source.Start];
                    var formula = NativeMathFormula.Create(device, layout);
                    _mathOwners.Add(formula);
                    Add(atoms, device, content.Source, new(new(node.Source, MathFlowAtomKind.Math,
                        layout.Width, layout.Height, layout.Depth), null, formula,
                        null, "", layout.Source), style.FontSize);
                }
                else if (visual.Image is { } image)
                {
                    var pixels = image.Resource.Bitmap?.SizeInPixels;
                    var scale = pixels is { } size ? Math.Min(1d,
                        Math.Min(520d / Math.Max(1, size.Width), 260d / Math.Max(1, size.Height))) : 1d;
                    var imageWidth = pixels is { } dimensions ? Math.Max(1, dimensions.Width * scale) : 210;
                    var imageHeight = pixels is { } dimensions2 ? Math.Max(1, dimensions2.Height * scale) : 74;
                    Add(atoms, device, content.Source, new(new(image.Source, MathFlowAtomKind.Math,
                        imageWidth, imageHeight, 0), null, null, image.Resource,
                        image.Alternative, ""), style.FontSize);
                }
                cursor = visual.Source.End;
            }
            if (cursor < style.Source.End)
                AddText(atoms, device, content.Source, new(cursor, style.Source.End - cursor), style);
            _atoms = atoms.ToArray();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void AddText(List<BoundAtom> atoms, CanvasDevice device, SourceTextSnapshot source,
        SourceRange range, ReflowParagraph style)
    {
        var shaper = new DirectWriteTextShaper();
        var localRange = new SourceRange(range.Start - style.Source.Start, range.Length);
        foreach (var styled in MarkdownStyleRuns.Partition(localRange, style.InlineStyles ?? []))
        {
            var code = (styled.Style & MarkdownVisualStyle.Code) != 0;
            using var format = new CanvasTextFormat
            {
                FontFamily = code ? style.CodeFamily : style.Family,
                FontSize = style.FontSize * (code && style.StyleIndex == ReflowContent.FileStyleIndex ? 0.85f : 1f),
                LocaleName = style.Locale, WordWrapping = CanvasWordWrapping.NoWrap,
                Direction = CanvasTextDirection.LeftToRightThenTopToBottom,
                FontWeight = new FontWeight { Weight = (ushort)((styled.Style & MarkdownVisualStyle.Strong) != 0 ? 600 : 400) },
                FontStyle = (styled.Style & MarkdownVisualStyle.Emphasis) != 0 ? FontStyle.Italic : FontStyle.Normal
            };
            using var typography = new CanvasTypography();
            typography.AddFeature(CanvasTypographyFeatureName.StandardLigatures, code ? 0u : 1u);
            foreach (var token in Tokenize(source,
                new(style.Source.Start + styled.Display.Start, styled.Display.Length)))
            {
                var text = source.GetText(token);
                ShapedText ShapeText() => shaper.Shape(device, text, format, typography,
                    style.CjkFamily is null ? null : layout =>
                    {
                        foreach (var span in ScriptFontPolicy.CjkOverrides(text))
                            layout.SetFontFamily(span.Start, span.Length, style.CjkFamily);
                    });
                if (text.All(char.IsWhiteSpace))
                {
                    using var space = ShapeText();
                    Add(atoms, device, source, new(new(token, MathFlowAtomKind.Space, space.Advance, 0, 0,
                        Math.Max(space.Advance * 1.6, style.FontSize * 0.35), space.Advance / 3),
                        null, null, null, "", text, styled.Style), style.FontSize);
                }
                else
                {
                    var shape = ShapeText();
                    _textOwners.Add(shape);
                    Add(atoms, device, source, new(new(token, MathFlowAtomKind.Text, shape.Advance,
                        Math.Max(1, shape.Baseline), Math.Max(0, shape.Height - shape.Baseline)),
                        shape, null, null, "", text, styled.Style), style.FontSize);
                }
            }
        }
    }

    private static IEnumerable<SourceRange> Tokenize(SourceTextSnapshot source, SourceRange range)
    {
        var text = source.GetText(range);
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        var start = 0; var kind = -1;
        for (var index = 0; index < boundaries.Length; index++)
        {
            var position = boundaries[index];
            var end = index + 1 < boundaries.Length ? boundaries[index + 1] : text.Length;
            var grapheme = text[position..end];
            var next = grapheme.All(char.IsWhiteSpace) ? 0 : TextSpacingPolicy.IsCjk(grapheme) ? 1 : 2;
            if (kind >= 0 && (kind != next || next == 1))
            {
                yield return new(range.Start + start, position - start);
                start = position;
            }
            kind = next;
        }
        if (start < text.Length) yield return new(range.Start + start, text.Length - start);
    }

    private static void Add(List<BoundAtom> atoms, CanvasDevice device,
        SourceTextSnapshot source, BoundAtom next, float size)
    {
        if (atoms.Count > 0)
        {
            var previous = atoms[^1];
            var gap = MarkdownMathSpacing.CreateFlexibleGap(source, previous.Atom, previous.Text,
                next.Atom, next.Text, size);
            if (previous.Atom.Source.End == next.Atom.Source.Start && next.Text.Length > 0 &&
                ItalicMarkerSpacing.IsBoundary(next.Text[0],
                    (previous.Style & MarkdownVisualStyle.Emphasis) != 0,
                    (next.Style & MarkdownVisualStyle.Emphasis) != 0) &&
                previous.TextShape is { } left && next.TextShape is { } right)
            {
                var correction = ItalicMarkerCorrection.Measure(device, left.CapturedRuns,
                    left.Text.Length - 1, right.CapturedRuns, 0, left.Advance,
                    (gap?.Width ?? 0) - (gap?.Shrink ?? 0));
                if (correction > 0)
                    gap = new(new(next.Atom.Source.Start, 0), MathFlowAtomKind.Gap,
                        (gap?.Width ?? 0) + correction, 0, 0, gap?.Stretch ?? 0, gap?.Shrink ?? 0);
            }
            if (gap is not null)
                atoms.Add(new(gap, null, null, null, "", ""));
        }
        atoms.Add(next);
    }

    internal sealed record BoundAtom(MathFlowAtom Atom, ShapedText? TextShape,
        NativeMathFormula? Formula, MarkdownImageResource? Image, string Alternative, string Text,
        MarkdownVisualStyle Style = MarkdownVisualStyle.None);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var formula in _mathOwners) formula.Dispose();
        foreach (var shape in _textOwners) shape.Dispose();
        _mathOwners.Clear(); _textOwners.Clear();
    }
}

internal sealed class EditableMathParagraphLayout : IDisposable
{
    private readonly SharedResource<EditableMathParagraphSession>.Lease _lease;
    private readonly double _xOffset;
    private bool _disposed;
    internal MathFlowResult Flow { get; }
    internal TextInteractionMap Interaction { get; }
    internal double Bottom { get; }
    internal double Top { get; }
    internal bool Feasible => Flow.IsSuccess;

    private EditableMathParagraphLayout(SharedResource<EditableMathParagraphSession>.Lease lease,
        double width, double top, CancellationToken cancellationToken)
    {
        _lease = lease;
        Top = top;
        try
        {
            var session = lease.Value;
            Flow = MarkdownMathFlow.Compose(session.Atoms.Select(atom => atom.Atom).ToArray(),
                width, top, cancellationToken: cancellationToken);
            _xOffset = session.DisplayOnly && Flow.IsSuccess && session.Atoms[0].Atom.Width <= width
                ? (width - session.Atoms[0].Atom.Width) / 2 : 0;
            Bottom = Flow.IsSuccess ? top + Flow.Height : top + 64;
            Interaction = BuildInteraction(session, width);
        }
        catch { lease.Dispose(); throw; }
    }

    internal static EditableMathParagraphLayout Create(SharedResource<EditableMathParagraphSession>.Lease lease,
        double width, double top, CancellationToken cancellationToken) =>
        new(lease, width, top, cancellationToken);

    private TextInteractionMap BuildInteraction(EditableMathParagraphSession session, double width)
    {
        var lines = new List<TextInteractionLine>();
        foreach (var line in Flow.Lines)
        {
            var spans = new List<TextInteractionSpan>();
            foreach (var placed in line.Placements)
            {
                if (!line.Source.Contains(placed.Source)) continue;
                var atom = session.Atoms[placed.AtomIndex];
                var x = placed.X + _xOffset;
                if (atom.TextShape is { } shape)
                {
                    foreach (var run in shape.Runs)
                    {
                        var prefix = new double[run.Glyphs.Count + 1];
                        for (var index = 0; index < run.Glyphs.Count; index++)
                            prefix[index + 1] = prefix[index] + run.Glyphs[index].Advance;
                        var rtl = (run.BidiLevel & 1) != 0;
                        foreach (var cluster in run.Clusters)
                        {
                            var source = new SourceRange(placed.Source.Start + cluster.SourceStart,
                                cluster.SourceLength);
                            var first = x + run.BaselineX +
                                (rtl ? -prefix[^1] + prefix[cluster.GlyphStart] : prefix[cluster.GlyphStart]);
                            var last = x + run.BaselineX + (rtl ? -prefix[^1] +
                                prefix[cluster.GlyphStart + cluster.GlyphCount] :
                                prefix[cluster.GlyphStart + cluster.GlyphCount]);
                            if (rtl) (first, last) = (last, first);
                            TextInteractionMap.AddGraphemes(spans, session.Content.Source, source, first, last);
                        }
                    }
                }
                else if (placed.Source.Length > 0)
                    spans.Add(new(placed.Source, x, x + placed.Width));
            }
            lines.Add(new(line.Source, new(0, line.Y, width, line.Height), spans));
        }
        return new(session.Content.Source, TextSurface.Body, lines);
    }

    /// <summary>Text-box height excludes the surrounding formula's ascender and depth.</summary>
    internal (double Y, double Height)? InlineTextBounds(SourceRange lineSource, SourceRange text)
    {
        var top = double.PositiveInfinity;
        var bottom = double.NegativeInfinity;
        foreach (var line in Flow.Lines)
        {
            if (line.Source != lineSource) continue;
            foreach (var placed in line.Placements)
            {
                if (placed.Source.Start >= text.End || text.Start >= placed.Source.End ||
                    _lease.Value.Atoms[placed.AtomIndex].TextShape is not { } shape) continue;
                top = Math.Min(top, placed.Top);
                bottom = Math.Max(bottom, placed.Top + shape.Height);
            }
            break;
        }
        return double.IsFinite(top) ? (top, bottom - top) : null;
    }

    internal void Draw(CanvasDrawingSession session, ICanvasBrush ink, double top, double bottom,
        GithubMarkdownTheme? theme = null, ICanvasBrush? link = null, ICanvasBrush? muted = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        theme ??= GithubMarkdownTheme.Light;
        var atoms = _lease.Value.Atoms;
        var paragraph = _lease.Value.Content.Paragraphs[_lease.Value.ParagraphIndex];
        var bodyInk = paragraph.Block is { QuoteDepth: > 0 } || paragraph.HeadingLevel == 6
            ? muted ?? ink : ink;
        foreach (var line in Flow.Lines)
        {
            if (line.Y + line.Height < top || line.Y > bottom) continue;
            foreach (var placed in line.Placements)
            {
                var atom = atoms[placed.AtomIndex];
                var origin = new Vector2((float)(placed.X + _xOffset), (float)placed.Top);
                if (atom.TextShape is not null) atom.TextShape.Draw(session, origin,
                    (atom.Style & MarkdownVisualStyle.Link) != 0 ? link ?? bodyInk : bodyInk);
                else if (atom.Formula is not null) atom.Formula.Draw(session, origin, bodyInk);
                else if (atom.Image is { } image)
                {
                    var rect = new Rect(origin.X, origin.Y, placed.Width,
                        Math.Max(1, atom.Atom.Height));
                    if (image.Bitmap is { } bitmap && image.Status == MarkdownImageStatus.Ready)
                        session.DrawImage(bitmap, rect);
                    else
                    {
                        session.FillRoundedRectangle(rect, 5, 5, theme.MutedBackground);
                        session.DrawRoundedRectangle(rect, 5, 5,
                            image.Status == MarkdownImageStatus.Loading ?
                                theme.Border : theme.Danger);
                        using var format = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 12,
                            WordWrapping = CanvasWordWrapping.Wrap };
                        var title = image.Status == MarkdownImageStatus.Loading ? "图片加载中" :
                            $"图片不可用：{image.Message}";
                        session.DrawText($"{title}\n{atom.Alternative}",
                            new Rect(rect.X + 8, rect.Y + 8, Math.Max(1, rect.Width - 16),
                                Math.Max(1, rect.Height - 16)),
                            image.Status == MarkdownImageStatus.Loading ?
                                theme.MutedForeground : theme.Danger, format);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lease.Dispose();
    }
}
