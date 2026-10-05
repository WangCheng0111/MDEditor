using MDEditor.Typesetting.Layout;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class ViewportIntervalIndexTests
{
    [TestMethod]
    public void Deep_viewport_reads_only_nearby_nonoverlapping_lines()
    {
        var index = new ViewportIntervalIndex<int>(Enumerable.Range(0, 100_000)
            .Select(number => (number, number * 30d, number * 30d + 24)));
        var top = 91_000 * 30d + 12;
        var (start, end) = index.CandidateRange(top, top + 60);

        Assert.IsTrue(end - start <= 4);
        CollectionAssert.AreEqual(new[] { 91_000, 91_001, 91_002 },
            Enumerable.Range(start, end - start)
                .Where(position => index.Intersects(position, top, top + 60))
                .Select(position => index[position]).ToArray());
    }

    [TestMethod]
    public void Overlap_and_equal_top_preserve_all_visible_items_in_stable_order()
    {
        var index = new ViewportIntervalIndex<string>(new[]
        {
            ("after", 20d, 30d), ("first", 0d, 100d),
            ("second", 20d, 21d), ("before", -10d, -1d)
        });
        var (start, end) = index.CandidateRange(20, 22);
        CollectionAssert.AreEqual(new[] { "first", "after", "second" },
            Enumerable.Range(start, end - start)
                .Where(position => index.Intersects(position, 20, 22))
                .Select(position => index[position]).ToArray());
    }

    [TestMethod]
    public void Empty_and_invalid_ranges_are_defined()
    {
        var empty = new ViewportIntervalIndex<int>([]);
        Assert.AreEqual((0, 0), empty.CandidateRange(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.CandidateRange(2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewportIntervalIndex<int>(
            [(1, 4d, 3d)]));
    }
}
