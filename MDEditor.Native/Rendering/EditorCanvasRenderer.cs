using MDEditor.Core.Markdown;
using MDEditor.Native.Text;
using MDEditor.Typesetting.Layout;
using MDEditor.Typesetting.Mathematics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Windows.UI;

namespace MDEditor.Native.Rendering;

/// <summary>Native Direct2D test renderer. Does not own the control's shared device or session.</summary>
public sealed class EditorCanvasRenderer : IDisposable
{
    private readonly DeviceResourceOwner<BrushPalette> _resources = new();
    private readonly CompletedLayoutCache<ReflowDocument> _completed = new(32,
        document => document.EstimatedRetainedBytes, 96L * 1024 * 1024);
    private ReflowDocument? _document;
    private ReflowDocument? _intermediate;
    private ReflowEngine? _engine;
    private ReflowContent _content = ReflowSample.Content;
    private bool _contentDirty;
    private IReadOnlyDictionary<int, MathLayoutResult>? _mathLayouts;
    private bool _mathContentDirty;
    private CanvasDevice? _device;
    private GithubMarkdownTheme _theme = GithubMarkdownTheme.Light;
    private MarkdownCodeHighlight? _codeHighlight;
    public ReflowEngine Engine => _engine ?? throw new InvalidOperationException("Resources not ready.");
    public ReflowDocument? Document => _document;
    public int PresentedZoomPercent { get; private set; } = 100;
    private bool _disposed;

    public bool HasResources => _resources.Current is not null;

