using System.Collections.Immutable;
using MDEditor.Core.Text;
using MDEditor.Typesetting.LineBreaking;
using MDEditor.Typesetting.Typography;

namespace MDEditor.Typesetting.Markdown;

public enum MathFlowAtomKind { Text, Math, Space, Gap }

/// <summary>Measured native text or worker math. Delimiter ranges remain in Source for hit testing.</summary>
public sealed record MathFlowAtom
{
    public SourceRange Source { get; }
    public MathFlowAtomKind Kind { get; }
    public double Width { get; }
    public double Height { get; }
    public double Depth { get; }
    public double Stretch { get; }
    public double Shrink { get; }

    public MathFlowAtom(SourceRange source, MathFlowAtomKind kind, double width, double height,
        double depth, double stretch = 0, double shrink = 0)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!double.IsFinite(width) || width < 0 || !double.IsFinite(height) || height < 0 ||
            !double.IsFinite(depth) || depth < 0 || !double.IsFinite(stretch) || stretch < 0 ||
            !double.IsFinite(shrink) || shrink < 0 || shrink > width)
            throw new ArgumentOutOfRangeException(nameof(width), "Math flow metrics must be finite and nonnegative.");
        if (kind is MathFlowAtomKind.Text or MathFlowAtomKind.Math && source.Length == 0)
            throw new ArgumentException("Visible atoms require a source range.", nameof(source));
        if (kind is MathFlowAtomKind.Text or MathFlowAtomKind.Math && (height == 0 || width == 0))
            throw new ArgumentException("Visible atoms require measured geometry.", nameof(width));
        if (kind is MathFlowAtomKind.Space or MathFlowAtomKind.Gap && (height != 0 || depth != 0))
            throw new ArgumentException("Glue cannot contribute ink height.", nameof(height));
        Source = source; Kind = kind; Width = width; Height = height; Depth = depth;
        Stretch = stretch; Shrink = shrink;
    }
}

public sealed record MathFlowPlacement(int AtomIndex, SourceRange Source, MathFlowAtomKind Kind,
    double X, double Top, double Width, double Height, double Depth);

public sealed record MathFlowLine(SourceRange Source, double Y, double Baseline, double Height,
    double Advance, bool Justified, ImmutableArray<MathFlowPlacement> Placements);

public sealed record MathFlowResult(LineBreakStatus Status, ImmutableArray<MathFlowLine> Lines,
    double Height, int AtomCount)
{
    public bool IsSuccess => Status == LineBreakStatus.Success;
}

/// <summary>Only legal CJK/mixed-script boundaries may become stretchable Markdown math glue.</summary>
public static class MarkdownMathSpacing
{
    private enum GapKind { None, InterCharacter, MixedScript, FormulaAdjacent }

    public static bool NeedsFlexibleGap(SourceTextSnapshot source, MathFlowAtom previous, string previousText,
        MathFlowAtom next, string nextText) => Classify(source, previous, previousText, next, nextText) != GapKind.None;

    public static MathFlowAtom? CreateFlexibleGap(SourceTextSnapshot source, MathFlowAtom previous,
        string previousText, MathFlowAtom next, string nextText, double fontSize)
    {
        if (!double.IsFinite(fontSize) || fontSize <= 0) throw new ArgumentOutOfRangeException(nameof(fontSize));
        var kind = Classify(source, previous, previousText, next, nextText);
        var options = CjkTypographyOptions.Refined;
        var (width, stretch, shrink) = kind switch
        {
            GapKind.InterCharacter => (0d, fontSize * options.InterCharacterMaximumEm, 0d),
            GapKind.MixedScript => (fontSize * options.MixedNaturalEm,
                fontSize * (options.MixedMaximumEm - options.MixedNaturalEm),
                fontSize * (options.MixedNaturalEm - options.MixedMinimumEm)),
            GapKind.FormulaAdjacent => (fontSize * 0.2, fontSize * 0.1, fontSize * 0.1),
            _ => (0d, 0d, 0d)
        };
        return kind == GapKind.None ? null : new(new(previous.Source.End, 0), MathFlowAtomKind.Gap,
            width, 0, 0, stretch, shrink);
    }

