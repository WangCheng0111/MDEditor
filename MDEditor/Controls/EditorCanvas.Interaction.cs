using System;
using System.Numerics;
using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Native.Text;
using MDEditor.Typesetting.Layout;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace MDEditor.Controls;

public sealed partial class EditorCanvas
{
    private TextSelection? _selection;
    private (ITextInteractionMap Map, TextCaret Caret)? _pendingImeClick;
    private uint? _dragPointerId;
    private Point? _dragStartPoint;
    private bool _dragMoved;
    private bool _caretVisible = true, _interactionFocused;
    private readonly DispatcherTimer _caretTimer = new() { Interval = TimeSpan.FromMilliseconds(530) };

    private void AttachInteraction(CanvasControl canvas)
    {
        canvas.PointerPressed += Canvas_PointerPressed;
        canvas.PointerMoved += Canvas_PointerMoved;
        canvas.PointerReleased += Canvas_PointerReleased;
        canvas.PointerCanceled += Canvas_PointerCanceled;
        canvas.PointerCaptureLost += Canvas_PointerCaptureLost;
        _caretTimer.Tick += CaretTimer_Tick;
        GotFocus += EditorCanvas_GotFocus;
        LostFocus += EditorCanvas_LostFocus;
    }

    private void DetachInteraction(CanvasControl canvas)
    {
        canvas.PointerPressed -= Canvas_PointerPressed;
        canvas.PointerMoved -= Canvas_PointerMoved;
        canvas.PointerReleased -= Canvas_PointerReleased;
        canvas.PointerCanceled -= Canvas_PointerCanceled;
        canvas.PointerCaptureLost -= Canvas_PointerCaptureLost;
        _caretTimer.Stop(); _caretTimer.Tick -= CaretTimer_Tick;
        GotFocus -= EditorCanvas_GotFocus; LostFocus -= EditorCanvas_LostFocus;
        _dragPointerId = null; _interactionFocused = false;
        _dragStartPoint = null; _dragMoved = false;
    }

    private ITextInteractionMap? InteractionMap(ReflowDocument document, TextSurface surface) =>
        surface == TextSurface.Body ? document.BodyInteraction : document.MathInteraction;

