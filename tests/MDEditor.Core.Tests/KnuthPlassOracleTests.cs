using MDEditor.Typesetting.LineBreaking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class KnuthPlassOracleTests
{
    [TestMethod]
    [DataRow(117)]
    [DataRow(7251)]
    [DataRow(2026)]
    [DataRow(1981)]
    [DataRow(17)]
    [DataRow(59)]
    [DataRow(31059)]
    [DataRow(98765)]
    public void Seeded_small_paragraphs_match_independent_exhaustive_paths(int seed)
    {
        var random = new Random(seed);
        var solved = 0;
        var failed = 0;
        for (var sample = 0; sample < 400; sample++)
        {
            var items = new List<LineBreakItem>();
            var count = random.Next(3, 8);
            for (var word = 0; word < count; word++)
            {
                items.Add(LineBreakItem.Box(random.Next(12, 65) / 4d));
                if (word == count - 1) break;
                var width = random.Next(0, 17) / 4d;
                var stretch = random.Next(0, 25) / 4d;
                var shrink = random.Next(0, (int)(width * 4) + 1) / 4d;
                var flag = random.Next(2) == 0;
                int[] penalties = [-90, -50, 0, 25, 80, 9999];
                switch (random.Next(6))
                {
                    case 0:
                        items.Add(LineBreakItem.Glue(width, stretch, shrink));
                        break;
                    case 1:
                        items.Add(LineBreakItem.Penalty(random.Next(0, 13) / 4d, penalties[random.Next(penalties.Length)], flag));
                        items.Add(LineBreakItem.Glue(width, stretch, shrink));
                        break;
                    case 2:
                        items.Add(LineBreakItem.Penalty(0, 10000, flag));
                        items.Add(LineBreakItem.Glue(width, stretch, shrink));
                        break;
                    case 3:
                        items.Add(LineBreakItem.Penalty(0, random.Next(5) == 0 ? -10000 : 0, flag));
                        items.Add(LineBreakItem.Glue(width, stretch, shrink));
                        break;
                    case 4:
                        items.Add(LineBreakItem.Penalty(0, -25, flag));
                        items.Add(LineBreakItem.Penalty(random.Next(3), 25, !flag));
                        items.Add(LineBreakItem.Glue(width, stretch, shrink));
                        break;
                    default:
                        items.Add(LineBreakItem.Glue(width, stretch, shrink));
                        items.Add(LineBreakItem.Glue(1, 1, 1));
                        items.Add(LineBreakItem.Penalty(0, 30, flag));
                        break;
                }
            }
            if (random.Next(4) == 0)
            {
                items.Add(LineBreakItem.Glue(random.Next(10), 1, 0));
                items.Add(LineBreakItem.Penalty(1, -50));
            }
            if (random.Next(5) == 0) items.Add(LineBreakItem.Penalty(random.Next(3), -10000, true));
            var widthArray = Enumerable.Range(0, random.Next(1, 5)).Select(_ => random.Next(16, 121) / 2d).ToArray();
            var options = new LineBreakOptions(maximumStretchRatio: new[] { 0d, 0.5, 1, 2, 3 }[random.Next(5)],
                linePenalty: random.Next(0, 31),
                adjacentFitnessDemerits: new[] { 0d, 100, 10000, 1000000 }[random.Next(4)],
                consecutiveFlaggedDemerits: new[] { 0d, 50, 500, 10000 }[random.Next(4)],
                finalFlaggedDemerits: new[] { 0d, 25, 250, 5000 }[random.Next(4)],
                lastLineAlignment: random.Next(2) == 0 ? LastLineAlignment.RaggedRight : LastLineAlignment.Justified);
            var result = Verify(items.ToArray(), widthArray, options, $"seed={seed}, sample={sample}");
            if (result.IsSuccess) solved++; else failed++;
        }
        Assert.IsTrue(solved > 0 && failed > 0, $"seed={seed} must exercise both feasible and infeasible paths.");
    }

    [TestMethod]
    public void All_five_word_combinations_and_eight_widths_match_exhaustive_optimum()
    {
        // 3^5 * 8 = 1944 independently enumerated paragraph problems.
        double[] widths = [16, 20, 24, 28, 32, 36, 40, 44];
        for (var code = 0; code < 243; code++)
        {
            var digits = code;
            var items = new List<LineBreakItem>();
            for (var word = 0; word < 5; word++)
            {
                if (word != 0) items.Add(LineBreakItem.Glue(3, 4, 2));
                items.Add(LineBreakItem.Box(4 + 4 * (digits % 3)));
                digits /= 3;
            }
            foreach (var width in widths)
                Verify(items.ToArray(), new[] { width }, new(), $"code={code}, width={width}");
        }
    }

    [TestMethod]
    public void Consecutive_controls_empty_content_and_terminal_policies_match_oracle()
    {
        var b = LineBreakItem.Box(10);
        var forced = LineBreakItem.Penalty(0, -10000);
        LineBreakItem[][] cases =
        [
            [],
            [LineBreakItem.Glue(0, 1, 0)],
            [LineBreakItem.Penalty(4, -50)],
            [forced],
            [forced, forced],
            [b, forced, forced],
            [b, LineBreakItem.Glue(0, 0, 0), forced, b],
            [b, LineBreakItem.Penalty(0, -9999), forced],
            [b, LineBreakItem.Penalty(0, 10000), LineBreakItem.Glue(0, 2, 0), b],
            [b, forced, LineBreakItem.Glue(900, 0, 0), LineBreakItem.Penalty(4, 10000)],
            [b, LineBreakItem.Glue(5, 2, 1), LineBreakItem.Penalty(3, -50)]
        ];
        foreach (var items in cases)
        foreach (var align in new[] { LastLineAlignment.RaggedRight, LastLineAlignment.Justified })
            Verify(items, new[] { 10d, 20d }, new(lastLineAlignment: align), $"controls: {align}");
    }

    [TestMethod]
    public void Extending_repeated_width_profile_does_not_change_optimal_cost()
    {
        var items = new List<LineBreakItem>();
        foreach (var word in new[] { 10d, 9, 8, 7, 10, 5, 8 })
        {
            if (items.Count != 0) items.Add(LineBreakItem.Glue(3, 4, 2));
            items.Add(LineBreakItem.Box(word));
        }
        var shortProfile = Verify(items.ToArray(), new[] { 33d, 25d }, new(), "short profile");
        var longProfile = Verify(items.ToArray(), new[] { 33d, 25d, 25d, 25d }, new(), "extended profile");
        Assert.AreEqual(shortProfile.Status, longProfile.Status);
        Assert.AreEqual(shortProfile.TotalDemerits, longProfile.TotalDemerits);
    }

    private static LineBreakResult Verify(LineBreakItem[] items, double[] widths, LineBreakOptions options, string context)
    {
        var exhaustive = IndependentLineBreakOracle.Enumerate(items, widths, options);
        var actual = KnuthPlassLineBreaker.Break(items, new LineWidthProfile(widths), options);
        Assert.AreEqual(exhaustive.Count != 0, actual.IsSuccess, context);
        Assert.AreEqual(items.Length, actual.Items.Length, context);
        if (exhaustive.Count == 0)
        {
            Assert.IsTrue(actual.Lines.IsEmpty, context);
            Assert.IsNull(actual.TotalDemerits, context);
            return actual;
        }
        var minimum = exhaustive.Min(path => path.Cost);
        var tolerance = 1e-6 + Math.Abs(minimum) * 2e-14;
        Assert.AreEqual(minimum, actual.TotalDemerits!.Value, tolerance, context);
        var selected = exhaustive.FirstOrDefault(path => path.Lines.Select(line => line.Break)
            .SequenceEqual(actual.Lines.Select(line => line.BreakItemIndex)));
        Assert.IsNotNull(selected, $"{context}: production path must be independently legal.");
        Assert.AreEqual(minimum, selected.Cost, tolerance, $"{context}: selected path must be a global optimum.");
        for (var i = 0; i < actual.Lines.Length; i++)
        {
            var e = selected.Lines[i];
            var a = actual.Lines[i];
            Assert.AreEqual(e.Start, a.StartItemIndex, context);
            Assert.AreEqual(e.End, a.EndItemIndex, context);
            Assert.AreEqual(e.Next, a.NextItemIndex, context);
            Assert.AreEqual(e.Target, a.TargetWidth, context);
            Assert.AreEqual(e.Natural, a.NaturalWidth, 1e-9, context);
            Assert.AreEqual(e.Stretch, a.Stretch, 1e-9, context);
            Assert.AreEqual(e.Shrink, a.Shrink, 1e-9, context);
            Assert.AreEqual(e.BreakWidth, a.BreakWidth, 1e-9, context);
            Assert.AreEqual(e.Ratio, a.AdjustmentRatio, 1e-9, context);
            Assert.AreEqual(e.Actual, a.ActualWidth, 1e-9, context);
            Assert.AreEqual(e.Badness, a.Badness, 1e-8, context);
            Assert.AreEqual(e.Fitness, (int)a.Fitness, context);
            Assert.AreEqual(e.Penalty, a.PenaltyValue, context);
            Assert.AreEqual(e.Flagged, a.Flagged, context);
            Assert.AreEqual(e.Forced, a.IsForced, context);
            Assert.AreEqual(e.Terminal, a.IsParagraphEnd, context);
            Assert.AreEqual(e.Score, a.Demerits, 1e-6 + Math.Abs(e.Score) * 2e-14, context);
        }
        return actual;
    }
}

