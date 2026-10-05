using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MarkdownBlockCommandsTests
{
    [TestMethod]
    public void Enter_continues_bullet_quote_ordered_and_resets_task_state()
    {
        Assert.AreEqual("- item\n- ", Enter("- item"));
        Assert.AreEqual("> note\n> ", Enter("> note"));
        Assert.AreEqual("9) item\n10) ", Enter("9) item"));
        Assert.AreEqual("- [x] done\n- [ ] ", Enter("- [x] done"));
        Assert.AreEqual(">   - child\n>   - ", Enter(">   - child"));
    }

    [TestMethod]
    public void Enter_inside_item_splits_at_source_caret()
    {
        var source = new SourceTextSnapshot("- alphaomega", 0);
        var edit = MarkdownBlockCommands.Enter(source, new(7, 0))!;

        Assert.AreEqual("- alpha\n- omega", Apply(source, edit));
        Assert.AreEqual(new SourceRange(10, 0), edit.Selection);
    }

    [TestMethod]
    public void Enter_on_empty_item_or_quote_exits_one_container_without_newline()
    {
        Assert.AreEqual("", Enter("- "));
        Assert.AreEqual("> ", Enter("> - [ ] "));
        Assert.AreEqual("", Enter("> "));
        Assert.AreEqual("> ", Enter("> > "));
    }

    [TestMethod]
    public void Enter_keeps_existing_crlf_or_cr_endings()
    {
        var crlf = new SourceTextSnapshot("> a\r\n> b", 0);
        var cr = new SourceTextSnapshot("- a\r- b", 0);

        Assert.AreEqual("> a\r\n> \r\n> b", Apply(crlf, MarkdownBlockCommands.Enter(crlf, new(3, 0))!));
        Assert.AreEqual("- a\r- \r- b", Apply(cr, MarkdownBlockCommands.Enter(cr, new(3, 0))!));
    }

    [TestMethod]
    public void Enter_between_ordered_siblings_renumbers_following_items_but_not_nested_children()
    {
        var source = new SourceTextSnapshot("1. alpha\n  1. child\n2. beta\n3. gamma", 0);
        var edit = MarkdownBlockCommands.Enter(source, new(8, 0))!;

        Assert.AreEqual("1. alpha\n2. \n  1. child\n3. beta\n4. gamma", Apply(source, edit));
        Assert.AreEqual(new SourceRange(12, 0), edit.Selection);
    }

    [TestMethod]
    public void Backspace_at_content_start_removes_task_then_list_then_quote()
    {
        var task = new SourceTextSnapshot("> - [ ] item", 0);
        var withoutTask = Apply(task, MarkdownBlockCommands.Backspace(task, 8)!);
        var list = new SourceTextSnapshot(withoutTask, 1);
        var withoutList = Apply(list, MarkdownBlockCommands.Backspace(list, 4)!);
        var quote = new SourceTextSnapshot(withoutList, 2);

        Assert.AreEqual("> - item", withoutTask);
        Assert.AreEqual("> item", withoutList);
        Assert.AreEqual("item", Apply(quote, MarkdownBlockCommands.Backspace(quote, 2)!));
    }

    [TestMethod]
    public void Tab_and_shift_tab_move_a_nested_subtree_together()
    {
        var source = new SourceTextSnapshot("- parent\n  - child\n    - grandchild\n- next", 0);
        var indented = MarkdownBlockCommands.Indent(source, 2, outdent: false)!;
        Assert.AreEqual("  - parent\n    - child\n      - grandchild\n- next", Apply(source, indented));
        var parsed = MarkdownBlockStructure.Create(new(Apply(source, indented), 1));
        Assert.AreEqual(MarkdownBlockKind.BulletList, parsed.Blocks[0].Kind);
        Assert.AreEqual(2, parsed.Blocks[0].ListDepth);
        Assert.AreEqual(3, parsed.Blocks[1].ListDepth);
        Assert.AreEqual(new SourceRange(4, 0), indented.Selection);
        var next = new SourceTextSnapshot(Apply(source, indented), 1);
        var outdented = MarkdownBlockCommands.Indent(next, 4, outdent: true)!;
        Assert.AreEqual(source.Text, Apply(next, outdented));
    }

    [TestMethod]
    public void Quote_tab_nests_and_shift_tab_removes_one_level()
    {
        var source = new SourceTextSnapshot("> text", 0);
        var nested = MarkdownBlockCommands.Indent(source, 2, outdent: false)!;
        Assert.AreEqual("> > text", Apply(source, nested));
        var next = new SourceTextSnapshot(Apply(source, nested), 1);
        Assert.AreEqual("> text", Apply(next, MarkdownBlockCommands.Indent(next, 4, outdent: true)!));
    }

    [TestMethod]
    public void Tab_on_a_multiline_selection_indents_each_line_once_and_preserves_newlines()
    {
        var source = new SourceTextSnapshot("- parent\r\n  - child\r\nplain", 0);
        var selection = new SourceRange(0, source.Text.IndexOf("plain", StringComparison.Ordinal));
        var indented = MarkdownBlockCommands.Indent(source, selection, outdent: false)!;

        Assert.AreEqual("  - parent\r\n    - child\r\nplain", Apply(source, indented));
        Assert.AreEqual(new SourceRange(2, selection.Length + 2), indented.Selection);
        var changed = new SourceTextSnapshot(Apply(source, indented), 1);
        Assert.AreEqual(source.Text, Apply(changed,
            MarkdownBlockCommands.Indent(changed, indented.Selection, outdent: true)!));
    }

    [TestMethod]
    public void Toolbar_commands_change_only_current_prefix_and_task_check()
    {
        var source = new SourceTextSnapshot("plain\nother", 0);
        var bullet = MarkdownBlockCommands.Format(source, new(2, 0), MarkdownBlockCommand.BulletList)!;
        Assert.AreEqual("- plain\nother", Apply(source, bullet));
        var task = new SourceTextSnapshot("- [ ] task\nother", 1);
        var toggled = MarkdownBlockCommands.Format(task, new(6, 0), MarkdownBlockCommand.ToggleTask)!;
        Assert.AreEqual("- [x] task\nother", Apply(task, toggled));
        var quoted = MarkdownBlockCommands.Format(task, new(6, 0), MarkdownBlockCommand.Quote)!;
        Assert.AreEqual("> - [ ] task\nother", Apply(task, quoted));
        Assert.AreEqual(task.Text, Apply(new SourceTextSnapshot(Apply(task, quoted), 2),
            MarkdownBlockCommands.Format(new(Apply(task, quoted), 2), new(8, 0), MarkdownBlockCommand.Quote)!));
    }

    [TestMethod]
    public void No_structure_inside_fenced_code_and_plain_lines_fall_back()
    {
        var source = new SourceTextSnapshot("```md\n- literal\n```\nplain", 0);
        var caret = source.Text.IndexOf("literal", StringComparison.Ordinal);

        Assert.IsNull(MarkdownBlockCommands.Enter(source, new(caret, 0)));
        Assert.IsNull(MarkdownBlockCommands.Backspace(source, caret));
        Assert.IsNull(MarkdownBlockCommands.Indent(source, source.Length, outdent: false));
    }

    [TestMethod]
    public void Renumbering_multiple_siblings_is_one_undoable_source_transaction()
    {
        var buffer = new StyledDocumentBuffer("1. a\n2. b\n3. c", [new(0, 0)], 6);
        var before = buffer.Capture();
        var caret = new TextCaret(TextSurface.Body, before.Source.Version, 4, CaretAffinity.Downstream);
        var selection = new TextSelection(caret, caret);
        var plan = MarkdownBlockCommands.Enter(before.Source, selection.Range)!;
        var start = caret with { Offset = plan.Replace.Start };
        var end = caret with { Offset = plan.Replace.End };
        var edit = TextEditingOperations.Replace(buffer, new(start, end), plan.Text);
        var after = new TextCaret(TextSurface.Body, edit.Change.NewVersion,
            plan.Selection.Start, CaretAffinity.Downstream);
        var history = new TextEditHistory(buffer);
        history.Record(before, selection, edit with { Selection = new(after, after) },
            HistoryEditKind.Replace, DateTimeOffset.UtcNow);

        Assert.AreEqual("1. a\n2. \n3. b\n4. c", buffer.Capture().Source.Text);
        Assert.AreEqual(before.Source.Text, UndoText(history, buffer));
        Assert.AreEqual("1. a\n2. \n3. b\n4. c", RedoText(history, buffer));
    }

    private static string UndoText(TextEditHistory history, StyledDocumentBuffer buffer)
    {
        Assert.IsNotNull(history.Undo());
        return buffer.Capture().Source.Text;
    }

    private static string RedoText(TextEditHistory history, StyledDocumentBuffer buffer)
    {
        Assert.IsNotNull(history.Redo());
        return buffer.Capture().Source.Text;
    }

    private static string Enter(string value)
    {
        var source = new SourceTextSnapshot(value, 0);
        return Apply(source, MarkdownBlockCommands.Enter(source, new(source.Length, 0))!);
    }

    private static string Apply(SourceTextSnapshot source, MarkdownSourceEdit edit) =>
        source.Text[..edit.Replace.Start] + edit.Text + source.Text[edit.Replace.End..];
}