    private (ITextInteractionMap Map, TextCaret Caret)? HitTestInteraction(CanvasControl canvas,
        PointerRoutedEventArgs args, TextSurface? restricted = null)
    {
        if (_renderer?.Document is not { } document) return null;
        var viewport = PresentedViewport(canvas);
        var point = args.GetCurrentPoint(canvas).Position;
        if (!viewport.ContainsDocumentViewX(point.X)) return null;
        var location = viewport.ToDocument(new(point.X, point.Y), _scroll);
        var body = document.BodyInteraction;
        var math = document.MathInteraction;
        if (restricted != TextSurface.MarkdownMath && document.TryHitTableRow(location, out var tableCaret))
            return tableCaret is { } insideCell ? (body, insideCell) : null;
        var map = restricted switch
        {
            TextSurface.Body => body,
            TextSurface.MarkdownMath => math,
            _ => math is not null && math.DistanceToY(location.Y) < body.DistanceToY(location.Y) ? math : body
        };
        var caret = map is MarkdownProjectionInteractionMap projected
            ? projected.HitTestForEditing(location) : map?.HitTest(location);
        if (map is null || caret is null) return null;
        return (map, caret.Value);
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not CanvasControl canvas || !args.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
        var hit = HitTestInteraction(canvas, args);
        if (hit is null) return;
        if (_imeComposing)
        {
            _pendingImeClick = hit;
            Focus(FocusState.Pointer);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_imeComposing && _pendingImeClick is not null) QueueImeFinish();
            });
            args.Handled = true;
            return;
        }
        // A queued TextChanged may still own the last English keystroke. Transfer it
        // before changing the native selection, then reset the scratch TSF text store.
        MirrorPlainText(_imeInput.Text);
        ClearImeInput();
        var current = _editorText.Capture();
        if (_renderer?.Document is { } taskDocument && taskDocument.Presentation?.Text.Source.Version ==
            current.Source.Version)
        {
            var pointer = args.GetCurrentPoint(canvas).Position;
            var location = PresentedViewport(canvas).ToDocument(new(pointer.X, pointer.Y), _scroll);
            if (taskDocument.HitTaskCheckbox(location) is { } taskOffset)
            {
                var plan = MarkdownBlockCommands.Format(current.Source, new(taskOffset, 0),
                    MarkdownBlockCommand.ToggleTask);
                if (plan is not null)
                {
                    var taskCaret = new TextCaret(TextSurface.Body, current.Source.Version, taskOffset,
                        CaretAffinity.Downstream);
                    ApplySourcePlan(plan, current, new(taskCaret, taskCaret));
                    args.Handled = true;
                    return;
                }
            }
        }
        _history.BreakCoalescing();
        var caret = RebaseBodyHit(hit.Value.Map, hit.Value.Caret);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        var extend = (shift & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0 &&
            _selection is { } previous && previous.Anchor.Surface == caret.Surface &&
            previous.Anchor.SourceVersion == caret.SourceVersion;
        _selection = new(extend ? _selection!.Value.Anchor : caret, caret);
        UpdateMarkdownReveal();
        _preferredCaretX = null; _followCaretAfterCommit = false; _inputFeedback = null;
        _dragPointerId = args.Pointer.PointerId;
        _dragStartPoint = args.GetCurrentPoint(canvas).Position;
        _dragMoved = false;
        canvas.CapturePointer(args.Pointer);
        Focus(FocusState.Pointer);
        if (caret.Surface == TextSurface.Body) FocusImeInput();
        _interactionFocused = true; ResetCaretBlink(); InvalidateInteraction(); UpdateStatusIfReady();
        args.Handled = true;
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not CanvasControl canvas || _dragPointerId != args.Pointer.PointerId || _selection is not { } selection) return;
        var point = args.GetCurrentPoint(canvas).Position;
        if (!_dragMoved && _dragStartPoint is { } start)
        {
            var dx = point.X - start.X;
            var dy = point.Y - start.Y;
            if (dx * dx + dy * dy < 16) return;
            _dragMoved = true;
        }
        var viewport = PresentedViewport(canvas);
        var oldScroll = _scroll;
        if (point.Y < DocumentViewport.Padding)
            _scroll -= Math.Min(24, DocumentViewport.Padding - point.Y) / viewport.Zoom;
        else if (point.Y > canvas.ActualHeight - DocumentViewport.Padding)
            _scroll += Math.Min(24, point.Y - canvas.ActualHeight + DocumentViewport.Padding) / viewport.Zoom;
        UpdateScroll(viewport);
        if (_scroll != oldScroll) Invalidate();
        var hit = HitTestInteraction(canvas, args, selection.Anchor.Surface);
        if (hit is null) return;
        var caret = RebaseBodyHit(hit.Value.Map, hit.Value.Caret);
        if (caret != selection.Focus)
        {
            _selection = selection with { Focus = caret };
            ResetCaretBlink(); InvalidateInteraction(); UpdateStatusIfReady();
        }
        args.Handled = true;
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not CanvasControl canvas || _dragPointerId != args.Pointer.PointerId) return;
        if (_dragMoved) Canvas_PointerMoved(sender, args);
        else if (_selection is { IsEmpty: true } && _renderer?.Document is { } document)
        {
            var point = args.GetCurrentPoint(canvas).Position;
            var location = PresentedViewport(canvas).ToDocument(new(point.X, point.Y), _scroll);
            if (HitEditableMathSource(document, location) is { } mathCaret)
            {
                _selection = new(mathCaret, mathCaret);
                ResetCaretBlink(); InvalidateInteraction();
            }
        }
        _dragPointerId = null;
        _dragStartPoint = null; _dragMoved = false;
        canvas.ReleasePointerCapture(args.Pointer);
        UpdateMarkdownReveal();
        args.Handled = true;
    }

    private void Canvas_PointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_dragPointerId == args.Pointer.PointerId)
        { _dragPointerId = null; _dragStartPoint = null; _dragMoved = false; }
    }

    private void Canvas_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_dragPointerId == args.Pointer.PointerId)
        { _dragPointerId = null; _dragStartPoint = null; _dragMoved = false; }
    }

    private TextCaret RebaseBodyHit(ITextInteractionMap map, TextCaret caret)
    {
        if (caret.Surface != TextSurface.Body) return caret;
        var current = _editorText.Capture().Source;
        if (map.Source.Version == current.Version) return caret;
        var oldText = map.Source.Text;
        var newText = current.Text;
        var prefix = 0;
        while (prefix < oldText.Length && prefix < newText.Length && oldText[prefix] == newText[prefix]) prefix++;
        var suffix = 0;
        while (suffix < oldText.Length - prefix && suffix < newText.Length - prefix &&
            oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix]) suffix++;
        var oldChangeEnd = oldText.Length - suffix;
        var newChangeEnd = newText.Length - suffix;
        var offset = caret.Offset <= prefix ? caret.Offset :
            caret.Offset >= oldChangeEnd ? caret.Offset + newText.Length - oldText.Length :
            caret.Offset - prefix < oldChangeEnd - caret.Offset ? prefix : newChangeEnd;
        return caret with { SourceVersion = current.Version, Offset = Math.Clamp(offset, 0, current.Length) };
    }

    private void ApplyPendingImeClick()
    {
        if (_pendingImeClick is not { } pending) return;
        _pendingImeClick = null;
        var caret = RebaseBodyHit(pending.Map, pending.Caret);
        _selection = new(caret, caret);
        UpdateMarkdownReveal();
        _preferredCaretX = null;
        _followCaretAfterCommit = false;
        _inputFeedback = null;
        if (caret.Surface == TextSurface.Body)
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed && _selection?.Focus == caret) FocusImeInput();
            });
        ResetCaretBlink();
        UpdateStatusIfReady();
    }

    private void EditorCanvas_GotFocus(object sender, RoutedEventArgs args)
    { _interactionFocused = true; ResetCaretBlink(); InvalidateInteraction(); }

    private void EditorCanvas_LostFocus(object sender, RoutedEventArgs args)
    {
        if (_imeInput.FocusState != Microsoft.UI.Xaml.FocusState.Unfocused) return;
        _interactionFocused = false; _caretTimer.Stop(); InvalidateInteraction();
    }

    private void CaretTimer_Tick(object? sender, object args)
    {
        _caretVisible = !_caretVisible; InvalidateInteraction();
    }

    private void ResetCaretBlink()
    {
        _caretVisible = true;
        _caretTimer.Stop();
        if (_interactionFocused && _selection?.IsEmpty == true && !_imeComposing) _caretTimer.Start();
        InvalidateInteraction();
    }

    private void DrawInteraction(CanvasDrawingSession session, CanvasControl canvas)
    {
        if (_renderer?.Document is not { } document || _selection is not { } selection) return;
        var map = InteractionMap(document, selection.Anchor.Surface);
        if (map is null) return;
        var viewport = PresentedViewport(canvas);
        var previous = session.Transform;
        using var clip = session.CreateLayer(1, new Rect(0, 0, viewport.Width, viewport.Height));
        try
        {
            session.Transform = Matrix3x2.CreateScale(viewport.Zoom) *
                Matrix3x2.CreateTranslation((float)DocumentViewport.Padding,
                    (float)(DocumentViewport.Padding - _scroll * viewport.Zoom)) * previous;
            DrawSearchMatches(session, document, _scroll, _scroll + viewport.DocumentHeight);
            if (selection.Anchor.Surface == TextSurface.Body &&
                map.Source.Version == _editorText.Capture().Source.Version)
                foreach (var diagnostic in FormulaDiagnostics())
                {
                    var start = new TextCaret(TextSurface.Body, map.Source.Version,
                        diagnostic.Source.Start, CaretAffinity.Downstream);
                    var end = new TextCaret(TextSurface.Body, map.Source.Version,
                        diagnostic.Source.End, CaretAffinity.Upstream);
                    foreach (var rectangle in map.SelectionRects(new TextSelection(start, end)))
                        session.DrawLine((float)rectangle.X, (float)(rectangle.Bottom - 1.5 / viewport.Zoom),
                            (float)rectangle.Right, (float)(rectangle.Bottom - 1.5 / viewport.Zoom),
                            _githubTheme.Danger, 1.5f / viewport.Zoom);
                }
            if (!selection.IsEmpty)
            {
                foreach (var rectangle in map.SelectionRects(selection))
                    session.FillRectangle(new Rect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height),
                        _githubTheme.Selection);
            }
            if (_imeComposing && selection.Anchor.Surface == TextSurface.Body &&
                _imePreviewText.Length > 0 && _imePreviewRange.Length > 0)
            {
                var preview = new TextSelection(
                    new(TextSurface.Body, map.Source.Version, _imePreviewRange.Start, CaretAffinity.Downstream),
                    new(TextSurface.Body, map.Source.Version, _imePreviewRange.End, CaretAffinity.Upstream));
                if (map.Source.Version == _editorText.Capture().Source.Version)
                    foreach (var rectangle in map.SelectionRects(preview))
                        session.DrawLine((float)rectangle.X, (float)(rectangle.Bottom - 1.5 / viewport.Zoom),
                            (float)rectangle.Right, (float)(rectangle.Bottom - 1.5 / viewport.Zoom),
                            _githubTheme.Accent, 1.5f / viewport.Zoom);
            }
            else if (_interactionFocused && _caretVisible && map.Resolve(selection.Focus) is { } caret)
                session.FillRectangle(new Rect(caret.X, caret.Y, 1.4 / viewport.Zoom, caret.Height),
                    _githubTheme.Caret);
        }
        finally { session.Transform = previous; }
    }

    private string InteractionStatus()
    {
        if (_imeComposing) return " · 输入法组词中";
        if (_inputFeedback is not null) return $" · {_inputFeedback}";
        if (_selection is not { } selection) return " · 点击正文以定位光标";
        if (selection.Anchor.Surface != TextSurface.Body) return " · 数学样张只读";
        if (FormulaDiagnosticStatus() is { } formulaError) return formulaError;
        if (ImageDiagnosticStatus() is { } imageError) return imageError;
        if (ReferenceDiagnosticStatus() is { } referenceError) return referenceError;
        return selection.IsEmpty ? "" : $" · 已选 {selection.Range.Length} 字符";
    }
}
