using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using MDEditor.Typesetting.LineBreaking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class KnuthPlassTests
{
    private static LineBreakItem B(double w) => LineBreakItem.Box(w);
    private static LineBreakItem G(double w, double s = 0, double h = 0) => LineBreakItem.Glue(w, s, h);
    private static LineBreakItem P(double w, int p, bool f = false) => LineBreakItem.Penalty(w, p, f);
    private static LineBreakOptions Justified(double maximumStretchRatio = 3) =>
        new(maximumStretchRatio: maximumStretchRatio, lastLineAlignment: LastLineAlignment.Justified);
    private static LineBreakItem[] Pair(double glueWidth = 2, double stretch = 5, double shrink = 2) =>
        [B(10), P(0, 10000), G(glueWidth, stretch, shrink), B(10)];
    private static int[] Breaks(LineBreakResult result) => result.Lines.Select(line => line.BreakItemIndex).ToArray();

    [TestMethod]
    public void Empty_paragraph_has_zero_lines_and_zero_cost()
    {
        var result = KnuthPlassLineBreaker.Break(Array.Empty<LineBreakItem>(), 10);
        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Items.IsEmpty);
        Assert.IsTrue(result.Lines.IsEmpty);
        Assert.AreEqual(0d, result.TotalDemerits);
    }

    [TestMethod]
    public void Short_last_line_keeps_natural_width_without_stretch()
    {
        var result = KnuthPlassLineBreaker.Break(new[] { B(13) }, 100);
        var line = result.Lines.Single();
        Assert.AreEqual(13d, line.NaturalWidth);
        Assert.AreEqual(13d, line.ActualWidth);
        Assert.AreEqual(0d, line.AdjustmentRatio);
        Assert.AreEqual(0d, line.Badness);
        Assert.AreEqual(LineFitness.Decent, line.Fitness);
        Assert.AreEqual(100d, result.TotalDemerits);
        Assert.IsTrue(line.IsParagraphEnd && line.IsForced);
        Assert.AreEqual(1, line.BreakItemIndex); // Virtual endpoint, not an input glyph.
    }

    [TestMethod]
    public void Overwide_object_returns_explicit_failure_not_greedy_overflow()
    {
        var result = KnuthPlassLineBreaker.Break(new[] { B(101) }, 100);
        Assert.AreEqual(LineBreakStatus.NoFeasibleBreaks, result.Status);
        Assert.IsFalse(result.IsSuccess);
        Assert.IsTrue(result.Lines.IsEmpty);
        Assert.IsNull(result.TotalDemerits);
        Assert.AreEqual(101d, result.Items[0].Width);
    }

    [TestMethod]
    public void Exact_width_requires_no_glue_even_when_final_line_is_justified()
    {
        var result = KnuthPlassLineBreaker.Break(new[] { B(20) }, 20, Justified());
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0d, result.Lines[0].AdjustmentRatio);
    }

    [TestMethod]
    public void Short_justified_line_without_stretch_has_no_solution()
    {
        Assert.IsFalse(KnuthPlassLineBreaker.Break(new[] { B(19) }, 20, Justified()).IsSuccess);
    }

    [TestMethod]
    public void Shrink_limit_is_inclusive_and_does_not_allow_negative_glue()
    {
        var result = KnuthPlassLineBreaker.Break(Pair(), 20);
        var line = result.Lines.Single();
        Assert.AreEqual(-1d, line.AdjustmentRatio);
        Assert.AreEqual(20d, line.ActualWidth);
        Assert.AreEqual(100d, line.Badness);
        Assert.AreEqual(LineFitness.Tight, line.Fitness);
        Assert.IsFalse(KnuthPlassLineBreaker.Break(Pair(), 19).IsSuccess);
    }

    [TestMethod]
    public void Ragged_last_line_can_shrink_when_it_would_otherwise_overflow()
    {
        var line = KnuthPlassLineBreaker.Break(Pair(), 21).Lines.Single();
        Assert.AreEqual(-0.5d, line.AdjustmentRatio);
        Assert.AreEqual(21d, line.ActualWidth);
        Assert.AreEqual(12.5d, line.Badness);
    }

    [TestMethod]
    public void Selected_glue_is_excluded_from_both_line_width_and_capacity()
    {
        var result = KnuthPlassLineBreaker.Break(new[] { B(10), G(999, 999, 999), B(5) }, 10);
        CollectionAssert.AreEqual(new[] { 1, 3 }, Breaks(result));
        var first = result.Lines[0];
        Assert.AreEqual(10d, first.NaturalWidth);
        Assert.AreEqual(0d, first.Stretch);
        Assert.AreEqual(0d, first.Shrink);
        Assert.AreEqual(1, first.EndItemIndex);
        Assert.AreEqual(2, first.NextItemIndex);
    }

    [TestMethod]
    public void After_break_discardable_glue_and_optional_penalties_are_not_counted_twice()
    {
        var result = KnuthPlassLineBreaker.Break(new[] { B(10), G(2, 2, 1), G(300, 300, 300), P(1, 200), B(10) }, 10);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(4, result.Lines[0].NextItemIndex);
        Assert.AreEqual(4, result.Lines[1].StartItemIndex);
        Assert.AreEqual(10d, result.Lines[1].NaturalWidth);
    }

    [TestMethod]
    [DataRow(10000)]
    [DataRow(int.MaxValue)]
    public void Forbidden_penalty_cannot_be_selected(int penalty)
    {
        Assert.IsFalse(KnuthPlassLineBreaker.Break(new[] { B(10), P(0, penalty), B(10) }, 10).IsSuccess);
    }

    [TestMethod]
    public void Forbidden_penalty_also_prevents_break_at_immediately_following_glue()
    {
        Assert.IsFalse(KnuthPlassLineBreaker.Break(new[] { B(10), P(0, 10000), G(0), B(10) }, 10).IsSuccess);
    }

    [TestMethod]
    [DataRow(-10000)]
    [DataRow(int.MinValue)]
    public void Forced_penalty_is_selected_and_not_squared(int penalty)
    {
        var result = KnuthPlassLineBreaker.Break(new[] { B(10), P(0, penalty), B(10) }, 10);
        CollectionAssert.AreEqual(new[] { 1, 3 }, Breaks(result));
        Assert.IsTrue(result.Lines[0].IsForced);
        Assert.AreEqual(200d, result.TotalDemerits);
    }

    [TestMethod]
    public void Infeasible_forced_break_does_not_allow_jumping_across_it()
    {
        Assert.IsFalse(KnuthPlassLineBreaker.Break(new[] { B(11), P(0, -10000), B(1) }, 10).IsSuccess);
        // A short hard line is not automatically ragged; finite glue must justify non-final lines.
        Assert.IsFalse(KnuthPlassLineBreaker.Break(new[] { B(9), P(0, -10000), B(1) }, 10).IsSuccess);
    }

    [TestMethod]
    public void Discarding_after_glue_break_never_discards_a_forced_penalty()
    {
        var result = KnuthPlassLineBreaker.Break(new[] { B(10), G(0), P(0, -10000), B(10) }, 10);
        CollectionAssert.AreEqual(new[] { 2, 4 }, Breaks(result));
    }

    [TestMethod]
    public void Multiple_forced_breaks_are_all_present_in_the_path()
    {
        var result = KnuthPlassLineBreaker.Break(new[] { B(10), P(0, -10000), B(10), P(0, -10000), B(10) }, 10);
        CollectionAssert.AreEqual(new[] { 1, 3, 5 }, Breaks(result));
    }

    [TestMethod]
    public void Explicit_terminal_break_and_trailing_discardables_do_not_add_a_phantom_line()
    {
        var result = KnuthPlassLineBreaker.Break(new[] { B(10), P(0, -10000), G(900), P(9, 10000) }, 100);
        Assert.AreEqual(1, result.Lines.Length);
        Assert.AreEqual(10d, result.Lines[0].NaturalWidth);
        Assert.AreEqual(4, result.Lines[0].NextItemIndex);
        Assert.AreEqual(1, result.Lines[0].EndItemIndex);
    }

    [TestMethod]
    public void Implicit_terminal_break_ignores_negative_tail_penalty_and_tail_glue()
    {
        foreach (var tail in new[] { P(3, -9999), G(900), P(900, 10000) })
        {
            var result = KnuthPlassLineBreaker.Break(new[] { B(10), tail }, 10);
            Assert.AreEqual(1, result.Lines.Length);
            Assert.AreEqual(100d, result.TotalDemerits);
            Assert.AreEqual(10d, result.Lines[0].NaturalWidth);
        }
    }

    [TestMethod]
    public void Terminal_forced_penalty_has_conditional_break_width()
    {
        var result = KnuthPlassLineBreaker.Break(new[] { B(8), P(2, -10000) }, 10, Justified());
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(10d, result.Lines.Single().NaturalWidth);
        Assert.AreEqual(2d, result.Lines[0].BreakWidth);
    }

    [TestMethod]
    public void Unselected_penalty_width_is_not_in_the_natural_line_width()
    {
        var result = KnuthPlassLineBreaker.Break(new[] { B(5), P(999, 10000), B(5) }, 10, Justified());
        Assert.AreEqual(10d, result.Lines.Single().NaturalWidth);
    }

    [TestMethod]
    public void Selected_penalty_width_is_appended_to_the_preceding_line()
    {
        var result = KnuthPlassLineBreaker.Break(new[] { B(8), P(2, 0), B(5) }, 10);
        CollectionAssert.AreEqual(new[] { 1, 3 }, Breaks(result));
        Assert.AreEqual(10d, result.Lines[0].NaturalWidth);
        Assert.AreEqual(5d, result.Lines[1].NaturalWidth);
    }

    [TestMethod]
    [DataRow(50, 2700d)]
    [DataRow(-50, -2300d)]
    public void Ordinary_penalty_adds_or_subtracts_its_square(int penalty, double expected)
    {
        var result = KnuthPlassLineBreaker.Break(new[] { B(10), P(0, penalty), B(10) }, 10);
        Assert.AreEqual(expected, result.TotalDemerits);
    }

    [TestMethod]
    public void Consecutive_and_penultimate_flagged_breaks_have_distinct_costs()
    {
        var options = new LineBreakOptions(adjacentFitnessDemerits: 0, consecutiveFlaggedDemerits: 700, finalFlaggedDemerits: 300);
        var result = KnuthPlassLineBreaker.Break(new[] { B(10), P(0, 0, true), B(10), P(0, 0, true), B(10) }, 10, options);
        Assert.AreEqual(1300d, result.TotalDemerits);
        Assert.AreEqual(100d, result.Lines[0].Demerits);
        Assert.AreEqual(800d, result.Lines[1].Demerits);
        Assert.AreEqual(400d, result.Lines[2].Demerits);
    }

    [TestMethod]
    public void Nonconsecutive_flagged_breaks_do_not_pay_the_consecutive_cost()
    {
        var options = new LineBreakOptions(adjacentFitnessDemerits: 0, consecutiveFlaggedDemerits: 700, finalFlaggedDemerits: 300);
        var result = KnuthPlassLineBreaker.Break(new[] { B(10), P(0, 0, true), B(10), P(0, 0), B(10), P(0, 0, true), B(10) }, 10, options);
        Assert.AreEqual(700d, result.TotalDemerits);
    }

    [TestMethod]
    public void First_line_is_compared_to_the_implicit_decent_start_state()
    {
        var line = KnuthPlassLineBreaker.Break(Pair(2, 4, 2), 30, Justified()).Lines.Single();
        Assert.AreEqual(LineFitness.VeryLoose, line.Fitness);
        Assert.AreEqual(800d, line.Badness);
        Assert.AreEqual(666100d, line.Demerits);
    }

    [TestMethod]
    [DataRow(-1d, LineFitness.Tight)]
    [DataRow(-0.51d, LineFitness.Tight)]
    [DataRow(-0.5d, LineFitness.Decent)]
    [DataRow(0d, LineFitness.Decent)]
    [DataRow(0.5d, LineFitness.Decent)]
    [DataRow(0.51d, LineFitness.Loose)]
    [DataRow(1d, LineFitness.Loose)]
    [DataRow(1.01d, LineFitness.VeryLoose)]
    [DataRow(3d, LineFitness.VeryLoose)]
    public void Fitness_and_badness_follow_the_continuous_ratio_model(double ratio, LineFitness fitness)
    {
        var items = new[] { B(5), P(0, 10000), G(10, 10, 10), B(5) };
        var line = KnuthPlassLineBreaker.Break(items, 20 + 10 * ratio, Justified()).Lines.Single();
        Assert.AreEqual(ratio, line.AdjustmentRatio, 1e-12);
        Assert.AreEqual(fitness, line.Fitness);
        Assert.AreEqual(100 * Math.Pow(Math.Abs(ratio), 3), line.Badness, 1e-9);
        Assert.AreEqual(line.TargetWidth, line.ActualWidth, 1e-9);
    }

    [TestMethod]
    public void Badness_is_capped_before_huge_ratio_arithmetic()
    {
        var result = KnuthPlassLineBreaker.Break(Pair(2, 1, 2), 1e300, Justified(1e300));
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(10000d, result.Lines.Single().Badness);
        Assert.IsTrue(double.IsFinite(result.TotalDemerits!.Value));
    }

    [TestMethod]
    public void Zero_maximum_stretch_allows_only_exact_or_shrinking_lines()
    {
        Assert.IsTrue(KnuthPlassLineBreaker.Break(Pair(), 22, Justified(0)).IsSuccess);
        Assert.IsFalse(KnuthPlassLineBreaker.Break(Pair(), 23, Justified(0)).IsSuccess);
        Assert.IsTrue(KnuthPlassLineBreaker.Break(Pair(), 21, Justified(0)).IsSuccess);
    }

    [TestMethod]
    public void Width_profile_repeats_last_width_and_keeps_line_number_semantics()
    {
        var profile = new LineWidthProfile(new[] { 10d, 20d });
        var result = KnuthPlassLineBreaker.Break(new[] { B(10), P(0, -10000), B(20), P(0, -10000), B(20) }, profile);
        CollectionAssert.AreEqual(new[] { 10d, 20d, 20d }, result.Lines.Select(line => line.TargetWidth).ToArray());
        Assert.AreEqual(20d, profile.GetWidth(int.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => profile.GetWidth(-1));
    }

    [TestMethod]
    public void Paragraph_optimization_can_beat_the_furthest_feasible_greedy_break()
    {
        double[] wordWidths = [10, 9, 8, 7, 10, 5, 8];
        var items = new List<LineBreakItem>();
        foreach (var width in wordWidths)
        {
            if (items.Count != 0) items.Add(G(3, 4, 2));
            items.Add(B(width));
        }
        var result = KnuthPlassLineBreaker.Break(items, 33);
        CollectionAssert.AreEqual(new[] { 5, 11, 13 }, Breaks(result));
        Assert.AreEqual(1384.3276977539062, result.TotalDemerits!.Value, 1e-9);
        Assert.IsTrue(result.TotalDemerits < 12200); // Greedy feasible break path: [5, 13].
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Caller_arrays_cannot_modify_the_result_or_width_profile(bool marshalArrays)
    {
        var array = new[] { B(10), P(0, -10000), B(20) };
        var widthArray = new[] { 10d, 20d };
        IEnumerable<LineBreakItem> input = marshalArrays ? ImmutableCollectionsMarshal.AsImmutableArray(array) : array;
        IEnumerable<double> widthInput = marshalArrays ? ImmutableCollectionsMarshal.AsImmutableArray(widthArray) : widthArray;
        var profile = new LineWidthProfile(widthInput);
        var result = KnuthPlassLineBreaker.Break(input, profile);
        var json = JsonSerializer.Serialize(result);
        Array.Fill(array, default);
        Array.Fill(widthArray, double.NaN);
        Assert.AreEqual(json, JsonSerializer.Serialize(result));
        Assert.AreEqual(10d, profile.GetWidth(0));
        Assert.AreEqual(20d, profile.GetWidth(1));
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    [DataRow("tr-TR")]
    [DataRow("ar-SA")]
    public void Output_and_cost_are_deterministic_across_cultures(string cultureName)
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var expected = JsonSerializer.Serialize(KnuthPlassLineBreaker.Break(Pair(), 23.125, Justified()));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            for (var i = 0; i < 10; i++)
                Assert.AreEqual(expected, JsonSerializer.Serialize(KnuthPlassLineBreaker.Break(Pair(), 23.125, Justified())));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [TestMethod]
    public void Failure_is_serializable_with_null_cost_not_infinity()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(KnuthPlassLineBreaker.Break(new[] { B(2) }, 1)));
        Assert.AreEqual(JsonValueKind.Null, json.RootElement.GetProperty("TotalDemerits").ValueKind);
    }

    [TestMethod]
    [DataRow(-1d)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void Primitive_metrics_and_options_reject_invalid_numbers(double value)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => B(value));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => G(value));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => G(1, value));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => G(1, 1, value));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => P(value, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LineBreakOptions(maximumStretchRatio: value));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LineBreakOptions(linePenalty: value));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LineBreakOptions(adjacentFitnessDemerits: value));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LineBreakOptions(consecutiveFlaggedDemerits: value));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LineBreakOptions(finalFlaggedDemerits: value));
    }

    [TestMethod]
    [DataRow(0d)]
    [DataRow(-1d)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void Line_width_must_be_positive_and_finite(double width)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => KnuthPlassLineBreaker.Break(new[] { B(1) }, width));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LineWidthProfile(new[] { 1d, width }));
    }

    [TestMethod]
    public void Invalid_collection_and_alignment_inputs_are_rejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => KnuthPlassLineBreaker.Break(null!, 10));
        Assert.ThrowsExactly<ArgumentNullException>(() => KnuthPlassLineBreaker.Break(Array.Empty<LineBreakItem>(), (LineWidthProfile)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new LineWidthProfile(null!));
        Assert.ThrowsExactly<ArgumentException>(() => new LineWidthProfile(Array.Empty<double>()));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => G(1, 0, 2));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LineBreakOptions(lastLineAlignment: (LastLineAlignment)99));
    }

    [TestMethod]
    public void Finite_metric_or_score_overflow_is_not_misreported_as_no_solution()
    {
        Assert.ThrowsExactly<OverflowException>(() => KnuthPlassLineBreaker.Break(new[] { B(double.MaxValue), B(double.MaxValue) }, 1));
        Assert.ThrowsExactly<OverflowException>(() => KnuthPlassLineBreaker.Break(new[] { G(0, double.MaxValue), G(0, double.MaxValue) }, 1));
        Assert.ThrowsExactly<OverflowException>(() => KnuthPlassLineBreaker.Break(new[] { B(1) }, 1, new(linePenalty: double.MaxValue)));
    }

    [TestMethod]
    public void Tiny_capacity_does_not_create_infinite_ratio_or_score()
    {
        Assert.IsFalse(KnuthPlassLineBreaker.Break(Pair(0, double.Epsilon, 0), 25, Justified(double.MaxValue)).IsSuccess);
    }

    [TestMethod]
    public void New_line_metrics_do_not_lose_small_widths_by_subtracting_large_prefix_sums()
    {
        var result = KnuthPlassLineBreaker.Break(new[] { B(1e20), P(0, -10000), B(5) }, new LineWidthProfile(new[] { 1e20, 5d }), Justified());
        Assert.AreEqual(5d, result.Lines[1].NaturalWidth);
        Assert.AreEqual(200d, result.TotalDemerits);
    }

    [TestMethod]
    public void Precancelled_and_cancelled_enumeration_requests_abort_without_partial_results()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            KnuthPlassLineBreaker.Break(Array.Empty<LineBreakItem>(), 1, cancellationToken: cancel.Token));
        using var during = new CancellationTokenSource();
        IEnumerable<LineBreakItem> Enumerate()
        {
            yield return B(1);
            during.Cancel();
            yield return B(2);
        }
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            KnuthPlassLineBreaker.Break(Enumerate(), 1, cancellationToken: during.Token));
    }

    [TestMethod]
    public void Long_finite_paragraph_returns_complete_finite_path()
    {
        var items = new List<LineBreakItem>();
        for (var i = 0; i < 250; i++)
        {
            if (i != 0) items.Add(G(3, 4, 2));
            items.Add(B(10 + i % 3));
        }
        var result = KnuthPlassLineBreaker.Break(items, 70);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(items.Count, result.Lines[^1].NextItemIndex);
        Assert.IsTrue(result.Lines.Length > 30);
        Assert.IsTrue(double.IsFinite(result.TotalDemerits!.Value));
        foreach (var line in result.Lines)
        {
            Assert.IsTrue(line.AdjustmentRatio >= -1 && line.AdjustmentRatio <= 3);
            if (!line.IsParagraphEnd) Assert.AreEqual(70d, line.ActualWidth, 1e-9);
        }
    }
}
