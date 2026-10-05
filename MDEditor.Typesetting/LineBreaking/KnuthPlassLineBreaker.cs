using System.Collections.Immutable;
using MDEditor.Typesetting.Layout;

namespace MDEditor.Typesetting.LineBreaking;

/// <summary>
/// Whole-paragraph dynamic programming over box/glue/penalty breaks. Strict infeasibility,
/// explicit final-line policy, and no shaping, hyphenation generation or rendering.
/// </summary>
public static class KnuthPlassLineBreaker
{
    public static LineBreakResult Break(IEnumerable<LineBreakItem> items, double lineWidth,
        LineBreakOptions? options = null, CancellationToken cancellationToken = default,
        Func<LineMeasureRequest, LineMeasurement?>? lineMeasurer = null) =>
        Break(items, new LineWidthProfile(new[] { lineWidth }), options, cancellationToken, lineMeasurer);

    public static LineBreakResult Break(IEnumerable<LineBreakItem> items, LineWidthProfile widths,
        LineBreakOptions? options = null, CancellationToken cancellationToken = default,
        Func<LineMeasureRequest, LineMeasurement?>? lineMeasurer = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(widths);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        // Copy even caller-owned ImmutableArray backing storage. Check cancellation during enumeration.
        var builder = ImmutableArray.CreateBuilder<LineBreakItem>();
        var aggregate = new Metrics();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Add(item);
            aggregate.Add(item); // Reject non-finite aggregate metrics rather than report "no solution".
        }
        var frozen = builder.ToImmutable();
        if (frozen.IsEmpty) return new(LineBreakStatus.Success, frozen, Array.Empty<LineBreakLine>(), 0);

        // O(N) preprocessing: discardable suffix and post-break starts; never cross a forced penalty.
        var nextStarts = new int[frozen.Length + 1];
        nextStarts[^1] = frozen.Length;
        var finalContentEnd = 0;
        for (var i = frozen.Length - 1; i >= 0; i--)
            nextStarts[i] = frozen[i].Kind == LineBreakItemKind.Box || frozen[i].IsForced ? i : nextStarts[i + 1];
        for (var i = 0; i < frozen.Length; i++)
            if (frozen[i].Kind == LineBreakItemKind.Box) finalContentEnd = i + 1;

