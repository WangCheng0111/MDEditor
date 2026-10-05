using System.Globalization;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;
using MDEditor.Typesetting.LineBreaking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class ParagraphLayoutTests
{
    [TestMethod]
    [DataRow(7)]
    [DataRow(1981)]
    [DataRow(31059)]
    public void Contextual_measurements_match_independent_exhaustive_optimum(int seed)
    {
        var random = new Random(seed);
        for (var sample = 0; sample < 400; sample++)
        {
            var items = new List<LineBreakItem>();
            for (var w = 0; w < random.Next(3, 8); w++)
            {
                if (w > 0) items.Add(LineBreakItem.Glue(3, 5, 2));
                items.Add(LineBreakItem.Box(random.Next(4, 15)));
            }
            var array = items.ToArray();
            var widths = new[] { (double)random.Next(16, 48), random.Next(16, 48) };
            var options = new LineBreakOptions(lastLineAlignment: sample % 2 == 0 ? LastLineAlignment.RaggedRight : LastLineAlignment.Justified);
            LineMeasurement? Measure(LineMeasureRequest r)
            {
                if (r.StartItemIndex != 0 && (r.StartItemIndex * 3 + r.EndItemIndex + seed) % 11 == 0) return null;
                var content = array.Skip(r.StartItemIndex).Take(r.EndItemIndex - r.StartItemIndex).ToArray();
                var width = content.Sum(i => i.Kind == LineBreakItemKind.Penalty ? 0 : i.Width);
                return new(Math.Max(0, width + ((r.StartItemIndex + r.EndItemIndex) % 5 - 2) * 0.25),
                    content.Sum(i => i.Stretch) * 0.875, content.Sum(i => i.Shrink) * 0.75);
            }
            var oracle = IndependentLineBreakOracle.Enumerate(array, widths, options, Measure);
            var result = KnuthPlassLineBreaker.Break(array, new LineWidthProfile(widths), options, lineMeasurer: Measure);
            Assert.AreEqual(oracle.Count > 0, result.IsSuccess);
            if (oracle.Count == 0) continue;
            var best = oracle.Min(p => p.Cost);
            Assert.AreEqual(best, result.TotalDemerits!.Value, 1e-6 + Math.Abs(best) * 2e-14);
            var path = oracle.Single(p => p.Lines.Select(l => l.Break).SequenceEqual(result.Lines.Select(l => l.BreakItemIndex)));
            Assert.AreEqual(best, path.Cost, 1e-6 + Math.Abs(best) * 2e-14);
            for (var i = 0; i < result.Lines.Length; i++)
            {
                Assert.AreEqual(path.Lines[i].Natural, result.Lines[i].NaturalWidth);
                Assert.AreEqual(path.Lines[i].Ratio, result.Lines[i].AdjustmentRatio);
            }
        }
    }

    [TestMethod]
    public void Context_callback_can_cancel_and_reject_forced_edges()
    {
        var items = new[] { LineBreakItem.Box(10), LineBreakItem.Penalty(0, -10000), LineBreakItem.Box(10) };
        var result = KnuthPlassLineBreaker.Break(items, 10, lineMeasurer: _ => null);
        Assert.IsFalse(result.IsSuccess);
        using var cancel = new CancellationTokenSource();
        Assert.ThrowsExactly<OperationCanceledException>(() => KnuthPlassLineBreaker.Break(items, 10,
            cancellationToken: cancel.Token, lineMeasurer: _ => { cancel.Cancel(); return new(10, 0, 0); }));
    }

    [TestMethod]
    public void Conditional_break_width_is_passed_to_contextual_measurement()
    {
        var items = new[] { LineBreakItem.Box(9), LineBreakItem.Penalty(1, -10000) };
        LineMeasureRequest? captured = null;
        var result = KnuthPlassLineBreaker.Break(items, 10, lineMeasurer: r => { captured = r; return new(9 + r.BreakWidth, 0, 0); });
        Assert.IsTrue(result.IsSuccess); Assert.AreEqual(1d, captured!.Value.BreakWidth);
        Assert.AreEqual(10d, result.Lines[0].NaturalWidth);
    }

    [TestMethod]
    [DataRow(double.NaN, 0d, 0d)]
    [DataRow(-1d, 0d, 0d)]
    [DataRow(1d, double.PositiveInfinity, 0d)]
    [DataRow(1d, 0d, 2d)]
    public void Invalid_context_measurements_are_rejected(double width, double stretch, double shrink) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LineMeasurement(width, stretch, shrink));

    [TestMethod]
    [DataRow("ffi")]
    [DataRow("a\u0308")]
    [DataRow("\U0001F600")]
    [DataRow("\U0001F469\u200D\U0001F4BB")]
    [DataRow("\U00020000")]
    public void Measured_clusters_are_not_split_into_characters(string text)
    {
        var source = new SourceTextSnapshot("prefix" + text, 2);
        var range = new SourceRange(6, text.Length);
        var map = ParagraphItemMap.Create(source, range, new[] { new MeasuredTextCluster(range, 17.125, true) }, 22);
        Assert.AreEqual(1, map.Items.Length); Assert.AreEqual(17.125, map.Items[0].Width);
        Assert.AreEqual(range, map.GetSource(new(0, 1, 1, 0, true)));
        Assert.AreEqual(range.End, map.Boundary(1));
    }

    [TestMethod]
    [DataRow("a\u0308")]
    [DataRow("\U0001F600")]
    [DataRow("\U0001F469\u200D\U0001F4BB")]
    public void Splitting_a_grapheme_is_rejected(string text)
    {
        var source = new SourceTextSnapshot(text, 0);
        Assert.ThrowsExactly<ArgumentException>(() => ParagraphItemMap.Create(source, source.FullRange,
            new[] { new MeasuredTextCluster(new(0, 1), 4, false), new MeasuredTextCluster(new(1, text.Length - 1), 4, true) }, 22));
    }

    [TestMethod]
    public void Source_mapping_excludes_the_break_space_and_preserves_internal_spaces()
    {
        var source = new SourceTextSnapshot("A B C", 0);
        var clusters = Enumerable.Range(0, 5).Select(i => new MeasuredTextCluster(new(i, 1), i % 2 == 0 ? 10 : 4, i % 2 == 1));
        var map = ParagraphItemMap.Create(source, source.FullRange, clusters, 22);
        Assert.AreEqual(new SourceRange(0, 3), map.GetSource(new(0, 3, 3, 0, false)));
        Assert.AreEqual(new SourceRange(4, 1), map.GetSource(new(4, 5, 5, 0, true)));
    }

    [TestMethod]
    public void Forbidden_boundaries_and_zero_width_cjk_glue_keep_measurement()
    {
        var source = new SourceTextSnapshot("中文文", 0);
        var map = ParagraphItemMap.Create(source, source.FullRange,
            new[] { new MeasuredTextCluster(new(0, 1), 20, false), new MeasuredTextCluster(new(1, 1), 20, true),
                new MeasuredTextCluster(new(2, 1), 20, true) }, 20);
        Assert.AreEqual(6, map.Items.Length);
        Assert.IsTrue(map.Items[1].IsForbidden); Assert.AreEqual(0d, map.Items[2].Width);
        Assert.AreEqual(1.6, map.Items[2].Stretch);
        Assert.AreEqual("中文", source.GetText(map.GetSource(new(0, 4, 4, 0, false))));
    }

    [TestMethod]
    public void Maps_copy_inputs_and_reject_incomplete_or_overlap_coverage()
    {
        var source = new SourceTextSnapshot("AB", 0);
        var input = new[] { new MeasuredTextCluster(new(0, 2), 10, true) };
        var map = ParagraphItemMap.Create(source, source.FullRange, input, 20);
        input[0] = new(new(0, 1), 999, false);
        Assert.AreEqual(10d, map.Clusters[0].Advance);
        Assert.ThrowsExactly<ArgumentException>(() => ParagraphItemMap.Create(source, source.FullRange, input, 20));
        Assert.ThrowsExactly<ArgumentException>(() => ParagraphItemMap.Create(source, source.FullRange,
            new[] { new MeasuredTextCluster(new(0, 2), 10, true), new MeasuredTextCluster(new(1, 1), 5, true) }, 20));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => map.Boundary(2));
    }

    [TestMethod]
    [DataRow(0.123456789)]
    [DataRow(0.5)]
    [DataRow(1.0)]
    [DataRow(2.75)]
    [DataRow(-0.25)]
    [DataRow(-1.0)]
    public void Actual_float_glyph_advances_reach_target_and_do_not_mutate_input(double ratio)
    {
        var (source, line) = MakeLine(["ffi", " ", "a\u0308", " ", "汉", "字"], [18.25, 5.125, 12.75, 5.125, 22, 22]);
        var original = line.Runs[0].Glyphs.ToArray();
        var plan = GlyphSpacingPlan.Create(source, line, 22);
        var target = plan.NaturalWidth + ratio * (ratio >= 0 ? plan.Stretch : plan.Shrink);
        var result = plan.Apply(ratio, target, false);
        Assert.IsTrue(Math.Abs(target - result.Line.Runs.Sum(r => r.Glyphs.Sum(g => g.Advance))) <= 0.01);
        CollectionAssert.AreEqual(original, line.Runs[0].Glyphs.ToArray());
        Assert.AreEqual(3, plan.OpportunityCount);
        for (var i = 0; i < original.Length; i++)
        {
            Assert.AreEqual(original[i].Index, result.Line.Runs[0].Glyphs[i].Index);
            Assert.AreEqual(original[i].AdvanceOffset, result.Line.Runs[0].Glyphs[i].AdvanceOffset);
        }
        Assert.AreEqual(original[0].Advance, result.Line.Runs[0].Glyphs[0].Advance); // ffi remains one cluster
        Assert.AreEqual(original[2].Advance, result.Line.Runs[0].Glyphs[2].Advance); // combining cluster unchanged
    }

    [TestMethod]
    public void Ragged_final_line_keeps_natural_spacing_and_source()
    {
        var (source, line) = MakeLine(["A", " ", "B"], [10, 4, 10]);
        var plan = GlyphSpacingPlan.Create(source, line, 22);
        var result = plan.Apply(0, 100, true);
        Assert.AreEqual(24d, result.Line.Advance); Assert.AreEqual(100d, result.Line.Bounds.Width);
        Assert.IsTrue(result.Spacing.All(s => s.Delta == 0));
    }

    [TestMethod]
    [DataRow("ffi")]
    [DataRow("a\u0308")]
    [DataRow("\U0001F600")]
    public void No_opportunities_means_no_fake_justification(string text)
    {
        var (source, line) = MakeLine([text], [20]);
        var plan = GlyphSpacingPlan.Create(source, line, 22);
        Assert.AreEqual(0, plan.OpportunityCount);
        Assert.ThrowsExactly<ArgumentException>(() => plan.Apply(1, 100, false));
        Assert.AreEqual(20d, plan.Apply(0, 20, false).Line.Advance);
    }

    [TestMethod]
    public void Leading_and_trailing_whitespace_is_not_justified()
    {
        var (source, line) = MakeLine([" ", " ", "A", " ", "B", " ", " "], [3, 3, 10, 3, 10, 3, 3]);
        var plan = GlyphSpacingPlan.Create(source, line, 22);
        Assert.AreEqual(1, plan.OpportunityCount);
        var result = plan.Apply(1, plan.NaturalWidth + plan.Stretch, false);
        Assert.AreEqual(new SourceRange(3, 1), result.Spacing[0].Source);
    }

    [TestMethod]
    public void Multiglyph_space_cluster_shrinks_without_negative_advances()
    {
        var source = new SourceTextSnapshot("A  B", 0);
        var glyphs = new[] { new GlyphPlacement(1, 10), new GlyphPlacement(2, 2), new GlyphPlacement(2, 4), new GlyphPlacement(3, 10) };
        var run = new GlyphRunLayout(0, source.FullRange, new(0, 20), 22, 0, "en-US", glyphs,
            new[] { new GlyphClusterLayout(new(0, 1), 0, 1, 10), new GlyphClusterLayout(new(1, 2), 1, 2, 6), new GlyphClusterLayout(new(3, 1), 3, 1, 10) });
        var line = new LineLayout(source.FullRange, new(0, 0, 26, 30), 20, 26, new[] { run });
        var plan = GlyphSpacingPlan.Create(source, line, 22);
        var result = plan.Apply(-1, 24, false);
        Assert.IsTrue(result.Line.Runs[0].Glyphs.All(g => g.Advance >= 0));
        Assert.AreEqual(4d, result.Line.Runs[0].Clusters[1].Advance, 0.00001);
    }

    [TestMethod]
    public void Rtl_origin_tracks_right_edge_and_logical_clusters_stay_ordered()
    {
        var (source, line) = MakeLine(["א", " ", "ב", " ", "ג"], [10, 4, 10, 4, 10], 1);
        var plan = GlyphSpacingPlan.Create(source, line, 22);
        var result = plan.Apply(1, 42, false);
        Assert.AreEqual(42d, result.Line.Runs[0].BaselineOrigin.X);
        Assert.AreEqual(0d, result.Line.Runs[0].BaselineOrigin.X - result.Line.Runs[0].Advance);
        CollectionAssert.AreEqual(line.Runs[0].Clusters.Select(c => c.Source).ToArray(), result.Line.Runs[0].Clusters.Select(c => c.Source).ToArray());
    }

    [TestMethod]
    public void Mixed_visual_run_positions_use_visual_not_logical_order()
    {
        var source = new SourceTextSnapshot("A ב ג C", 0);
        var a = Run(new(0, 2), 0, 0, [10, 4]); // visual first
        var c = Run(new(6, 1), 0, 42, [10]); // visual last, intentionally second in captured array
        var rtl = Run(new(2, 4), 1, 42, [4, 10, 4, 10]); // visual middle, source order is ב SP ג SP
        var line = new LineLayout(source.FullRange, new(0, 0, 52, 30), 20, 52, new[] { a, c, rtl });
        var plan = GlyphSpacingPlan.Create(source, line, 22);
        var result = plan.Apply(1, 58, false);
        Assert.AreEqual(0d, result.Line.Runs[0].BaselineOrigin.X);
        Assert.AreEqual(48d, result.Line.Runs[1].BaselineOrigin.X);
        Assert.AreEqual(48d, result.Line.Runs[2].BaselineOrigin.X);
        GlyphRunLayout Run(SourceRange range, uint bidi, double x, double[] advances)
        {
            var clusters = Enumerable.Range(0, range.Length).Select(i => new GlyphClusterLayout(new(range.Start + i, 1),
                bidi == 0 ? i : range.Length - 1 - i, 1, advances[bidi == 0 ? i : range.Length - 1 - i]));
            return new(0, range, new(x, 20), 22, bidi, "he-IL", advances.Select((a, i) => new GlyphPlacement(i + 1, a)), clusters);
        }
    }

    [TestMethod]
    public void Incomplete_source_coverage_is_rejected_before_spacing()
    {
        var source = new SourceTextSnapshot("AB", 0);
        var run = new GlyphRunLayout(0, new(0, 1), new(0, 20), 22, 0, "en-US",
            [new GlyphPlacement(1, 10)], [new GlyphClusterLayout(new(0, 1), 0, 1, 10)]);
        var line = new LineLayout(source.FullRange, new(0, 0, 10, 30), 20, 10, [run]);
        Assert.ThrowsExactly<ArgumentException>(() => GlyphSpacingPlan.Create(source, line, 22));
    }

    [TestMethod]
    public void Consecutive_spaces_keep_final_wrap_flag_and_full_source_range()
    {
        var source = new SourceTextSnapshot("A  B", 0);
        var map = ParagraphItemMap.Create(source, source.FullRange,
            [new(new(0, 1), 10, false), new(new(1, 1), 3, false), new(new(2, 1), 3, true), new(new(3, 1), 10, true)], 20);
        Assert.AreEqual(3, map.Items.Length);
        Assert.AreEqual(6d, map.Items[1].Width);
        Assert.AreEqual(new SourceRange(1, 2), map.ItemSources[1]);
        Assert.IsTrue(KnuthPlassLineBreaker.Break(map.Items, 10).IsSuccess);
    }

    [TestMethod]
    public void Unsafe_internal_grapheme_boundary_never_receives_spacing()
    {
        var (source, line) = MakeLine(["汉", "\u0308", "字"], [20, 0, 20]);
        var plan = GlyphSpacingPlan.Create(source, line, 20);
        Assert.AreEqual(0, plan.OpportunityCount);
        var result = plan.Apply(0, 60, true);
        Assert.AreEqual(40d, result.Line.Advance);
    }

    [TestMethod]
    public void Empty_map_and_spacing_plan_produce_a_blank_visual_line()
    {
        var source = new SourceTextSnapshot("", 0);
        var map = ParagraphItemMap.Create(source, source.FullRange, [], 20);
        Assert.AreEqual(0, map.Items.Length);
        var line = new LineLayout(source.FullRange, new(0, 0, 0, 30), 20, 0, []);
        var plan = GlyphSpacingPlan.Create(source, line, 20);
        Assert.AreEqual(0d, plan.Apply(0, 100, true).Line.Advance);
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(-1.01)]
    public void Invalid_adjustment_ratios_are_rejected(double ratio)
    {
        var (source, line) = MakeLine(["A", " ", "B"], [10, 4, 10]);
        var plan = GlyphSpacingPlan.Create(source, line, 20);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => plan.Apply(ratio, 24, false));
    }

    private static (SourceTextSnapshot Source, LineLayout Line) MakeLine(string[] clusters, double[] advances, uint bidi = 0)
    {
        var source = new SourceTextSnapshot(string.Concat(clusters), 0);
        var ranges = new List<GlyphClusterLayout>();
        var cursor = 0;
        for (var i = 0; i < clusters.Length; i++)
        {
            ranges.Add(new(new(cursor, clusters[i].Length), bidi == 0 ? i : clusters.Length - 1 - i, 1, advances[i]));
            cursor += clusters[i].Length;
        }
        var orderedAdvances = bidi == 0 ? advances : advances.Reverse().ToArray();
        var total = advances.Sum();
        var run = new GlyphRunLayout(0, source.FullRange, new(bidi == 0 ? 0 : total, 20), 22, bidi, "en-US",
            orderedAdvances.Select((advance, i) => new GlyphPlacement(i + 1, advance, 0.125, -0.25)), ranges);
        return (source, new(source.FullRange, new(0, 0, total, 30), 20, total, [run]));
    }
}
