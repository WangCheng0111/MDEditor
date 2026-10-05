using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using MDEditor.Core.Text;
using MDEditor.Native.Mathematics;
using MDEditor.Native.Rendering;
using MDEditor.Typesetting.Markdown;
using MDEditor.Typesetting.Mathematics;
using MDEditor.Typesetting.LineBreaking;
using MDEditor.Typesetting.Layout;
using MDEditor.Typesetting.Typography;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;

namespace MDEditor.Native.Text;

/// <summary>Read-only Markdown source used to exercise real inline/display math in the body viewport.</summary>
public static class MarkdownMathSample
{
    public static SourceTextSnapshot Source { get; } = new(
        "正文中的行内公式会和文字共同断行：设 $x_i^2+1$ 是一个量，根式 $\\frac{x+1}{\\sqrt{y+1}}$ 也作为完整盒子参与排版。中文与数学符号之间保留细微的自然间距，调整窗口时应重新选择断行。\n\n" +
        "The formula $\\sum_{i=1}^{n}\\frac{1}{i^2}$ stays with its surrounding prose. " +
        "The paragraph solver distributes measured spaces across nonfinal lines, while the final line ends naturally.\n\n" +
        "$$\\frac{x_i^2+1}{\\sqrt{y+1}}$$",
        1400);

    public static MarkdownMathDocument Document { get; } = MarkdownMathDocument.Parse(Source);
}

internal sealed class MarkdownMathSession : IDisposable
{
    private const float TextSize = 18;
    private readonly TypographyPreset _typography;
    internal double LineGap => _typography == TypographyPreset.Book ? 8 : 6;
    private readonly List<ShapedText> _textOwners = [];
    private readonly List<NativeMathFormula> _mathOwners = [];
    private readonly Block[] _blocks;
    private bool _disposed;

    private MarkdownMathSession(CanvasDevice device, IReadOnlyDictionary<int, MathLayoutResult> layouts,
        TypographyPreset typography)
    {
        _typography = typography;
        var shaper = new DirectWriteTextShaper();
        var blocks = new List<Block>();
        try
        {
            foreach (var block in MarkdownMathSample.Document.Blocks)
            {
                if (block.Kind == MarkdownBlockKind.DisplayMath)
                {
                    var node = block.DisplayMath!;
                    var formula = BindFormula(device, layouts, node);
                    blocks.Add(new DisplayBlock(block.Source, formula));
                    continue;
                }
                var atoms = new List<BoundAtom>();
                foreach (var inline in block.Inlines)
                {
                    if (inline is MarkdownMathNode math)
                    {
                        var formula = BindFormula(device, layouts, math);
                        Add(atoms, new(new(math.Source, MathFlowAtomKind.Math, formula.Layout.Width,
                            formula.Layout.Height, formula.Layout.Depth), null, formula, math.GetContent(MarkdownMathSample.Source)));
                        continue;
                    }
                    foreach (var token in Tokenize(inline.Source))
                    {
                        var text = MarkdownMathSample.Source.GetText(token);
                        if (text.All(char.IsWhiteSpace))
                        {
                            using var space = shaper.Shape(device, text,
                                _typography.TextFamily("Arial", false), TextSize, "zh-CN",
                                cjkFamily: _typography.CjkFamily);
                            Add(atoms, new(new(token, MathFlowAtomKind.Space, space.Advance, 0, 0,
                                Math.Max(space.Advance * 1.6, TextSize * 0.35), space.Advance / 3), null, null, text));
                        }
                        else
                        {
                            var shape = shaper.Shape(device, text,
                                _typography.TextFamily("Arial", false), TextSize, "zh-CN",
                                cjkFamily: _typography.CjkFamily);
                            _textOwners.Add(shape);
                            Add(atoms, new(new(token, MathFlowAtomKind.Text, shape.Advance,
                                Math.Max(1, shape.Baseline), Math.Max(0, shape.Height - shape.Baseline)),
                                shape, null, text));
                        }
                    }
                }
                blocks.Add(new ParagraphBlock(block.Source, atoms.ToArray()));
            }
            _blocks = blocks.ToArray();
        }
        catch
        {
            foreach (var formula in _mathOwners) formula.Dispose();
            foreach (var shape in _textOwners) shape.Dispose();
            throw;
        }
    }

    public static MarkdownMathSession Create(CanvasDevice device, IReadOnlyDictionary<int, MathLayoutResult> layouts,
        TypographyPreset? typography = null)
    {
        ArgumentNullException.ThrowIfNull(device); ArgumentNullException.ThrowIfNull(layouts);
        if (layouts.Count != MarkdownMathSample.Document.Math.Length)
            throw new ArgumentException("Every Markdown math node must have a worker layout.", nameof(layouts));
        return new(device, layouts, typography ?? TypographyPreset.Balanced);
    }

