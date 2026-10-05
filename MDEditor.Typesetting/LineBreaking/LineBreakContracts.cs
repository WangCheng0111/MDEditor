using System.Collections.Immutable;
using MDEditor.Typesetting.Layout;

namespace MDEditor.Typesetting.LineBreaking;

public enum LineBreakItemKind { Box, Glue, Penalty }

/// <summary>Measured primitives, not characters. Penalty width appears only when selected.</summary>
public readonly record struct LineBreakItem
{
    public const int InfinitePenalty = 10000;
    public LineBreakItemKind Kind { get; }
    public double Width { get; }
    public double Stretch { get; }
    public double Shrink { get; }
    public int PenaltyValue { get; }
    public bool Flagged { get; }
    public bool IsForced => Kind == LineBreakItemKind.Penalty && PenaltyValue <= -InfinitePenalty;
    public bool IsForbidden => Kind == LineBreakItemKind.Penalty && PenaltyValue >= InfinitePenalty;

    private LineBreakItem(LineBreakItemKind kind, double width, double stretch, double shrink,
        int penalty, bool flagged)
    {
        LayoutValidation.Nonnegative(width, nameof(width));
        LayoutValidation.Nonnegative(stretch, nameof(stretch));
        LayoutValidation.Nonnegative(shrink, nameof(shrink));
        if (shrink > width) throw new ArgumentOutOfRangeException(nameof(shrink), "Glue cannot shrink below zero.");
        Kind = kind; Width = width; Stretch = stretch; Shrink = shrink;
        PenaltyValue = penalty; Flagged = flagged;
    }
    public static LineBreakItem Box(double width) => new(LineBreakItemKind.Box, width, 0, 0, 0, false);
    public static LineBreakItem Glue(double width, double stretch, double shrink) =>
        new(LineBreakItemKind.Glue, width, stretch, shrink, 0, false);
    public static LineBreakItem Penalty(double width, int value, bool flagged = false) =>
        new(LineBreakItemKind.Penalty, width, 0, 0, value, flagged);
}

public enum LastLineAlignment { RaggedRight, Justified }
public enum LineFitness { Tight, Decent, Loose, VeryLoose }

/// <summary>Continuous-ratio model, not TeX's integer arithmetic or emergency passes.</summary>
public sealed class LineBreakOptions
{
    public double MaximumStretchRatio { get; }
    public double LinePenalty { get; }
    public double AdjacentFitnessDemerits { get; }
    public double ConsecutiveFlaggedDemerits { get; }
    public double FinalFlaggedDemerits { get; }
    public LastLineAlignment LastLineAlignment { get; }
    public LineBreakOptions(double maximumStretchRatio = 3, double linePenalty = 10,
        double adjacentFitnessDemerits = 10000, double consecutiveFlaggedDemerits = 10000,
        double finalFlaggedDemerits = 5000, LastLineAlignment lastLineAlignment = LastLineAlignment.RaggedRight)
    {
        LayoutValidation.Nonnegative(maximumStretchRatio, nameof(maximumStretchRatio));
        LayoutValidation.Nonnegative(linePenalty, nameof(linePenalty));
        LayoutValidation.Nonnegative(adjacentFitnessDemerits, nameof(adjacentFitnessDemerits));
        LayoutValidation.Nonnegative(consecutiveFlaggedDemerits, nameof(consecutiveFlaggedDemerits));
        LayoutValidation.Nonnegative(finalFlaggedDemerits, nameof(finalFlaggedDemerits));
        if (!Enum.IsDefined(lastLineAlignment)) throw new ArgumentOutOfRangeException(nameof(lastLineAlignment));
        MaximumStretchRatio = maximumStretchRatio; LinePenalty = linePenalty;
        AdjacentFitnessDemerits = adjacentFitnessDemerits;
        ConsecutiveFlaggedDemerits = consecutiveFlaggedDemerits;
        FinalFlaggedDemerits = finalFlaggedDemerits; LastLineAlignment = lastLineAlignment;
    }
}

/// <summary>Zero-based line widths; after the last entry that width repeats. No line-count preference.</summary>
public sealed class LineWidthProfile
{
    public ImmutableArray<double> Widths { get; }
    public LineWidthProfile(IEnumerable<double> widths)
    {
        ArgumentNullException.ThrowIfNull(widths);
        Widths = LayoutValidation.Freeze(widths);
        if (Widths.IsEmpty) throw new ArgumentException("At least one line width is required.", nameof(widths));
        foreach (var width in Widths) LayoutValidation.Positive(width, nameof(widths));
    }
    public double GetWidth(int lineIndex)
    {
        if (lineIndex < 0) throw new ArgumentOutOfRangeException(nameof(lineIndex));
        return Widths[Math.Min(lineIndex, Widths.Length - 1)];
    }
}

public enum LineBreakStatus { Success, NoFeasibleBreaks }

/// <summary>Optional contextual shaping input. End excludes the selected glue/penalty.</summary>
public readonly record struct LineMeasureRequest(int StartItemIndex, int EndItemIndex,
    int BreakItemIndex, double BreakWidth, bool IsParagraphEnd);

/// <summary>True candidate-line metrics; NaturalWidth includes conditional BreakWidth.</summary>
public readonly record struct LineMeasurement
{
    public double NaturalWidth { get; }
    public double Stretch { get; }
    public double Shrink { get; }
    public LineMeasurement(double naturalWidth, double stretch, double shrink)
    {
        LayoutValidation.Nonnegative(naturalWidth, nameof(naturalWidth));
        LayoutValidation.Nonnegative(stretch, nameof(stretch));
        LayoutValidation.Nonnegative(shrink, nameof(shrink));
        if (shrink > naturalWidth) throw new ArgumentOutOfRangeException(nameof(shrink));
        NaturalWidth = naturalWidth; Stretch = stretch; Shrink = shrink;
    }
}

/// <summary>Item indices are not UTF-16 positions. Content is [StartItemIndex, EndItemIndex).</summary>
public sealed record LineBreakLine(
    int StartItemIndex, int EndItemIndex, int BreakItemIndex, int NextItemIndex,
    double TargetWidth, double NaturalWidth, double Stretch, double Shrink,
    double BreakWidth, double AdjustmentRatio, double ActualWidth, double Badness,
    LineFitness Fitness, int PenaltyValue, bool Flagged, bool IsForced,
    bool IsParagraphEnd, double Demerits);

public sealed class LineBreakResult
{
    public LineBreakStatus Status { get; }
    public bool IsSuccess => Status == LineBreakStatus.Success;
    public ImmutableArray<LineBreakItem> Items { get; }
    public ImmutableArray<LineBreakLine> Lines { get; }
    public double? TotalDemerits { get; }
    internal LineBreakResult(LineBreakStatus status, ImmutableArray<LineBreakItem> items,
        IEnumerable<LineBreakLine> lines, double? totalDemerits)
    {
        Status = status; Items = items; Lines = LayoutValidation.Freeze(lines); TotalDemerits = totalDemerits;
    }
}
