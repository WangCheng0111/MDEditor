using System;
using System.Linq;
using MDEditor.Core.Text;
using MDEditor.Native.Text;
using MDEditor.Typesetting.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using XamlCanvas = Microsoft.UI.Xaml.Controls.Canvas;

namespace MDEditor.Controls;

public sealed partial class EditorCanvas
{
    // A WinUI TextBox owns the desktop TSF context and candidate window. The document,
    // selection, layout and committed text remain in the native editor, not in this box.
    private readonly XamlCanvas _imeLayer = new();
    private readonly TextBox _imeInput = new EditorImeInput()
    {
        Width = 160, Height = 34, MinWidth = 0, MinHeight = 0,
        Opacity = 0.01, IsHitTestVisible = false, IsSpellCheckEnabled = false,
        AcceptsReturn = false, TextWrapping = TextWrapping.NoWrap,
        BorderThickness = new Thickness(0), Padding = new Thickness(0),
        Background = new SolidColorBrush(Color.FromArgb(255, 243, 245, 248)),
        Foreground = new SolidColorBrush(Color.FromArgb(255, 31, 42, 58))
    };
    private bool _imeAttached, _imeProgrammaticChange, _imeComposing, _imeInsideCompositionEvent;
    private StyledDocumentSnapshot? _imeBefore;
    private TextSelection _imeBeforeSelection;
    private SourceRange _imePreviewRange;
    private string _imePreviewText = "";
    private string _imeObservedText = "";
    private string _imeMirroredSourceText = "";
    private SourceRange? _imePlainRange;
    private int _imeBoxStart, _imeClearRevision;
    private bool _imeFinishQueued, _imeSyncQueued;

