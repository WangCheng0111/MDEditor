using MDEditor.Core.Text;

namespace MDEditor.Typesetting.Layout;

public enum HistoryEditKind { Typing, Backspace, DeleteForward, Replace, Paste, Cut, Composition }

/// <summary>Bounded, version-checked undo/redo for one styled source. Stores changed text, not document copies.</summary>
public sealed class TextEditHistory
{
    private const int MaximumGroups = 256;
    private static readonly TimeSpan GroupInterval = TimeSpan.FromMilliseconds(850);
    private readonly StyledDocumentBuffer _buffer;
    private readonly List<Group> _undo = [];
    private readonly List<Group> _redo = [];
    private long _knownVersion;
    private bool _barrier;

    public TextEditHistory(StyledDocumentBuffer buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _knownVersion = buffer.Capture().Source.Version;
    }

    public bool CanUndo => _undo.Count != 0 && _knownVersion == _buffer.Capture().Source.Version;
    public bool CanRedo => _redo.Count != 0 && _knownVersion == _buffer.Capture().Source.Version;

    public void BreakCoalescing() => _barrier = true;

    /// <summary>Records a completed IME composition as one edit, irrespective of its preview revisions.</summary>
    public void RecordComposition(StyledDocumentSnapshot before, TextSelection beforeSelection,
        SourceRange replacedRange, string committedText, TextSelection afterSelection, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(committedText);
        var after = _buffer.Capture();
        if (!before.Source.FullRange.Contains(replacedRange) ||
            beforeSelection.Anchor.Surface != TextSurface.Body || beforeSelection.Focus.Surface != TextSurface.Body ||
            beforeSelection.Anchor.SourceVersion != before.Source.Version ||
            beforeSelection.Focus.SourceVersion != before.Source.Version ||
            beforeSelection.Range != replacedRange ||
            afterSelection.Anchor.Surface != TextSurface.Body || afterSelection.Focus.Surface != TextSurface.Body ||
            afterSelection.Anchor.SourceVersion != after.Source.Version ||
            afterSelection.Focus.SourceVersion != after.Source.Version ||
            after.Source.Text != string.Concat(before.Source.Text.AsSpan(0, replacedRange.Start),
                committedText.AsSpan(), before.Source.Text.AsSpan(replacedRange.End)))
            throw new ArgumentException("Composition does not match the source and selections.");

        if (_knownVersion != before.Source.Version)
        {
            _undo.Clear();
            _redo.Clear();
        }
        _redo.Clear();
        var original = before.Source.GetText(replacedRange);
        if (original != committedText || !before.Styles.SequenceEqual(after.Styles))
        {
            _undo.Add(new Group(replacedRange.Start, original, committedText,
                before.Styles.ToArray(), after.Styles.ToArray(), beforeSelection, afterSelection,
                HistoryEditKind.Composition, timestamp));
            if (_undo.Count > MaximumGroups) _undo.RemoveAt(0);
        }
        _knownVersion = after.Source.Version;
        _barrier = true;
    }

    /// <summary>Rebases history after a cancelled preview restored exactly the original document.</summary>
    public void CancelComposition(StyledDocumentSnapshot before)
    {
        ArgumentNullException.ThrowIfNull(before);
        var after = _buffer.Capture();
        if (after.Source.Text != before.Source.Text || !before.Styles.SequenceEqual(after.Styles))
            throw new InvalidOperationException("Cancelled composition must restore the original document.");
        if (_knownVersion != before.Source.Version)
        {
            _undo.Clear();
            _redo.Clear();
        }
        _knownVersion = after.Source.Version;
        _barrier = true;
    }

    public void Record(StyledDocumentSnapshot before, TextSelection beforeSelection,
        AppliedTextEdit edit, HistoryEditKind kind, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(edit);
        if (!edit.Change.Changed) return;
        var after = _buffer.Capture();
        if (edit.Change.Changes.Count != 1 || edit.Change.OldVersion != before.Source.Version ||
            edit.Change.NewVersion != after.Source.Version ||
            beforeSelection.Anchor.Surface != TextSurface.Body || beforeSelection.Focus.Surface != TextSurface.Body ||
            beforeSelection.Anchor.SourceVersion != before.Source.Version ||
            beforeSelection.Focus.SourceVersion != before.Source.Version ||
            edit.Selection.Anchor.SourceVersion != after.Source.Version ||
            edit.Selection.Focus.SourceVersion != after.Source.Version)
            throw new ArgumentException("History requires one current body edit and valid selections.", nameof(edit));
        if (_knownVersion != before.Source.Version)
        {
            _undo.Clear();
            _redo.Clear();
            _barrier = true;
        }
        _redo.Clear();
        var change = edit.Change.Changes[0];
        var previous = _undo.Count == 0 ? null : _undo[^1];
        if (!_barrier && previous is not null && CanMerge(previous, change, beforeSelection, kind, timestamp))
        {
            switch (kind)
            {
                case HistoryEditKind.Typing: previous.NewText += change.NewText; break;
                case HistoryEditKind.Backspace:
                    previous.Start = change.OldRange.Start;
                    previous.OldText = change.OldText + previous.OldText;
                    break;
                case HistoryEditKind.DeleteForward: previous.OldText += change.OldText; break;
            }
            previous.AfterStyles = after.Styles.ToArray();
            previous.AfterSelection = edit.Selection;
            previous.Timestamp = timestamp;
        }
        else
        {
            _undo.Add(new Group(change.OldRange.Start, change.OldText, change.NewText,
                before.Styles.ToArray(), after.Styles.ToArray(), beforeSelection, edit.Selection, kind, timestamp));
            if (_undo.Count > MaximumGroups) _undo.RemoveAt(0);
        }
        _knownVersion = after.Source.Version;
        _barrier = false;
    }