    private static GapKind Classify(SourceTextSnapshot source, MathFlowAtom previous, string previousText,
        MathFlowAtom next, string nextText)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(previousText);
        ArgumentNullException.ThrowIfNull(nextText);
        if (previous.Kind is not (MathFlowAtomKind.Text or MathFlowAtomKind.Math) ||
            next.Kind is not (MathFlowAtomKind.Text or MathFlowAtomKind.Math) ||
            previous.Source.End != next.Source.Start ||
            !source.FullRange.Contains(previous.Source) || !source.FullRange.Contains(next.Source) ||
            !CjkTypographyRules.MayBreak(source, next.Source.Start, CjkTypographyOptions.Refined))
            return GapKind.None;
        if (previous.Kind == MathFlowAtomKind.Text && next.Kind == MathFlowAtomKind.Text)
        {
            if (CjkTypographyRules.IsInterCharacterBoundary(previousText, nextText))
                return GapKind.InterCharacter;
            return CjkTypographyRules.IsMixedBoundary(previousText, nextText)
                ? GapKind.MixedScript : GapKind.None;
        }
        return TextSpacingPolicy.IsCjk(previousText) || TextSpacingPolicy.IsCjk(nextText)
            ? GapKind.FormulaAdjacent : GapKind.None;
    }
}

/// <summary>Knuth–Plass composition with measured math boxes and bounded glue; no fallback scaling.</summary>
public static class MarkdownMathFlow
{
    public static MathFlowResult Compose(IReadOnlyList<MathFlowAtom> atoms, double width,
        double firstY = 0, double lineGap = 6, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(atoms);
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(firstY) ||
            !double.IsFinite(lineGap) || lineGap < 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        cancellationToken.ThrowIfCancellationRequested();
        if (atoms.Count == 0) return new(LineBreakStatus.Success, [], 0, 0);
        var items = new LineBreakItem[atoms.Count];
        var sourceCursor = 0;
        for (var index = 0; index < atoms.Count; index++)
        {
            var atom = atoms[index] ?? throw new ArgumentException("A flow atom is null.", nameof(atoms));
            if (atom.Source.Start < sourceCursor)
                throw new ArgumentException("Flow atom ranges overlap or are out of source order.", nameof(atoms));
            sourceCursor = atom.Source.End;
            items[index] = atom.Kind is MathFlowAtomKind.Space or MathFlowAtomKind.Gap
                ? LineBreakItem.Glue(atom.Width, atom.Stretch, atom.Shrink)
                : LineBreakItem.Box(atom.Width);
        }
        var breaks = KnuthPlassLineBreaker.Break(items, width,
            new LineBreakOptions(maximumStretchRatio: 1), cancellationToken);
        if (!breaks.IsSuccess) return new(breaks.Status, [], 0, atoms.Count);
        var lines = ImmutableArray.CreateBuilder<MathFlowLine>();
        var y = firstY;
        foreach (var line in breaks.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var visible = Enumerable.Range(line.StartItemIndex, line.EndItemIndex - line.StartItemIndex)
                .Where(index => atoms[index].Kind is MathFlowAtomKind.Text or MathFlowAtomKind.Math).ToArray();
            if (visible.Length == 0) continue;
            var height = visible.Max(index => atoms[index].Height);
            var depth = visible.Max(index => atoms[index].Depth);
            var baseline = y + height;
            var x = 0d;
            var placements = ImmutableArray.CreateBuilder<MathFlowPlacement>();
            for (var index = line.StartItemIndex; index < line.EndItemIndex; index++)
            {
                var atom = atoms[index];
                var advance = atom.Width + line.AdjustmentRatio *
                    (line.AdjustmentRatio < 0 ? atom.Shrink : atom.Stretch);
                if (advance < -1e-9) throw new InvalidOperationException("Negative glue advance.");
                advance = Math.Max(0, advance);
                placements.Add(new(index, atom.Source, atom.Kind, x, baseline - atom.Height,
                    advance, atom.Height, atom.Depth));
                x += advance;
            }
            var justified = !line.IsParagraphEnd || line.AdjustmentRatio != 0;
            var expected = justified ? width : line.NaturalWidth;
            if (Math.Abs(x - expected) > 1e-5)
                throw new InvalidOperationException("Math flow width differs from the chosen Knuth–Plass line.");
            var source = new SourceRange(atoms[visible[0]].Source.Start,
                atoms[visible[^1]].Source.End - atoms[visible[0]].Source.Start);
            var lineHeight = height + depth;
            lines.Add(new(source, y, baseline, lineHeight, x, justified, placements.ToImmutable()));
            y += lineHeight + lineGap;
        }
        return new(LineBreakStatus.Success, lines.ToImmutable(), y - firstY, atoms.Count);
    }
}