    public void CreateResources(CanvasDevice device)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        _resources.Replace(() => BrushPalette.Create(device, _theme));
        _intermediate?.Dispose(); _intermediate = null;
        _document = null; _completed.Clear();
        _engine?.Dispose(); _engine = new(device);
        _engine.SetContent(_content);
        if (_mathLayouts is not null) _engine.SetMarkdownMathLayouts(_mathLayouts);
    }

    /// <summary>Recreates color brushes only; the paragraph shapes and break cache stay valid.</summary>
    public void SetTheme(GithubMarkdownTheme theme)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(theme);
        if (ReferenceEquals(theme, _theme)) return;
        _theme = theme;
        if (_device is { } device && HasResources)
            _resources.Replace(() => BrushPalette.Create(device, theme));
    }

    public void SetMarkdownMathLayouts(IReadOnlyDictionary<int, MathLayoutResult> layouts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(layouts);
        _mathLayouts = layouts; _mathContentDirty = true;
        _engine?.SetMarkdownMathLayouts(layouts);
    }

    public void SetContent(ReflowContent content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(content);
        _content = content; _contentDirty = true;
        _engine?.SetContent(content);
    }

    /// <summary>Changes paint only, independently of layout/cache/source epochs.</summary>
    public void SetCodeHighlight(MarkdownCodeHighlight? highlight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _codeHighlight = highlight;
    }

    public void ClearPresentedDocument()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _document = null;
        _intermediate?.Dispose(); _intermediate = null;
        _completed.Clear();
        _contentDirty = true;
    }

    public bool TryReuseLayout(DocumentViewport viewport)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mathContentDirty || _contentDirty || !_completed.TryGet(viewport.DocumentWidth, out var cached)) return false;
        _document = cached;
        PresentedZoomPercent = viewport.ZoomPercent; return true;
    }

    public void Commit(ReflowDocument document, int zoomPercent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(document);
        if (zoomPercent is < 50 or > 300) throw new ArgumentOutOfRangeException(nameof(zoomPercent));
        // UI-thread-only: zoom and snapshot form one presented frame, with no draw between these writes.
        PresentedZoomPercent = zoomPercent;
        if (_mathContentDirty || _contentDirty)
        {
            _document = null; _completed.Clear(); _mathContentDirty = false; _contentDirty = false;
        }
        _completed.Remember(document.Width, document); _document = document;
        _intermediate?.Dispose(); _intermediate = null;
    }

    /// <summary>Shows an older completed edit while preserving the latest content epoch and its pending solve.</summary>
    public void PresentIntermediate(ReflowDocument document, int zoomPercent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(document);
        if (zoomPercent is < 50 or > 300) throw new ArgumentOutOfRangeException(nameof(zoomPercent));
        if (document.SourceVersion >= _content.Source.Version)
            throw new ArgumentException("An intermediate frame must precede the current content.", nameof(document));
        var previous = _intermediate;
        _document = document; _intermediate = document; PresentedZoomPercent = zoomPercent;
        previous?.Dispose();
    }

    public void Draw(CanvasDrawingSession session, DocumentViewport viewport, double scroll)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(session);
        var bounds = CanvasSceneBounds.FromViewport(viewport.Width, viewport.Height);
        var brushes = _resources.Current ?? throw new InvalidOperationException("Canvas resources are not ready.");
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // The host clears to transparent; leave the window backdrop visible behind the document.
        var presented = new DocumentViewport(viewport.Width, viewport.Height, PresentedZoomPercent, viewport.Dpi);
        _document?.Draw(session, presented, scroll, brushes.Ink, brushes.Blue, brushes.Green,
            brushes.Muted, _theme, stableFirstLabel: true, codeColors: brushes.CodeColors,
            codeHighlight: _codeHighlight);
    }

    public void DrawStableHeading(CanvasDrawingSession session, DocumentViewport viewport, double scroll)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var presented = new DocumentViewport(viewport.Width, viewport.Height, PresentedZoomPercent, viewport.Dpi);
        _document?.DrawFirstLabel(session, presented, scroll, _theme);
    }

    /// <summary>Records the same production draw path at document scale, with no caret/search overlays.</summary>
    public Export.NativePdfCapture CapturePdf()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var document = _document ?? throw new InvalidOperationException("请等待正文排版完成后再导出。");
        if (!document.Geometry.Passed || document.InfeasibleParagraphs > 0 || document.InfeasibleMathBlocks > 0)
            throw new InvalidOperationException("当前版心存在未完成的排版；请调整窗口宽度后再导出。");
        var device = _device ?? throw new InvalidOperationException("Canvas device is not ready.");
        var brushes = _resources.Current ?? throw new InvalidOperationException("Canvas resources are not ready.");
        var width = Math.Max(document.Width, Math.Max(document.Snapshot.Bounds.Width,
            document.BodyInteraction.Lines.Select(line => line.Bounds.Right).DefaultIfEmpty(0).Max())) + DocumentViewport.Padding * 2;
        var scale = Typesetting.Export.PdfPagePlanner.A4Width / width;
        var capacity = Typesetting.Export.PdfPagePlanner.A4Height / scale - DocumentViewport.Padding * 2;
        var bands = document.BodyInteraction.Lines.Select(line => line.Bounds).Concat(
            document.Sections.Where(section => section.Style.Table is not null)
                .Select(section => new LayoutRect(0, section.BodyY, document.Width, Math.Max(0, section.Bottom - section.BodyY))));
        if (document.MathInteraction is { } math) bands = bands.Concat(math.Lines.Select(line => line.Bounds));
        var pages = Typesetting.Export.PdfPagePlanner.Plan(document.Height, capacity, bands);
        var paper = _theme.Background;
        var capture = new Export.NativePdfCapture(device, width, new(paper.R / 255f, paper.G / 255f, paper.B / 255f));
        try
        {
            using var descriptions = new Export.PdfTextDescriptions();
            foreach (var page in pages)
            {
                var height = page.Height + DocumentViewport.Padding * 2;
                if (height > 50_000) throw new InvalidOperationException("单个对象过高，超过原生绘制坐标范围。");
                var commands = new CanvasCommandList(device);
                try
                {
                    using (var drawing = commands.CreateDrawingSession())
                    {
                        using var pageClip = drawing.CreateLayer(1, new Windows.Foundation.Rect(0,
                            DocumentViewport.Padding, width, page.Height));
                        document.Draw(drawing, new DocumentViewport(width, height, 100, 96), page.Top,
                            brushes.Ink, brushes.Blue, brushes.Green, brushes.Muted, _theme,
                            codeColors: brushes.CodeColors, codeHighlight: _codeHighlight);
                    }
                    capture.Add(commands, height);
                }
                catch { commands.Dispose(); throw; }
            }
            return capture;
        }
        catch { capture.Dispose(); throw; }
    }

    public void DrawStableMathPage(CanvasDrawingSession session, DocumentViewport viewport, double scroll)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var brushes = _resources.Current ?? throw new InvalidOperationException("Canvas resources are not ready.");
        var presented = new DocumentViewport(viewport.Width, viewport.Height, PresentedZoomPercent, viewport.Dpi);
        _document?.DrawMathPage(session, presented, scroll, brushes.Ink, brushes.Blue, brushes.Green);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _intermediate?.Dispose(); _intermediate = null;
        _document = null; _completed.Dispose();
        _engine?.Dispose(); _engine = null;
        _device = null;
        _codeHighlight = null;
        _resources.Dispose();
    }

    private sealed class BrushPalette : IDisposable
    {
        private readonly List<CanvasSolidColorBrush> _brushes;
        private readonly ICanvasBrush[] _codeColors;
        private BrushPalette(List<CanvasSolidColorBrush> brushes)
        {
            _brushes = brushes;
            _codeColors = brushes.Skip(4).Cast<ICanvasBrush>().ToArray();
        }
        public CanvasSolidColorBrush Blue => _brushes[0];
        public CanvasSolidColorBrush Green => _brushes[1];
        public CanvasSolidColorBrush Ink => _brushes[2];
        public CanvasSolidColorBrush Muted => _brushes[3];
        public IReadOnlyList<ICanvasBrush> CodeColors => _codeColors;

        public static BrushPalette Create(CanvasDevice device, GithubMarkdownTheme theme)
        {
            var brushes = new List<CanvasSolidColorBrush>(10);
            try
            {
                foreach (var color in new[]
                {
                    theme.Accent, theme.Success,
                    theme.Foreground, theme.MutedForeground
                }.Concat(theme.CodeColors))
                {
                    brushes.Add(new CanvasSolidColorBrush(device, color));
                }
                return new(brushes);
            }
            catch
            {
                foreach (var brush in brushes) brush.Dispose();
                throw; // Let CanvasControl handle device-lost exceptions.
            }
        }

        public void Dispose()
        {
            foreach (var brush in _brushes) brush.Dispose();
            _brushes.Clear();
        }
    }
}