    private NativeMathFormula BindFormula(CanvasDevice device, IReadOnlyDictionary<int, MathLayoutResult> layouts,
        MarkdownMathNode node)
    {
        if (!layouts.TryGetValue(node.Source.Start, out var layout) ||
            layout.Source != node.GetContent(MarkdownMathSample.Source))
            throw new ArgumentException("Math layout does not match the exact Markdown source node.", nameof(layouts));
        var formula = NativeMathFormula.Create(device, layout);
        _mathOwners.Add(formula); return formula;
    }

    private void Add(List<BoundAtom> atoms, BoundAtom next)
    {
        if (atoms.Count > 0)
        {
            var previous = atoms[^1];
            // CJK glue has no natural width; full-line adjustment may stretch it.
            if (MarkdownMathSpacing.CreateFlexibleGap(MarkdownMathSample.Source,
                previous.Atom, previous.Text, next.Atom, next.Text, TextSize) is { } gap)
                atoms.Add(new(gap, null, null, ""));
        }
        atoms.Add(next);
    }

    private static IEnumerable<SourceRange> Tokenize(SourceRange source)
    {
        var text = MarkdownMathSample.Source.GetText(source);
        var boundaries = System.Globalization.StringInfo.ParseCombiningCharacters(text);
        var start = 0; var kind = -1;
        for (var i = 0; i < boundaries.Length; i++)
        {
            var point = boundaries[i];
            var end = i + 1 < boundaries.Length ? boundaries[i + 1] : text.Length;
            var grapheme = text[point..end];
            var nextKind = grapheme.All(char.IsWhiteSpace) ? 0 : TextSpacingPolicy.IsCjk(grapheme) ? 1 : 2;
            if (kind >= 0 && (kind != nextKind || nextKind == 1))
            {
                yield return new(source.Start + start, point - start);
                start = point;
            }
            kind = nextKind;
        }
        if (start < text.Length) yield return new(source.Start + start, text.Length - start);
    }

    internal IReadOnlyList<Block> Blocks => _blocks;

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var formula in _mathOwners) formula.Dispose();
        foreach (var shape in _textOwners) shape.Dispose();
        _mathOwners.Clear(); _textOwners.Clear();
    }

    internal abstract record Block(SourceRange Source);
    internal sealed record ParagraphBlock(SourceRange Source, BoundAtom[] Atoms) : Block(Source);
    internal sealed record DisplayBlock(SourceRange Source, NativeMathFormula Formula) : Block(Source);
    internal sealed record BoundAtom(MathFlowAtom Atom, ShapedText? TextShape,
        NativeMathFormula? Formula, string Text);
}

internal sealed class MarkdownMathPage : IDisposable
{
    private readonly SharedResource<MarkdownMathSession>.Lease _lease;
    private readonly List<FrameBlock> _blocks = [];
    private bool _disposed;
    public double Width { get; }
    public double Top { get; }
    public double Bottom { get; private set; }
    public int InfeasibleBlocks { get; private set; }
    public string Fingerprint { get; private set; } = "";
    public TextInteractionMap Interaction { get; private set; } = null!;

    private MarkdownMathPage(SharedResource<MarkdownMathSession>.Lease lease, double width,
        double top, CancellationToken cancellationToken)
    {
        _lease = lease; Width = width; Top = top;
        try
        {
            var y = top + 32;
            foreach (var block in lease.Value.Blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (block is MarkdownMathSession.DisplayBlock display)
                {
                    var layout = display.Formula.Layout;
                    var feasible = layout.Width <= width;
                    var x = feasible ? (width - layout.Width) / 2 : 0;
                    _blocks.Add(new DisplayFrame(display, y, x, feasible));
                    if (!feasible) InfeasibleBlocks++;
                    y += feasible ? layout.TotalHeight + 34 : 72;
                }
                else if (block is MarkdownMathSession.ParagraphBlock paragraph)
                {
                    var atoms = paragraph.Atoms.Select(atom => atom.Atom).ToArray();
                    var flow = MarkdownMathFlow.Compose(atoms, width, y,
                        lineGap: lease.Value.LineGap, cancellationToken: cancellationToken);
                    _blocks.Add(new ParagraphFrame(paragraph, y, flow));
                    if (!flow.IsSuccess) InfeasibleBlocks++;
                    y += flow.IsSuccess ? flow.Height + 24 : 72;
                }
            }
            Bottom = y;
            var signature = string.Join("|", _blocks.Select(block => block switch
            {
                ParagraphFrame paragraph => string.Join(";", paragraph.Flow.Lines.Select(line =>
                    string.Create(CultureInfo.InvariantCulture, $"{line.Source.Start}:{line.Source.Length}:{line.Advance:R}"))),
                DisplayFrame display => string.Create(CultureInfo.InvariantCulture,
                    $"display:{display.Block.Source.Start}:{display.X:R}:{display.Feasible}"),
                _ => ""
            }));
            Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
            Interaction = BuildInteraction();
        }
        catch { lease.Dispose(); throw; }
    }

