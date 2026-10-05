using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Hyphenation;
using MDEditor.Typesetting.Layout;
using MDEditor.Typesetting.LineBreaking;
using MDEditor.Typesetting.Typography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class HyphenationTests
{
    [TestMethod]
    [DataRow("hyphenation", "2,6")]
    [DataRow("representation", "3,5,8,10")]
    [DataRow("associate", "2,4")]
    [DataRow("associates", "2,4")]
    [DataRow("declination", "3,5,7")]
    [DataRow("obligatory", "5,6")]
    [DataRow("philanthropic", "4,6")]
    [DataRow("present", "")]
    [DataRow("presents", "")]
    [DataRow("project", "")]
    [DataRow("projects", "")]
    [DataRow("reciprocity", "4")]
    [DataRow("recognizance", "2,5,7")]
    [DataRow("reformation", "3,5,7")]
    [DataRow("retribution", "3,5,7")]
    [DataRow("table", "2")]
    [DataRow("democrat", "3,4")]
    public void Offline_patterns_and_explicit_exceptions(string word, string expected)
    {
        var engine = LiangHyphenator.EnglishUs;
        Assert.AreEqual(4938, engine.PatternCount); Assert.AreEqual(15, engine.ExceptionCount);
        Assert.AreEqual(expected, string.Join(",", engine.GetBreakOffsets(word)));
    }

    [TestMethod]
    [DataRow("HYPHENATION")]
    [DataRow("Hyphenation")]
    [DataRow("hYpHeNaTiOn")]
    public void Engine_case_folding_is_invariant(string word) =>
        CollectionAssert.AreEqual(new[] { 2, 6 }, LiangHyphenator.EnglishUs.GetBreakOffsets(word).ToArray());

    [TestMethod]
    [DataRow("")]
    [DataRow("four")]
    [DataRow("café")]
    [DataRow("cafe\u0301")]
    [DataRow("ofﬁce")]
    [DataRow("中文")]
    [DataRow("representation123")]
    [DataRow("العربية")]
    [DataRow("😀representation")]
    public void Unsupported_words_are_not_transliterated_or_split(string word) =>
        Assert.AreEqual(0, LiangHyphenator.EnglishUs.GetBreakOffsets(word).Length);

    [TestMethod]
    public void Higher_even_weight_wins_and_exceptions_override_patterns()
    {
        CollectionAssert.AreEqual(new[] { 2 }, new LiangHyphenator("ab1c").GetBreakOffsets("abcdef", 2, 3).ToArray());
        Assert.AreEqual(0, new LiangHyphenator("ab1c b2c").GetBreakOffsets("abcdef", 2, 3).Length);
        CollectionAssert.AreEqual(new[] { 2 }, new LiangHyphenator("ab1c b2c", "ab-cdef").GetBreakOffsets("abcdef", 2, 3).ToArray());
        Assert.AreEqual(0, new LiangHyphenator("ab1c", "abcdef").GetBreakOffsets("abcdef", 2, 3).Length);
    }

    [TestMethod]
    [DataRow(7)]
    [DataRow(31059)]
    [DataRow(202609)]
    public void Trie_matches_independent_brute_force_pattern_overlay(int seed)
    {
        var random = new Random(seed);
        for (var problem = 0; problem < 300; problem++)
        {
            var patterns = new List<string>();
            for (var n = 0; n < 25; n++)
            {
                var p = new StringBuilder();
                if (random.Next(3) == 0) p.Append('.');
                for (var i = 0; i < random.Next(1, 5); i++) p.Append((char)('a' + random.Next(4))).Append(random.Next(10));
                if (random.Next(3) == 0) p.Append('.');
                patterns.Add(p.ToString());
            }
            var word = new string(Enumerable.Range(0, 12).Select(_ => (char)('a' + random.Next(4))).ToArray());
            var padded = "." + word + "."; var scores = new int[padded.Length + 1];
            foreach (var p in patterns)
            {
                var letters = Regex.Replace(p, "[0-9]", "");
                var weights = new int[letters.Length + 1]; var position = 0;
                foreach (var c in p) { if (char.IsDigit(c)) weights[position] = c - '0'; else position++; }
                for (var i = 0; i + letters.Length <= padded.Length; i++)
                    if (padded.AsSpan(i, letters.Length).SequenceEqual(letters))
                        for (var j = 0; j < weights.Length; j++) scores[i + j] = Math.Max(scores[i + j], weights[j]);
            }
            var expected = Enumerable.Range(2, word.Length - 4).Where(i => scores[i + 1] % 2 == 1).ToArray();
            CollectionAssert.AreEqual(expected, new LiangHyphenator(string.Join(" ", patterns)).GetBreakOffsets(word, 2, 3).ToArray());
        }
    }

    [TestMethod]
    [DataRow("AB1c")]
    [DataRow("ab11c")]
    [DataRow("12")]
    [DataRow("ä1bc")]
    [DataRow("ab/c")]
    public void Malformed_patterns_fail_loudly(string pattern) =>
        Assert.ThrowsExactly<ArgumentException>(() => new LiangHyphenator(pattern));

    [TestMethod]
    [DataRow("-word")]
    [DataRow("word-")]
    [DataRow("wo--rd")]
    [DataRow("Word")]
    public void Malformed_exceptions_fail_loudly(string exception) =>
        Assert.ThrowsExactly<ArgumentException>(() => new LiangHyphenator("", exception));

    [TestMethod]
    [DataRow(2, 3)]
    [DataRow(3, 4)]
    [DataRow(7, 6)]
    public void Minimum_fragments_and_long_word_limit_are_honored(int left, int right)
    {
        Assert.IsTrue(LiangHyphenator.EnglishUs.GetBreakOffsets("representation", left, right).All(i => i >= left && 14 - i >= right));
        Assert.AreEqual(0, LiangHyphenator.EnglishUs.GetBreakOffsets(new string('a', 129)).Length);
    }

    [TestMethod]
    [DataRow("https://representation.example")]
    [DataRow("representation@example.com")]
    [DataRow("representation_value")]
    [DataRow("representation123")]
    [DataRow("someRepresentation")]
    [DataRow("REPRESENTATION")]
    [DataRow("representation's")]
    [DataRow("representation’s")]
    [DataRow("`representation`")]
    [DataRow("C:\\representation\\file")]
    [DataRow("representation/path")]
    [DataRow("re-representation")]
    [DataRow("érepresentation")]
    [DataRow("representation\u0301")]
    public void Non_prose_tokens_and_unicode_word_fragments_are_protected(string text)
    {
        var source = new SourceTextSnapshot(text, 1);
        Assert.AreEqual(0, EnglishWordBreaks.Find(source, source.FullRange, Enumerable.Range(0, text.Length + 1), HyphenationOptions.EnglishUs).Count);
    }

    [TestMethod]
    public void Absolute_ranges_capitalization_and_exclusions_are_respected()
    {
        var source = new SourceTextSnapshot("HEADER\nRepresentation representation.\nFOOT", 3);
        var range = new SourceRange(7, 30);
        var points = EnglishWordBreaks.Find(source, range, Enumerable.Range(0, source.Length + 1),
            new(excludedRanges: [new(22, 14)]));
        CollectionAssert.AreEqual(new[] { 10, 12, 15, 17 }, points.Order().ToArray());
        Assert.AreEqual(0, EnglishWordBreaks.Find(source, range, Enumerable.Range(0, source.Length + 1),
            new(capitalizedWords: false, excludedRanges: [new(22, 14)])).Count);
        Assert.ThrowsExactly<ArgumentException>(() => EnglishWordBreaks.Find(source, range, [],
            new(excludedRanges: [new(source.Length, 1)])));
    }

    [TestMethod]
    public void Actual_ligature_and_grapheme_clusters_cannot_be_split()
    {
        var source = new SourceTextSnapshot("office", 1);
        var map = ParagraphItemMap.Create(source, source.FullRange,
            [new(new(0, 1), 5, false), new(new(1, 3), 12, false), new(new(4, 1), 5, false), new(new(5, 1), 5, false)],
            20, hyphenation: HyphenationOptions.EnglishUs, hyphenAdvance: 6);
        Assert.AreEqual(0, map.HyphenBreakItems.Count);
        var safe = new SourceTextSnapshot("hyphenation a\u0308", 1);
        var points = EnglishWordBreaks.Find(safe, safe.FullRange, [2, 6, 13], HyphenationOptions.EnglishUs);
        CollectionAssert.AreEqual(new[] { 2, 6 }, points.Order().ToArray());
    }

    [TestMethod]
    public void Conditional_penalties_preserve_source_and_skip_no_letters()
    {
        var source = new SourceTextSnapshot("HEADER\nrepresentation\nEND", 9);
        var paragraph = new SourceRange(7, 14);
        var map = ParagraphItemMap.Create(source, paragraph,
            Enumerable.Range(7, 14).Select(i => new MeasuredTextCluster(new(i, 1), 10, false)),
            20, hyphenation: HyphenationOptions.EnglishUs, hyphenAdvance: 4);
        Assert.AreEqual(4, map.HyphenBreakItems.Count);
        var item = map.HyphenBreakItems.Single(i => map.ItemSources[i].Start == 15);
        Assert.AreEqual(50, map.Items[item].PenaltyValue); Assert.IsTrue(map.Items[item].Flagged);
        var result = KnuthPlassLineBreaker.Break(map.Items, 84);
        Assert.IsTrue(result.IsSuccess); Assert.AreEqual(item, result.Lines[0].BreakItemIndex);
        var first = map.GetLine(new(0, item, item, 4, false));
        Assert.AreEqual("represen‐", first.GetText(source));
        Assert.AreEqual(new SourceRange(15, 0), first.MapDisplayRange(new(8, 1)));
        Assert.AreEqual(15, map.Boundary(result.Lines[0].NextItemIndex));
        Assert.AreEqual("representation", source.GetText(paragraph));
        Assert.AreEqual("", map.GetLine(new(0, map.Items.Length, map.Items.Length, 0, true)).Suffix);
    }

    [TestMethod]
    [DataRow(-1d)]
    [DataRow(0d)]
    [DataRow(0.5d)]
    [DataRow(1d)]
    public void Generated_hyphen_survives_bounded_spacing_and_paint_segmentation(double ratio)
    {
        var source = new SourceTextSnapshot("HEADER\nA represen", 2); var range = new SourceRange(7, 10);
        var glyphs = Enumerable.Range(0, 11).Select(i => new GlyphPlacement(i + 1, i == 1 ? 5 : 10)).ToArray();
        var clusters = Enumerable.Range(0, 10).Select(i => new GlyphClusterLayout(new(7 + i, 1), i, 1, glyphs[i].Advance))
            .Append(new(new(17, 0), 10, 1, 10, "‐")).ToArray();
        var line = new LineLayout(range, new(14.125, 0, 200, 30), 20, 105,
            [new(0, range, new(14.125, 20), 20, 0, "en-US", glyphs, clusters)]);
        var plan = CjkGlyphSpacingPlan.Create(source, line, 20, CjkTypographyOptions.Refined);
        Assert.AreEqual(105d, plan.NaturalWidth); Assert.AreEqual(1, plan.OpportunityCount);
        var target = plan.NaturalWidth + ratio * (ratio < 0 ? plan.Shrink : plan.Stretch);
        var result = plan.Apply(ratio, target, false);
        var generated = result.Line.Runs.SelectMany(r => r.Clusters).Single(c => c.GeneratedText is not null);
        Assert.AreEqual(new SourceRange(17, 0), generated.Source); Assert.AreEqual("‐", generated.GeneratedText);
        Assert.AreEqual(10d, generated.Advance); Assert.IsTrue(result.Spacing.All(s => s.Source.Length > 0));
        Assert.IsTrue(Math.Abs(target - result.Line.Advance) < 0.01);
        var legacy = GlyphSpacingPlan.Create(source, line, 20);
        Assert.AreEqual("‐", legacy.Apply(0, 105, true).Line.Runs[0].Clusters[^1].GeneratedText);
        var snapshot = new LayoutSnapshot(source, new(14.125, 0, 200, 30), [new("Cambria", "Regular")],
            [new(range, new(14.125, 0, 200, 30), [result.Line])]);
        StringAssert.Contains(LayoutSnapshotInspector.ToJson(snapshot), "GeneratedText");
    }

    [TestMethod]
    [DataRow(1, 3, 50)]
    [DataRow(2, 2, 50)]
    [DataRow(2, 3, -1)]
    [DataRow(2, 3, 10000)]
    public void Invalid_policy_is_rejected(int left, int right, int penalty) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new HyphenationOptions(leftMinimum: left, rightMinimum: right, penalty: penalty));

    [TestMethod]
    public void Invalid_generated_ranges_and_unmeasured_hyphens_are_rejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new DiscretionaryLine(new(0, 0), "--"));
        Assert.AreEqual("", default(DiscretionaryLine).Suffix);
        Assert.ThrowsExactly<ArgumentException>(() => new GlyphClusterLayout(new(0, 1), 0, 1, 5, "‐"));
        Assert.ThrowsExactly<ArgumentException>(() => new GlyphClusterLayout(new(0, 0), 0, 1, 5, "word"));
        Assert.ThrowsExactly<ArgumentException>(() => new HyphenationOptions(hyphen: "--"));
        var display = new DiscretionaryLine(new(7, 8), "‐");
        Assert.ThrowsExactly<ArgumentException>(() => display.MapDisplayRange(new(7, 2)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => display.MapDisplayRange(new(9, 1)));
        var source = new SourceTextSnapshot("hyphenation", 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ParagraphItemMap.Create(source, source.FullRange,
            [new(source.FullRange, 30, false)], 20, hyphenation: HyphenationOptions.EnglishUs));
    }

    [TestMethod]
    [DataRow("中文representation汉字", 2)]
    [DataRow("（representation）。", 1)]
    [DataRow("Representation.", 0)]
    public void Prose_punctuation_and_cjk_boundaries_keep_english_points(string text, int start)
    {
        var source = new SourceTextSnapshot(text, 1);
        var points = EnglishWordBreaks.Find(source, source.FullRange, Enumerable.Range(0, text.Length + 1), HyphenationOptions.EnglishUs);
        CollectionAssert.AreEqual(new[] { start + 3, start + 5, start + 8, start + 10 }, points.Order().ToArray());
    }

    [TestMethod]
    public void Generated_clusters_must_be_single_line_end_anchors()
    {
        var source = new SourceTextSnapshot("A", 1);
        var run = new GlyphRunLayout(0, source.FullRange, new(0, 20), 20, 0, "en-US",
            [new(1, 5), new(2, 5)], [new(new(0, 0), 0, 1, 5, "‐"), new(source.FullRange, 1, 1, 5)]);
        Assert.ThrowsExactly<ArgumentException>(() => new LineLayout(source.FullRange, new(0, 0, 20, 30), 20, 10, [run]));
        var end = new SourceRange(1, 0);
        var two = new GlyphRunLayout(0, end, new(0, 20), 20, 0, "en-US",
            [new(1, 5), new(2, 5)], [new(end, 0, 1, 5, "‐"), new(end, 1, 1, 5, "‐")]);
        Assert.ThrowsExactly<ArgumentException>(() => new LineLayout(source.FullRange, new(0, 0, 20, 30), 20, 10, [two]));
    }

    [TestMethod]
    [DataRow(316.80882352941177)]
    [DataRow(-278.123456789)]
    [DataRow(14.125)]
    [DataRow(1048.987654321)]
    public void Native_paint_translation_rounds_the_line_anchor_once(double x)
    {
        var range = new SourceRange(0, 1); const double y = 71.987654321;
        var local = (double)(float)278.115753173828; var baseline = (double)(float)16.15332;
        var run = new GlyphRunLayout(0, range, new(x + local, y + baseline), 17, 0, "en-US",
            [new(1, 8)], [new(range, 0, 1, 8)]);
        var line = new LineLayout(range, new(x, y, 400, 30), y + baseline, 8, [run]);
        var offset = new LayoutPoint((float)(16 - x), (float)(16 - y));
        var actual = GlyphPaintCoordinates.Resolve(line, run, offset);
        var anchorX = (float)(x + offset.X); var anchorY = (float)(y + offset.Y);
        Assert.AreEqual((double)(float)(anchorX + local), actual.X);
        Assert.AreEqual((double)(float)(anchorY + baseline), actual.Y);
    }

    [TestMethod]
    [DataRow(0d, 0d)]
    [DataRow(10000d, 5000d)]
    [DataRow(1000000d, 1000000d)]
    public void Flagged_paths_match_exhaustive_oracle_including_final_line_cost(double consecutive, double final)
    {
        var source = new SourceTextSnapshot("representation hyphenation typography", 1);
        var map = ParagraphItemMap.Create(source, source.FullRange,
            Enumerable.Range(0, source.Length).Select(i => new MeasuredTextCluster(new(i, 1), source.Text[i] == ' ' ? 4 : 7, source.Text[i] == ' ')),
            20, hyphenation: HyphenationOptions.EnglishUs, hyphenAdvance: 3);
        var options = new LineBreakOptions(maximumStretchRatio: 3, consecutiveFlaggedDemerits: consecutive, finalFlaggedDemerits: final);
        for (var width = 50; width <= 120; width += 5)
        {
            var oracle = IndependentLineBreakOracle.Enumerate(map.Items.ToArray(), [width], options);
            var actual = KnuthPlassLineBreaker.Break(map.Items, width, options);
            Assert.AreEqual(oracle.Count > 0, actual.IsSuccess);
            if (actual.IsSuccess) Assert.AreEqual(oracle.Min(o => o.Cost), actual.TotalDemerits!.Value, 1e-6);
        }
    }
}
