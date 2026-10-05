using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MDEditor.Core.Text;
using MDEditor.Native.Rendering;
using MDEditor.Native.Text;
using MDEditor.Typesetting.Layout;
using MDEditor.Typesetting.Typography;
using MDEditor.ViewModels;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace MDEditor.Controls;

/// <summary>Native reflow/zoom/scroll host with source-mapped pointer caret and selection.</summary>
public sealed partial class EditorCanvas : UserControl, IDisposable
{
    public EditorViewportViewModel ViewModel { get; } = new();
    private StyledDocumentBuffer _editorText = ReflowSample.CreateEditableBuffer();
    private bool _showSampleMathPage = true;
    private TypographyPreset _typography = TypographyPreset.Balanced;
    private long _typographyRevision;
    private long _documentIdentity;
    private static readonly Color EditorSurfaceClearColor = Color.FromArgb(0, 0, 0, 0);
    private readonly Grid _root = new();
    private readonly Grid _surface = new();
    private readonly ScrollBar _scrollBar = new() { Orientation = Orientation.Vertical, Width = 16, Minimum = 0 };
    private readonly TextBlock _status = new()
    {
        Margin = new Thickness(16, 8, 16, 8), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 75, 85, 100)), Text = "原生正文准备中…"
    };
    private readonly LayoutRequestGate _gate = new();
    private bool _rebuilding;
    private bool _editingStatusPending;
    private readonly DispatcherTimer _editStatusTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private long _latestEditRevision;
    private const int LongDocumentLineThreshold = 512;
    private CanvasControl? _canvas;
    private CanvasControl? _headingCanvas;
    private CanvasControl? _mathHeadingCanvas;
    private CanvasControl? _interactionCanvas;
    private string? _headingLabel;
    private int _headingZoom;
    private double _headingScroll;
    private double? _mathHeadingY;
    private double? _mathPageWidth;
    private string? _mathPageFingerprint;
    private int _mathHeadingZoom;
    private double _mathHeadingScroll;
    private CanvasDevice? _device;
    private EditorCanvasRenderer? _renderer;
    private CancellationTokenSource? _cancellation;
    private bool _disposed, _pending, _updatingScroll;
    private double _targetWidth, _scroll;
    private string? _error;

    public EditorCanvas()
    {
        _history = new TextEditHistory(_editorText);
        DataContext = ViewModel;
        IsTabStop = true;
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new() { Height = new GridLength(36) });
        _root.Background = new SolidColorBrush(EditorSurfaceClearColor);
        InitializeTheme();
        _surface.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _surface.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(_scrollBar, 1); _surface.Children.Add(_scrollBar);
        _scrollBar.ValueChanged += Scroll_ValueChanged;
        _surface.PointerWheelChanged += Surface_Wheel;
        Grid.SetRow(_surface, 0); Grid.SetRow(_status, 1);
        _root.Children.Add(_surface); _root.Children.Add(_status);
        Content = _root;
        InitializeSearch();
        AddAccelerator(VirtualKey.E, _ => ExportPdfRequested?.Invoke(this, EventArgs.Empty),
            VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        AddAccelerator(VirtualKey.Add, ViewModel.ZoomInCommand.Execute);
        AddAccelerator((VirtualKey)187, ViewModel.ZoomInCommand.Execute);
        AddAccelerator((VirtualKey)187, ViewModel.ZoomInCommand.Execute, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        AddAccelerator(VirtualKey.Subtract, ViewModel.ZoomOutCommand.Execute);
        AddAccelerator((VirtualKey)189, ViewModel.ZoomOutCommand.Execute);
        AddAccelerator(VirtualKey.Number0, ViewModel.ResetZoomCommand.Execute);
        KeyDown += EditorCanvas_KeyDown;
        CharacterReceived += EditorCanvas_CharacterReceived;
        ViewModel.PropertyChanged += ViewModel_Changed;
        Loaded += EditorCanvas_Loaded; Unloaded += EditorCanvas_Unloaded;
        _editStatusTimer.Tick += EditStatusTimer_Tick;
    }

    /// <summary>Presentation setting retained independently of toolbar controls.</summary>
    public void SetTypographyPreset(TypographyPreset selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (_disposed || selected == _typography) return;
        _typography = selected;
        _typographyRevision++;
        _renderer?.SetContent(EditableContent());
        RequestLayout(force: true, immediate: true);
        InvalidateInteraction();
    }

    private void AddAccelerator(VirtualKey key, Action<object?> execute, VirtualKeyModifiers modifiers = VirtualKeyModifiers.Control)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) => { execute(null); args.Handled = true; };
        KeyboardAccelerators.Add(accelerator);
    }
    private DocumentViewport Viewport(CanvasControl canvas) =>
        new(canvas.ActualWidth, canvas.ActualHeight, ViewModel.ZoomPercent, canvas.Dpi);
    private DocumentViewport PresentedViewport(CanvasControl canvas) =>
        new(canvas.ActualWidth, canvas.ActualHeight, _renderer?.PresentedZoomPercent ?? ViewModel.ZoomPercent, canvas.Dpi);
    public void Invalidate()
    {
        if (!_disposed && _canvas is { ReadyToDraw: true } canvas) canvas.Invalidate();
        InvalidateHeadingIfChanged();
        InvalidateInteraction();
    }
    private void InvalidateHeadingIfChanged()
    {
        var label = _renderer?.Document?.Sections.FirstOrDefault()?.Style.Label;
        var zoom = _renderer?.PresentedZoomPercent ?? ViewModel.ZoomPercent;
        if (label != _headingLabel || zoom != _headingZoom || _scroll != _headingScroll)
        {
            _headingLabel = label; _headingZoom = zoom; _headingScroll = _scroll;
            if (!_disposed && _headingCanvas is { ReadyToDraw: true } heading) heading.Invalidate();
        }
        var mathY = _renderer?.Document?.MathHeadingY;
        var mathWidth = mathY is null ? null : _renderer?.Document?.Width;
        var mathFingerprint = _renderer?.Document?.MathPageFingerprint;
        if (mathY != _mathHeadingY || mathWidth != _mathPageWidth ||
            mathFingerprint != _mathPageFingerprint || zoom != _mathHeadingZoom || _scroll != _mathHeadingScroll)
        {
            _mathHeadingY = mathY; _mathPageWidth = mathWidth; _mathPageFingerprint = mathFingerprint;
            _mathHeadingZoom = zoom; _mathHeadingScroll = _scroll;
            if (!_disposed && _mathHeadingCanvas is { ReadyToDraw: true } mathHeading) mathHeading.Invalidate();
        }
    }
    private void InvalidateInteraction()
    {
        if (!_disposed && _interactionCanvas is { ReadyToDraw: true } overlay) overlay.Invalidate();
        UpdateImeAnchor();
        NotifyAccessibilityChanges();
    }
    private void EditorCanvas_Loaded(object sender, RoutedEventArgs args)
    {
        if (_disposed || _canvas is not null) return;
        ApplyCurrentTheme();
        _renderer = new EditorCanvasRenderer();
        _renderer.SetTheme(_githubTheme);
        _renderer.SetContent(EditableContent());
        _renderer.SetCodeHighlight(_codeHighlight);
        if (_markdownLayouts is not null) _renderer.SetMarkdownMathLayouts(_markdownLayouts);
        var canvas = new CanvasControl
        {
            ClearColor = EditorSurfaceClearColor,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
        };
        canvas.CreateResources += Canvas_CreateResources;
        canvas.Draw += Canvas_Draw; canvas.SizeChanged += Canvas_SizeChanged;
        AttachInteraction(canvas);
        var heading = new CanvasControl
        {
            ClearColor = Color.FromArgb(0, 0, 0, 0), IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
        };
        heading.Draw += HeadingCanvas_Draw;
        var mathHeading = new CanvasControl
        {
            ClearColor = Color.FromArgb(0, 0, 0, 0), IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
        };
        mathHeading.Draw += MathHeadingCanvas_Draw;
        var overlay = new CanvasControl
        {
            ClearColor = Color.FromArgb(0, 0, 0, 0), IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
        };
        overlay.Draw += InteractionCanvas_Draw;
        _canvas = canvas; _headingCanvas = heading; _mathHeadingCanvas = mathHeading; _interactionCanvas = overlay;
        _surface.Children.Insert(0, canvas); _surface.Children.Insert(1, heading);
        _surface.Children.Insert(2, mathHeading); _surface.Children.Insert(3, overlay);
        AttachImeInput();
        _ = InitializeMarkdownMathAsync();
    }
    private void EditorCanvas_Unloaded(object sender, RoutedEventArgs args)
    { _markdownMathCancellation?.Cancel(); _editableMathCancellation?.Cancel(); DetachImeInput(); ReleaseSurface(); }

    private void Canvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        if (_disposed || !ReferenceEquals(sender, _canvas)) return;
        // Brushes/font bindings are DPI-independent. Only a genuinely new device revokes them.
        if (args.Reason == CanvasCreateResourcesReason.DpiChanged && ReferenceEquals(_device, sender.Device) && _renderer!.HasResources)
        {
            RequestLayout(); Invalidate(); UpdateStatus(); return;
        }
        Revoke();
        _renderer!.CreateResources(sender.Device);
        ReleaseImageResources();
        _device = sender.Device;
        RequestImageResources();
        _renderer.SetContent(EditableContent());
        RequestLayout(force: true, immediate: true);
    }
    private void ViewModel_Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ViewModel.ZoomPercent)) return;
        RequestLayout(immediate: true);
    }
    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        _headingCanvas?.Invalidate();
        _mathHeadingCanvas?.Invalidate();
        RequestLayout();
    }

    private void Revoke()
    {
        _cancellation?.Cancel(); _gate.Next(); _pending = false;
    }
    private void RequestLayout(bool force = false, bool immediate = false)
    {
        if (_disposed || _canvas is not { } canvas || _renderer?.HasResources != true) return;
        var viewport = Viewport(canvas);
        if (!viewport.IsVisible) { Revoke(); UpdateStatus(); return; }
        var oldDocument = _renderer.Document; var oldZoom = _renderer.PresentedZoomPercent;
        if (!force && _renderer.TryReuseLayout(viewport))
        {
            var hadPending = _pending;
            if (_pending) Revoke();
            _error = null;
            var changed = !ReferenceEquals(oldDocument, _renderer.Document) || oldZoom != _renderer.PresentedZoomPercent;
            if (changed)
            {
                if (!hadPending) _gate.Next();
                RestoreAnchor(_renderer.Document!, oldDocument);
            }
            UpdateScroll(viewport);
            if (_followCaretAfterCommit) RevealCaret(_renderer.Document, canvas);
            Invalidate(); UpdateStatus();
            return;
        }
        if (!force && _pending && _targetWidth == viewport.DocumentWidth)
        {
            UpdateScroll(PresentedViewport(canvas)); Invalidate(); return;
        }
        // Resize revokes publication, NOT useful in-flight cache work. There is one latest-value pump.
        // Cache-only layout has a small budget and never waits for native worker locks or shapes new text.
        var longDocument = _editorText.Capture().Lines.Count > LongDocumentLineThreshold;
        if (immediate || longDocument && _rebuilding) _cancellation?.Cancel();
        _gate.Next(); _targetWidth = viewport.DocumentWidth; _pending = true; _error = null;
        if (!force && !longDocument && _renderer.Engine.TryBuildCached(_targetWidth, out var cachedLayout))
        {
            RestoreAnchor(cachedLayout!, _renderer.Document);
            _renderer.Commit(cachedLayout!, ViewModel.ZoomPercent); _pending = false;
            UpdateScroll(PresentedViewport(canvas));
            if (_followCaretAfterCommit) RevealCaret(_renderer.Document, canvas);
            Invalidate();
        }
        else RebuildAsync();
        UpdateStatus();
    }
    private void RebuildAsync()
    {
        if (_rebuilding) return;
        _rebuilding = true;
        AdvanceRebuild();
    }
    private void AdvanceRebuild()
    {
        if (_disposed || !_pending || _canvas is not { } canvas || _device is not { } device || _renderer is not { } renderer)
        {
            _rebuilding = false;
            return;
        }
        var revision = _gate.Revision; var width = _targetWidth; var zoomPercent = ViewModel.ZoomPercent;
        var typographyRevision = _typographyRevision;
        var documentIdentity = _documentIdentity;
        var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        Task<ReflowDocument> task;
        try
        {
            task = _editorText.Capture().Lines.Count <= LongDocumentLineThreshold &&
                renderer.Engine.TryBuildCached(width, out var cached)
                ? Task.FromResult(cached!) : renderer.Engine.BuildAsync(width, cancellation.Token);
        }
        catch (Exception ex)
        {
            task = Task.FromException<ReflowDocument>(ex);
        }
        // Native work completes off-thread; all viewport, renderer, and control state is
        // published through the UI dispatcher in one serialized continuation.
        if (task.IsCompleted)
            CompleteRebuild(task, canvas, device, renderer, cancellation, revision, width, zoomPercent,
                typographyRevision, documentIdentity);
        else
        {
            _ = task.ContinueWith(completed =>
            {
                if (!DispatcherQueue.TryEnqueue(() => CompleteRebuild(completed, canvas, device, renderer,
                    cancellation, revision, width, zoomPercent, typographyRevision, documentIdentity)))
                {
                    if (completed.Status == TaskStatus.RanToCompletion) completed.Result.Dispose();
                    else if (completed.IsFaulted) _ = completed.Exception;
                    cancellation.Dispose();
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
    private void CompleteRebuild(Task<ReflowDocument> task, CanvasControl canvas, CanvasDevice device,
        EditorCanvasRenderer renderer, CancellationTokenSource cancellation, long revision, double width,
        int zoomPercent, long typographyRevision, long documentIdentity)
    {
        ReflowDocument? result = null;
        try
        {
            result = task.GetAwaiter().GetResult();
            if (_disposed || !ReferenceEquals(canvas, _canvas) || !ReferenceEquals(device, _device) ||
                typographyRevision != _typographyRevision || documentIdentity != _documentIdentity)
            { return; }
            if (!_gate.IsCurrent(revision))
            {
                var latestVersion = _editorText.Capture().Source.Version;
                if (result.SourceVersion == latestVersion &&
                    !ReferenceEquals(result.Presentation, _livePresentation)) return;
                var latestRequestIsEdit = _latestEditRevision == _gate.Revision;
                if (ProgressiveLayoutPublication.CanCompleteCurrent(result.SourceVersion, latestVersion,
                    width, _targetWidth, zoomPercent, ViewModel.ZoomPercent, latestRequestIsEdit))
                {
                    RestoreAnchor(result, renderer.Document);
                    renderer.Commit(result, zoomPercent); result = null;
                    _pending = false; _error = null;
                    UpdateScroll(PresentedViewport(canvas));
                    if (_followCaretAfterCommit) RevealCaret(renderer.Document, canvas);
                    Invalidate();
                }
                else if (ProgressiveLayoutPublication.CanPresentIntermediate(result.SourceVersion, latestVersion,
                    renderer.Document?.SourceVersion ?? -1, width, _targetWidth, zoomPercent,
                    ViewModel.ZoomPercent, latestRequestIsEdit))
                {
                    RestoreAnchor(result, renderer.Document);
                    renderer.PresentIntermediate(result, zoomPercent); result = null;
                    UpdateScroll(PresentedViewport(canvas)); Invalidate();
                }
                return;
            }
            RestoreAnchor(result, renderer.Document);
            renderer.Commit(result, zoomPercent); result = null; _pending = false; _error = null;
            UpdateScroll(PresentedViewport(canvas));
            if (_followCaretAfterCommit) RevealCaret(renderer.Document, canvas);
            Invalidate();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed && _gate.IsCurrent(revision))
            {
                _pending = false;
                if (device.IsDeviceLost(ex.HResult)) device.RaiseDeviceLost();
                else _error = ex.Message;
            }
        }
        finally
        {
            result?.Dispose();
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose();
            if (!_disposed && ReferenceEquals(canvas, _canvas)) UpdateStatus();
            _rebuilding = false;
            if (!_disposed && _pending && _canvas is not null && _device is not null && _renderer is not null)
                RebuildAsync();
        }
    }
    private void RestoreAnchor(ReflowDocument target, ReflowDocument? previous)
    {
        var anchor = FindAnchor(previous);
        if (anchor is not { } current) return;
        // BodyInteraction reports offsets in the editable Markdown source. The
        // layout snapshot may instead contain the shorter folded display text.
        var mapped = previous is null ? current.Source : ViewportAnchorMap.Map(
            previous.BodyInteraction, target.BodyInteraction, current.Source);
        foreach (var line in target.BodyInteraction.Lines)
            if (line.Source.End >= mapped)
            { _scroll = Math.Max(0, line.Bounds.Y + current.Offset); return; }
    }
    private (int Source, double Offset)? FindAnchor(ReflowDocument? doc)
    {
        if (doc is null || _scroll == 0) return null;
        foreach (var line in doc.BodyInteraction.Lines)
            if (line.Bounds.Bottom >= _scroll) return (line.Source.Start, _scroll - line.Bounds.Y);
        return null;
    }
    private void UpdateScroll(DocumentViewport viewport)
    {
        _scroll = viewport.ClampScroll(_scroll, _renderer?.Document?.Height ?? 0);
        _updatingScroll = true;
        try
        {
            _scrollBar.Maximum = viewport.MaximumScroll(_renderer?.Document?.Height ?? 0);
            _scrollBar.ViewportSize = viewport.DocumentHeight;
            _scrollBar.SmallChange = 48 / viewport.Zoom; _scrollBar.LargeChange = viewport.DocumentHeight * 0.85;
            _scrollBar.Value = _scroll; _scrollBar.IsEnabled = _scrollBar.Maximum > 0;
        }
        finally { _updatingScroll = false; }
    }
    private void Scroll_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_updatingScroll || _disposed) return;
        _scroll = args.NewValue; Invalidate();
        UpdateStatusIfReady();
    }
    private void Surface_Wheel(object sender, PointerRoutedEventArgs args)
    {
        if (_canvas is not { } canvas) return;
        var delta = args.GetCurrentPoint(_surface).Properties.MouseWheelDelta;
        var control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if ((control & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
        {
            if (delta > 0) ViewModel.ZoomInCommand.Execute(null); else if (delta < 0) ViewModel.ZoomOutCommand.Execute(null);
        }
        else
        {
            var viewport = PresentedViewport(canvas);
            _scroll -= delta / 120d * 48 / viewport.Zoom; UpdateScroll(viewport); Invalidate();
        }
        args.Handled = true;
    }
    private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_disposed || !ReferenceEquals(sender, _canvas) || _renderer?.HasResources != true) return;
        _renderer.Draw(args.DrawingSession, Viewport(sender), _scroll);
    }
    private void InteractionCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_disposed || !ReferenceEquals(sender, _interactionCanvas)) return;
        DrawInteraction(args.DrawingSession, sender);
    }
    private void HeadingCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_disposed || !ReferenceEquals(sender, _headingCanvas) || _renderer?.Document is null) return;
        _renderer.DrawStableHeading(args.DrawingSession, PresentedViewport(sender), _scroll);
    }
    private void MathHeadingCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_disposed || !ReferenceEquals(sender, _mathHeadingCanvas) || _renderer?.Document is null) return;
        _renderer.DrawStableMathPage(args.DrawingSession, PresentedViewport(sender), _scroll);
    }
    private void DeferEditStatus()
    {
        _editingStatusPending = true;
        _editStatusTimer.Stop(); _editStatusTimer.Start();
    }
    private void EditStatusTimer_Tick(object? sender, object args)
    {
        _editStatusTimer.Stop();
        _editingStatusPending = false;
        UpdateStatusIfReady();
    }
    private void UpdateStatusIfReady() { if (!_disposed && _canvas is not null) UpdateStatus(); }
    private void UpdateStatus()
    {
        if (_editingStatusPending) return;
        var document = _renderer?.Document;
        var state = _error is not null ? $"排版失败：{_error}" :
            _markdownMathError is not null ? $"公式载入失败：{_markdownMathError}" :
            document?.InfeasibleMathBlocks > 0 ? "部分公式在当前宽度下无法断行" :
            document?.InfeasibleParagraphs > 0 ? "部分段落在当前宽度下无法断行" :
            _pending ? "正在排版" : _markdownMathLoading ? "正在载入公式" : "就绪";
        var text = $"{state} · {ViewModel.ZoomPercent}%{(SearchViewModel.IsSourceMode ? " · 源码模式（Ctrl+/ 返回预览）" : "")}{InteractionStatus()}";
        if (_status.Text != text) _status.Text = text;
    }
    private void ReleaseSurface()
    {
        Revoke();
        var canvas = _canvas; var heading = _headingCanvas; var mathHeading = _mathHeadingCanvas;
        var overlay = _interactionCanvas; var renderer = _renderer;
        _canvas = null; _headingCanvas = null; _mathHeadingCanvas = null;
        _interactionCanvas = null; _renderer = null; _device = null;
        _automationText = null; _automationMap = null; _automationGeometry = null;
        _headingLabel = null; _headingZoom = 0; _headingScroll = 0;
        _mathHeadingY = null; _mathPageWidth = null; _mathPageFingerprint = null;
        _mathHeadingZoom = 0; _mathHeadingScroll = 0;
        if (canvas is null) return;
        canvas.CreateResources -= Canvas_CreateResources;
        canvas.Draw -= Canvas_Draw; canvas.SizeChanged -= Canvas_SizeChanged;
        DetachInteraction(canvas);
        if (overlay is not null)
        {
            overlay.Draw -= InteractionCanvas_Draw;
            overlay.RemoveFromVisualTree(); _surface.Children.Remove(overlay);
        }
        if (heading is not null)
        {
            heading.Draw -= HeadingCanvas_Draw;
            heading.RemoveFromVisualTree(); _surface.Children.Remove(heading);
        }
        if (mathHeading is not null)
        {
            mathHeading.Draw -= MathHeadingCanvas_Draw;
            mathHeading.RemoveFromVisualTree(); _surface.Children.Remove(mathHeading);
        }
        try { canvas.RemoveFromVisualTree(); _surface.Children.Remove(canvas); }
        finally { renderer?.Dispose(); ReleaseImageResources(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("Dispose the canvas on its UI thread.");
        _disposed = true; Loaded -= EditorCanvas_Loaded; Unloaded -= EditorCanvas_Unloaded;
        _root.ActualThemeChanged -= Root_ActualThemeChanged;
        _editStatusTimer.Stop(); _editStatusTimer.Tick -= EditStatusTimer_Tick;
        ViewModel.PropertyChanged -= ViewModel_Changed;
        KeyDown -= EditorCanvas_KeyDown;
        CharacterReceived -= EditorCanvas_CharacterReceived;
        _markdownMathCancellation?.Cancel();
        _mathClient.Dispose();
        _codeHighlightCancellation?.Cancel();
        _codeHighlighter.Dispose();
        DisposeSearch();
        DetachImeInput();
        ReleaseSurface(); _gate.Close();
    }
}
