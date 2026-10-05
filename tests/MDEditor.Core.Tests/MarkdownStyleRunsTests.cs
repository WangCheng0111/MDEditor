using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MarkdownStyleRunsTests
{
    [TestMethod]
    public void Unstyled_text_and_empty_ranges_keep_their_coordinates()
    {
        CollectionAssert.AreEqual(new[] { new MarkdownStyleSpan(new(20, 8), MarkdownVisualStyle.None) },
            MarkdownStyleRuns.Partition(new(20, 8), []).ToArray());
        Assert.IsEmpty(MarkdownStyleRuns.Partition(new(28, 0), []));
    }

    [TestMethod]
    public void Nested_styles_are_combined_without_losing_any_display_units()
    {
        var actual = MarkdownStyleRuns.Partition(new(10, 10),
            [new(new(12, 6), MarkdownVisualStyle.Strong),
             new(new(14, 2), MarkdownVisualStyle.Emphasis | MarkdownVisualStyle.Link)]);
        var expected = new[]
        {
            new MarkdownStyleSpan(new(10, 2), MarkdownVisualStyle.None),
            new MarkdownStyleSpan(new(12, 2), MarkdownVisualStyle.Strong),
            new MarkdownStyleSpan(new(14, 2), MarkdownVisualStyle.Strong | MarkdownVisualStyle.Emphasis | MarkdownVisualStyle.Link),
            new MarkdownStyleSpan(new(16, 2), MarkdownVisualStyle.Strong),
            new MarkdownStyleSpan(new(18, 2), MarkdownVisualStyle.None)
        };
        CollectionAssert.AreEqual(expected, actual.ToArray());
        Assert.AreEqual(10, actual.Sum(run => run.Display.Length));
    }

    [TestMethod]
    public void Styles_are_clipped_to_the_text_range_between_visual_atoms()
    {
        var actual = MarkdownStyleRuns.Partition(new(10, 5),
            [new(new(8, 4), MarkdownVisualStyle.Code),
             new(new(13, 8), MarkdownVisualStyle.Link),
             new(new(30, 4), MarkdownVisualStyle.Strong)]);
        CollectionAssert.AreEqual(new[]
        {
            new MarkdownStyleSpan(new(10, 2), MarkdownVisualStyle.Code),
            new MarkdownStyleSpan(new(12, 1), MarkdownVisualStyle.None),
            new MarkdownStyleSpan(new(13, 2), MarkdownVisualStyle.Link)
        }, actual.ToArray());
    }

    [TestMethod]
    public void Adjacent_equivalent_styles_share_one_run()
    {
        var actual = MarkdownStyleRuns.Partition(new(0, 8),
            [new(new(0, 4), MarkdownVisualStyle.Code),
             new(new(4, 4), MarkdownVisualStyle.Code)]);
        CollectionAssert.AreEqual(new[] { new MarkdownStyleSpan(new(0, 8), MarkdownVisualStyle.Code) },
            actual.ToArray());
    }
}
