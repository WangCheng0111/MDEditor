using System;
using System.Linq;
using MDEditor.Core.Markdown;
using MDEditor.Core.Documents;
using MDEditor.Core.Text;
using MDEditor.Native.Text;
using MDEditor.Typesetting.Markdown;
using MDEditor.Typesetting.Layout;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MDEditor.Controls;

public sealed partial class EditorCanvas
{
    private char? _pendingHighSurrogate;
    private double? _preferredCaretX;
    private bool _followCaretAfterCommit;
    private string? _inputFeedback;

    private bool ModifierDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private TextSelection? CurrentEditableSelection()
    {
        var source = _editorText.Capture().Source;
        if (_selection is not { } selection)
        {
            var initial = new TextCaret(TextSurface.Body, source.Version, 0, CaretAffinity.Downstream);
            return new(initial, initial);
        }
        return selection.Anchor.Surface == TextSurface.Body && selection.Anchor.SourceVersion == source.Version &&
            selection.Focus.SourceVersion == source.Version ? selection : null;
    }

    private void EditorCanvas_CharacterReceived(object sender, CharacterReceivedRoutedEventArgs args)
    {
        if (_searchPanel.ContainsFocus) return;
        if (_disposed || ModifierDown(VirtualKey.Control) || ModifierDown(VirtualKey.Menu)) return;
        if (_imeInput.FocusState != Microsoft.UI.Xaml.FocusState.Unfocused)
        {
            // Some desktop IMEs update TextBox.Text without raising its asynchronous
            // TextChanged event. The routed character event guarantees a later sync.
            if (!_imeComposing) QueueImeSync();
            return;
        }
        string text;
        if (args.Character > char.MaxValue)
        {
            _pendingHighSurrogate = null;
            if (args.Character > 0x10FFFF) return;
            text = char.ConvertFromUtf32((int)args.Character);
        }
        else
        {
            var value = (char)args.Character;
            if (char.IsHighSurrogate(value))
            {
                _pendingHighSurrogate = value; args.Handled = true; return;
            }
            if (char.IsLowSurrogate(value))
            {
                if (_pendingHighSurrogate is not { } high) return;
                text = new string([high, value]); _pendingHighSurrogate = null;
            }
            else
            {
                _pendingHighSurrogate = null;
                if (char.IsControl(value) || value is '\u2028' or '\u2029' or '\u00AD') return;
                text = value.ToString();
            }
        }
        if (CurrentEditableSelection() is { } selection)
        {
            var before = _editorText.Capture();
            text = PrepareTableInsertion(before, selection, text);
            ApplyEdit(TextEditingOperations.Replace(_editorText, selection, text), before, selection,
                selection.IsEmpty ? HistoryEditKind.Typing : HistoryEditKind.Replace);
        }
        else ShowReadOnlyMathFeedback();
        args.Handled = true;
    }

