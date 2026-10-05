using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class LayoutSnapshotTests
{
    private static LayoutRect TestBounds => new(10, 20, 100, 40);

    [TestMethod]
    public void Equivalent_snapshots_have_deterministic_fingerprints()
    {
        var first = CreateSnapshot();
        var second = CreateSnapshot();
        var fingerprint = LayoutSnapshotInspector.Fingerprint(first);

        Assert.AreNotSame(first, second);
        Assert.AreEqual(fingerprint, LayoutSnapshotInspector.Fingerprint(first));
        Assert.AreEqual(fingerprint, LayoutSnapshotInspector.Fingerprint(second));
        Assert.AreEqual(LayoutSnapshotInspector.ToJson(first), LayoutSnapshotInspector.ToJson(second));
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    [DataRow("tr-TR")]
    [DataRow("ar-SA")]
    public void Fingerprint_and_diagnostics_do_not_depend_on_current_culture(string cultureName)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            var expectedJson = LayoutSnapshotInspector.ToJson(CreateSnapshot());
            var expectedFingerprint = LayoutSnapshotInspector.Fingerprint(CreateSnapshot());

            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            var snapshot = CreateSnapshot();

            Assert.AreEqual(expectedJson, LayoutSnapshotInspector.ToJson(snapshot));
            Assert.AreEqual(expectedFingerprint, LayoutSnapshotInspector.Fingerprint(snapshot));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("source")]
    [DataRow("bounds")]
    [DataRow("baseline")]
    [DataRow("glyph-offset")]
    [DataRow("glyph-advance")]
    [DataRow("line-advance")]
    [DataRow("font")]
    [DataRow("locale")]
    [DataRow("bidi")]
    public void Fingerprint_changes_with_source_version_or_layout_metadata(string change)
    {
        var changed = change switch
        {
            "version" => CreateSnapshot(version: 8),
            "source" => CreateSnapshot(text: "Yffi a\u0308"),
            "bounds" => CreateSnapshot(snapshotWidth: 121.25),
            "baseline" => CreateSnapshot(baselineX: 14.125),
            "glyph-offset" => CreateSnapshot(glyphOffset: -0.625),
            "glyph-advance" => CreateSnapshot(glyphAdvance: 15.25),
            "line-advance" => CreateSnapshot(lineAdvance: 43.5),
            "font" => CreateSnapshot(family: "Gabriola"),
            "locale" => CreateSnapshot(locale: "fr-FR"),
            "bidi" => CreateSnapshot(bidiLevel: 1),
            _ => throw new ArgumentException(nameof(change))
        };

        Assert.AreNotEqual(LayoutSnapshotInspector.Fingerprint(CreateSnapshot()),
            LayoutSnapshotInspector.Fingerprint(changed));
    }

    [TestMethod]
    public void Inspector_counts_the_whole_snapshot_and_preserves_source_metadata()
    {
        var snapshot = CreateSnapshot();
        var summary = LayoutSnapshotInspector.Inspect(snapshot);

        Assert.AreEqual(new LayoutSummary(7, 7, 2, 2, 2, 2, 3, 2), summary);
        using var json = JsonDocument.Parse(LayoutSnapshotInspector.ToJson(snapshot));
        var root = json.RootElement;
        Assert.AreEqual(snapshot.Source.Text, root.GetProperty("Source").GetProperty("Text").GetString());
        Assert.AreEqual(7L, root.GetProperty("Source").GetProperty("Version").GetInt64());
        Assert.AreEqual(2, root.GetProperty("Blocks").GetArrayLength());
        Assert.AreEqual(13.75, root.GetProperty("Blocks")[0].GetProperty("Lines")[0]
            .GetProperty("Runs")[0].GetProperty("BaselineOrigin").GetProperty("X").GetDouble());
    }

    [TestMethod]
    public void Inspector_rejects_null_snapshots()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => LayoutSnapshotInspector.Inspect(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => LayoutSnapshotInspector.ToJson(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => LayoutSnapshotInspector.Fingerprint(null!));
    }

    [TestMethod]
    public void Source_ranges_are_half_open_utf16_ranges_at_nonzero_offsets()
    {
        var source = new SourceTextSnapshot("xx\U0001F600a\u0308!", 19);
        var range = new SourceRange(2, 4);

        Assert.AreEqual(7, source.Length);
        Assert.AreEqual(new SourceRange(0, 7), source.FullRange);
        Assert.AreEqual(6, range.End);
        Assert.AreEqual("\U0001F600a\u0308", source.GetText(range));
        Assert.AreEqual("\uD83D", source.GetText(new SourceRange(2, 1)));
        Assert.IsTrue(range.Contains(new SourceRange(2, 0)));
        Assert.IsTrue(range.Contains(new SourceRange(6, 0)));
        Assert.IsFalse(range.Contains(new SourceRange(1, 0)));
        Assert.IsFalse(range.Contains(new SourceRange(6, 1)));
    }

    [TestMethod]
    public void Empty_source_range_is_valid_at_eof_including_empty_source()
    {
        var source = new SourceTextSnapshot("abc", long.MaxValue);
        var empty = new SourceTextSnapshot("", 0);

        Assert.AreEqual("", source.GetText(new SourceRange(source.Length, 0)));
        Assert.AreEqual(long.MaxValue, source.Version);
        Assert.AreEqual(new SourceRange(0, 0), empty.FullRange);
        Assert.AreEqual("", empty.GetText(empty.FullRange));
        Assert.AreEqual(int.MaxValue, new SourceRange(int.MaxValue, 0).End);
        Assert.AreEqual(int.MaxValue, new SourceRange(0, int.MaxValue).End);
    }

    [TestMethod]
    [DataRow(-1, 0)]
    [DataRow(0, -1)]
    [DataRow(int.MaxValue, 1)]
    [DataRow(int.MaxValue - 1, 2)]
    public void Invalid_or_overflowing_source_ranges_are_rejected(int start, int length) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SourceRange(start, length));

    [TestMethod]
    [DataRow(4, 0)]
    [DataRow(2, 2)]
    [DataRow(0, 4)]
    [DataRow(int.MaxValue, 0)]
    public void Source_text_rejects_ranges_outside_its_utf16_length(int start, int length) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new SourceTextSnapshot("abc", 0).GetText(new SourceRange(start, length)));

    [TestMethod]
    public void Source_text_rejects_null_text_and_negative_version()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new SourceTextSnapshot(null!, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SourceTextSnapshot("abc", -1));
    }

    [TestMethod]
    public void Snapshot_preserves_nonzero_surrogate_and_combining_source_ranges()
    {
        var source = new SourceTextSnapshot("xx\U0001F600a\u0308!", 19);
        var range = new SourceRange(2, 4);
        var run = CreateRun(range,
            [new GlyphPlacement(20, 26), new GlyphPlacement(21, 12.5), new GlyphPlacement(22, 0)],
            [new GlyphClusterLayout(new SourceRange(2, 2), 0, 1, 26),
             new GlyphClusterLayout(new SourceRange(4, 2), 1, 2, 12.5)]);
        var line = new LineLayout(range, TestBounds, 30.25, run.Advance, [run]);
        var snapshot = new LayoutSnapshot(source, TestBounds, [new FontFaceDescriptor("Test", "Regular")],
            [new BlockLayout(range, TestBounds, [line])]);

        Assert.AreEqual(range, snapshot.Blocks[0].Lines[0].Runs[0].Source);
        Assert.AreEqual("\U0001F600", source.GetText(run.Clusters[0].Source));
        Assert.AreEqual("a\u0308", source.GetText(run.Clusters[1].Source));
        Assert.AreEqual(38.5, run.Advance);
        Assert.AreEqual(new LayoutSummary(19, 7, 1, 1, 1, 1, 3, 2),
            LayoutSnapshotInspector.Inspect(snapshot));
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void Coordinates_must_be_finite(double value)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LayoutPoint(value, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LayoutPoint(0, value));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LayoutRect(value, 0, 1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LayoutRect(0, value, 1, 1));
    }

    [TestMethod]
    [DataRow(-1d)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void Rectangle_dimensions_must_be_finite_and_nonnegative(double value)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LayoutRect(0, 0, value, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LayoutRect(0, 0, 1, value));
    }

    [TestMethod]
    public void Rectangle_edges_must_not_overflow()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new LayoutRect(double.MaxValue, 0, double.MaxValue, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new LayoutRect(0, double.MaxValue, 1, double.MaxValue));
    }

    [TestMethod]
    public void Negative_coordinates_and_glyph_offsets_and_zero_sizes_are_legal()
    {
        var point = new LayoutPoint(-12.5, -8.25);
        var rect = new LayoutRect(point.X, point.Y, 0, 0);
        var glyph = new GlyphPlacement(0, 0, -2.5, -3.75);
        var run = CreateRun(new SourceRange(0, 1), [glyph],
            [new GlyphClusterLayout(new SourceRange(0, 1), 0, 1, 0)], baseline: point);
        var line = new LineLayout(run.Source, rect, point.Y, 0, [run]);
        var snapshot = new LayoutSnapshot(new SourceTextSnapshot("a", 0), rect,
            [new FontFaceDescriptor("Test", "Regular")], [new BlockLayout(run.Source, rect, [line])]);

        Assert.AreEqual(point, snapshot.Blocks[0].Lines[0].Runs[0].BaselineOrigin);
        Assert.AreEqual(-2.5, run.Glyphs[0].AdvanceOffset);
        Assert.AreEqual(-3.75, run.Glyphs[0].AscenderOffset);
        Assert.AreEqual(0, run.Glyphs[0].Index);
        Assert.AreEqual(0d, run.Advance);
        Assert.IsTrue(rect.Contains(rect));
        Assert.AreEqual((int)ushort.MaxValue, new GlyphPlacement(ushort.MaxValue, 1).Index);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(ushort.MaxValue + 1)]
    [DataRow(int.MaxValue)]
    public void Glyph_indices_must_fit_unsigned_16_bit_ids(int index) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GlyphPlacement(index, 0));

    [TestMethod]
    [DataRow(-1d)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void Glyph_and_cluster_advances_must_be_finite_and_nonnegative(double value)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GlyphPlacement(1, value));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new GlyphClusterLayout(new SourceRange(0, 1), 0, 1, value));
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void Glyph_offsets_must_be_finite(double value)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GlyphPlacement(1, 1, value, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GlyphPlacement(1, 1, 0, value));
    }

    [TestMethod]
    [DataRow(-1, 1)]
    [DataRow(0, 0)]
    [DataRow(0, -1)]
    [DataRow(int.MaxValue, 1)]
    [DataRow(int.MaxValue - 1, 2)]
    public void Cluster_glyph_spans_must_be_positive_and_not_overflow(int start, int count) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new GlyphClusterLayout(new SourceRange(0, 1), start, count, 0));

    [TestMethod]
    [DataRow(0d)]
    [DataRow(-1d)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void Run_font_size_must_be_finite_and_positive(double size) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SingleRun(fontSize: size));

    [TestMethod]
    public void Negative_font_reference_is_rejected_at_run_construction() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SingleRun(fontIndex: -1));

    [TestMethod]
    [DataRow(1)]
    [DataRow(int.MaxValue)]
    public void Snapshot_rejects_unresolved_font_references(int fontIndex) =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            SnapshotForRun(SingleRun(fontIndex: fontIndex), [new FontFaceDescriptor("Test", "Regular")]));

    [TestMethod]
    public void A_run_cannot_resolve_a_font_from_an_empty_font_table() =>
        Assert.ThrowsExactly<ArgumentException>(() => SnapshotForRun(SingleRun(), []));

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t")]
    public void Font_names_and_run_locale_cannot_be_blank(string value)
    {
        Assert.ThrowsExactly<ArgumentException>(() => new FontFaceDescriptor(value, "Regular"));
        Assert.ThrowsExactly<ArgumentException>(() => new FontFaceDescriptor("Test", value));
        Assert.ThrowsExactly<ArgumentException>(() => SingleRun(locale: value));
    }

    [TestMethod]
    public void Font_names_and_run_locale_cannot_be_null()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new FontFaceDescriptor(null!, "Regular"));
        Assert.ThrowsExactly<ArgumentNullException>(() => new FontFaceDescriptor("Test", null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => SingleRun(locale: null!));
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    [DataRow(19.75)]
    [DataRow(60.25)]
    public void Line_baseline_must_be_finite_and_inside_its_bounds(double baseline) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new LineLayout(new SourceRange(0, 0), TestBounds, baseline, 0, []));

    [TestMethod]
    [DataRow(-1d)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void Line_advance_must_be_finite_and_nonnegative(double advance) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new LineLayout(new SourceRange(0, 0), TestBounds, 30, advance, []));

    [TestMethod]
    public void Line_spacing_and_run_baselines_are_independent_of_glyph_advances()
    {
        var first = SingleRun(new SourceRange(1, 1), baseline: new LayoutPoint(12.5, 30.25));
        var second = SingleRun(new SourceRange(2, 1), baseline: new LayoutPoint(25.125, 31.75));
        var line = new LineLayout(new SourceRange(1, 2), TestBounds, 32.5, 99.125, [first, second]);

        Assert.AreEqual(99.125, line.Advance);
        Assert.AreNotEqual(line.Advance, line.Runs.Sum(run => run.Advance));
        Assert.AreEqual(32.5, line.Baseline);
        Assert.AreEqual(30.25, line.Runs[0].BaselineOrigin.Y);
        Assert.AreEqual(31.75, line.Runs[1].BaselineOrigin.Y);
        Assert.AreEqual(20d, new LineLayout(line.Source, TestBounds, TestBounds.Y, 0, []).Baseline);
        Assert.AreEqual(60d, new LineLayout(line.Source, TestBounds, TestBounds.Bottom, 0, []).Baseline);
    }

    [TestMethod]
    public void Child_source_ranges_must_be_contained_in_their_parents()
    {
        var run = SingleRun(new SourceRange(3, 1));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new LineLayout(new SourceRange(1, 2), TestBounds, 30, 0, [run]));

        var line = new LineLayout(run.Source, TestBounds, 30, 0, [run]);
        Assert.ThrowsExactly<ArgumentException>(() =>
            new BlockLayout(new SourceRange(1, 2), TestBounds, [line]));

        var block = new BlockLayout(new SourceRange(4, 1), TestBounds, []);
        Assert.ThrowsExactly<ArgumentException>(() =>
            new LayoutSnapshot(new SourceTextSnapshot("abcd", 0), TestBounds, [], [block]));
    }

    [TestMethod]
    [DataRow("left")]
    [DataRow("top")]
    [DataRow("right")]
    [DataRow("bottom")]
    public void Line_and_block_bounds_must_be_contained_on_every_edge(string edge)
    {
        var outside = edge switch
        {
            "left" => new LayoutRect(9.75, 20, 20, 20),
            "top" => new LayoutRect(10, 19.75, 20, 20),
            "right" => new LayoutRect(100.25, 20, 10, 20),
            "bottom" => new LayoutRect(10, 50.25, 20, 10),
            _ => throw new ArgumentException(nameof(edge))
        };
        var range = new SourceRange(1, 1);
        var line = new LineLayout(range, outside, outside.Y, 0, []);

        Assert.ThrowsExactly<ArgumentException>(() => new BlockLayout(range, TestBounds, [line]));
        var block = new BlockLayout(range, outside, [line]);
        Assert.ThrowsExactly<ArgumentException>(() =>
            new LayoutSnapshot(new SourceTextSnapshot("abc", 0), TestBounds, [], [block]));
    }

    [TestMethod]
    public void Rtl_clusters_remain_source_ordered_with_descending_glyph_spans()
    {
        var clusters = new[]
        {
            new GlyphClusterLayout(new SourceRange(4, 1), 3, 1, 8),
            new GlyphClusterLayout(new SourceRange(5, 2), 1, 2, 7),
            new GlyphClusterLayout(new SourceRange(7, 1), 0, 1, 6)
        };
        var run = CreateRun(new SourceRange(4, 4),
            [new GlyphPlacement(10, 6), new GlyphPlacement(11, 7),
             new GlyphPlacement(12, 0), new GlyphPlacement(13, 8)],
            clusters, bidiLevel: 1, baseline: new LayoutPoint(90.125, 30.25), locale: "he-IL");

        Assert.AreEqual(1u, run.BidiLevel);
        Assert.AreEqual(new LayoutPoint(90.125, 30.25), run.BaselineOrigin);
        CollectionAssert.AreEqual(new[] { 4, 5, 7 }, run.Clusters.Select(cluster => cluster.Source.Start).ToArray());
        CollectionAssert.AreEqual(new[] { 3, 1, 0 }, run.Clusters.Select(cluster => cluster.GlyphStart).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2, 1 }, run.Clusters.Select(cluster => cluster.GlyphCount).ToArray());
        CollectionAssert.AreEqual(new[] { 10, 11, 12, 13 }, run.Glyphs.Select(glyph => glyph.Index).ToArray());
        Assert.AreEqual(21d, run.Advance);
        Assert.AreEqual(run.Advance, run.Clusters.Sum(cluster => cluster.Advance));
    }

    [TestMethod]
    public void Ligature_and_combining_clusters_cover_each_glyph_once_with_fractional_widths()
    {
        var run = CreateRun(new SourceRange(10, 5),
            [new GlyphPlacement(30, 14.5), new GlyphPlacement(31, 12.125), new GlyphPlacement(32, 0)],
            [new GlyphClusterLayout(new SourceRange(10, 3), 0, 1, 14.5),
             new GlyphClusterLayout(new SourceRange(13, 2), 1, 2, 12.125)]);

        Assert.AreEqual(new SourceRange(10, 3), run.Clusters[0].Source);
        Assert.AreEqual(1, run.Clusters[0].GlyphCount);
        Assert.AreEqual(new SourceRange(13, 2), run.Clusters[1].Source);
        Assert.AreEqual(2, run.Clusters[1].GlyphCount);
        Assert.AreEqual(12.125, run.Clusters[1].Advance);
        Assert.AreEqual(26.625, run.Advance);
        Assert.AreEqual(run.Source.Length, run.Clusters.Sum(cluster => cluster.Source.Length));
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, run.Clusters
            .SelectMany(cluster => Enumerable.Range(cluster.GlyphStart, cluster.GlyphCount)).ToArray());
    }

    [TestMethod]
    public void Ordered_clusters_may_leave_source_gaps_while_covering_all_glyphs()
    {
        var run = CreateRun(new SourceRange(10, 8),
            [new GlyphPlacement(1, 7.25), new GlyphPlacement(2, 8.5)],
            [new GlyphClusterLayout(new SourceRange(11, 1), 0, 1, 7.25),
             new GlyphClusterLayout(new SourceRange(15, 1), 1, 1, 8.5)]);

        Assert.AreEqual(new SourceRange(10, 8), run.Source);
        Assert.AreEqual(2, run.Clusters.Sum(cluster => cluster.Source.Length));
        Assert.AreEqual(15.75, run.Advance);
    }

    [TestMethod]
    public void Generated_glyph_clusters_may_use_empty_source_anchors_at_boundaries()
    {
        var range = new SourceRange(4, 2);
        var run = CreateRun(range,
            [new GlyphPlacement(1, 3), new GlyphPlacement(2, 4), new GlyphPlacement(3, 5)],
            [new GlyphClusterLayout(new SourceRange(4, 0), 0, 1, 3),
             new GlyphClusterLayout(range, 1, 1, 4),
             new GlyphClusterLayout(new SourceRange(6, 0), 2, 1, 5)]);

        Assert.AreEqual(12d, run.Advance);
        Assert.AreEqual(0, run.Clusters[0].Source.Length);
        Assert.AreEqual(0, run.Clusters[2].Source.Length);
    }

    [TestMethod]
    public void Multiple_generated_clusters_can_share_an_empty_eof_source_range()
    {
        var source = new SourceTextSnapshot("abc", 3);
        var eof = new SourceRange(3, 0);
        var run = CreateRun(eof, [new GlyphPlacement(1, 3), new GlyphPlacement(2, 4)],
            [new GlyphClusterLayout(eof, 0, 1, 3), new GlyphClusterLayout(eof, 1, 1, 4)]);
        var line = new LineLayout(eof, TestBounds, 30, 7, [run]);
        var snapshot = new LayoutSnapshot(source, TestBounds, [new FontFaceDescriptor("Test", "Regular")],
            [new BlockLayout(eof, TestBounds, [line])]);

        Assert.AreEqual("", source.GetText(run.Source));
        Assert.AreEqual(7d, run.Advance);
        Assert.AreEqual(new LayoutSummary(3, 3, 1, 1, 1, 1, 2, 2),
            LayoutSnapshotInspector.Inspect(snapshot));
    }

    [TestMethod]
    public void Runs_cannot_be_empty_or_have_no_clusters()
    {
        var range = new SourceRange(0, 1);
        var glyph = new GlyphPlacement(1, 3);
        var cluster = new GlyphClusterLayout(range, 0, 1, 3);

        Assert.ThrowsExactly<ArgumentException>(() => CreateRun(range, [], []));
        Assert.ThrowsExactly<ArgumentException>(() => CreateRun(range, [], [cluster]));
        Assert.ThrowsExactly<ArgumentException>(() => CreateRun(range, [glyph], []));
    }

    [TestMethod]
    public void Incomplete_glyph_coverage_is_rejected() =>
        Assert.ThrowsExactly<ArgumentException>(() => CreateRun(new SourceRange(0, 2),
            [new GlyphPlacement(1, 3), new GlyphPlacement(2, 4)],
            [new GlyphClusterLayout(new SourceRange(0, 2), 0, 1, 3)]));

    [TestMethod]
    public void Overlapping_glyph_clusters_are_rejected() =>
        Assert.ThrowsExactly<ArgumentException>(() => CreateRun(new SourceRange(0, 2),
            [new GlyphPlacement(1, 3)],
            [new GlyphClusterLayout(new SourceRange(0, 1), 0, 1, 3),
             new GlyphClusterLayout(new SourceRange(1, 1), 0, 1, 3)]));

    [TestMethod]
    public void Overlapping_source_clusters_are_rejected() =>
        Assert.ThrowsExactly<ArgumentException>(() => CreateRun(new SourceRange(0, 3),
            [new GlyphPlacement(1, 3), new GlyphPlacement(2, 4)],
            [new GlyphClusterLayout(new SourceRange(0, 2), 0, 1, 3),
             new GlyphClusterLayout(new SourceRange(1, 2), 1, 1, 4)]));

    [TestMethod]
    public void Source_clusters_cannot_be_reordered_even_when_glyph_coverage_is_complete() =>
        Assert.ThrowsExactly<ArgumentException>(() => CreateRun(new SourceRange(0, 3),
            [new GlyphPlacement(1, 3), new GlyphPlacement(2, 4)],
            [new GlyphClusterLayout(new SourceRange(2, 1), 0, 1, 3),
             new GlyphClusterLayout(new SourceRange(0, 1), 1, 1, 4)]));

    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    public void Cluster_source_must_be_inside_the_run(int start) =>
        Assert.ThrowsExactly<ArgumentException>(() => CreateRun(new SourceRange(2, 2),
            [new GlyphPlacement(1, 3)], [new GlyphClusterLayout(new SourceRange(start, 1), 0, 1, 3)]));

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(0, 2)]
    public void Cluster_glyph_range_must_be_inside_the_run(int start, int count) =>
        Assert.ThrowsExactly<ArgumentException>(() => CreateRun(new SourceRange(0, 1),
            [new GlyphPlacement(1, 3)], [new GlyphClusterLayout(new SourceRange(0, 1), start, count, 3)]));

    [TestMethod]
    public void Cluster_advance_must_match_its_glyph_span() =>
        Assert.ThrowsExactly<ArgumentException>(() => CreateRun(new SourceRange(0, 1),
            [new GlyphPlacement(1, 3), new GlyphPlacement(2, 4)],
            [new GlyphClusterLayout(new SourceRange(0, 1), 0, 2, 3)]));

    [TestMethod]
    public void Summed_run_advance_must_remain_finite() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateRun(new SourceRange(0, 2),
            [new GlyphPlacement(1, double.MaxValue), new GlyphPlacement(2, double.MaxValue)],
            [new GlyphClusterLayout(new SourceRange(0, 1), 0, 1, double.MaxValue),
             new GlyphClusterLayout(new SourceRange(1, 1), 1, 1, double.MaxValue)]));

    [TestMethod]
    public void Null_constructor_inputs_are_rejected()
    {
        var range = new SourceRange(0, 1);
        var source = new SourceTextSnapshot("a", 0);
        var glyph = new GlyphPlacement(1, 3);
        var cluster = new GlyphClusterLayout(range, 0, 1, 3);

        Assert.ThrowsExactly<ArgumentNullException>(() => CreateRun(range, null!, [cluster]));
        Assert.ThrowsExactly<ArgumentNullException>(() => CreateRun(range, [glyph], null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new LineLayout(range, TestBounds, 30, 0, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new BlockLayout(range, TestBounds, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new LayoutSnapshot(null!, TestBounds, [], []));
        Assert.ThrowsExactly<ArgumentNullException>(() => new LayoutSnapshot(source, TestBounds, null!, []));
        Assert.ThrowsExactly<ArgumentNullException>(() => new LayoutSnapshot(source, TestBounds, [], null!));
    }

    [TestMethod]
    public void Null_children_are_rejected_at_every_collection_level()
    {
        var range = new SourceRange(0, 1);
        var source = new SourceTextSnapshot("a", 0);

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            CreateRun(range, [new GlyphPlacement(1, 3)], [null!]));
        Assert.ThrowsExactly<ArgumentNullException>(() => new LineLayout(range, TestBounds, 30, 0, [null!]));
        Assert.ThrowsExactly<ArgumentNullException>(() => new BlockLayout(range, TestBounds, [null!]));
        Assert.ThrowsExactly<ArgumentNullException>(() => new LayoutSnapshot(source, TestBounds, [null!], []));
        Assert.ThrowsExactly<ArgumentNullException>(() => new LayoutSnapshot(source, TestBounds, [], [null!]));
    }

    [TestMethod]
    public void Empty_line_and_block_are_valid_at_eof()
    {
        var source = new SourceTextSnapshot("abc", 4);
        var eof = new SourceRange(source.Length, 0);
        var bounds = new LayoutRect(12.5, 30.25, 0, 0);
        var line = new LineLayout(eof, bounds, bounds.Y, 0, []);
        var block = new BlockLayout(eof, bounds, [line]);
        var snapshot = new LayoutSnapshot(source, bounds, [], [block]);

        Assert.IsTrue(line.Runs.IsEmpty);
        Assert.AreEqual(0d, line.Advance);
        Assert.AreEqual(new LayoutSummary(4, 3, 0, 1, 1, 0, 0, 0),
            LayoutSnapshotInspector.Inspect(snapshot));
        Assert.IsTrue(new BlockLayout(eof, bounds, []).Lines.IsEmpty);
    }

    [TestMethod]
    public void Empty_snapshot_has_zero_counts_and_a_deterministic_fingerprint()
    {
        var snapshot = new LayoutSnapshot(new SourceTextSnapshot("", 0), new LayoutRect(0, 0, 0, 0), [], []);
        var equivalent = new LayoutSnapshot(new SourceTextSnapshot("", 0), new LayoutRect(0, 0, 0, 0), [], []);

        Assert.IsTrue(snapshot.Fonts.IsEmpty);
        Assert.IsTrue(snapshot.Blocks.IsEmpty);
        Assert.AreEqual(new LayoutSummary(0, 0, 0, 0, 0, 0, 0, 0),
            LayoutSnapshotInspector.Inspect(snapshot));
        Assert.AreEqual(LayoutSnapshotInspector.Fingerprint(snapshot),
            LayoutSnapshotInspector.Fingerprint(equivalent));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Every_input_array_is_copied_including_marshaled_immutable_array_storage(bool marshalInputs)
    {
        var source = new SourceTextSnapshot("Xaffib", 5);
        var range = new SourceRange(1, 5);
        var firstGlyph = new GlyphPlacement(10, 7.25, -0.375, 0.625);
        var firstCluster = new GlyphClusterLayout(new SourceRange(1, 1), 0, 1, 7.25);
        var glyphs = new[]
        {
            firstGlyph, new GlyphPlacement(11, 14.5), new GlyphPlacement(12, 6.75)
        };
        var clusters = new[]
        {
            firstCluster,
            new GlyphClusterLayout(new SourceRange(2, 3), 1, 1, 14.5),
            new GlyphClusterLayout(new SourceRange(5, 1), 2, 1, 6.75)
        };
        var run = CreateRun(range, Input(glyphs, marshalInputs), Input(clusters, marshalInputs));
        var runs = new[] { run };
        var line = new LineLayout(range, TestBounds, 30.25, 31.125, Input(runs, marshalInputs));
        var lines = new[] { line };
        var block = new BlockLayout(range, TestBounds, Input(lines, marshalInputs));
        var blocks = new[] { block };
        var font = new FontFaceDescriptor("Test", "Regular");
        var fonts = new[] { font };
        var snapshot = new LayoutSnapshot(source, TestBounds,
            Input(fonts, marshalInputs), Input(blocks, marshalInputs));
        var expectedJson = LayoutSnapshotInspector.ToJson(snapshot);
        var expectedFingerprint = LayoutSnapshotInspector.Fingerprint(snapshot);
        var expectedSummary = LayoutSnapshotInspector.Inspect(snapshot);
        var expectedGlyphs = glyphs.ToArray();
        var expectedClusters = clusters.ToArray();

        // Mutate only caller-owned storage, never storage obtained from snapshot getters.
        Array.Fill(glyphs, new GlyphPlacement(99, 100, 20, -30));
        Array.Fill(clusters, null!);
        Array.Fill(runs, null!);
        Array.Fill(lines, null!);
        Array.Fill(blocks, null!);
        Array.Fill(fonts, null!);

        Assert.AreSame(source, snapshot.Source);
        Assert.AreSame(font, snapshot.Fonts[0]);
        Assert.AreSame(block, snapshot.Blocks[0]);
        Assert.AreSame(line, block.Lines[0]);
        Assert.AreSame(run, line.Runs[0]);
        Assert.AreSame(firstCluster, run.Clusters[0]);
        Assert.AreEqual(firstGlyph, run.Glyphs[0]);
        CollectionAssert.AreEqual(expectedGlyphs, run.Glyphs.ToArray());
        CollectionAssert.AreEqual(expectedClusters, run.Clusters.ToArray());
        Assert.AreEqual(28.5, run.Advance);
        Assert.AreEqual(expectedJson, LayoutSnapshotInspector.ToJson(snapshot));
        Assert.AreEqual(expectedFingerprint, LayoutSnapshotInspector.Fingerprint(snapshot));
        Assert.AreEqual(expectedSummary, LayoutSnapshotInspector.Inspect(snapshot));
    }

    private static IEnumerable<T> Input<T>(T[] array, bool marshalInputs)
    {
        if (marshalInputs) return ImmutableCollectionsMarshal.AsImmutableArray(array);
        return array;
    }

    private static GlyphRunLayout CreateRun(SourceRange source, IEnumerable<GlyphPlacement> glyphs,
        IEnumerable<GlyphClusterLayout> clusters, int fontIndex = 0, uint bidiLevel = 0,
        LayoutPoint? baseline = null, double fontSize = 26, string locale = "en-US") =>
        new(fontIndex, source, baseline ?? new LayoutPoint(12.5, 30.25), fontSize,
            bidiLevel, locale, glyphs, clusters);

    private static GlyphRunLayout SingleRun(SourceRange? source = null, int fontIndex = 0,
        LayoutPoint? baseline = null, double fontSize = 26, string locale = "en-US")
    {
        var range = source ?? new SourceRange(1, 1);
        return CreateRun(range, [new GlyphPlacement(17, 7.25, -0.375, 0.625)],
            [new GlyphClusterLayout(range, 0, 1, 7.25)], fontIndex,
            baseline: baseline, fontSize: fontSize, locale: locale);
    }

    private static LayoutSnapshot SnapshotForRun(GlyphRunLayout run, IEnumerable<FontFaceDescriptor> fonts)
    {
        var line = new LineLayout(run.Source, TestBounds, 30.25, run.Advance, [run]);
        return new LayoutSnapshot(new SourceTextSnapshot("abcdefg", 7), TestBounds, fonts,
            [new BlockLayout(run.Source, TestBounds, [line])]);
    }

    private static LayoutSnapshot CreateSnapshot(long version = 7, string text = "Xffi a\u0308",
        double snapshotWidth = 120.75, double baselineX = 13.75, double glyphOffset = -0.375,
        double glyphAdvance = 14.5, double lineAdvance = 42.125, string family = "Cambria",
        string locale = "en-US", uint bidiLevel = 0)
    {
        var source = new SourceTextSnapshot(text, version);
        var ligatureRange = new SourceRange(1, 3);
        var combiningRange = new SourceRange(5, 2);
        var ligature = CreateRun(ligatureRange, [new GlyphPlacement(30, glyphAdvance, glyphOffset, 0.625)],
            [new GlyphClusterLayout(ligatureRange, 0, 1, glyphAdvance)],
            bidiLevel: bidiLevel, baseline: new LayoutPoint(baselineX, 56.75), locale: locale);
        var combining = CreateRun(combiningRange,
            [new GlyphPlacement(31, 12.5), new GlyphPlacement(32, 0, -2.25, 1.125)],
            [new GlyphClusterLayout(combiningRange, 0, 2, 12.5)],
            fontIndex: 1, baseline: new LayoutPoint(30.125, 57.25));
        var lineRange = new SourceRange(1, 6);
        var line = new LineLayout(lineRange, new LayoutRect(12.5, 30.25, 80.5, 40.75),
            56.75, lineAdvance, [ligature, combining]);
        var block = new BlockLayout(lineRange, new LayoutRect(11.5, 29.5, 100.5, 60.75), [line]);
        var eof = new SourceRange(source.Length, 0);
        var emptyLine = new LineLayout(eof, new LayoutRect(12.5, 98.25, 0, 12.5), 105, 0, []);
        var emptyBlock = new BlockLayout(eof, new LayoutRect(11.5, 95.5, 100.5, 20), [emptyLine]);

        return new LayoutSnapshot(source, new LayoutRect(10.25, 20.5, snapshotWidth, 100.25),
            [new FontFaceDescriptor(family, "Regular"), new FontFaceDescriptor("Segoe UI", "Regular")],
            [block, emptyBlock]);
    }
}