/// <summary>
/// TEST ONLY: enumerate every legal path without merging/pruning. Direct interval summation,
/// separately implemented endpoint/ratio/scoring rules; never call production measurement helpers.
/// </summary>
internal static class IndependentLineBreakOracle
{
    internal sealed record OracleLine(int Start, int End, int Break, int Next, double Target,
        double Natural, double Stretch, double Shrink, double BreakWidth, double Ratio, double Actual,
        double Badness, int Fitness, int Penalty, bool Flagged, bool Forced, bool Terminal, double Score);
    internal sealed record OraclePath(OracleLine[] Lines, double Cost);

    public static List<OraclePath> Enumerate(LineBreakItem[] items, double[] widths, LineBreakOptions options,
        Func<LineMeasureRequest, LineMeasurement?>? measurement = null)
    {
        var answers = new List<OraclePath>();
        var path = new List<OracleLine>();
        if (items.Length == 0) { answers.Add(new([], 0)); return answers; }
        Search(0, 0, 1, false, 0);
        return answers;

        void Search(int start, int lineNumber, int previousFitness, bool previousFlagged, double cost)
        {
            for (var endpoint = start; endpoint <= items.Length; endpoint++)
            {
                var virtualEnd = endpoint == items.Length;
                var penaltyNode = !virtualEnd && items[endpoint].Kind == LineBreakItemKind.Penalty;
                var forced = virtualEnd || penaltyNode && items[endpoint].PenaltyValue <= -10000;
                var eligible = virtualEnd ||
                    penaltyNode && items[endpoint].PenaltyValue < 10000 ||
                    !virtualEnd && items[endpoint].Kind == LineBreakItemKind.Glue && endpoint > 0 &&
                    items[endpoint - 1].Kind == LineBreakItemKind.Box;
                if (!eligible) continue;
                var next = virtualEnd ? items.Length : endpoint + 1;
                while (next < items.Length && items[next].Kind != LineBreakItemKind.Box &&
                    !(items[next].Kind == LineBreakItemKind.Penalty && items[next].PenaltyValue <= -10000)) next++;
                if (!forced && next == items.Length) continue; // A soft break cannot introduce a phantom final line.
                var terminal = virtualEnd || forced && next == items.Length;
                var end = endpoint;
                if (terminal) while (end > start && items[end - 1].Kind != LineBreakItemKind.Box) end--;
                double width = 0, stretch = 0, shrink = 0;
                for (var i = start; i < end; i++)
                {
                    if (items[i].Kind == LineBreakItemKind.Penalty) continue;
                    width += items[i].Width;
                    if (items[i].Kind == LineBreakItemKind.Glue)
                    {
                        stretch += items[i].Stretch;
                        shrink += items[i].Shrink;
                    }
                }
                var breakWidth = penaltyNode ? items[endpoint].Width : 0;
                var natural = width + breakWidth;
                if (measurement is not null)
                {
                    var measured = measurement(new(start, end, endpoint, breakWidth, terminal));
                    if (measured is null) { if (forced) return; continue; }
                    natural = measured.Value.NaturalWidth; stretch = measured.Value.Stretch; shrink = measured.Value.Shrink;
                }
                var target = widths[Math.Min(lineNumber, widths.Length - 1)];
                var difference = target - natural;
                double ratio = 0;
                var feasible = true;
                if (!(terminal && options.LastLineAlignment == LastLineAlignment.RaggedRight && difference >= 0) && difference != 0)
                {
                    if (difference > 0)
                    {
                        if (stretch == 0) feasible = false;
                        else
                        {
                            ratio = difference / stretch;
                            feasible &= target <= natural + options.MaximumStretchRatio * stretch;
                            if (double.IsFinite(ratio)) ratio = Math.Min(ratio, options.MaximumStretchRatio);
                        }
                    }
                    else
                    {
                        if (shrink == 0) feasible = false;
                        else
                        {
                            ratio = difference / shrink;
                            feasible &= target >= natural - shrink;
                            if (double.IsFinite(ratio)) ratio = Math.Max(ratio, -1);
                        }
                    }
                }
                feasible &= double.IsFinite(ratio) && ratio >= -1 && ratio <= options.MaximumStretchRatio;
                if (feasible)
                {
                    var fitness = 1;
                    if (ratio < -0.5) fitness = 0;
                    else if (ratio > 1) fitness = 3;
                    else if (ratio > 0.5) fitness = 2;
                    var badness = Math.Min(10000, 100 * Math.Pow(Math.Abs(ratio), 3));
                    var score = Math.Pow(options.LinePenalty + badness, 2);
                    var penalty = virtualEnd ? -10000 : penaltyNode ? items[endpoint].PenaltyValue : 0;
                    var flagged = penaltyNode && items[endpoint].Flagged;
                    if (penalty > 0) score += Math.Pow((double)penalty, 2);
                    if (penalty < 0 && penalty > -10000) score -= Math.Pow((double)penalty, 2);
                    if (Math.Abs(previousFitness - fitness) > 1) score += options.AdjacentFitnessDemerits;
                    if (previousFlagged && terminal) score += options.FinalFlaggedDemerits;
                    if (previousFlagged && flagged && !terminal) score += options.ConsecutiveFlaggedDemerits;
                    var actual = natural + ratio * (ratio < 0 ? shrink : stretch);
                    path.Add(new(start, end, endpoint, next, target, natural, stretch, shrink, breakWidth,
                        ratio, actual, badness, fitness, penalty, flagged, forced, terminal, score));
                    if (terminal) answers.Add(new(path.ToArray(), cost + score));
                    else Search(next, lineNumber + 1, fitness, flagged, cost + score);
                    path.RemoveAt(path.Count - 1);
                }
                if (forced) return; // Even if infeasible, a mandatory boundary cannot be skipped.
            }
        }
    }
}
