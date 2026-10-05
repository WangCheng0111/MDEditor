using System.Collections.Immutable;
using MDEditor.Typesetting.Layout;

namespace MDEditor.Typesetting.LineBreaking;

/// <summary>Unknown and known-infeasible are distinct. Measure must permanently resolve an unknown edge.</summary>
public interface ILineMeasurementCache
{
    bool TryGet(LineMeasureRequest request, out LineMeasurement? measurement);
    void Measure(LineMeasureRequest request);
}

/// <summary>
/// Exact constant-width Knuth-Plass with lazy contextual measurements. Unknown edges have admissible
/// cost bounds, not estimated widths. A result is published only when its entire winning path is exact;
/// it then also beats every remaining optimistic path. No monotonic-width/font assumption is made.
/// </summary>
public static class LazyKnuthPlassLineBreaker
{
    private static readonly LineFitness[] Fitnesses = Enum.GetValues<LineFitness>();
    public static LineBreakResult Break(IEnumerable<LineBreakItem> items, double width, ILineMeasurementCache cache,
        LineBreakOptions? options = null, CancellationToken cancellationToken = default,
        bool prefetchInitialCandidates = false)
    {
        ArgumentNullException.ThrowIfNull(items); ArgumentNullException.ThrowIfNull(cache);
        cancellationToken.ThrowIfCancellationRequested();
        LayoutValidation.Positive(width, nameof(width)); options ??= new();
        var builder = ImmutableArray.CreateBuilder<LineBreakItem>();
        double aggregate = 0, totalStretch = 0, totalShrink = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Add(item); aggregate = Sum(aggregate, item.Width);
            totalStretch = Sum(totalStretch, item.Stretch); totalShrink = Sum(totalShrink, item.Shrink);
        }
        var frozen = builder.ToImmutable();
        if (frozen.IsEmpty) return new(LineBreakStatus.Success, frozen, [], 0);
        var nextStarts = new int[frozen.Length + 1]; nextStarts[^1] = frozen.Length;
        var contentEnds = new int[frozen.Length + 1];
        for (var i = frozen.Length - 1; i >= 0; i--)
            nextStarts[i] = frozen[i].Kind == LineBreakItemKind.Box || frozen[i].IsForced ? i : nextStarts[i + 1];
        for (var i = 0; i < frozen.Length; i++)
            contentEnds[i + 1] = frozen[i].Kind == LineBreakItemKind.Box ? i + 1 : contentEnds[i];
        var prefetched = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var best = Solve(frozen, nextStarts, contentEnds, width, options, cache, cancellationToken);
            if (best is null) return new(LineBreakStatus.NoFeasibleBreaks, frozen, [], null);
            var path = new List<Node>();
            for (var node = best; node.Line is not null; node = node.Previous!) path.Add(node);
            path.Reverse();
            var unknown = path.Where(n => n.Unknown.HasValue).Select(n => n.Unknown!.Value).Distinct().ToArray();
            if (unknown.Length == 0) return new(LineBreakStatus.Success, frozen, path.Select(n => n.Line!), best.Total);
            if (prefetchInitialCandidates && !prefetched && path.Count > 1 && unknown[0].StartItemIndex == 0)
            {
                // Resolve the root frontier in one pass. It avoids re-running the optimistic graph once per
                // rejected prefix, while descendants remain lazy. This changes scheduling, never costs/results.
                prefetched = true;
                for (var i = 0; i < frozen.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested(); var item = frozen[i];
                    var candidate = item.Kind == LineBreakItemKind.Penalty && !item.IsForbidden ||
                        item.Kind == LineBreakItemKind.Glue && i > 0 && frozen[i - 1].Kind == LineBreakItemKind.Box;
                    if (candidate && (item.IsForced || nextStarts[i + 1] < frozen.Length))
                    {
                        var terminal = item.IsForced && nextStarts[i + 1] == frozen.Length;
                        var request = new LineMeasureRequest(0, terminal ? contentEnds[i + 1] : i, i,
                            item.Kind == LineBreakItemKind.Penalty ? item.Width : 0, terminal);
                        if (!cache.TryGet(request, out _)) cache.Measure(request);
                        if (item.IsForced) break;
                    }
                }
                continue;
            }
            foreach (var request in unknown)
            {
                cancellationToken.ThrowIfCancellationRequested(); cache.Measure(request);
                if (!cache.TryGet(request, out _)) throw new InvalidOperationException("Lazy measurement did not resolve the candidate.");
            }
        }
    }
    private sealed record Node(Node? Previous, LineBreakLine? Line, int LineCount, double Total,
        LineFitness Fitness, bool Flagged, LineMeasureRequest? Unknown = null);
    private sealed record Group(int Start, Node[] Nodes);
    private static Node? Solve(ImmutableArray<LineBreakItem> items, int[] nextStarts, int[] contentEnds,
        double width, LineBreakOptions options, ILineMeasurementCache cache, CancellationToken token)
    {
        var root = new Node(null, null, 0, 0, LineFitness.Decent, false);
        var groups = new List<Group> { new(0, [root]) };
        for (var i = 0; i < items.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var item = items[i];
            var candidate = item.Kind == LineBreakItemKind.Penalty && !item.IsForbidden ||
                item.Kind == LineBreakItemKind.Glue && i > 0 && items[i - 1].Kind == LineBreakItemKind.Box;
            if (!candidate || !item.IsForced && nextStarts[i + 1] == items.Length) continue;
            var next = nextStarts[i + 1]; var terminal = item.IsForced && next == items.Length;
            var winners = Evaluate(groups, i, terminal ? contentEnds[i + 1] : i, next,
                item.Kind == LineBreakItemKind.Penalty ? item.Width : 0,
                item.Kind == LineBreakItemKind.Penalty ? item.PenaltyValue : 0,
                item.Flagged, item.IsForced, terminal, width, options, cache, token);
            if (terminal) return SelectBest(winners);
            if (item.IsForced) groups.Clear();
            if (winners.Length != 0) groups.Add(new(next, winners));
            if (item.IsForced && groups.Count == 0) return null;
        }
        return SelectBest(Evaluate(groups, items.Length, contentEnds[^1], items.Length, 0,
            -LineBreakItem.InfinitePenalty, false, true, true, width, options, cache, token));
    }
    private static Node? SelectBest(Node[] candidates)
    {
        Node? best = null;
        foreach (var candidate in candidates) if (best is null || candidate.Total < best.Total) best = candidate;
        return best;
    }
    private static Node[] Evaluate(List<Group> groups, int breakIndex, int contentEnd, int next,
        double breakWidth, int penalty, bool flagged, bool forced, bool terminal, double width,
        LineBreakOptions options, ILineMeasurementCache cache, CancellationToken token)
    {
        var best = new Dictionary<LineFitness, Node>();
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested(); if (group.Start > breakIndex) continue;
            var request = new LineMeasureRequest(group.Start, Math.Max(group.Start, contentEnd), breakIndex, breakWidth, terminal);
            var known = cache.TryGet(request, out var measured);
            if (known && measured is null) continue;
            if (!known)
            {
                // Badness is nonnegative and LinePenalty >= 0. Other costs/fitness transitions are exact.
                foreach (var previous in group.Nodes)
                foreach (var fitness in Fitnesses)
                    Add(previous, fitness, options.LinePenalty * options.LinePenalty, 0, 0, 0, 0, 0, 0, request);
                continue;
            }
            var metrics = measured!.Value; var difference = width - metrics.NaturalWidth;
            double ratio;
            if (terminal && options.LastLineAlignment == LastLineAlignment.RaggedRight && difference >= 0 || difference == 0) ratio = 0;
            else
            {
                var capacity = difference > 0 ? metrics.Stretch : metrics.Shrink;
                if (capacity == 0) continue;
                ratio = difference / capacity; if (!double.IsFinite(ratio)) continue;
                if (difference > 0)
                {
                    if (width > metrics.NaturalWidth + capacity * options.MaximumStretchRatio) continue;
                    ratio = Math.Min(ratio, options.MaximumStretchRatio);
                }
                else
                {
                    if (width < metrics.NaturalWidth - capacity) continue;
                    ratio = Math.Max(ratio, -1);
                }
            }
            var actualFitness = ratio < -0.5 ? LineFitness.Tight : ratio <= 0.5 ? LineFitness.Decent : ratio <= 1 ? LineFitness.Loose : LineFitness.VeryLoose;
            var magnitude = Math.Abs(ratio); var badness = magnitude >= Math.Cbrt(100) ? 10000 : 100 * magnitude * magnitude * magnitude;
            var basic = Sum(options.LinePenalty, badness);
            foreach (var previous in group.Nodes)
                Add(previous, actualFitness, Finite(basic * basic), metrics.NaturalWidth, metrics.Stretch,
                    metrics.Shrink, ratio, Sum(metrics.NaturalWidth, ratio * (ratio < 0 ? metrics.Shrink : metrics.Stretch)), badness, null);

            void Add(Node previous, LineFitness fitness, double baseCost, double natural, double stretch,
                double shrink, double adjustment, double actual, double badness, LineMeasureRequest? unknown)
            {
                token.ThrowIfCancellationRequested(); var demerits = Finite(baseCost); var p = (double)penalty;
                if (penalty > 0) demerits = Sum(demerits, p * p);
                else if (penalty > -LineBreakItem.InfinitePenalty) demerits = Sum(demerits, -p * p);
                if (Math.Abs((int)previous.Fitness - (int)fitness) > 1) demerits = Sum(demerits, options.AdjacentFitnessDemerits);
                if (previous.Flagged)
                {
                    if (terminal) demerits = Sum(demerits, options.FinalFlaggedDemerits);
                    else if (flagged) demerits = Sum(demerits, options.ConsecutiveFlaggedDemerits);
                }
                var line = new LineBreakLine(group.Start, Math.Max(group.Start, contentEnd), breakIndex, next,
                    width, natural, stretch, shrink, breakWidth, adjustment, actual, badness, fitness,
                    penalty, flagged, forced, terminal, demerits);
                var node = new Node(previous, line, previous.LineCount + 1, Sum(previous.Total, demerits), fitness, flagged, unknown);
                if (!best.TryGetValue(fitness, out var incumbent) || node.Total < incumbent.Total) best[fitness] = node;
            }
        }
        return best.OrderBy(p => p.Key).Select(p => p.Value).ToArray();
    }
    private static double Sum(double left, double right) => Finite(left + right);
    private static double Finite(double value) => double.IsFinite(value) ? value : throw new OverflowException("Non-finite line metrics or costs.");
}
