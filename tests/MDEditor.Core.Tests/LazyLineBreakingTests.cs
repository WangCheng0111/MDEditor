using MDEditor.Typesetting.LineBreaking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class LazyLineBreakingTests
{
    private sealed class Measurements(Func<LineMeasureRequest, LineMeasurement?> exact) : ILineMeasurementCache
    {
        internal readonly Dictionary<LineMeasureRequest, LineMeasurement?> Values = new();
        internal int Resolves;
        public bool TryGet(LineMeasureRequest request, out LineMeasurement? measurement) => Values.TryGetValue(request, out measurement);
        public void Measure(LineMeasureRequest request) { Values[request] = exact(request); Resolves++; }
    }
    private static LineBreakItem[] Items(bool forced = false) =>
    [LineBreakItem.Box(10), LineBreakItem.Glue(2, 2, 1), LineBreakItem.Box(14),
     LineBreakItem.Penalty(2, 50, true), LineBreakItem.Box(8), LineBreakItem.Glue(3, 4, 1),
     LineBreakItem.Box(6), LineBreakItem.Penalty(0, forced ? -10000 : -30, true),
     LineBreakItem.Box(11), LineBreakItem.Glue(3, 3, 1), LineBreakItem.Box(12),
     LineBreakItem.Penalty(2, 80, true), LineBreakItem.Box(18), LineBreakItem.Glue(4, 3, 2),
     LineBreakItem.Box(7), LineBreakItem.Glue(2, 2, 1)];
    private static void Same(LineBreakResult expected, LineBreakResult actual)
    {
        Assert.AreEqual(expected.Status, actual.Status); Assert.AreEqual(expected.TotalDemerits, actual.TotalDemerits);
        CollectionAssert.AreEqual(expected.Lines.ToArray(), actual.Lines.ToArray());
    }
    [TestMethod]
    [DataRow(0)] [DataRow(1)] [DataRow(2)] [DataRow(3)] [DataRow(4)]
    [DataRow(5)] [DataRow(6)] [DataRow(7)] [DataRow(8)] [DataRow(9)]
    [DataRow(10)] [DataRow(11)] [DataRow(12)] [DataRow(13)] [DataRow(14)]
    [DataRow(15)] [DataRow(16)] [DataRow(17)] [DataRow(18)] [DataRow(19)]
    public void ExactOracleAgreesForNonmonotonicContextualWidthsAndCosts(int seed)
    {
        for (var trial = 0; trial < 30; trial++)
        {
            var random = new Random(seed * 100 + trial);
            var width = random.Next(10, 90); var forced = trial % 3 == 0;
            LineMeasurement? Exact(LineMeasureRequest r)
            {
                var rng = new Random(seed * 100003 + trial * 101 + r.StartItemIndex * 197 + r.EndItemIndex * 17 + r.BreakItemIndex * 37);
                if (rng.Next(7) == 0) return null;
                var natural = rng.Next(0, 160); return new(natural, rng.Next(0, 100), rng.Next(0, natural + 1));
            }
            var options = new LineBreakOptions(maximumStretchRatio: trial % 4, linePenalty: trial % 11,
                adjacentFitnessDemerits: random.Next(0, 1000), consecutiveFlaggedDemerits: random.Next(0, 1000),
                finalFlaggedDemerits: random.Next(0, 1000), lastLineAlignment: trial % 2 == 0 ? LastLineAlignment.RaggedRight : LastLineAlignment.Justified);
            var cache = new Measurements(Exact);
            var expected = KnuthPlassLineBreaker.Break(Items(forced), width, options, lineMeasurer: Exact);
            Same(expected, LazyKnuthPlassLineBreaker.Break(Items(forced), width, cache, options));
            Same(expected, LazyKnuthPlassLineBreaker.Break(Items(forced), width, new Measurements(Exact), options,
                prefetchInitialCandidates: true));
        }
    }
    [TestMethod]
    public void WarmCacheDoesNotMeasureAgain()
    {
        static LineMeasurement? Exact(LineMeasureRequest r) => new((r.EndItemIndex - r.StartItemIndex) * 8, 15, 0);
        var cache = new Measurements(Exact); var first = LazyKnuthPlassLineBreaker.Break(Items(), 40, cache);
        var resolves = cache.Resolves; Same(first, LazyKnuthPlassLineBreaker.Break(Items(), 40, cache));
        Assert.AreEqual(resolves, cache.Resolves);
    }
    [TestMethod]
    public void KnownInfeasibleIsNotConfusedWithUnknown()
    {
        var cache = new Measurements(_ => null);
        Assert.AreEqual(LineBreakStatus.NoFeasibleBreaks, LazyKnuthPlassLineBreaker.Break(Items(), 40, cache).Status);
        Assert.IsTrue(cache.Resolves > 0);
    }
    [TestMethod]
    public void WinningWholeParagraphAvoidsMeasuringDiscardedCandidates()
    {
        static LineMeasurement? Exact(LineMeasureRequest r) => new(r.EndItemIndex - r.StartItemIndex, 1, 0);
        var eager = 0; var cache = new Measurements(Exact);
        LineBreakItem[] items = [LineBreakItem.Box(10), LineBreakItem.Glue(2, 2, 0), LineBreakItem.Box(10),
            LineBreakItem.Glue(2, 2, 0), LineBreakItem.Box(10)];
        var expected = KnuthPlassLineBreaker.Break(items, 100, lineMeasurer: r => { eager++; return Exact(r); });
        Same(expected, LazyKnuthPlassLineBreaker.Break(items, 100, cache));
        Assert.IsTrue(cache.Resolves < eager); Assert.AreEqual(1, cache.Resolves);
    }
    private sealed class BrokenCache : ILineMeasurementCache
    {
        public bool TryGet(LineMeasureRequest r, out LineMeasurement? m) { m = null; return false; }
        public void Measure(LineMeasureRequest r) { }
    }
    [TestMethod]
    public void BrokenMeasurementProgressContractFailsInsteadOfLoopingForever() =>
        Assert.Throws<InvalidOperationException>(() => LazyKnuthPlassLineBreaker.Break(Items(), 40, new BrokenCache()));
    [TestMethod]
    public void PrecanceledEmptyInputStillObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => LazyKnuthPlassLineBreaker.Break([], 40, new Measurements(_ => null), cancellationToken: cancellation.Token));
    }
    [TestMethod]
    public void CancellationDuringMeasurementDoesNotPublishAnOptimisticLine()
    {
        using var cancellation = new CancellationTokenSource();
        var cache = new Measurements(_ => { cancellation.Cancel(); return new(1, 1, 0); });
        Assert.Throws<OperationCanceledException>(() => LazyKnuthPlassLineBreaker.Break(Items(), 40, cache, cancellationToken: cancellation.Token));
    }
    [TestMethod]
    public void WidthAndAggregateOverflowAreRejected()
    {
        var cache = new Measurements(_ => new(1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LazyKnuthPlassLineBreaker.Break(Items(), 0, cache));
        Assert.Throws<OverflowException>(() => LazyKnuthPlassLineBreaker.Break([LineBreakItem.Box(double.MaxValue), LineBreakItem.Box(double.MaxValue)], 40, cache));
    }
    [TestMethod]
    public void EmptyParagraphMatchesEagerOracle() => Same(KnuthPlassLineBreaker.Break([], 40),
        LazyKnuthPlassLineBreaker.Break([], 40, new Measurements(_ => null)));
}
