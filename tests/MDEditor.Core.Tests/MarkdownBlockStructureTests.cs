using MDEditor.Core.Markdown;
using MDEditor.Core.Text;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MarkdownBlockStructureTests
{
    [TestMethod]
    public void Quote_nested_lists_tasks_and_ordered_markers_have_source_ranges()
    {
        const string text = "> - [x] 完成\n>   1) 子项\n+ item";
        var structure = MarkdownBlockStructure.Create(new(text, 5));

        Assert.AreEqual(MarkdownBlockKind.TaskList, structure.Blocks[0].Kind);
        Assert.AreEqual(1, structure.Blocks[0].QuoteDepth);
        Assert.IsTrue(structure.Blocks[0].TaskChecked);
        Assert.AreEqual("完成", text[structure.Blocks[0].Content.Start..structure.Blocks[0].Content.End]);
        Assert.AreEqual(MarkdownBlockKind.OrderedList, structure.Blocks[1].Kind);
        Assert.AreEqual(2, structure.Blocks[1].ListDepth);
        Assert.AreEqual(1, structure.Blocks[1].OrderedNumber);
        Assert.AreEqual(')', structure.Blocks[1].OrderedDelimiter);
        Assert.AreEqual(MarkdownBlockKind.BulletList, structure.Blocks[2].Kind);
        Assert.AreEqual('+', structure.Blocks[2].Bullet);
    }

    [TestMethod]
    public void Fences_thematic_breaks_and_indented_code_are_not_list_items()
    {
        var blocks = MarkdownBlockStructure.Create(new("```md\n- literal\n```\n---\n    - code\n- real", 0));

        for (var index = 0; index < 5; index++)
            Assert.AreEqual(MarkdownBlockKind.None, blocks.Blocks[index].Kind, $"line {index}");
        Assert.AreEqual(MarkdownBlockKind.BulletList, blocks.Blocks[5].Kind);
    }

    [TestMethod]
    public void Projected_markers_fold_and_reveal_without_changing_source()
    {
        var source = new SourceTextSnapshot("> quote\n- [ ] task\n  - child\n1. ordered", 42);
        var folded = MarkdownRichTextProjection.Create(source);
        var revealTask = MarkdownRichTextProjection.Create(source, source.Text.IndexOf("task", StringComparison.Ordinal));

        Assert.AreEqual("quote\ntask\nchild\nordered", folded.Text.Display.Text);
        Assert.AreEqual("quote\n- [ ] task\nchild\nordered", revealTask.Text.Display.Text);
        Assert.AreEqual(source.Text, revealTask.Text.Source.Text);
        Assert.AreEqual(42L, folded.Text.Display.Version);
        Assert.AreEqual(4, folded.Blocks.Length);
        Assert.IsTrue(folded.Blocks.All(block => block.PrefixHidden));
        Assert.IsFalse(revealTask.Blocks[1].PrefixHidden);
        Assert.AreEqual("☐", folded.Blocks[1].Marker);
    }

    [TestMethod]
    public void Empty_item_at_content_caret_reveals_its_marker()
    {
        var source = new SourceTextSnapshot("- \nnext", 0);
        var folded = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(source));
        var active = folded.RevealAt(2);

        Assert.AreEqual("\nnext", folded.Display.Text);
        Assert.AreEqual(source.Text, active.Display.Text);
    }

    [TestMethod]
    public void Full_visible_item_selection_includes_hidden_prefix_but_partial_does_not()
    {
        var projection = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(new("- hello world", 0)));
        Assert.AreEqual(new SourceRange(0, 13), projection.ToSourceRange(new(0, 11), true));
        Assert.AreEqual(new SourceRange(2, 5), projection.ToSourceRange(new(0, 5), true));
    }

    [TestMethod]
    public void Heading_inside_a_quote_keeps_its_actual_level()
    {
        var rich = MarkdownRichTextProjection.Create(new("> # Heading", 0));

        Assert.AreEqual("Heading", rich.Text.Display.Text);
        Assert.AreEqual(1, rich.HeadingLevelAt(new(0, 7)));
        Assert.AreEqual(MarkdownBlockKind.Quote, rich.BlockAt(new(0, 7))!.Value.Kind);
    }

    [TestMethod]
    public void Structure_respects_commonmark_ordered_list_interruption_rules()
    {
        var invalid = new SourceTextSnapshot("paragraph\n2. still paragraph", 0);
        var valid = new SourceTextSnapshot("paragraph\n1. list item", 0);
        var empty = new SourceTextSnapshot("- ", 0);

        Assert.IsFalse(MarkdownSyntaxParser.Parse(invalid).Descendants().Any(node =>
            node.Kind == MarkdownSyntaxKind.ListItem));
        Assert.IsTrue(MarkdownSyntaxParser.Parse(valid).Descendants().Any(node =>
            node.Kind == MarkdownSyntaxKind.ListItem));
        Assert.IsTrue(MarkdownSyntaxParser.Parse(empty).Descendants().Any(node =>
            node.Kind == MarkdownSyntaxKind.ListItem));
        Assert.AreEqual(MarkdownBlockKind.None, MarkdownBlockStructure.Create(invalid).Blocks[1].Kind);
        Assert.AreEqual(invalid.Text, MarkdownRichTextProjection.Create(invalid).Text.Display.Text);
    }

    [TestMethod]
    public void Parser_list_item_ranges_start_on_their_marker_line()
    {
        const string text = "> - parent\n>   - child";
        var items = MarkdownSyntaxParser.Parse(new(text, 0)).Descendants()
            .Where(node => node.Kind == MarkdownSyntaxKind.ListItem).ToArray();

        Assert.HasCount(2, items);
        Assert.AreEqual(2, items[0].Source.Start);
        Assert.AreEqual(text.IndexOf("- child", StringComparison.Ordinal), items[1].Source.Start);
    }
}