    public TextSelection? Undo()
    {
        if (!CheckVersion() || _undo.Count == 0) return null;
        var group = _undo[^1];
        var result = _buffer.Restore(new(group.Start, group.NewText.Length), group.NewText,
            group.OldText, group.BeforeStyles, _knownVersion);
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(group);
        _knownVersion = result.NewVersion;
        _barrier = true;
        return Rebase(group.BeforeSelection, result.NewVersion);
    }

    public TextSelection? Redo()
    {
        if (!CheckVersion() || _redo.Count == 0) return null;
        var group = _redo[^1];
        var result = _buffer.Restore(new(group.Start, group.OldText.Length), group.OldText,
            group.NewText, group.AfterStyles, _knownVersion);
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(group);
        _knownVersion = result.NewVersion;
        _barrier = true;
        return Rebase(group.AfterSelection, result.NewVersion);
    }

    private bool CheckVersion()
    {
        if (_knownVersion == _buffer.Capture().Source.Version) return true;
        _undo.Clear();
        _redo.Clear();
        _knownVersion = _buffer.Capture().Source.Version;
        _barrier = true;
        return false;
    }

    private static TextSelection Rebase(TextSelection selection, long version) => new(
        selection.Anchor with { SourceVersion = version },
        selection.Focus with { SourceVersion = version });

    private static bool CanMerge(Group group, DocumentTextChange change, TextSelection beforeSelection,
        HistoryEditKind kind, DateTimeOffset timestamp)
    {
        if (kind != group.Kind || kind is not (HistoryEditKind.Typing or HistoryEditKind.Backspace or HistoryEditKind.DeleteForward) ||
            !beforeSelection.IsEmpty || !group.AfterSelection.IsEmpty ||
            beforeSelection.Focus.Offset != group.AfterSelection.Focus.Offset ||
            timestamp < group.Timestamp || timestamp - group.Timestamp > GroupInterval)
            return false;
        return kind switch
        {
            HistoryEditKind.Typing => change.OldText.Length == 0 && change.NewText.Length > 0 &&
                change.OldRange.Start == group.Start + group.NewText.Length &&
                !char.IsWhiteSpace(group.NewText[^1]) && !char.IsWhiteSpace(change.NewText[0]),
            HistoryEditKind.Backspace => change.NewText.Length == 0 && change.OldText.Length > 0 &&
                change.OldRange.End == group.Start && !group.OldText.Contains('\n') &&
                !group.OldText.Contains('\r') && !change.OldText.Contains('\n') &&
                !change.OldText.Contains('\r'),
            HistoryEditKind.DeleteForward => change.NewText.Length == 0 && change.OldText.Length > 0 &&
                change.OldRange.Start == group.Start && !group.OldText.Contains('\n') &&
                !group.OldText.Contains('\r') && !change.OldText.Contains('\n') &&
                !change.OldText.Contains('\r'),
            _ => false
        };
    }

    private sealed class Group(int start, string oldText, string newText,
        DocumentStyleMarker[] beforeStyles, DocumentStyleMarker[] afterStyles,
        TextSelection beforeSelection, TextSelection afterSelection, HistoryEditKind kind,
        DateTimeOffset timestamp)
    {
        public int Start = start;
        public string OldText = oldText;
        public string NewText = newText;
        public DocumentStyleMarker[] BeforeStyles = beforeStyles;
        public DocumentStyleMarker[] AfterStyles = afterStyles;
        public TextSelection BeforeSelection = beforeSelection;
        public TextSelection AfterSelection = afterSelection;
        public HistoryEditKind Kind = kind;
        public DateTimeOffset Timestamp = timestamp;
    }
}
