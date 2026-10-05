using MDEditor.Native.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class GlyphClusterTests
{
    [TestMethod]
    public void Ligature_covers_multiple_source_units_with_one_advance()
    {
        var result = GlyphClusterDecoder.Decode(10, [0, 1, 1, 1, 2], [7.25f, 14.5f, 6.75f]);
        Assert.AreEqual(3, result.Count);
        Assert.AreEqual(new GlyphCluster(11, 3, 1, 1, 14.5), result[1]);
        Assert.AreEqual(28.5, result.Sum(cluster => cluster.Advance));
    }

    [TestMethod]
    public void Combining_cluster_keeps_mark_glyph_and_zero_advance()
    {
        var result = GlyphClusterDecoder.Decode(0, [0, 0, 2], [12.5f, 0, 8.25f]);
        Assert.AreEqual(new GlyphCluster(0, 2, 0, 2, 12.5), result[0]);
        Assert.AreEqual(20.75, result.Sum(cluster => cluster.Advance));
    }

    [TestMethod]
    public void Surrogate_pair_is_not_split()
    {
        var result = GlyphClusterDecoder.Decode(3, [0, 0], [26]);
        Assert.AreEqual(new GlyphCluster(3, 2, 0, 1, 26), result.Single());
    }

    [TestMethod]
    public void Rtl_map_uses_glyph_order_for_spans_and_source_order_for_offsets()
    {
        var result = GlyphClusterDecoder.Decode(4, [3, 1, 1, 0], [6, 7, 0, 8]);
        CollectionAssert.AreEqual(new[]
        {
            new GlyphCluster(4, 1, 3, 1, 8),
            new GlyphCluster(5, 2, 1, 2, 7),
            new GlyphCluster(7, 1, 0, 1, 6)
        }, result.ToArray());
    }

    [TestMethod]
    public void Empty_input_is_empty() => Assert.AreEqual(0, GlyphClusterDecoder.Decode(0, [], []).Count);

    [TestMethod]
    public void Fractional_advances_are_not_rounded_per_glyph()
    {
        var result = GlyphClusterDecoder.Decode(0, [0, 1, 2], [0.125f, 1.375f, 2.0625f]);
        Assert.AreEqual(3.5625, result.Sum(cluster => cluster.Advance));
    }

    [TestMethod]
    public void Map_cannot_repeat_a_cluster_noncontiguously() =>
        Assert.ThrowsExactly<ArgumentException>(() => GlyphClusterDecoder.Decode(0, [0, 1, 0], [1, 2]));

    [TestMethod]
    [DataRow(-1)]
    [DataRow(int.MaxValue)]
    public void Invalid_source_offset_is_rejected(int start) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GlyphClusterDecoder.Decode(start, [0], [1]));

    [TestMethod]
    public void Out_of_range_map_is_rejected() =>
        Assert.ThrowsExactly<ArgumentException>(() => GlyphClusterDecoder.Decode(0, [0, 2], [1, 2]));

    [TestMethod]
    public void Missing_first_glyph_is_rejected() =>
        Assert.ThrowsExactly<ArgumentException>(() => GlyphClusterDecoder.Decode(0, [1], [1, 2]));

    [TestMethod]
    public void Source_without_glyphs_is_rejected() =>
        Assert.ThrowsExactly<ArgumentException>(() => GlyphClusterDecoder.Decode(0, [0], []));

    [TestMethod]
    public void Glyphs_without_source_are_rejected() =>
        Assert.ThrowsExactly<ArgumentException>(() => GlyphClusterDecoder.Decode(0, [], [1]));

    [TestMethod]
    [DataRow(float.NaN)]
    [DataRow(float.PositiveInfinity)]
    [DataRow(-1f)]
    public void Invalid_advance_is_rejected(float advance) =>
        Assert.ThrowsExactly<ArgumentException>(() => GlyphClusterDecoder.Decode(0, [0], [advance]));

    [TestMethod]
    public void Null_arguments_are_rejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => GlyphClusterDecoder.Decode(0, null!, []));
        Assert.ThrowsExactly<ArgumentNullException>(() => GlyphClusterDecoder.Decode(0, [], null!));
    }
}
