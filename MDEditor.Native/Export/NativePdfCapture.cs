using MDEditor.Typesetting.Export;
using Microsoft.Graphics.Canvas;

namespace MDEditor.Native.Export;

/// <summary>Owns recorded COM references, not the live editor document. May replay after editing resumes.</summary>
public sealed class NativePdfCapture : IDisposable
{
    private readonly CanvasDevice _device;
    private readonly List<(CanvasCommandList Commands, double Height)> _pages = [];
    private readonly double _width, _scale;
    private readonly PdfColor _background;
    private bool _disposed;
    internal NativePdfCapture(CanvasDevice device, double width, PdfColor background)
    { _device = device; _width = width; _scale = PdfPagePlanner.A4Width / width; _background = background; }
    internal void Add(CanvasCommandList commands, double height)
    { D2DPdfInterop.Close(commands); _pages.Add((commands, height)); }
    public byte[] ToBytes(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pdf = new PdfDocument();
        var outlines = new Dictionary<(nint, ushort), (string, string, PdfGlyphBounds)>();
        var images = new Dictionary<nint, PdfImage>();
        foreach (var (commands, height) in _pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = pdf.AddPage(PdfPagePlanner.A4Width, Math.Max(PdfPagePlanner.A4Height, height * _scale), _scale, _background);
            D2DPdfInterop.Stream(commands, new(_device, page, outlines, images));
        }
        return pdf.ToBytes(cancellationToken);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var page in _pages) page.Commands.Dispose();
        _pages.Clear();
    }
}
