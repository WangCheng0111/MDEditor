using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class StyledDocumentEditingTests
{
    private static StyledDocumentBuffer TwoStyles() =>
        new("Alpha\nBeta", [new(0, 0), new(6, 1)], 100);

    private static TextSelection At(StyledDocumentBuffer buffer, int offset)
    {
        var caret = new TextCaret(TextSurface.Body, buffer.Capture().Source.Version, offset, CaretAffinity.Downstream);
        return new(caret, caret);
    }

    [TestMethod]
    public void Insertion_shifts_later_style_anchor_without_changing_earlier_text()
    {
        var buffer = TwoStyles();
        var old = buffer.Capture();
        var result = TextEditingOperations.Replace(buffer, At(buffer, 2), "中");
        var current = buffer.Capture();
        Assert.AreEqual("Al中pha\nBeta", current.Source.Text);
        Assert.AreEqual(101L, current.Source.Version);
        Assert.AreEqual(7, current.Styles[1].Offset);
        Assert.AreEqual(3, result.Selection.Focus.Offset);
        Assert.AreEqual("Alpha\nBeta", old.Source.Text);
        Assert.AreEqual(6, old.Styles[1].Offset);
    }

    [TestMethod]
    public void Enter_splits_a_paragraph_and_preserves_the_following_style()
    {
        var buffer = TwoStyles();
        var result = TextEditingOperations.Replace(buffer, At(buffer, 3), "\n");
        var current = buffer.Capture();
        Assert.AreEqual("Alp\nha\nBeta", current.Source.Text);
        Assert.AreEqual(3, current.Lines.Count);
        Assert.AreEqual(0, current.StyleAt(current.Lines.Lines[1].Content.Start));
        Assert.AreEqual(1, current.StyleAt(current.Lines.Lines[2].Content.Start));
        Assert.AreEqual(4, result.Selection.Focus.Offset);
    }

    [TestMethod]
    public void Empty_line_after_two_enters_stays_addressable()
    {
        var buffer = new StyledDocumentBuffer("A", [new(0, 0)]);
        var first = TextEditingOperations.Replace(buffer, At(buffer, 1), "\n");
        TextEditingOperations.Replace(buffer, first.Selection, "\n");
        Assert.AreEqual("A\n\n", buffer.Capture().Source.Text);
        Assert.AreEqual(3, buffer.Capture().Lines.Count);
        Assert.AreEqual(0, buffer.Capture().Lines.Lines[^1].Content.Length);
    }

    [TestMethod]
    public void Selection_replace_and_backspace_commit_one_atomic_change_each()
    {
        var buffer = TwoStyles();
        var source = buffer.Capture().Source;
        var selection = new TextSelection(new(TextSurface.Body, source.Version, 1, CaretAffinity.Downstream),
            new(TextSurface.Body, source.Version, 4, CaretAffinity.Upstream));
        var replaced = TextEditingOperations.Replace(buffer, selection, "文");
        Assert.AreEqual("A文a\nBeta", buffer.Capture().Source.Text);
        Assert.AreEqual(2, replaced.Selection.Focus.Offset);
        Assert.AreEqual(1, replaced.Change.Changes.Count);
        var deleted = TextEditingOperations.Backspace(buffer, replaced.Selection);
        Assert.AreEqual("Aa\nBeta", buffer.Capture().Source.Text);
        Assert.AreEqual(1, deleted.Selection.Focus.Offset);
        Assert.AreEqual(102L, buffer.Capture().Source.Version);
    }

    [TestMethod]
    public void Deleting_across_style_boundary_merges_into_preceding_style()
    {
        var buffer = TwoStyles();
        buffer.Replace(new(3, 4), "");
        var snapshot = buffer.Capture();
        Assert.AreEqual("Alpeta", snapshot.Source.Text);
        Assert.AreEqual(1, snapshot.Styles.Count);
        Assert.AreEqual(0, snapshot.StyleAt(snapshot.Source.Length));
    }

    [TestMethod]
    public void Backspace_and_delete_never_split_combining_surrogates_or_crlf()
    {
        var buffer = new StyledDocumentBuffer("a\u0308😀\r\n中", [new(0, 0)]);
        var afterEmoji = TextEditingOperations.Backspace(buffer, At(buffer, 4));
        Assert.AreEqual("a\u0308\r\n中", buffer.Capture().Source.Text);
        var afterAccent = TextEditingOperations.Backspace(buffer, afterEmoji.Selection);
        Assert.AreEqual("\r\n中", buffer.Capture().Source.Text);
        var afterBreak = TextEditingOperations.DeleteForward(buffer, afterAccent.Selection);
        Assert.AreEqual("中", buffer.Capture().Source.Text);
        Assert.AreEqual(0, afterBreak.Selection.Focus.Offset);
    }

    [TestMethod]
    public void Arrow_movement_uses_grapheme_boundaries_and_shift_keeps_anchor()
    {
        var buffer = new StyledDocumentBuffer("a\u0308😀中", [new(0, 0)], 2);
        var source = buffer.Capture().Source;
        var first = TextEditingOperations.MoveLogical(source, At(buffer, 0), 1, false);
        var extended = TextEditingOperations.MoveLogical(source, first, 1, true);
        Assert.AreEqual(2, first.Focus.Offset);
        Assert.AreEqual(new SourceRange(2, 2), extended.Range);
        var collapsed = TextEditingOperations.MoveLogical(source, extended, -1, false);
        Assert.AreEqual(2, collapsed.Focus.Offset);
        Assert.IsTrue(collapsed.IsEmpty);
    }

    [TestMethod]
    public void Stale_selection_cannot_edit_a_newer_buffer_version()
    {
        var buffer = TwoStyles();
        var stale = At(buffer, 1);
        buffer.Replace(new(0, 0), "x");
        Assert.ThrowsExactly<ArgumentException>(() => TextEditingOperations.Replace(buffer, stale, "bad"));
        Assert.AreEqual("xAlpha\nBeta", buffer.Capture().Source.Text);
    }
}
