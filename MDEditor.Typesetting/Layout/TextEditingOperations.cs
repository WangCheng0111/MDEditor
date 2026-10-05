using System.Globalization;
using MDEditor.Core.Text;

namespace MDEditor.Typesetting.Layout;

public sealed record AppliedTextEdit(DocumentEditResult Change, TextSelection Selection);

/// <summary>Keyboard-independent edit rules. UI events only supply intent and never mutate layout snapshots.</summary>
public static class TextEditingOperations
{
    public static string SelectedText(SourceTextSnapshot source, TextSelection selection)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (selection.Anchor.Surface != selection.Focus.Surface ||
            selection.Anchor.SourceVersion != source.Version || selection.Focus.SourceVersion != source.Version ||
            !source.FullRange.Contains(selection.Range))
            throw new ArgumentException("Selection does not belong to the current source.", nameof(selection));
        return selection.IsEmpty ? "" : source.GetText(selection.Range);
    }

    public static AppliedTextEdit Replace(StyledDocumentBuffer buffer, TextSelection selection, string text)
    {
        ArgumentNullException.ThrowIfNull(buffer); ArgumentNullException.ThrowIfNull(text);
        var snapshot = buffer.Capture().Source;
        Validate(selection, snapshot);
        var range = selection.IsEmpty ? new SourceRange(selection.Focus.Offset, 0) : selection.Range;
        var change = buffer.Replace(range, text);
        var caret = new TextCaret(TextSurface.Body, change.NewVersion, range.Start + text.Length,
            CaretAffinity.Downstream);
        return new(change, new(caret, caret));
    }

    public static AppliedTextEdit Backspace(StyledDocumentBuffer buffer, TextSelection selection)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var source = buffer.Capture().Source;
        Validate(selection, source);
        if (!selection.IsEmpty) return Replace(buffer, selection, "");
        var previous = PreviousBoundary(source.Text, selection.Focus.Offset);
        var range = new SourceRange(previous, selection.Focus.Offset - previous);
        return Replace(buffer, new(new(TextSurface.Body, source.Version, range.Start, CaretAffinity.Downstream),
            new(TextSurface.Body, source.Version, range.End, CaretAffinity.Upstream)), "");
    }

    public static AppliedTextEdit DeleteForward(StyledDocumentBuffer buffer, TextSelection selection)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var source = buffer.Capture().Source;
        Validate(selection, source);
        if (!selection.IsEmpty) return Replace(buffer, selection, "");
        var next = NextBoundary(source.Text, selection.Focus.Offset);
        return Replace(buffer, new(selection.Focus,
            new(TextSurface.Body, source.Version, next, CaretAffinity.Upstream)), "");
    }

    public static TextSelection MoveLogical(SourceTextSnapshot source, TextSelection selection, int direction, bool extend)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        Validate(selection, source);
        var offset = !extend && !selection.IsEmpty
            ? direction < 0 ? selection.Range.Start : selection.Range.End
            : direction < 0 ? PreviousBoundary(source.Text, selection.Focus.Offset) :
                NextBoundary(source.Text, selection.Focus.Offset);
        var caret = new TextCaret(TextSurface.Body, source.Version, offset,
            direction < 0 ? CaretAffinity.Upstream : CaretAffinity.Downstream);
        return new(extend ? selection.Anchor : caret, caret);
    }

    public static int PreviousBoundary(string text, int offset)
    {
        var boundaries = Boundaries(text);
        if (offset < 0 || offset > text.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var index = Array.BinarySearch(boundaries, offset);
        if (index < 0) index = ~index;
        return boundaries[Math.Max(0, index - 1)];
    }

    public static int NextBoundary(string text, int offset)
    {
        var boundaries = Boundaries(text);
        if (offset < 0 || offset > text.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var index = Array.BinarySearch(boundaries, offset);
        if (index < 0) index = ~index - 1;
        return boundaries[Math.Min(boundaries.Length - 1, index + 1)];
    }

    private static int[] Boundaries(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var boundaries = StringInfo.ParseCombiningCharacters(text)
            .Where(index => index == 0 || index >= text.Length || text[index - 1] != '\r' || text[index] != '\n')
            .ToList();
        if (boundaries.Count == 0 || boundaries[0] != 0) boundaries.Insert(0, 0);
        if (boundaries[^1] != text.Length) boundaries.Add(text.Length);
        return boundaries.ToArray();
    }

    private static void Validate(TextSelection selection, SourceTextSnapshot source)
    {
        if (selection.Anchor.Surface != TextSurface.Body || selection.Focus.Surface != TextSurface.Body ||
            selection.Anchor.SourceVersion != source.Version || selection.Focus.SourceVersion != source.Version ||
            selection.Anchor.Offset < 0 || selection.Focus.Offset < 0 ||
            selection.Anchor.Offset > source.Length || selection.Focus.Offset > source.Length)
            throw new ArgumentException("Selection is not in the current editable document.", nameof(selection));
    }
}