    private void EditorCanvas_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_disposed) return;
        if (TrySearchKey(args)) return;
        if (_searchPanel.ContainsFocus) return;
        if (_imeComposing && !ReferenceEquals(sender, _imeInput)) return;
        _pendingHighSurrogate = null;
        var control = ModifierDown(VirtualKey.Control);
        var shift = ModifierDown(VirtualKey.Shift);
        if (control)
        {
            switch (args.Key)
            {
                case VirtualKey.N: NewRequested?.Invoke(this, EventArgs.Empty); args.Handled = true; return;
                case VirtualKey.O: OpenRequested?.Invoke(this, EventArgs.Empty); args.Handled = true; return;
                case VirtualKey.E when shift: ExportPdfRequested?.Invoke(this, EventArgs.Empty); args.Handled = true; return;
                case VirtualKey.S when shift: SaveAsRequested?.Invoke(this, EventArgs.Empty);
                    args.Handled = true; return;
                case VirtualKey.S: SaveRequested?.Invoke(this, EventArgs.Empty); args.Handled = true; return;
                case VirtualKey.Z: UndoOrRedo(shift); args.Handled = true; return;
                case VirtualKey.Y: UndoOrRedo(redo: true); args.Handled = true; return;
                case VirtualKey.C when !shift: CopySelection(); args.Handled = true; return;
                case VirtualKey.B: ApplyMarkdownFormat(MarkdownFormatKind.Strong);
                    args.Handled = true; return;
                case VirtualKey.I: ApplyMarkdownFormat(MarkdownFormatKind.Emphasis);
                    args.Handled = true; return;
                case VirtualKey.K: ApplyMarkdownFormat(MarkdownFormatKind.Link);
                    args.Handled = true; return;
                case VirtualKey.X when shift: ApplyMarkdownFormat(MarkdownFormatKind.Strikethrough);
                    args.Handled = true; return;
                case VirtualKey.X: CutSelection(); args.Handled = true; return;
                case VirtualKey.V: _ = PasteAsync(); args.Handled = true; return;
                case VirtualKey.A: SelectAllSource(); args.Handled = true; return;
            }
        }
        if (control && args.Key is not (VirtualKey.Home or VirtualKey.End)) return;
        var selection = _selection;
        if (args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or
            VirtualKey.Home or VirtualKey.End)
        {
            _history.BreakCoalescing();
            if (selection is null)
            {
                var source = _editorText.Capture().Source;
                var start = new TextCaret(TextSurface.Body, source.Version, 0, CaretAffinity.Downstream);
                selection = new(start, start);
            }
            var moved = Navigate(selection.Value, args.Key, shift, control);
            if (moved is { } next)
            {
                _selection = next; _inputFeedback = null;
                UpdateMarkdownReveal();
                ResetCaretBlink(); InvalidateInteraction(); UpdateStatusIfReady();
                RevealCaret(_renderer?.Document, _canvas);
                args.Handled = true;
            }
            return;
        }
        if (args.Key is not (VirtualKey.Back or VirtualKey.Delete or VirtualKey.Enter or VirtualKey.Tab)) return;
        if (CurrentEditableSelection() is not { } editable)
        {
            ShowReadOnlyMathFeedback(); args.Handled = true; return;
        }
        var before = _editorText.Capture();
        if (!SearchViewModel.IsSourceMode && (args.Key == VirtualKey.Tab && TryTableTab(before, editable, shift) ||
            args.Key == VirtualKey.Enter && TryTableEnter(before, editable)))
        {
            args.Handled = true;
            return;
        }
        MarkdownSourceEdit? structural = SearchViewModel.IsSourceMode ? null : args.Key switch
        {
            VirtualKey.Enter => MarkdownBlockCommands.Enter(before.Source, editable.Range),
            VirtualKey.Back when editable.IsEmpty =>
                MarkdownBlockCommands.Backspace(before.Source, editable.Focus.Offset),
            VirtualKey.Tab => MarkdownBlockCommands.Indent(before.Source, editable.Range, shift),
            _ => null
        };
        if (structural is not null)
        {
            ApplySourcePlan(structural, before, editable);
            args.Handled = true;
            return;
        }
        if (shift && args.Key == VirtualKey.Tab)
        {
            args.Handled = true;
            return;
        }
        var edit = args.Key switch
        {
            VirtualKey.Back => DeleteRenderedMathAtCaret(editable, before, backward: true) ??
                DeleteProjected(editable, backward: true),
            VirtualKey.Delete => DeleteRenderedMathAtCaret(editable, before, backward: false) ??
                DeleteProjected(editable, backward: false),
            VirtualKey.Enter => TextEditingOperations.Replace(_editorText, editable,
                DocumentNewlinePolicy.ForInsertion(before.Lines, editable.Range.Start)),
            VirtualKey.Tab => TextEditingOperations.Replace(_editorText, editable, "    "),
            _ => throw new InvalidOperationException()
        };
        var kind = !editable.IsEmpty ? HistoryEditKind.Replace : args.Key switch
        {
            VirtualKey.Back => HistoryEditKind.Backspace,
            VirtualKey.Delete => HistoryEditKind.DeleteForward,
            _ => HistoryEditKind.Replace
        };
        ApplyEdit(edit, before, editable, kind); args.Handled = true;
    }

    private AppliedTextEdit DeleteProjected(TextSelection selection, bool backward)
    {
        var projection = CurrentPresentation().Text;
        SourceRange? range = selection.IsEmpty
            ? backward ? projection.BackspaceRange(selection.Focus.Offset) :
                projection.DeleteForwardRange(selection.Focus.Offset)
            : selection.Range == projection.Source.FullRange ? projection.Source.FullRange :
                projection.ToSourceRange(projection.ToDisplayRange(selection.Range), true);
        if (range is not { Length: > 0 } target)
            return TextEditingOperations.Replace(_editorText, selection, "");
        var first = new TextCaret(TextSurface.Body, projection.Source.Version,
            target.Start, CaretAffinity.Downstream);
        var last = first with { Offset = target.End, Affinity = CaretAffinity.Upstream };
        return TextEditingOperations.Replace(_editorText, new(first, last), "");
    }

    private AppliedTextEdit? DeleteRenderedMathAtCaret(TextSelection selection,
        StyledDocumentSnapshot before, bool backward)
    {
        if (!selection.IsEmpty) return null;
        var offset = selection.Focus.Offset;
        if (backward ? offset == 0 || before.Source.Text[offset - 1] is not ('$' or ')' or ']') :
            offset == before.Source.Length || before.Source.Text[offset] is not ('$' or '\\')) return null;
        foreach (var node in CurrentPresentation().Text.MathSpans)
        {
            if ((backward ? node.Source.End : node.Source.Start) != offset) continue;
            var kind = node.Kind == MarkdownMathSyntaxKind.Display ? MarkdownMathKind.Display :
                MarkdownMathKind.Inline;
            if (!_editableMathCache.ContainsKey((CurrentPresentation().Text.References.LayoutContent(node), kind))) continue;
            var start = new TextCaret(TextSurface.Body, before.Source.Version, node.Source.Start,
                CaretAffinity.Downstream);
            var end = new TextCaret(TextSurface.Body, before.Source.Version, node.Source.End,
                CaretAffinity.Upstream);
            return TextEditingOperations.Replace(_editorText, new(start, end), "");
        }
        return null;
    }

    private TextSelection? Navigate(TextSelection selection, VirtualKey key, bool extend, bool control)
    {
        var map = _renderer?.Document is { } document ? InteractionMap(document, selection.Focus.Surface) : null;
        var source = selection.Focus.Surface == TextSurface.Body ? _editorText.Capture().Source : map?.Source;
        if (source is null || selection.Focus.SourceVersion != source.Version) return null;
        TextCaret next;
        if (control && key is VirtualKey.Home or VirtualKey.End)
        {
            var offset = key == VirtualKey.Home ? 0 : source.Length;
            next = new(selection.Focus.Surface, source.Version, offset,
                key == VirtualKey.Home ? CaretAffinity.Downstream : CaretAffinity.Upstream);
        }
        else if (key is VirtualKey.Left or VirtualKey.Right)
        {
            var direction = key == VirtualKey.Left ? -1 : 1;
            if (!extend && !selection.IsEmpty)
            {
                var offset = direction < 0 ? selection.Range.Start : selection.Range.End;
                next = new(selection.Focus.Surface, source.Version, offset,
                    direction < 0 ? CaretAffinity.Upstream : CaretAffinity.Downstream);
            }
            else if (map is not null && map.Source.Version == source.Version)
                next = map.MoveHorizontal(selection.Focus, direction) ?? selection.Focus;
            else if (selection.Focus.Surface == TextSurface.Body)
                next = TextEditingOperations.MoveLogical(source, selection, direction, true).Focus;
            else return null;
        }
        else if (key is VirtualKey.Home or VirtualKey.End)
        {
            var line = CurrentVisualLine(map, selection.Focus);
            var range = line?.Source ?? (selection.Focus.Surface == TextSurface.Body
                ? _editorText.Capture().Lines.Lines[_editorText.Capture().Lines.FindLine(selection.Focus.Offset)].Content
                : source.FullRange);
            next = new(selection.Focus.Surface, source.Version,
                key == VirtualKey.Home ? range.Start : range.End,
                key == VirtualKey.Home ? CaretAffinity.Downstream : CaretAffinity.Upstream);
        }
        else
        {
            if (map is null || map.Source.Version != source.Version ||
                map.Resolve(selection.Focus) is not { } caret || map.Lines.Count == 0) return null;
            var current = Array.FindIndex(map.Lines.ToArray(), line => Math.Abs(line.Bounds.Y - caret.Y) < 1e-6);
            if (current < 0) return null;
            var target = Math.Clamp(current + (key == VirtualKey.Up ? -1 : 1), 0, map.Lines.Count - 1);
            var line = map.Lines[target];
            _preferredCaretX ??= caret.X;
            next = map.HitTest(new(_preferredCaretX.Value, line.Bounds.Y + line.Bounds.Height / 2)) ?? selection.Focus;
        }
        if (key is not (VirtualKey.Up or VirtualKey.Down)) _preferredCaretX = null;
        return new(extend ? selection.Anchor : next, next);
    }

    private static TextInteractionLine? CurrentVisualLine(ITextInteractionMap? map, TextCaret caret)
    {
        if (map?.Resolve(caret) is not { } position) return null;
        return map.Lines.FirstOrDefault(line => Math.Abs(line.Bounds.Y - position.Y) < 1e-6);
    }

    private void ApplyEdit(AppliedTextEdit edit, StyledDocumentSnapshot before,
        TextSelection beforeSelection, HistoryEditKind kind)
    {
        _history.Record(before, beforeSelection, edit, kind, DateTimeOffset.UtcNow);
        ApplyCommittedSelection(edit.Selection, edit.Change.Changed,
            immediate: kind is HistoryEditKind.Paste or HistoryEditKind.Cut);
    }

    private void ApplyCommittedSelection(TextSelection selection, bool changed, bool immediate = false)
    {
        _selection = selection; _preferredCaretX = null; _inputFeedback = null;
        ResetCaretBlink();
        if (changed)
        {
            QueueSearch();
            if (!_imeComposing) DocumentChanged?.Invoke(_editorText.Capture());
            DeferEditStatus();
            _renderer?.SetContent(EditableContent());
            RequestEditableMathLayouts();
            RequestImageResources();
            _followCaretAfterCommit = true;
            // Do not cancel the in-flight paragraph solve on every repeat key. It can show
            // an intermediate edit frame while the single worker catches up to the latest text.
            RequestLayout(force: true, immediate: immediate);
            _latestEditRevision = _gate.Revision;
        }
        InvalidateInteraction(); UpdateStatusIfReady();
    }

    private void ShowReadOnlyMathFeedback()
    {
        _inputFeedback = "数学样张只读；请点击普通正文编辑";
        UpdateStatusIfReady();
    }

    private void RevealCaret(ReflowDocument? document, CanvasControl? canvas)
    {
        if (document is null || canvas is null || _selection is not { } selection) return;
        var map = InteractionMap(document, selection.Focus.Surface);
        if (map?.Resolve(selection.Focus) is not { } caret) return;
        var viewport = PresentedViewport(canvas);
        const double margin = 24;
        if (caret.Y < _scroll + margin / viewport.Zoom)
            _scroll = Math.Max(0, caret.Y - margin / viewport.Zoom);
        else if (caret.Bottom > _scroll + viewport.DocumentHeight - margin / viewport.Zoom)
            _scroll = caret.Bottom - viewport.DocumentHeight + margin / viewport.Zoom;
        UpdateScroll(viewport); Invalidate();
        _followCaretAfterCommit = false;
    }
}
