using MDEditor.Core.Text;
using MDEditor.Core.Markdown;
using MDEditor.Typesetting.Layout;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class ViewportAnchorMapTests
{
    [TestMethod]
    public void Insert_or_delete_above_viewport_preserves_the_same_text_anchor()
    {
        var original = new SourceTextSnapshot("前文\n目标段落\n后文", 1);
        var inserted = new SourceTextSnapshot("前文新增\n目标段落\n后文", 2);
        var offset = original.Text.IndexOf("目标", StringComparison.Ordinal);
        Assert.AreEqual(inserted.Text.IndexOf("目标", StringComparison.Ordinal),
            ViewportAnchorMap.Map(original, inserted, offset));
        Assert.AreEqual(offset, ViewportAnchorMap.Map(inserted, original,
            inserted.Text.IndexOf("目标", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Changed_anchor_clamps_to_replacement_end_and_same_text_is_identity()
    {
        var old = new SourceTextSnapshot("abcXYZend", 1);
        var next = new SourceTextSnapshot("abc12345end", 2);
        Assert.AreEqual(8, ViewportAnchorMap.Map(old, next, 4));
        Assert.AreEqual(0, ViewportAnchorMap.Map(old, next, 0));
        Assert.AreEqual(11, ViewportAnchorMap.Map(old, next, 9));
        Assert.AreEqual(4, ViewportAnchorMap.Map(old, new SourceTextSnapshot(old.Text, 3), 4));
    }

    [TestMethod]
    public void Projected_interaction_anchor_uses_markdown_source_not_shorter_display()
    {
        var before = MarkdownRichTextProjection.Create(new("**bold**\n\n`code`", 1)).Text;
        var after = MarkdownRichTextProjection.Create(new("**bold**\n\n`coXde`", 2)).Text;
        Assert.IsTrue(before.Source.Length > before.Display.Length);
        var beforeMap = new MarkdownProjectionInteractionMap(before,
            new TextInteractionMap(before.Display, TextSurface.Body, []));
        var afterMap = new MarkdownProjectionInteractionMap(after,
            new TextInteractionMap(after.Display, TextSurface.Body, []));
        var offset = before.Source.Text.IndexOf("code", StringComparison.Ordinal);
        Assert.IsTrue(offset > before.Display.Length);
        Assert.AreEqual(after.Source.Text.IndexOf("coXde", StringComparison.Ordinal),
            ViewportAnchorMap.Map(beforeMap, afterMap, offset));
    }
}
