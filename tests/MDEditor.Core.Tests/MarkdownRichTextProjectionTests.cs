using MDEditor.Core.Markdown;
using MDEditor.Core.Text;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MarkdownRichTextProjectionTests
{
    [TestMethod]
    public void Heading_and_nested_inline_styles_are_projected_without_rewriting_source()
    {
        var source = new SourceTextSnapshot("# A **bold *and italic***\nnext", 17);
        var rich = MarkdownRichTextProjection.Create(source);

        Assert.AreSame(source, rich.Text.Source);
        Assert.AreEqual(17L, rich.Text.Display.Version);
        Assert.AreEqual("A bold and italic\nnext", rich.Text.Display.Text);
        Assert.AreEqual(1, rich.HeadingLevelAt(new(0, "A bold and italic".Length)));
        Assert.AreEqual(0, rich.HeadingLevelAt(new("A bold and italic\n".Length, 4)));
        Assert.AreEqual(MarkdownVisualStyle.Strong | MarkdownVisualStyle.Emphasis,
            StyleAt(rich, rich.Text.Display.Text.IndexOf("italic", StringComparison.Ordinal)));
        Assert.AreEqual("# A **bold *and italic***\nnext", source.Text);
    }

    [TestMethod]
    public void Setext_headings_keep_level_and_newline_after_the_underline_is_folded()
    {
        var first = MarkdownRichTextProjection.Create(new("Title\n=====\nbody", 0));
        var second = MarkdownRichTextProjection.Create(new("Title\n-----\nbody", 0));

        Assert.AreEqual("Title\nbody", first.Text.Display.Text);
        Assert.AreEqual(1, first.HeadingLevelAt(new(0, 5)));
        Assert.AreEqual(2, second.HeadingLevelAt(new(0, 5)));
    }

    [TestMethod]
    public void Code_links_and_strikethrough_have_distinct_display_styles()
    {
        var rich = MarkdownRichTextProjection.Create(new("`code` [site](https://x.test) ~~old~~", 0));

        Assert.AreEqual("code site old", rich.Text.Display.Text);
        Assert.AreEqual(MarkdownVisualStyle.Code, StyleAt(rich, 0));
        Assert.AreEqual(MarkdownVisualStyle.Link, StyleAt(rich, 5));
        Assert.AreEqual(MarkdownVisualStyle.Strikethrough, StyleAt(rich, 10));
        Assert.AreEqual(MarkdownVisualStyle.None, StyleAt(rich, 4));
    }

    [TestMethod]
    public void Entering_and_leaving_formatting_changes_only_the_display_snapshot()
    {
        var source = new SourceTextSnapshot("a **bold** z", 9);
        var folded = MarkdownRichTextProjection.Create(source);
        var entered = MarkdownRichTextProjection.Create(source, 5);
        var left = MarkdownRichTextProjection.Create(source, 11);

        Assert.AreEqual("a bold z", folded.Text.Display.Text);
        Assert.AreEqual(source.Text, entered.Text.Display.Text);
        Assert.AreEqual(folded.Text.Display.Text, left.Text.Display.Text);
        Assert.AreSame(source, entered.Text.Source);
        Assert.AreEqual(MarkdownVisualStyle.Strong, StyleAt(entered, 4));
        Assert.AreEqual(MarkdownVisualStyle.None, StyleAt(entered, 2));
        var code = MarkdownRichTextProjection.Create(new("`code`", 9), 3);
        Assert.AreEqual(MarkdownVisualStyle.None, StyleAt(code, 0));
        Assert.AreEqual(MarkdownVisualStyle.Code, StyleAt(code, 1));
        Assert.AreEqual(MarkdownVisualStyle.None, StyleAt(code, 5));
    }

    [TestMethod]
    public void Paragraph_styles_are_relative_and_safe_when_a_paragraph_moves()
    {
        var rich = MarkdownRichTextProjection.Create(new("plain\n**bold** and `code`", 0));
        var second = new SourceRange("plain\n".Length, rich.Text.Display.Length - "plain\n".Length);
        var styles = rich.ParagraphStyles(second);

        Assert.AreEqual("plain\nbold and code", rich.Text.Display.Text);
        Assert.AreEqual(new SourceRange(0, 4), styles[0].Display);
        Assert.AreEqual(MarkdownVisualStyle.Strong, styles[0].Style);
        Assert.AreEqual(MarkdownVisualStyle.Code, styles[^1].Style);
        Assert.HasCount(0, rich.ParagraphStyles(new(0, 5)));
    }

    [TestMethod]
    public void Incomplete_syntax_and_math_source_remain_visible_and_unstyled()
    {
        var source = new SourceTextSnapshot("**unfinished and $x_i$", 4);
        var rich = MarkdownRichTextProjection.Create(source);

        Assert.AreEqual(source.Text, rich.Text.Display.Text);
        Assert.AreEqual(MarkdownVisualStyle.None, StyleAt(rich, 0));
        Assert.AreEqual(MarkdownVisualStyle.None, StyleAt(rich, source.Text.IndexOf('$')));
    }

    private static MarkdownVisualStyle StyleAt(MarkdownRichTextProjection rich, int displayOffset) =>
        rich.Styles.FirstOrDefault(span => span.Display.Start <= displayOffset &&
            displayOffset < span.Display.End).Style;
}
