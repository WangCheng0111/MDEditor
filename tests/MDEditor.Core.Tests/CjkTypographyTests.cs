using System.Globalization;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;
using MDEditor.Typesetting.LineBreaking;
using MDEditor.Typesetting.Typography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class CjkTypographyTests
{
    [TestMethod]
    [DataRow("中，文", 1)]
    [DataRow("中。文", 1)]
    [DataRow("中、文", 1)]
    [DataRow("中；文", 1)]
    [DataRow("中：文", 1)]
    [DataRow("中？文", 1)]
    [DataRow("中！文", 1)]
    [DataRow("中）文", 1)]
    [DataRow("中》文", 1)]
    [DataRow("中】文", 1)]
    [DataRow("中”文", 1)]
    [DataRow("中」文", 1)]
    [DataRow("（中文", 1)]
    [DataRow("《中文", 1)]
    [DataRow("【中文", 1)]
    [DataRow("“中文", 1)]
    [DataRow("「中文", 1)]
    [DataRow("（   中文", 4)]
    [DataRow("中文  ，文", 3)]
    public void Opening_at_end_and_closing_at_start_are_forbidden_even_across_spaces(string text, int index)
    {
        Assert.IsFalse(CjkTypographyRules.MayBreak(new(text, 0), index, CjkTypographyOptions.Refined));
        Assert.IsTrue(CjkTypographyRules.MayBreak(new(text, 0), index, CjkTypographyOptions.Legacy));
    }

    [TestMethod]
    [DataRow("……", 1)]
    [DataRow("——", 1)]
    [DataRow("…………", 1)]
    [DataRow("…………", 3)]
    [DataRow("————", 3)]
    public void Two_character_marks_are_not_split(string text, int index) =>
        Assert.IsFalse(CjkTypographyRules.MayBreak(new(text, 0), index, CjkTypographyOptions.Refined));

    [TestMethod]
    public void Basic_allows_between_complete_pairs_but_strict_forbids_ellipsis_at_start()
    {
        var source = new SourceTextSnapshot("…………", 0);
        Assert.IsTrue(CjkTypographyRules.MayBreak(source, 2, CjkTypographyOptions.Refined));
        Assert.IsFalse(CjkTypographyRules.MayBreak(source, 2, new(prohibitionLevel: CjkProhibitionLevel.Strict)));
        Assert.IsFalse(CjkTypographyRules.MayBreak(new("中/文", 0), 2, new(prohibitionLevel: CjkProhibitionLevel.Strict)));
    }

    [TestMethod]
    [DataRow("12", 1)]
    [DataRow("2%", 1)]
    [DataRow("2％", 1)]
    [DataRow("2℃", 1)]
    [DataRow("2°", 1)]
    [DataRow("¥2", 1)]
    [DataRow("±2", 1)]
    [DataRow("$2", 1)]
    public void Numeric_prefixes_and_units_stay_with_digits(string text, int index) =>
        Assert.IsFalse(CjkTypographyRules.MayBreak(new(text, 0), index, CjkTypographyOptions.Refined));

    [TestMethod]
    public void Paragraph_literal_punctuation_is_preserved_not_deleted()
    {
        var source = new SourceTextSnapshot("，中（文", 0);
        Assert.IsTrue(CjkTypographyRules.LineBoundariesValid(source, source.FullRange, source.FullRange, CjkTypographyOptions.Refined));
        Assert.IsFalse(CjkTypographyRules.LineBoundariesValid(source, source.FullRange, new(0, 3), CjkTypographyOptions.Refined));
    }

    [TestMethod]
    [DataRow("中A", true)]
    [DataRow("A中", true)]
    [DataRow("中2", true)]
    [DataRow("cafe\u0301中", true)]
    [DataRow("中文", false)]
    [DataRow("中，", false)]
    [DataRow("，A", false)]
    [DataRow("中 ", false)]
    [DataRow("中😀", false)]
    [DataRow("中العربية", false)]
    public void Mixed_script_classification_does_not_insert_around_punctuation_or_explicit_spaces(string text, bool expected)
    {
        var elements = StringInfo.ParseCombiningCharacters(text);
        var boundary = elements[^1];
        Assert.AreEqual(expected, CjkTypographyRules.IsMixedBoundary(text[..boundary], text[boundary..]));
    }

    [TestMethod]
    public void Mapping_disables_illegal_candidates_and_keeps_mixed_glue_out_of_break_edge()
    {
        var source = new SourceTextSnapshot("（中，A文）", 0);
        var clusters = Enumerable.Range(0, source.Length).Select(i => new MeasuredTextCluster(new(i, 1), 20, true));
        var map = ParagraphItemMap.Create(source, source.FullRange, clusters, 20, CjkTypographyOptions.Refined);
        var requests = new List<LineMeasureRequest>();
        KnuthPlassLineBreaker.Break(map.Items, 60, lineMeasurer: r => { requests.Add(r); return new(40, 20, 5); });
        Assert.IsTrue(requests.All(r => CjkTypographyRules.LineBoundariesValid(source, source.FullRange, map.GetSource(r), CjkTypographyOptions.Refined)));
        var mixedSource = new SourceTextSnapshot("中A文", 0);
        var mixed = ParagraphItemMap.Create(mixedSource, mixedSource.FullRange,
            [new(new(0, 1), 20, true), new(new(1, 1), 10, true), new(new(2, 1), 20, true)], 20, CjkTypographyOptions.Refined);
        Assert.AreEqual(5d, mixed.Items[1].Width);
        Assert.AreEqual("中", mixedSource.GetText(mixed.GetSource(new(0, 1, 1, 0, false))));
    }

    [TestMethod]
    [DataRow(-1.0)]
    [DataRow(-0.333333)]
    [DataRow(0.0)]
    [DataRow(0.5)]
    [DataRow(1.0)]
    public void Bounded_mixed_spacing_preserves_glyphs_and_actual_native_width(double ratio)
    {
        var (source, line) = Line(["中", "ffi", "文"], [20, 18, 20]);
        var plan = CjkGlyphSpacingPlan.Create(source, line, 20, CjkTypographyOptions.Refined);
        Assert.AreEqual(68d, plan.NaturalWidth); Assert.AreEqual(5d, plan.Shrink); Assert.AreEqual(10d, plan.Stretch);
        var target = plan.NaturalWidth + ratio * (ratio < 0 ? plan.Shrink : plan.Stretch);
        var result = plan.Apply(ratio, target, false);
        Assert.AreEqual(target, result.Line.Runs.Sum(r => r.Glyphs.Sum(g => g.Advance)), 0.01);
        Assert.IsTrue(result.Spacing.All(s => s.Delta >= 2.5 - 1e-5 && s.Delta <= 10 + 1e-5));
        CollectionAssert.AreEqual(line.Runs[0].Glyphs.Select(g => g.Index).ToArray(), Glyphs(result).Select(g => g.Index).ToArray());
        Assert.AreEqual(new SourceRange(1, 3), result.Line.Runs.SelectMany(r => r.Clusters).ElementAt(1).Source);
    }

    [TestMethod]
    public void No_ink_metrics_means_no_guessed_punctuation_compression()
    {
        var (source, line) = Line(["中", "，", "文"], [20, 20, 20]);
        var plan = CjkGlyphSpacingPlan.Create(source, line, 20, CjkTypographyOptions.Refined);
        Assert.AreEqual(0d, plan.Shrink); Assert.AreEqual(60d, plan.NaturalWidth);
    }

    [TestMethod]
    public void Compression_is_limited_by_measured_white_margin_and_preserves_clearance()
    {
        var (source, line) = Line(["中", "，", "文"], [20, 20, 20]);
        var plan = CjkGlyphSpacingPlan.Create(source, line, 20, CjkTypographyOptions.Refined, [new(new(1, 1), 0, 8)]);
        Assert.AreEqual(7.2, plan.Shrink, 1e-7);
        var result = plan.Apply(-1, 52.8, false);
        Assert.AreEqual(-7.2, result.Spacing.Single(s => s.Kind.Contains("Punctuation")).Delta, 1e-5);
        Assert.AreEqual(12.8, Glyphs(result)[1].Advance, 1e-5);
        Assert.AreEqual(0d, Glyphs(result)[1].AdvanceOffset);
    }

    [TestMethod]
    public void Opening_uses_leading_offset_and_halfwidth_limit()
    {
        var (source, line) = Line(["（", "中", "文"], [20, 20, 20]);
        var plan = CjkGlyphSpacingPlan.Create(source, line, 20, CjkTypographyOptions.Refined, [new(new(0, 1), 12, 0)]);
        Assert.AreEqual(50d, plan.NaturalWidth);
        var result = plan.Apply(0, 100, true);
        Assert.AreEqual(-10d, result.Line.Runs[0].Glyphs[0].AdvanceOffset);
        Assert.AreEqual(10d, result.Line.Runs[0].Glyphs[0].Advance);
    }

    [TestMethod]
    public void Paragraph_end_and_adjacent_marks_are_adjusted_before_final_line_scoring()
    {
        var (source, line) = Line(["中", "。", "”"], [20, 20, 20]);
        var plan = CjkGlyphSpacingPlan.Create(source, line, 20, CjkTypographyOptions.Refined,
            [new(new(1, 1), 0, 12), new(new(2, 1), 0, 12)]);
        Assert.AreEqual(40d, plan.NaturalWidth);
        Assert.AreEqual(40d, plan.Apply(0, 100, true).Line.Advance);
    }

    [TestMethod]
    public void Shrink_priority_exhausts_punctuation_before_mixed_gap()
    {
        var (source, line) = Line(["中", "，", "文", "A"], [20, 20, 20, 10]);
        var plan = CjkGlyphSpacingPlan.Create(source, line, 20, CjkTypographyOptions.Refined, [new(new(1, 1), 0, 12)]);
        var ratio = -5d / plan.Shrink;
        var result = plan.Apply(ratio, plan.NaturalWidth - 5, false);
        Assert.AreEqual(-5d, result.Spacing.Single(s => s.Kind.Contains("Punctuation")).Delta);
        Assert.AreEqual(5d, result.Spacing.Single(s => s.Kind == "MixedScript").Delta);
    }

    [TestMethod]
    public void Expansion_priority_uses_words_then_mixed_then_cjk()
    {
        var (source, line) = Line(["A", " ", "B", "中", "文"], [10, 5, 10, 20, 20]);
        var plan = CjkGlyphSpacingPlan.Create(source, line, 20, CjkTypographyOptions.Refined);
        var result = plan.Apply(3 / plan.Stretch, plan.NaturalWidth + 3, false);
        Assert.AreEqual(3d, result.Spacing.Single(s => s.Kind == "WordSpace").Delta);
        Assert.AreEqual(5d, result.Spacing.Single(s => s.Kind == "MixedScript").Delta);
        Assert.AreEqual(0d, result.Spacing.Single(s => s.Kind == "InterCharacter").Delta);
    }

    [TestMethod]
    public void Narrow_fonts_and_negative_or_tiny_bearings_are_not_compressed()
    {
        var (source, line) = Line(["（", "中", "）"], [8, 20, 8]);
        var plan = CjkGlyphSpacingPlan.Create(source, line, 20, CjkTypographyOptions.Refined,
            [new(new(0, 1), 5, 0), new(new(2, 1), 0, 0.1)]);
        Assert.AreEqual(36d, plan.NaturalWidth); Assert.AreEqual(0d, plan.Shrink);
    }

    [TestMethod]
    public void Disabling_compression_and_fixed_mixed_gap_are_explicit_styles()
    {
        var (source, line) = Line(["中", "，", "文", "A"], [20, 20, 20, 10]);
        var options = new CjkTypographyOptions(compressPunctuation: false, mixedMinimumEm: 0.25, mixedMaximumEm: 0.25);
        var plan = CjkGlyphSpacingPlan.Create(source, line, 20, options, [new(new(1, 1), 0, 12)]);
        Assert.AreEqual(75d, plan.NaturalWidth); Assert.AreEqual(0d, plan.Shrink);
        Assert.IsFalse(plan.Apply(0, 100, true).Spacing.Any(s => s.Kind == "Punctuation"));
    }

    [TestMethod]
    public void Rtl_word_spacing_still_places_origin_at_right_edge_without_cjk_letter_spacing()
    {
        var (source, line) = Line(["א", " ", "ב"], [10, 5, 10], 1);
        var plan = CjkGlyphSpacingPlan.Create(source, line, 20, CjkTypographyOptions.Refined);
        var result = plan.Apply(1, 30, false);
        Assert.AreEqual(30d, result.Line.Runs[0].BaselineOrigin.X);
        Assert.AreEqual(0d, result.Line.Runs[0].BaselineOrigin.X - result.Line.Runs[0].Advance);
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(-1.01)]
    [DataRow(1.01)]
    public void Refined_ratio_does_not_exceed_spacing_bounds(double ratio)
    {
        var (source, line) = Line(["中", "A"], [20, 10]);
        var plan = CjkGlyphSpacingPlan.Create(source, line, 20, CjkTypographyOptions.Refined);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => plan.Apply(ratio, 35, false));
    }

    [TestMethod]
    public void Invalid_profiles_and_nonmatching_ink_metrics_are_rejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new CjkTypographyOptions(mixedMinimumEm: 0.5));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CjkTypographyOptions(inkClearanceEm: double.NaN));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ClusterInkMargins(new(0, 1), -1, 2));
        var (source, line) = Line(["中", "，"], [20, 20]);
        Assert.ThrowsExactly<ArgumentException>(() => CjkGlyphSpacingPlan.Create(source, line, 20,
            CjkTypographyOptions.Refined, [new(new(0, 2), 1, 1)]));
    }

    [TestMethod]
    public void Thousands_of_measured_fonts_and_ratios_preserve_halfwidth_and_clearance()
    {
        var random = new Random(31059);
        for (var sample = 0; sample < 1000; sample++)
        {
            var (source, line) = Line(["中", "，", "文", "A"], [20, 20, 20, 10]);
            var a = random.NextDouble() * 8; var b = random.NextDouble() * 10;
            var plan = CjkGlyphSpacingPlan.Create(source, line, 20, CjkTypographyOptions.Refined, [new(new(1, 1), a, b)]);
            var ratio = -random.NextDouble();
            var result = plan.Apply(ratio, plan.NaturalWidth + ratio * plan.Shrink, false);
            var glyph = Glyphs(result)[1];
            Assert.IsTrue(glyph.Advance >= 10 - 1e-5);
            Assert.IsTrue(a + glyph.AdvanceOffset >= Math.Min(a, 0.8) - 1e-5);
            Assert.IsTrue(b + glyph.Advance - 20 - glyph.AdvanceOffset >= Math.Min(b, 0.8) - 1e-5);
        }
    }

    [TestMethod]
    public void Inconsistent_ink_data_is_rejected_not_used_to_create_fake_capacity()
    {
        var (source, line) = Line(["中", "，"], [20, 20]);
        Assert.ThrowsExactly<ArgumentException>(() => CjkGlyphSpacingPlan.Create(source, line, 20,
            CjkTypographyOptions.Refined, [new(new(1, 1), 15, 15)]));
    }

    [TestMethod]
    [DataRow(-1.0)]
    [DataRow(0.0)]
    [DataRow(0.375)]
    [DataRow(1.0)]
    public void Combining_marks_keep_zero_advance_and_compensated_ink_positions(double ratio)
    {
        var source = new SourceTextSnapshot("中a\u0308\u0301文", 0);
        var raw = new[] { new GlyphPlacement(1, 20), new GlyphPlacement(2, 9.765625),
            new GlyphPlacement(3, 0, -2.138671875, 0.25), new GlyphPlacement(4, 0, -1.75, 1), new GlyphPlacement(5, 20) };
        var run = new GlyphRunLayout(0, source.FullRange, new(0, 20), 20, 0, "zh-CN", raw,
            [new(new(0, 1), 0, 1, 20), new(new(1, 3), 1, 3, 9.765625), new(new(4, 1), 4, 1, 20)]);
        var line = new LineLayout(source.FullRange, new(0, 0, run.Advance, 30), 20, run.Advance, [run]);
        var plan = CjkGlyphSpacingPlan.Create(source, line, 20, CjkTypographyOptions.Refined);
        var target = plan.NaturalWidth + ratio * (ratio < 0 ? plan.Shrink : plan.Stretch);
        var result = plan.Apply(ratio, target, false);
        var actual = Glyphs(result);
        var baseDelta = actual[1].Advance - raw[1].Advance;
        Assert.AreEqual(target, result.Line.Advance, 0.01);
        for (var g = 2; g <= 3; g++)
        {
            Assert.AreEqual(0d, actual[g].Advance);
            Assert.AreEqual(raw[g].AdvanceOffset, baseDelta + actual[g].AdvanceOffset, 1e-5);
            Assert.AreEqual(raw[g].AscenderOffset, actual[g].AscenderOffset);
        }
        CollectionAssert.AreEqual(raw.Select(g => g.Index).ToArray(), actual.Select(g => g.Index).ToArray());
        Assert.AreEqual(new SourceRange(1, 3), result.Line.Runs.SelectMany(r => r.Clusters).ElementAt(1).Source);
    }

    [TestMethod]
    [DataRow("12 34", 2)]
    [DataRow("12 34", 3)]
    [DataRow("$ 2", 2)]
    [DataRow("12  34", 4)]
    public void Explicit_spaces_between_numeric_tokens_are_not_removed_or_made_unbreakable(string text, int boundary) =>
        Assert.IsTrue(CjkTypographyRules.MayBreak(new(text, 0), boundary, CjkTypographyOptions.Refined));

    [TestMethod]
    [DataRow(0.4, 0.2, 1.0)]
    [DataRow(0.4, 0.2, -1.0)]
    [DataRow(184.16015625, 34.4, 1.0)]
    [DataRow(184.16015625, 10.0, -1.0)]
    [DataRow(10.0, 0.03, 1.0)]
    [DataRow(10.0, 0.03, -1.0)]
    public void Inclusive_spacing_boundary_survives_roundtrip_division_without_allowing_extra_ulp(double natural, double capacity, double limit)
    {
        var options = new LineBreakOptions(maximumStretchRatio: 1, lastLineAlignment: LastLineAlignment.Justified);
        var width = natural + capacity * limit;
        LineMeasurement? Measure(LineMeasureRequest _) => new(natural, capacity, capacity);
        var accepted = KnuthPlassLineBreaker.Break([LineBreakItem.Box(natural)], width, options, lineMeasurer: Measure);
        Assert.IsTrue(accepted.IsSuccess);
        Assert.AreEqual(limit, accepted.Lines.Single().AdjustmentRatio, 1e-12);
        var outside = limit > 0 ? Math.BitIncrement(width) : Math.BitDecrement(width);
        Assert.AreEqual(LineBreakStatus.NoFeasibleBreaks,
            KnuthPlassLineBreaker.Break([LineBreakItem.Box(natural)], outside, options, lineMeasurer: Measure).Status);
    }

    [TestMethod]
    public void Paint_segments_merge_equal_spacing_and_keep_full_clusters_font_slots_and_source()
    {
        var source = new SourceTextSnapshot("AAe\u0301中文", 0);
        var glyphs = new[] { new GlyphPlacement(1, 10), new GlyphPlacement(2, 10), new GlyphPlacement(3, 9.765625),
            new GlyphPlacement(4, 0, -2, 0.125), new GlyphPlacement(5, 20), new GlyphPlacement(6, 20) };
        var run = new GlyphRunLayout(4, source.FullRange, new(37.125, 20), 20, 0, "zh-CN", glyphs,
            [new(new(0, 1), 0, 1, 10), new(new(1, 1), 1, 1, 10), new(new(2, 2), 2, 2, 9.765625),
             new(new(4, 1), 4, 1, 20), new(new(5, 1), 5, 1, 20)]);
        var line = new LineLayout(source.FullRange, new(37.125, 0, run.Advance, 30), 20, run.Advance, [run]);
        var plan = CjkGlyphSpacingPlan.Create(source, line, 20, CjkTypographyOptions.Refined);
        var result = plan.Apply(0, 100, true);
        Assert.AreEqual(3, result.Line.Runs.Length);
        CollectionAssert.AreEqual(new[] { new SourceRange(0, 2), new(2, 2), new(4, 2) }, result.Line.Runs.Select(r => r.Source).ToArray());
        Assert.IsTrue(result.Line.Runs.All(r => r.FontIndex == 4 && r.BidiLevel == 0 && r.Locale == "zh-CN"));
        Assert.AreEqual(2, result.Line.Runs[1].Clusters.Single().GlyphCount);
        CollectionAssert.AreEqual(glyphs.Select(g => g.Index).ToArray(), Glyphs(result).Select(g => g.Index).ToArray());
        float cursor = 0;
        foreach (var segment in result.Line.Runs)
        {
            Assert.AreEqual(37.125 + cursor, segment.BaselineOrigin.X);
            cursor = (float)(cursor + segment.Advance);
        }
        Assert.AreEqual(plan.NaturalWidth, result.Line.Advance, 0.01);
    }

    private static GlyphPlacement[] Glyphs(GlyphJustificationResult result) => result.Line.Runs.SelectMany(r => r.Glyphs).ToArray();

    private static (SourceTextSnapshot Source, LineLayout Line) Line(string[] texts, double[] advances, uint bidi = 0)
    {
        var source = new SourceTextSnapshot(string.Concat(texts), 0);
        var clusters = new List<GlyphClusterLayout>(); var cursor = 0;
        for (var i = 0; i < texts.Length; i++)
        {
            clusters.Add(new(new(cursor, texts[i].Length), bidi == 0 ? i : texts.Length - 1 - i, 1, advances[i]));
            cursor += texts[i].Length;
        }
        var ordered = bidi == 0 ? advances : advances.Reverse().ToArray();
        var run = new GlyphRunLayout(0, source.FullRange, new(bidi == 0 ? 0 : advances.Sum(), 20), 20, bidi, "zh-CN",
            ordered.Select((a, i) => new GlyphPlacement(i + 1, a)), clusters);
        return (source, new(source.FullRange, new(0, 0, advances.Sum(), 30), 20, advances.Sum(), [run]));
    }
}
