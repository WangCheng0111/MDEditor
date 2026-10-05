using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class TextEditHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static TextSelection At(StyledDocumentBuffer buffer, int offset)
    {
        var caret = new TextCaret(TextSurface.Body, buffer.Capture().Source.Version, offset, CaretAffinity.Downstream);
        return new(caret, caret);
    }

    private static TextSelection Record(StyledDocumentBuffer buffer, TextEditHistory history,
        TextSelection selection, string replacement, HistoryEditKind kind, int milliseconds)
    {
        var before = buffer.Capture();
        var edit = TextEditingOperations.Replace(buffer, selection, replacement);
        history.Record(before, selection, edit, kind, Start.AddMilliseconds(milliseconds));
        return edit.Selection;
    }

    [TestMethod]
    public void Adjacent_typing_undoes_as_one_group_and_redo_restores_caret()
    {
        var buffer = new StyledDocumentBuffer("X", [new(0, 0)]);
        var history = new TextEditHistory(buffer);
        var selection = At(buffer, 1);
        selection = Record(buffer, history, selection, "a", HistoryEditKind.Typing, 0);
        selection = Record(buffer, history, selection, "b", HistoryEditKind.Typing, 100);
        selection = Record(buffer, history, selection, "c", HistoryEditKind.Typing, 200);
        Assert.AreEqual("Xabc", buffer.Capture().Source.Text);
        var undone = history.Undo();
        Assert.IsNotNull(undone);
        Assert.AreEqual("X", buffer.Capture().Source.Text);
        Assert.AreEqual(1, undone.Value.Focus.Offset);
        Assert.AreEqual(buffer.Capture().Source.Version, undone.Value.Focus.SourceVersion);
        Assert.IsFalse(history.CanUndo);
        var redone = history.Redo();
        Assert.IsNotNull(redone);
        Assert.AreEqual("Xabc", buffer.Capture().Source.Text);
        Assert.AreEqual(4, redone.Value.Focus.Offset);
        Assert.AreEqual(5L, buffer.Capture().Source.Version);
    }

    [TestMethod]
    public void Pause_and_caret_move_split_typing_groups()
    {
        var buffer = new StyledDocumentBuffer("", [new(0, 0)]);
        var history = new TextEditHistory(buffer);
        var selection = Record(buffer, history, At(buffer, 0), "a", HistoryEditKind.Typing, 0);
        selection = Record(buffer, history, selection, "b", HistoryEditKind.Typing, 1000);
        history.BreakCoalescing();
        Record(buffer, history, selection, "c", HistoryEditKind.Typing, 1100);
        history.Undo(); Assert.AreEqual("ab", buffer.Capture().Source.Text);
        history.Undo(); Assert.AreEqual("a", buffer.Capture().Source.Text);
        history.Undo(); Assert.AreEqual("", buffer.Capture().Source.Text);
    }

    [TestMethod]
    public void Repeated_backspace_and_delete_forward_each_form_one_undo_group()
    {
        var buffer = new StyledDocumentBuffer("abcd", [new(0, 0)]);
        var history = new TextEditHistory(buffer);
        var selection = At(buffer, 4);
        for (var index = 0; index < 3; index++)
        {
            var before = buffer.Capture();
            var edit = TextEditingOperations.Backspace(buffer, selection);
            history.Record(before, selection, edit, HistoryEditKind.Backspace, Start.AddMilliseconds(index * 100));
            selection = edit.Selection;
        }
        Assert.AreEqual("a", buffer.Capture().Source.Text);
        Assert.AreEqual(4, history.Undo()!.Value.Focus.Offset);
        Assert.AreEqual("abcd", buffer.Capture().Source.Text);
        Assert.AreEqual("a", ReplayRedo(buffer, history));

        var forwardBuffer = new StyledDocumentBuffer("abcd", [new(0, 0)]);
        var forwardHistory = new TextEditHistory(forwardBuffer);
        selection = At(forwardBuffer, 1);
        for (var index = 0; index < 2; index++)
        {
            var before = forwardBuffer.Capture();
            var edit = TextEditingOperations.DeleteForward(forwardBuffer, selection);
            forwardHistory.Record(before, selection, edit, HistoryEditKind.DeleteForward,
                Start.AddMilliseconds(index * 100));
            selection = edit.Selection;
        }
        Assert.AreEqual("ad", forwardBuffer.Capture().Source.Text);
        forwardHistory.Undo();
        Assert.AreEqual("abcd", forwardBuffer.Capture().Source.Text);
    }

    [TestMethod]
    public void Cross_paragraph_cut_undo_restores_styles_and_reversed_selection()
    {
        var buffer = new StyledDocumentBuffer("Alpha\nBeta", [new(0, 0), new(6, 1)], 40);
        var history = new TextEditHistory(buffer);
        var selection = new TextSelection(
            new(TextSurface.Body, 40, 8, CaretAffinity.Downstream),
            new(TextSurface.Body, 40, 3, CaretAffinity.Upstream));
        var editSelection = Record(buffer, history, selection, "", HistoryEditKind.Cut, 0);
        Assert.AreEqual("Alpta", buffer.Capture().Source.Text);
        Assert.AreEqual(1, buffer.Capture().Styles.Count);
        var undone = history.Undo()!.Value;
        Assert.AreEqual("Alpha\nBeta", buffer.Capture().Source.Text);
        Assert.AreEqual(6, buffer.Capture().Styles[1].Offset);
        Assert.AreEqual(8, undone.Anchor.Offset);
        Assert.AreEqual(3, undone.Focus.Offset);
        Assert.AreEqual(buffer.Capture().Source.Version, undone.Focus.SourceVersion);
        history.Redo();
        Assert.AreEqual("Alpta", buffer.Capture().Source.Text);
        Assert.AreEqual(editSelection.Focus.Offset, history.Undo()!.Value.Range.Start);
    }

    [TestMethod]
    public void Cross_paragraph_paste_preserves_crlf_unicode_and_redo_is_invalidated_by_new_edit()
    {
        var buffer = new StyledDocumentBuffer("AB", [new(0, 0)]);
        var history = new TextEditHistory(buffer);
        var selection = Record(buffer, history, At(buffer, 1), "中\r\n😀", HistoryEditKind.Paste, 0);
        Assert.AreEqual("A中\r\n😀B", buffer.Capture().Source.Text);
        Assert.AreEqual(2, buffer.Capture().Lines.Count);
        Assert.AreEqual(DocumentLineEnding.CrLf, buffer.Capture().Lines.Lines[0].Ending);
        var undone = history.Undo()!.Value;
        Assert.AreEqual("AB", buffer.Capture().Source.Text);
        Assert.AreEqual(1, undone.Focus.Offset);
        Assert.IsTrue(history.CanRedo);
        selection = Record(buffer, history, undone, "x", HistoryEditKind.Typing, 100);
        Assert.IsFalse(history.CanRedo);
        Assert.IsNull(history.Redo());
        Assert.AreEqual("AxB", buffer.Capture().Source.Text);
    }

    [TestMethod]
    public void Foreign_edit_invalidates_history_without_overwriting_document()
    {
        var buffer = new StyledDocumentBuffer("A", [new(0, 0)]);
        var history = new TextEditHistory(buffer);
        Record(buffer, history, At(buffer, 1), "b", HistoryEditKind.Typing, 0);
        buffer.Replace(new(0, 0), "X");
        Assert.IsNull(history.Undo());
        Assert.AreEqual("XAb", buffer.Capture().Source.Text);
        Assert.IsFalse(history.CanRedo);
    }

    [TestMethod]
    public void Selected_text_across_paragraphs_preserves_exact_crlf_and_unicode()
    {
        var source = new SourceTextSnapshot("A\r\n中文😀\nB", 7);
        var selection = new TextSelection(
            new(TextSurface.Body, 7, 8, CaretAffinity.Upstream),
            new(TextSurface.Body, 7, 1, CaretAffinity.Downstream));
        Assert.AreEqual("\r\n中文😀\n", TextEditingOperations.SelectedText(source, selection));
        Assert.ThrowsExactly<ArgumentException>(() => TextEditingOperations.SelectedText(source,
            selection with { Focus = selection.Focus with { SourceVersion = 8 } }));
    }

    [TestMethod]
    public void Deleting_a_line_break_is_a_separate_undo_group()
    {
        var buffer = new StyledDocumentBuffer("A\nB", [new(0, 0)]);
        var history = new TextEditHistory(buffer);
        var selection = At(buffer, 2);
        for (var index = 0; index < 2; index++)
        {
            var before = buffer.Capture();
            var edit = TextEditingOperations.Backspace(buffer, selection);
            history.Record(before, selection, edit, HistoryEditKind.Backspace, Start.AddMilliseconds(index * 100));
            selection = edit.Selection;
        }
        Assert.AreEqual("B", buffer.Capture().Source.Text);
        history.Undo();
        Assert.AreEqual("AB", buffer.Capture().Source.Text);
        history.Undo();
        Assert.AreEqual("A\nB", buffer.Capture().Source.Text);
    }

    [TestMethod]
    public void Restore_rejects_mismatch_without_mutating_text_or_style()
    {
        var buffer = new StyledDocumentBuffer("A\nB", [new(0, 0), new(2, 1)], 4);
        Assert.ThrowsExactly<InvalidOperationException>(() => buffer.Restore(new(0, 1), "X", "Q",
            [new(0, 0)], 4));
        Assert.AreEqual("A\nB", buffer.Capture().Source.Text);
        Assert.AreEqual(2, buffer.Capture().Styles.Count);
        Assert.AreEqual(4, buffer.Capture().Source.Version);
    }

    [TestMethod]
    public void Random_edit_sequence_round_trips_text_and_style_anchors()
    {
        var buffer = new StyledDocumentBuffer("Alpha\nBeta", [new(0, 0), new(6, 1)]);
        var history = new TextEditHistory(buffer);
        var initial = buffer.Capture();
        var random = new Random(18);
        for (var index = 0; index < 100; index++)
        {
            var snapshot = buffer.Capture();
            var position = random.Next(snapshot.Source.Length + 1);
            var selection = At(buffer, position);
            var insert = random.Next(4) != 0;
            var edit = insert
                ? TextEditingOperations.Replace(buffer, selection, random.Next(5) == 0 ? "\n" : "中")
                : TextEditingOperations.Backspace(buffer, selection);
            history.Record(snapshot, selection, edit, insert ? HistoryEditKind.Typing : HistoryEditKind.Backspace,
                Start.AddMilliseconds(index * 100));
        }
        var final = buffer.Capture();
        while (history.CanUndo) history.Undo();
        Assert.AreEqual(initial.Source.Text, buffer.Capture().Source.Text);
        CollectionAssert.AreEqual(initial.Styles.ToArray(), buffer.Capture().Styles.ToArray());
        while (history.CanRedo) history.Redo();
        Assert.AreEqual(final.Source.Text, buffer.Capture().Source.Text);
        CollectionAssert.AreEqual(final.Styles.ToArray(), buffer.Capture().Styles.ToArray());
    }

    private static string ReplayRedo(StyledDocumentBuffer buffer, TextEditHistory history)
    {
        history.Redo();
        return buffer.Capture().Source.Text;
    }
}
