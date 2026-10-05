namespace MDEditor.Typesetting.Layout;

/// <summary>Document DIP -> viewport DIP -> pixels. Zoom changes available document width; DPI does not.</summary>
public sealed class DocumentViewport
{
    public const double Padding = 24;
    public double Width { get; }
    public double Height { get; }
    public int ZoomPercent { get; }
    public float Zoom => ZoomPercent / 100f; // Same canonical Single as the Direct2D transform.
    public double Dpi { get; }
    public bool IsVisible => Width > Padding * 2 && Height > Padding * 2;
    public double DocumentWidth => IsVisible ? (float)((Width - Padding * 2) / Zoom) : 0;
    public double DocumentHeight => IsVisible ? (Height - Padding * 2) / Zoom : 0;
    public DocumentViewport(double width, double height, int zoomPercent = 100, double dpi = 96)
    {
        if (!double.IsFinite(width) || width < 0 || width > 50_000) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height < 0 || height > 50_000) throw new ArgumentOutOfRangeException(nameof(height));
        if (zoomPercent is < 50 or > 300) throw new ArgumentOutOfRangeException(nameof(zoomPercent));
        if (!double.IsFinite(dpi) || dpi <= 0 || dpi > 9600) throw new ArgumentOutOfRangeException(nameof(dpi));
        Width = width; Height = height; ZoomPercent = zoomPercent; Dpi = dpi;
    }
    public double MaximumScroll(double contentHeight)
    {
        if (!double.IsFinite(contentHeight) || contentHeight < 0) throw new ArgumentOutOfRangeException(nameof(contentHeight));
        return Math.Max(0, contentHeight - DocumentHeight);
    }
    public double ClampScroll(double scroll, double contentHeight)
    {
        if (!double.IsFinite(scroll)) throw new ArgumentOutOfRangeException(nameof(scroll));
        return Math.Clamp(scroll, 0, MaximumScroll(contentHeight));
    }
    public LayoutPoint ToView(LayoutPoint document, double scroll = 0)
    {
        if (!double.IsFinite(scroll)) throw new ArgumentOutOfRangeException(nameof(scroll));
        return new(Padding + document.X * Zoom, Padding + (document.Y - scroll) * Zoom);
    }
    public LayoutPoint ToDocument(LayoutPoint view, double scroll = 0)
    {
        if (!double.IsFinite(scroll)) throw new ArgumentOutOfRangeException(nameof(scroll));
        return new((view.X - Padding) / Zoom, (view.Y - Padding) / Zoom + scroll);
    }
    public bool ContainsDocumentViewX(double viewX) => IsVisible &&
        double.IsFinite(viewX) && viewX >= Padding && viewX <= Width - Padding;
    public LayoutPoint ToPixels(LayoutPoint view) => new(view.X * Dpi / 96, view.Y * Dpi / 96);
}

/// <summary>UI-thread request arbitration. Hidden surfaces/device changes also revoke older requests.</summary>
public sealed class LayoutRequestGate
{
    private long _revision;
    private bool _closed;
    public long Revision => _revision;
    public long Next()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        return checked(++_revision);
    }
    public bool IsCurrent(long revision) => !_closed && revision == _revision;
    public void Close() { _closed = true; }
}