        var root = new Node(null, null, 0, 0, LineFitness.Decent, false);
        var groups = new List<Group> { new(0, new[] { root }) };
        for (var i = 0; i < frozen.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = frozen[i];
            var candidate = item.Kind == LineBreakItemKind.Penalty && !item.IsForbidden ||
                item.Kind == LineBreakItemKind.Glue && i > 0 && frozen[i - 1].Kind == LineBreakItemKind.Box;
            if (candidate && (item.IsForced || nextStarts[i + 1] < frozen.Length))
            {
                var next = nextStarts[i + 1];
                var terminal = item.IsForced && next == frozen.Length;
                var end = terminal ? finalContentEnd : i;
                var winners = Evaluate(groups, i, end, next, item.Kind == LineBreakItemKind.Penalty ? item.Width : 0,
                    item.Kind == LineBreakItemKind.Penalty ? item.PenaltyValue : 0,
                    item.Flagged, item.IsForced, terminal, widths, options, cancellationToken, lineMeasurer);
                if (terminal) return Finish(frozen, winners);
                if (item.IsForced) groups.Clear(); // Nothing may jump across this mandatory break.
                if (winners.Length != 0) groups.Add(new(next, winners));
                if (item.IsForced && groups.Count == 0)
                    return new(LineBreakStatus.NoFeasibleBreaks, frozen, Array.Empty<LineBreakLine>(), null);
            }
            foreach (var group in groups)
            {
                if (i < group.Start) continue;
                group.Current.Add(item);
                if (item.Kind == LineBreakItemKind.Box) group.LastContent = group.Current;
            }
        }
        // Implicit paragraph end; strip trailing glue/unselected penalties, without adding phantom lines.
        var final = Evaluate(groups, frozen.Length, finalContentEnd, frozen.Length, 0,
            -LineBreakItem.InfinitePenalty, false, true, true, widths, options, cancellationToken, lineMeasurer);
        return Finish(frozen, final);
    }

    private static Node[] Evaluate(List<Group> groups, int breakIndex, int contentEnd, int next,
        double breakWidth, int penalty, bool flagged, bool forced, bool terminal,
        LineWidthProfile widths, LineBreakOptions options, CancellationToken token,
        Func<LineMeasureRequest, LineMeasurement?>? lineMeasurer)
    {
        var best = new Dictionary<StateKey, Node>();
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            if (group.Start > breakIndex) continue; // A previous break discarded these nodes.
            var metrics = terminal ? group.LastContent : group.Current;
            var natural = Sum(metrics.Width, breakWidth);
            if (lineMeasurer is not null)
            {
                var measured = lineMeasurer(new(group.Start, Math.Max(group.Start, contentEnd),
                    breakIndex, breakWidth, terminal));
                token.ThrowIfCancellationRequested();
                if (measured is null) continue;
                natural = measured.Value.NaturalWidth;
                metrics.Stretch = measured.Value.Stretch;
                metrics.Shrink = measured.Value.Shrink;
            }
            foreach (var previous in group.Nodes)
            {
                token.ThrowIfCancellationRequested();
                var target = widths.GetWidth(previous.LineCount);
                var difference = target - natural;
                double ratio;
                if (terminal && options.LastLineAlignment == LastLineAlignment.RaggedRight && difference >= 0)
                    ratio = 0; // Final fill does not stretch visible spaces.
                else if (difference == 0) ratio = 0;
                else
                {
                    var capacity = difference > 0 ? metrics.Stretch : metrics.Shrink;
                    if (capacity == 0) continue;
                    ratio = difference / capacity;
                    if (!double.IsFinite(ratio)) continue;
                    // Compare the actual width to the inclusive capacity boundary first.
                    // (natural + capacity - natural) / capacity can round slightly above 1.
                    // No epsilon permits a target beyond the computed boundary.
                    if (difference > 0)
                    {
                        if (target > natural + capacity * options.MaximumStretchRatio) continue;
                        ratio = Math.Min(ratio, options.MaximumStretchRatio);
                    }
                    else
                    {
                        if (target < natural - capacity) continue;
                        ratio = Math.Max(ratio, -1);
                    }
                }
                var fitness = ratio < -0.5 ? LineFitness.Tight : ratio <= 0.5 ? LineFitness.Decent :
                    ratio <= 1 ? LineFitness.Loose : LineFitness.VeryLoose;
                var magnitude = Math.Abs(ratio);
                var badness = magnitude >= Math.Cbrt(100) ? 10000 : 100 * magnitude * magnitude * magnitude;
                var basic = Sum(options.LinePenalty, badness);
                var demerits = Finite(basic * basic);
                // Cast BEFORE squaring: int.MinValue is a valid forced penalty, not integer overflow.
                var p = (double)penalty;
                if (penalty > 0) demerits = Sum(demerits, p * p);
                else if (penalty > -LineBreakItem.InfinitePenalty) demerits = Sum(demerits, -p * p);
                if (Math.Abs((int)previous.Fitness - (int)fitness) > 1)
                    demerits = Sum(demerits, options.AdjacentFitnessDemerits);
                if (previous.Flagged)
                {
                    if (terminal) demerits = Sum(demerits, options.FinalFlaggedDemerits);
                    else if (flagged) demerits = Sum(demerits, options.ConsecutiveFlaggedDemerits);
                }
                var actual = Sum(natural, ratio * (ratio < 0 ? metrics.Shrink : metrics.Stretch));
                var line = new LineBreakLine(group.Start, Math.Max(group.Start, contentEnd), breakIndex, next,
                    target, natural, metrics.Stretch, metrics.Shrink, breakWidth, ratio, actual, badness,
                    fitness, penalty, flagged, forced, terminal, demerits);
                var node = new Node(previous, line, previous.LineCount + 1, Sum(previous.Total, demerits), fitness, flagged);
                // Only merge states whose FUTURE costs/widths are identical. Endpoint flag is shared.
                var key = new StateKey(Math.Min(node.LineCount, widths.Widths.Length - 1), fitness);
                if (!best.TryGetValue(key, out var incumbent) || node.Total < incumbent.Total)
                    best[key] = node; // Exact ties retain deterministic first encounter, no epsilon pruning.
            }
        }
        return best.OrderBy(pair => pair.Key.WidthSlot).ThenBy(pair => pair.Key.Fitness)
            .Select(pair => pair.Value).ToArray();
    }

    private static LineBreakResult Finish(ImmutableArray<LineBreakItem> items, Node[] finals)
    {
        if (finals.Length == 0)
            return new(LineBreakStatus.NoFeasibleBreaks, items, Array.Empty<LineBreakLine>(), null);
        var best = finals[0];
        foreach (var node in finals) if (node.Total < best.Total) best = node;
        var lines = new List<LineBreakLine>();
        for (var node = best; node.Line is not null; node = node.Previous!) lines.Add(node.Line);
        lines.Reverse();
        return new(LineBreakStatus.Success, items, lines, best.Total);
    }

    private static double Finite(double value)
    {
        if (!double.IsFinite(value)) throw new OverflowException("Line metrics or demerits exceed finite double range.");
        return value;
    }
    private static double Sum(double left, double right) => Finite(left + right);

    private readonly record struct StateKey(int WidthSlot, LineFitness Fitness);
    private sealed record Node(Node? Previous, LineBreakLine? Line, int LineCount,
        double Total, LineFitness Fitness, bool Flagged);
    private sealed class Group(int start, Node[] nodes)
    {
        public int Start { get; } = start;
        public Node[] Nodes { get; } = nodes;
        public Metrics Current;
        public Metrics LastContent;
    }
    private struct Metrics
    {
        public double Width;
        public double Stretch;
        public double Shrink;
        public void Add(LineBreakItem item)
        {
            if (item.Kind == LineBreakItemKind.Penalty) return;
            Width = Sum(Width, item.Width);
            Stretch = Sum(Stretch, item.Stretch);
            Shrink = Sum(Shrink, item.Shrink);
        }
    }
}