    private void AttachImeInput()
    {
        if (_imeAttached) return;
        ((EditorImeInput)_imeInput).Editor = this;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(_imeInput,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        _imeAttached = true;
        Grid.SetColumn(_imeLayer, 0);
        _imeLayer.Children.Add(_imeInput);
        _surface.Children.Add(_imeLayer);
        _imeInput.PreviewKeyDown += ImeInput_PreviewKeyDown;
        _imeInput.TextChanging += ImeInput_TextChanging;
        _imeInput.TextChanged += ImeInput_TextChanged;
        _imeInput.TextCompositionStarted += ImeInput_CompositionStarted;
        _imeInput.TextCompositionChanged += ImeInput_CompositionChanged;
        _imeInput.TextCompositionEnded += ImeInput_CompositionEnded;
        _imeInput.GotFocus += ImeInput_GotFocus;
        _imeInput.LostFocus += ImeInput_LostFocus;
        UpdateImeAnchor();
    }

    private void DetachImeInput()
    {
        if (!_imeAttached) return;
        if (_imeComposing) CancelImeComposition();
        _imeAttached = false;
        _imeInput.PreviewKeyDown -= ImeInput_PreviewKeyDown;
        _imeInput.TextChanging -= ImeInput_TextChanging;
        _imeInput.TextChanged -= ImeInput_TextChanged;
        _imeInput.TextCompositionStarted -= ImeInput_CompositionStarted;
        _imeInput.TextCompositionChanged -= ImeInput_CompositionChanged;
        _imeInput.TextCompositionEnded -= ImeInput_CompositionEnded;
        _imeInput.GotFocus -= ImeInput_GotFocus;
        _imeInput.LostFocus -= ImeInput_LostFocus;
        _imeLayer.Children.Remove(_imeInput);
        _surface.Children.Remove(_imeLayer);
    }

    private void FocusImeInput()
    {
        if (!_imeAttached || _disposed) return;
        UpdateImeAnchor();
        if (!_imeInput.Focus(Microsoft.UI.Xaml.FocusState.Pointer))
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed && _imeAttached && _selection?.Anchor.Surface == TextSurface.Body)
                    _imeInput.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            });
    }

    private void ImeInput_GotFocus(object sender, RoutedEventArgs args)
    {
        _interactionFocused = true;
        ResetCaretBlink();
        NotifyAccessibilityChanges();
    }

    private void ImeInput_LostFocus(object sender, RoutedEventArgs args)
    {
        // The IME normally ends its composition before this continuation runs.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !_imeAttached ||
                _imeInput.FocusState != Microsoft.UI.Xaml.FocusState.Unfocused) return;
            if (_imeComposing) QueueImeFinish();
            else
            {
                MirrorPlainText(_imeInput.Text);
                ClearImeInput();
            }
            _interactionFocused = FocusState != Microsoft.UI.Xaml.FocusState.Unfocused;
            if (!_interactionFocused) _caretTimer.Stop();
            InvalidateInteraction();
        });
    }

    private void ImeInput_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_disposed || _imeComposing) return; // Let TSF consume candidate and composition keys.
        // TextChanged is asynchronous; flush any pending direct input before a command uses the caret.
        if (args.Key is VirtualKey.Back or VirtualKey.Delete or VirtualKey.Enter or VirtualKey.Tab or
            VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or
            VirtualKey.Home or VirtualKey.End || ModifierDown(VirtualKey.Control))
        {
            MirrorPlainText(_imeInput.Text);
            ClearImeInput();
        }
        var control = ModifierDown(VirtualKey.Control);
        if (TrySearchKey(args)) return;
        if (control && ModifierDown(VirtualKey.Shift) && args.Key == VirtualKey.C)
        {
            args.Handled = true; // Ctrl+C is the only copy shortcut.
            return;
        }
        if (control && args.Key is VirtualKey.N or VirtualKey.O or VirtualKey.S or VirtualKey.E or
            VirtualKey.Z or VirtualKey.Y or VirtualKey.C or VirtualKey.X or
            VirtualKey.V or VirtualKey.A or VirtualKey.B or VirtualKey.I or VirtualKey.K ||
            args.Key is VirtualKey.Back or VirtualKey.Delete or VirtualKey.Enter or VirtualKey.Tab or
                VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or
                VirtualKey.Home or VirtualKey.End)
            EditorCanvas_KeyDown(sender, args);
    }

    private void ImeInput_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (_disposed || _imeProgrammaticChange) return;
        if (_imeComposing)
        {
            QueueImeSync();
            return;
        }
        MirrorPlainText(_imeInput.Text);
    }

    private void ImeInput_TextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
    {
        // TextChanging runs inside the TextBox/TSF transaction, where changing the
        // document or XAML layout is unsafe. Process its latest text after it unwinds.
        if (!_imeProgrammaticChange) QueueImeSync();
    }

    private void QueueImeSync()
    {
        if (_imeSyncQueued) return;
        _imeSyncQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _imeSyncQueued = false;
                if (_disposed || !_imeAttached || _imeProgrammaticChange) return;
                if (_imeComposing) UpdateImePreview(CurrentImeText());
                else MirrorPlainText(_imeInput.Text);
            }))
            _imeSyncQueued = false;
    }

    private void MirrorPlainText(string text)
    {
        if (text == _imeObservedText) return;
        if (text.Length == 0)
        {
            _imeObservedText = "";
            _imeMirroredSourceText = "";
            _imePlainRange = null;
            return;
        }
        if (!IsSupportedInput(text)) return;
        var before = _editorText.Capture();
        TextSelection? selection = null;
        string inserted;
        if (text.StartsWith(_imeObservedText, StringComparison.Ordinal))
        {
            inserted = text[_imeObservedText.Length..];
            selection = CurrentEditableSelection();
        }
        else if (_imePlainRange is { } range && range.End <= before.Source.Length &&
            before.Source.GetText(range) == _imeMirroredSourceText)
        {
            // A text service may replace a previously supplied fragment (for example
            // autocorrection). Replace only the scratch fragment mirrored into the document.
            var start = new TextCaret(TextSurface.Body, before.Source.Version, range.Start,
                CaretAffinity.Downstream);
            var end = new TextCaret(TextSurface.Body, before.Source.Version, range.End,
                CaretAffinity.Upstream);
            selection = new(start, end);
            inserted = text;
        }
        else
        {
            // The scratch box is no longer adjacent to the native caret. Do not replay
            // its entire stale contents into a different place in the document. In
            // particular, do not mutate the TSF store inside CompositionStarted.
            QueueImeClear();
            return;
        }
        if (selection is not { } editable || inserted.Length == 0) return;
        var startOffset = _imePlainRange?.Start ?? editable.Range.Start;
        var append = text.StartsWith(_imeObservedText, StringComparison.Ordinal);
        inserted = PrepareTableInsertion(before, editable, inserted);
        var edit = TextEditingOperations.Replace(_editorText, editable, inserted);
        _imeMirroredSourceText = append ? _imeMirroredSourceText + inserted : inserted;
        _imePlainRange = new(startOffset, _imeMirroredSourceText.Length);
        _imeObservedText = text;
        ApplyEdit(edit, before, editable, editable.IsEmpty ? HistoryEditKind.Typing : HistoryEditKind.Replace);
        QueueImeClear();
    }

    private void ImeInput_CompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
    {
        if (_disposed || CurrentEditableSelection() is null) return;
        _imeInsideCompositionEvent = true;
        try
        {
            _imeBoxStart = Math.Clamp(args.StartIndex, 0, sender.Text.Length);
            // TextChanged can still be queued when TSF starts composition. Mirror the
            // already-committed prefix before taking the undo snapshot, without
            // moving/resizing the TSF host from inside this transaction.
            MirrorPlainText(sender.Text[.._imeBoxStart]);
            if (CurrentEditableSelection() is not { } current) return;
            _history.BreakCoalescing();
            _imeBefore = _editorText.Capture();
            _imeBeforeSelection = current;
            _imePreviewRange = current.Range;
            _imePreviewText = _imeBefore.Source.GetText(current.Range);
            _imeComposing = true;
            _caretTimer.Stop(); // Composition has its own underline, not a blinking native caret.
            QueueImeSync();
        }
        finally { _imeInsideCompositionEvent = false; }
        UpdateStatusIfReady();
    }

    private void ImeInput_CompositionChanged(TextBox sender, TextCompositionChangedEventArgs args)
    {
        if (_imeComposing)
        {
            _imeBoxStart = Math.Clamp(args.StartIndex, 0, sender.Text.Length);
            // Native text/layout updates run after the TSF event unwinds. Mutating
            // XAML geometry here can notify TSF again and make its candidate window jump.
            QueueImeSync();
        }
    }

    private void ImeInput_CompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        if (!_imeComposing) return;
        _imeBoxStart = Math.Clamp(args.StartIndex, 0, sender.Text.Length);
        QueueImeFinish();
    }

    private void QueueImeFinish()
    {
        if (!_imeComposing || _imeFinishQueued) return;
        _imeFinishQueued = true;
        // Do not clear the TSF store from inside its composition transaction. A final
        // asynchronous TextChanged may still contain the committed Chinese candidate.
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _imeFinishQueued = false;
                if (!_disposed && _imeComposing) FinishImeComposition();
            }))
            _imeFinishQueued = false;
    }

    private string CurrentImeText()
    {
        var text = _imeInput.Text;
        return text[Math.Clamp(_imeBoxStart, 0, text.Length)..];
    }

    private void UpdateImePreview(string text)
    {
        if (!_imeComposing || _imeBefore is null) return;
        var source = _editorText.Capture().Source;
        if (!IsSupportedInput(text) || _imePreviewRange.End > source.Length)
            return;
        var start = new TextCaret(TextSurface.Body, source.Version, _imePreviewRange.Start,
            CaretAffinity.Downstream);
        var end = new TextCaret(TextSurface.Body, source.Version, _imePreviewRange.End,
            CaretAffinity.Upstream);
        text = PrepareTableInsertion(_editorText.Capture(), new(start, end), text);
        if (text == _imePreviewText) return;
        var edit = TextEditingOperations.Replace(_editorText, new(start, end), text);
        _imePreviewRange = new(_imePreviewRange.Start, text.Length);
        _imePreviewText = text;
        ApplyCommittedSelection(edit.Selection, edit.Change.Changed);
        UpdateImeAnchor();
    }

    private void FinishImeComposition(string? finalText = null)
    {
        if (!_imeComposing || _imeBefore is not { } before) return;
        finalText ??= CurrentImeText();
        if (finalText.Length == 0)
        {
            CancelImeComposition();
            return;
        }
        UpdateImePreview(finalText);
        var original = before.Source.GetText(_imeBeforeSelection.Range);
        TextSelection selection;
        if (_imePreviewText == original)
        {
            selection = RestoreImeBefore(before);
            _history.CancelComposition(before);
        }
        else
        {
            selection = _selection ?? throw new InvalidOperationException("IME selection was lost.");
            _history.RecordComposition(before, _imeBeforeSelection, _imeBeforeSelection.Range,
                _imePreviewText, selection, DateTimeOffset.UtcNow);
        }
        _imeComposing = false;
        _imeBefore = null;
        _imePreviewText = "";
        _imeFinishQueued = false;
        var sourceStart = _imePlainRange?.Start ?? _imeBeforeSelection.Range.Start;
        var sourceEnd = Math.Clamp(selection.Focus.Offset, sourceStart, _editorText.Capture().Source.Length);
        _imePlainRange = new(sourceStart, sourceEnd - sourceStart);
        _imeMirroredSourceText = _editorText.Capture().Source.GetText(_imePlainRange.Value);
        _imeObservedText = _imeInput.Text;
        QueueImeClear();
        ApplyCommittedSelection(selection, changed: _editorText.Capture().Source.Version != before.Source.Version,
            immediate: true);
        ApplyPendingImeClick();
    }

    private void CancelImeComposition()
    {
        if (!_imeComposing || _imeBefore is not { } before) return;
        var selection = RestoreImeBefore(before);
        _history.CancelComposition(before);
        _imeComposing = false;
        _imeBefore = null;
        _imePreviewText = "";
        _imeFinishQueued = false;
        ClearImeInput();
        if (!_disposed) ApplyCommittedSelection(selection,
            changed: _editorText.Capture().Source.Version != before.Source.Version, immediate: true);
        if (!_disposed) ApplyPendingImeClick();
    }

    private TextSelection RestoreImeBefore(StyledDocumentSnapshot before)
    {
        var source = _editorText.Capture().Source;
        var original = before.Source.GetText(_imeBeforeSelection.Range);
        if (_imePreviewText != original)
            _editorText.Restore(_imePreviewRange, _imePreviewText, original, before.Styles, source.Version);
        else if (!before.Styles.SequenceEqual(_editorText.Capture().Styles))
            _editorText.RestoreStyles(before.Styles, source.Version);
        var version = _editorText.Capture().Source.Version;
        return new(_imeBeforeSelection.Anchor with { SourceVersion = version },
            _imeBeforeSelection.Focus with { SourceVersion = version });
    }

    private void ClearImeInput()
    {
        _imeClearRevision++;
        _imeProgrammaticChange = true;
        try { _imeInput.Text = ""; }
        finally { _imeProgrammaticChange = false; }
        _imeObservedText = "";
        _imeMirroredSourceText = "";
        _imePlainRange = null;
        _imeBoxStart = 0;
        UpdateImeAnchor();
    }

    private void QueueImeClear()
    {
        var revision = ++_imeClearRevision;
        var expected = _imeInput.Text;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_disposed || !_imeAttached || _imeComposing || revision != _imeClearRevision ||
                _imeInput.Text != expected) return;
            var source = _editorText.Capture().Source;
            if (_renderer?.Document?.SourceVersion == source.Version) ClearImeInput();
        });
    }

    private static bool IsSupportedInput(string text) => text.All(value =>
        !char.IsControl(value) && value is not ('\u2028' or '\u2029' or '\u00AD'));

    private void UpdateImeAnchor()
    {
        if (_imeInsideCompositionEvent || !_imeAttached || _canvas is not { } canvas || _renderer?.Document is not { } document ||
            _selection is not { Anchor.Surface: TextSurface.Body } selection) return;
        var styled = _editorText.Capture();
        // Keep the last valid candidate anchor while the native paragraph catches up.
        // Never alternate between old-layout start coordinates and new-layout end coordinates.
        if (_imeComposing && document.SourceVersion != styled.Source.Version) return;
        var map = document.BodyInteraction;
        var offset = _imePlainRange?.Start ?? (_imeComposing ? _imePreviewRange.Start : selection.Focus.Offset);
        var caret = map.Resolve(new(TextSurface.Body, map.Source.Version,
            Math.Clamp(offset, 0, map.Source.Length), CaretAffinity.Downstream));
        if (caret is null) return;
        var viewport = PresentedViewport(canvas);
        var point = viewport.ToView(new(caret.Value.X, caret.Value.Y), _scroll);
        var style = ReflowContent.StyleFor(styled.StyleAt(Math.Clamp(offset, 0, styled.Source.Length)));
        var fontSize = style.FontSize * viewport.Zoom;
        if (!_imeComposing && _imeInput.FontSize != fontSize) _imeInput.FontSize = fontSize;
        var family = _typography.TextFamily(style.Family,
            style.Family == MDEditor.Typesetting.Typography.TypographyPreset.CodeFamily);
        if (!_imeComposing && _imeInput.FontFamily.Source != family) _imeInput.FontFamily = new FontFamily(family);
        var available = Math.Max(28, canvas.ActualWidth - point.X - 2);
        var desired = Math.Max(36, _imeInput.Text.Length * fontSize * 0.95 + 20);
        var width = Math.Min(available, desired);
        // TSF owns horizontal scrolling during composition. Changing the scratch
        // viewport every keystroke would invalidate GetRectFromCharacterIndex.
        if (!_imeComposing && _imeInput.Width != width) _imeInput.Width = width;
        width = _imeInput.Width;
        var height = Math.Max(30, caret.Value.Height * viewport.Zoom + 6);
        if (!_imeComposing && _imeInput.Height != height) _imeInput.Height = height;
        height = _imeInput.Height;
        var x = point.X;
        var y = point.Y - 3;
        if (_imeComposing && _imeInput.Text.Length > 0 &&
            document.SourceVersion == styled.Source.Version &&
            map.Resolve(new(TextSurface.Body, map.Source.Version, _imePreviewRange.End,
                CaretAffinity.Downstream)) is { } nativeEnd && _imeInput.ActualWidth > 0)
        {
            // Align the TSF-owned caret, rather than the box origin, to the native caret.
            // This keeps candidates next to a composition that reflowed onto another line.
            var local = _imeInput.GetRectFromCharacterIndex(_imeInput.Text.Length - 1, true);
            var end = viewport.ToView(new(nativeEnd.X, nativeEnd.Y), _scroll);
            x = end.X - local.X;
            y = end.Y - local.Y;
        }
        x = Math.Clamp(x, -Math.Max(0, width - 28), Math.Max(0, canvas.ActualWidth - 28));
        y = Math.Clamp(y, -Math.Max(0, height - 28), Math.Max(0, canvas.ActualHeight - 28));
        if (XamlCanvas.GetLeft(_imeInput) != x) XamlCanvas.SetLeft(_imeInput, x);
        if (XamlCanvas.GetTop(_imeInput) != y) XamlCanvas.SetTop(_imeInput, y);
        var waitingForLayout = document.SourceVersion != styled.Source.Version;
        // The native document is the sole visual composition owner. The TextBox
        // only hosts TSF; showing it until each layout commit causes duplicate/flashing text.
        if (!waitingForLayout && !_imeComposing && _imeInput.Text.Length > 0)
            QueueImeClear();
    }
}
