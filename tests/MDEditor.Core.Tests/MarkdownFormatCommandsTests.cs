using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MarkdownFormatCommandsTests
{
    [TestMethod]
    [DataRow(MarkdownFormatKind.Strong, "**word**")]
    [DataRow(MarkdownFormatKind.Emphasis, "*word*")]
    [DataRow(MarkdownFormatKind.Strikethrough, "~~word~~")]
    [DataRow(MarkdownFormatKind.CodeSpan, "`word`")]
    [DataRow(MarkdownFormatKind.Link, "[word](https://example.com)")]
    public void Inline_command_changes_only_the_selected_source(MarkdownFormatKind kind, string expected)
    {
        var source = new SourceTextSnapshot("before word after $x+1$", 6);
        var edit = MarkdownFormatCommands.Plan(source, new(7, 4), kind)!;
        var changed = Apply(source, edit);

        Assert.AreEqual("before " + expected + " after $x+1$", changed);
        Assert.AreEqual("word", changed.Substring(edit.Selection.Start, edit.Selection.Length));
        Assert.AreEqual("before word after $x+1$", source.Text);
    }

    [TestMethod]
    [DataRow(MarkdownFormatKind.Strong, "**word**")]
    [DataRow(MarkdownFormatKind.Emphasis, "*word*")]
    [DataRow(MarkdownFormatKind.Strikethrough, "~~word~~")]
    [DataRow(MarkdownFormatKind.CodeSpan, "`word`")]
    [DataRow(MarkdownFormatKind.Link, "[word](https://example.com)")]
    public void A_second_command_removes_an_exact_existing_wrapper(MarkdownFormatKind kind, string wrapped)
    {
        var source = new SourceTextSnapshot(wrapped, 0);
        var selected = kind == MarkdownFormatKind.Link ? new SourceRange(1, 4) :
            new SourceRange(wrapped.IndexOf("word", StringComparison.Ordinal), 4);
        var edit = MarkdownFormatCommands.Plan(source, selected, kind)!;

        Assert.AreEqual("word", Apply(source, edit));
        Assert.AreEqual(new SourceRange(0, 4), edit.Selection);
    }

    [TestMethod]
    public void Empty_selection_creates_an_editable_pair_and_keeps_the_caret_inside()
    {
        var source = new SourceTextSnapshot("abc", 0);
        var strong = MarkdownFormatCommands.Plan(source, new(1, 0), MarkdownFormatKind.Strong)!;
        var link = MarkdownFormatCommands.Plan(source, new(1, 0), MarkdownFormatKind.Link)!;

        Assert.AreEqual("a****bc", Apply(source, strong));
        Assert.AreEqual(new SourceRange(3, 0), strong.Selection);
        Assert.AreEqual("a[link](https://example.com)bc", Apply(source, link));
        Assert.AreEqual(new SourceRange(2, 4), link.Selection);
    }

    [TestMethod]
    public void Toggle_inside_an_existing_strong_run_unwraps_it_and_keeps_the_caret()
    {
        var source = new SourceTextSnapshot("a **bold** z", 3);
        var edit = MarkdownFormatCommands.Plan(source, new(5, 0), MarkdownFormatKind.Strong)!;

        Assert.AreEqual("a bold z", Apply(source, edit));
        Assert.AreEqual(new SourceRange(3, 0), edit.Selection);
    }

    [TestMethod]
    public void Heading_changes_only_its_marker_and_can_return_to_paragraph()
    {
        var source = new SourceTextSnapshot("lead\n  ## Title *kept*\ntail", 0);
        var offset = source.Text.IndexOf("Title", StringComparison.Ordinal);
        var h1 = MarkdownFormatCommands.Plan(source, new(offset, 0), MarkdownFormatKind.Heading1)!;
        var changed = Apply(source, h1);
        Assert.AreEqual("lead\n  # Title *kept*\ntail", changed);
        Assert.AreEqual(changed.IndexOf("Title", StringComparison.Ordinal), h1.Selection.Start);

        var plain = MarkdownFormatCommands.Plan(new SourceTextSnapshot(changed, 1), h1.Selection,
            MarkdownFormatKind.Paragraph)!;
        Assert.AreEqual("lead\n  Title *kept*\ntail", Apply(new SourceTextSnapshot(changed, 1), plain));
    }

    [TestMethod]
    public void Heading_toggle_does_not_touch_other_lines_or_original_line_endings()
    {
        var source = new SourceTextSnapshot("one\r\ntwo\r\n**three**", 0);
        var edit = MarkdownFormatCommands.Plan(source, new(5, 0), MarkdownFormatKind.Heading2)!;

        Assert.AreEqual("one\r\n## two\r\n**three**", Apply(source, edit));
        Assert.AreEqual("one\r\ntwo\r\n**three**", source.Text);
    }

    [TestMethod]
    public void All_six_heading_levels_are_available_without_rewriting_the_paragraph()
    {
        for (var level = 1; level <= 6; level++)
        {
            var source = new SourceTextSnapshot("Title *kept*", level);
            var kind = (MarkdownFormatKind)((int)MarkdownFormatKind.Heading1 + level - 1);
            var edit = MarkdownFormatCommands.Plan(source, new(2, 0), kind)!;
            Assert.AreEqual(new string('#', level) + " Title *kept*", Apply(source, edit));
        }
    }

    [TestMethod]
    public void Multiline_inline_format_is_rejected_without_a_partial_edit()
    {
        var source = new SourceTextSnapshot("one\ntwo", 0);
        Assert.IsNull(MarkdownFormatCommands.Plan(source, source.FullRange,
            MarkdownFormatKind.Strong));
    }

    [TestMethod]
    public void Command_rejects_out_of_range_selection()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MarkdownFormatCommands.Plan(
            new SourceTextSnapshot("x", 0), new(1, 1), MarkdownFormatKind.Strong));
    }

    [TestMethod]
    public void Formatting_is_one_undoable_source_edit_and_preserves_unrelated_markdown()
    {
        var buffer = new StyledDocumentBuffer("keep [link](https://x.test) and word $x+1$",
            [new(0, 0)], 30);
        var history = new TextEditHistory(buffer);
        var before = buffer.Capture();
        var word = before.Source.Text.IndexOf("word", StringComparison.Ordinal);
        var original = new TextSelection(
            new(TextSurface.Body, 30, word, CaretAffinity.Downstream),
            new(TextSurface.Body, 30, word + 4, CaretAffinity.Upstream));
        var plan = MarkdownFormatCommands.Plan(before.Source, original.Range, MarkdownFormatKind.Strong)!;
        var editRange = new TextSelection(
            original.Anchor with { Offset = plan.Replace.Start },
            original.Focus with { Offset = plan.Replace.End });
        var edit = TextEditingOperations.Replace(buffer, editRange, plan.Text);
        var afterSelection = new TextSelection(
            new(TextSurface.Body, edit.Change.NewVersion, plan.Selection.Start, CaretAffinity.Downstream),
            new(TextSurface.Body, edit.Change.NewVersion, plan.Selection.End, CaretAffinity.Upstream));
        history.Record(before, original, edit with { Selection = afterSelection },
            HistoryEditKind.Replace, DateTimeOffset.UtcNow);

        Assert.AreEqual("keep [link](https://x.test) and **word** $x+1$", buffer.Capture().Source.Text);
        Assert.AreEqual(original.Range, history.Undo()!.Value.Range);
        Assert.AreEqual(before.Source.Text, buffer.Capture().Source.Text);
        Assert.AreEqual(afterSelection.Range, history.Redo()!.Value.Range);
        Assert.AreEqual("keep [link](https://x.test) and **word** $x+1$", buffer.Capture().Source.Text);
    }

    private static string Apply(SourceTextSnapshot source, MarkdownSourceEdit edit) =>
        string.Concat(source.Text.AsSpan(0, edit.Replace.Start), edit.Text.AsSpan(),
            source.Text.AsSpan(edit.Replace.End));
}