    public static MarkdownMathPage Create(SharedResource<MarkdownMathSession>.Lease lease,
        double width, double top, CancellationToken cancellationToken = default) =>
        new(lease, width, top, cancellationToken);

    private TextInteractionMap BuildInteraction()
    {
        var lines = new List<TextInteractionLine>();
        foreach (var frame in _blocks)
        {
            if (frame is DisplayFrame display)
            {
                if (display.Feasible)
                {
                    var box = display.Block.Formula.Layout;
                    lines.Add(new(display.Block.Source,
                        new(display.X, display.Y, box.Width, box.TotalHeight),
                        [new(display.Block.Source, display.X, display.X + box.Width)]));
                }
                continue;
            }
            var paragraph = (ParagraphFrame)frame;
            foreach (var line in paragraph.Flow.Lines)
            {
                var spans = new List<TextInteractionSpan>();
                foreach (var placed in line.Placements)
                {
                    if (!line.Source.Contains(placed.Source)) continue;
                    var atom = paragraph.Block.Atoms[placed.AtomIndex];
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
                                var source = new SourceRange(placed.Source.Start + cluster.SourceStart, cluster.SourceLength);
                                var first = placed.X + run.BaselineX + (rtl ? -prefix[^1] + prefix[cluster.GlyphStart] : prefix[cluster.GlyphStart]);
                                var last = placed.X + run.BaselineX + (rtl ? -prefix[^1] + prefix[cluster.GlyphStart + cluster.GlyphCount] :
                                    prefix[cluster.GlyphStart + cluster.GlyphCount]);
                                if (rtl) (first, last) = (last, first);
                                TextInteractionMap.AddGraphemes(spans, MarkdownMathSample.Source, source, first, last);
                            }
                        }
                    }
                    else if (placed.Source.Length > 0)
                        spans.Add(new(placed.Source, placed.X, placed.X + placed.Width));
                }
                lines.Add(new(line.Source, new(0, line.Y, Width, line.Height), spans));
            }
        }
        return new(MarkdownMathSample.Source, TextSurface.MarkdownMath, lines);
    }

    public void Draw(CanvasDrawingSession session, ICanvasBrush ink, ICanvasBrush label,
        ICanvasBrush edge, double visibleTop, double visibleBottom, float zoom, bool drawHeading = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Bottom < visibleTop || Top > visibleBottom) return;
        using var heading = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 12 };
        if (drawHeading)
            session.DrawText("MARKDOWN BODY / $inline$ and $$display$$", new Vector2(0, (float)Top), label, heading);
        foreach (var frame in _blocks)
        {
            if (frame is DisplayFrame display)
            {
                if (display.Y > visibleBottom || display.Y + 80 < visibleTop) continue;
                if (display.Feasible)
                    display.Block.Formula.Draw(session, new((float)display.X, (float)display.Y), ink);
                else DrawFailure(session, label, display.Y, heading);
                continue;
            }
            var paragraph = (ParagraphFrame)frame;
            if (!paragraph.Flow.IsSuccess)
            {
                if (paragraph.Y >= visibleTop && paragraph.Y <= visibleBottom)
                    DrawFailure(session, label, paragraph.Y, heading);
                continue;
            }
            foreach (var line in paragraph.Flow.Lines)
            {
                if (line.Y + line.Height < visibleTop || line.Y > visibleBottom) continue;
                foreach (var placed in line.Placements)
                {
                    var atom = paragraph.Block.Atoms[placed.AtomIndex];
                    if (atom.TextShape is not null)
                        atom.TextShape.Draw(session, new((float)placed.X, (float)placed.Top), ink);
                    else if (atom.Formula is not null)
                        atom.Formula.Draw(session, new((float)placed.X, (float)placed.Top), ink);
                }
            }
        }
    }

    public void DrawHeading(CanvasDrawingSession session, Windows.UI.Color color)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var heading = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 12 };
        session.DrawText("MARKDOWN BODY / $inline$ and $$display$$", new Vector2(0, (float)Top), color, heading);
    }

    private void DrawFailure(CanvasDrawingSession session, ICanvasBrush label, double y,
        CanvasTextFormat format) => session.DrawText(
            "公式盒子或段落在此宽度下无合法断行；请扩大窗口或降低缩放。",
            new Rect(0, y, Width, 54), label, format);

    public void Dispose() { if (_disposed) return; _disposed = true; _lease.Dispose(); }

    private abstract record FrameBlock;
    private sealed record ParagraphFrame(MarkdownMathSession.ParagraphBlock Block, double Y,
        MathFlowResult Flow) : FrameBlock;
    private sealed record DisplayFrame(MarkdownMathSession.DisplayBlock Block, double Y, double X,
        bool Feasible) : FrameBlock;
}
