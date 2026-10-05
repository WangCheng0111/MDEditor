using System.Numerics;
using System.Runtime.InteropServices;
using MDEditor.Typesetting.Export;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;

namespace MDEditor.Native.Export;

/// <summary>Streams the actual editor command list into vector PDF primitives.</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed unsafe class D2DPdfSink : ID2DPdfCommandSink
{
    private readonly CanvasDevice _device;
    private readonly PdfPage _page;
    private readonly Dictionary<(nint Face, ushort Glyph), (string Key, string Path, PdfGlyphBounds Bounds)> _outlines;
    private readonly Dictionary<nint, PdfImage> _images;
    private Matrix3x2 _transform = Matrix3x2.Identity;
    public Exception? Error { get; private set; }
    internal D2DPdfSink(CanvasDevice device, PdfPage page,
        Dictionary<(nint, ushort), (string, string, PdfGlyphBounds)> outlines, Dictionary<nint, PdfImage> images)
    { _device = device; _page = page; _outlines = outlines; _images = images; }

    private int Run(Action draw)
    {
        if (Error is not null) return Error.HResult;
        try { draw(); return 0; }
        catch (Exception error) { Error = error; return error.HResult < 0 ? error.HResult : unchecked((int)0x80004005); }
    }
    private int Unsupported(string name) => Run(() => throw new NotSupportedException($"PDF does not support this native drawing command: {name}."));
    private void Paint(Action paint)
    { _page.Push(); _page.Transform(_transform); paint(); _page.Pop(); }
    private static string N(double value) => PdfPath.PdfDocumentNumber(value);
    private static string Rect(D2DPdfRect r) => $"{N(r.Left)} {N(r.Top)} {N(r.Right - r.Left)} {N(r.Bottom - r.Top)} re\n";
    private PdfColor Color(nint brush)
    {
        var iid = new Guid("2cd906a9-12e2-11dc-9fed-001143a055f9");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(brush, in iid, out var solid));
        try
        {
            var wrapper = D2DPdfInterop.BorrowWrapper<CanvasSolidColorBrush>(_device, solid);
            var rgba = wrapper.ColorHdr;
            return new(rgba.X, rgba.Y, rgba.Z, Math.Clamp(rgba.W * wrapper.Opacity, 0, 1));
        }
        finally { Marshal.Release(solid); }
    }

    public int BeginDraw() => 0;
    public int EndDraw() => 0;
    public int SetAntialiasMode(int mode) => 0;
    public int SetTags(ulong tag1, ulong tag2) => 0;
    public int SetTextAntialiasMode(int mode) => 0;
    public int SetTextRenderingParams(nint parameters) => 0;
    public int SetTransform(nint transform) => Run(() => _transform = *(Matrix3x2*)transform);
    public int SetPrimitiveBlend(int blend) => blend == 0 ? 0 : Unsupported("primitive blend");
    public int SetUnitMode(int mode) => mode == 0 ? 0 : Unsupported("pixel units");
    public int Clear(nint color) => Unsupported("clear"); // The page paper is painted by PdfDocument.

    public int DrawGlyphRun(Vector2 origin, nint runPointer, nint description, nint brush, int measuring) => Run(() => Paint(() =>
    {
        var run = *(D2DPdfGlyphRun*)runPointer;
        if (run.Count == 0) return; // DirectWrite emits newline/control runs with no painted glyphs.
        if (run.Sideways != 0) throw new NotSupportedException("Sideways glyph runs are not supported.");
        if (run.Count > 1_000_000 || !float.IsFinite(run.Size) || run.Size <= 0 ||
            run.Face == 0 || run.Indices == 0 || run.Advances == 0) throw new InvalidOperationException("Invalid glyph run.");
        var texts = new string[run.Count];
        Array.Fill(texts, "");
        string logicalText = "";
        if (description != 0)
        {
            var desc = *(D2DPdfRunDescription*)description;
            var text = Marshal.PtrToStringUni(desc.Text, checked((int)desc.Length)) ?? "";
            if (text.Length > 0 && desc.Clusters == 0) throw new InvalidOperationException("Missing Unicode cluster map.");
            logicalText = text;
            for (var i = 0; i < text.Length;)
            {
                var glyph = ((ushort*)desc.Clusters)[i]; var end = i + 1;
                while (end < text.Length && ((ushort*)desc.Clusters)[end] == glyph) end++;
                if (glyph >= run.Count) throw new InvalidOperationException("Invalid Unicode cluster map.");
                texts[glyph] += text[i..end]; i = end;
            }
        }
        var ink = Color(brush); double cursor = 0;
        var rtl = (run.Bidi & 1) != 0;
        // PDF consumers that support ActualText can copy the logical RTL run, not visual glyph order.
        if (rtl && logicalText.Length > 0) _page.ActualText(logicalText);
        for (var i = 0; i < run.Count; i++)
        {
            var index = ((ushort*)run.Indices)[i]; var advance = ((float*)run.Advances)[i];
            var offset = run.Offsets == 0 ? Vector2.Zero : ((Vector2*)run.Offsets)[i];
            if (!_outlines.TryGetValue((run.Face, index), out var outline))
            {
                var path = D2DPdfInterop.Glyph(run.Face, index);
                outline = ($"{run.Face:X}:{index}", path + (path.EvenOdd ? "f*\n" : "f\n"), path.Bounds);
                _outlines.Add((run.Face, index), outline);
            }
            var x = origin.X + (rtl ? -cursor - advance - offset.X : cursor + offset.X);
            _page.Glyph(outline.Key, outline.Path, texts[i], x, origin.Y - offset.Y, run.Size, advance, ink, outline.Bounds);
            cursor += advance;
        }
        if (rtl && logicalText.Length > 0) _page.EndActualText();
    }));

    private void StrokeStyle(nint style)
    {
        if (style == 0) return;
        int Get(int slot) => ((delegate* unmanaged[Stdcall]<nint, int>)D2DPdfInterop.V(style, slot))(style);
        if (Get(10) != 0) throw new NotSupportedException("Dashed native strokes are not supported.");
        var start = Get(4); var end = Get(5); var join = Get(8);
        if (start != end || start == 3) throw new NotSupportedException("Asymmetric native stroke caps are not supported.");
        var miter = ((delegate* unmanaged[Stdcall]<nint, float>)D2DPdfInterop.V(style, 7))(style);
        _page.StrokeStyle(start == 2 ? 1 : start == 1 ? 2 : 0, join == 2 ? 1 : join == 1 ? 2 : 0, miter);
    }
    public int DrawLine(Vector2 start, Vector2 end, nint brush, float width, nint style) => Run(() => Paint(() =>
    { StrokeStyle(style); _page.Path($"{N(start.X)} {N(start.Y)} m {N(end.X)} {N(end.Y)} l\n", Color(brush), width); }));
    public int DrawGeometry(nint geometry, nint brush, float width, nint style) => Run(() => Paint(() =>
    { var path = D2DPdfInterop.Geometry(geometry); StrokeStyle(style); _page.Path(path.ToString(), Color(brush), width); }));
    public int DrawRectangle(nint rect, nint brush, float width, nint style) => Run(() => Paint(() =>
    { StrokeStyle(style); _page.Path(Rect(*(D2DPdfRect*)rect), Color(brush), width); }));
    public int FillGeometry(nint geometry, nint brush, nint opacityBrush) => opacityBrush != 0 ? Unsupported("opacity brush") : Run(() => Paint(() =>
    { var path = D2DPdfInterop.Geometry(geometry); _page.Path(path.ToString(), Color(brush), evenOdd: path.EvenOdd); }));
    public int FillRectangle(nint rect, nint brush) => Run(() => Paint(() => _page.Path(Rect(*(D2DPdfRect*)rect), Color(brush))));

    private PdfImage Image(nint bitmap)
    {
        if (_images.TryGetValue(bitmap, out var image)) return image;
        image = D2DPdfInterop.Bitmap(_device, bitmap); _images.Add(bitmap, image); return image;
    }
    public int DrawBitmap(nint bitmap, nint destination, float opacity, int interpolation, nint source, nint perspective) => Run(() => Paint(() =>
    {
        if (perspective != 0 || Math.Abs(opacity - 1) > 0.0001) throw new NotSupportedException("Perspective/opacity bitmap transforms are not supported.");
        var image = Image(bitmap);
        var dst = destination == 0 ? new D2DPdfRect { Right = image.Width, Bottom = image.Height } : *(D2DPdfRect*)destination;
        var src = source == 0 ? new D2DPdfRect { Right = image.Width, Bottom = image.Height } : *(D2DPdfRect*)source;
        DrawImageCrop(image, dst, src);
    }));
    private void DrawImageCrop(PdfImage image, D2DPdfRect dst, D2DPdfRect src)
    {
        var scaleX = (dst.Right - dst.Left) / (src.Right - src.Left);
        var scaleY = (dst.Bottom - dst.Top) / (src.Bottom - src.Top);
        _page.Push(); _page.Clip(Rect(dst));
        _page.Bitmap(image, dst.Left - src.Left * scaleX, dst.Top - src.Top * scaleY, image.Width * scaleX, image.Height * scaleY);
        _page.Pop();
    }
    public int DrawImage(nint image, nint offset, nint rect, int interpolation, int composite) => Run(() => Paint(() =>
    {
        if (composite != 0) throw new NotSupportedException("Non-source-over image composition is not supported.");
        var iid = new Guid("a2296057-ea42-4099-983b-539fb6505426");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(image, in iid, out var bitmap));
        try
        {
            var data = Image(bitmap); var p = offset == 0 ? Vector2.Zero : *(Vector2*)offset;
            var src = rect == 0 ? new D2DPdfRect { Right = data.Width, Bottom = data.Height } : *(D2DPdfRect*)rect;
            var dst = new D2DPdfRect { Left = p.X, Top = p.Y, Right = p.X + src.Right - src.Left, Bottom = p.Y + src.Bottom - src.Top };
            DrawImageCrop(data, dst, src);
        }
        finally { Marshal.Release(bitmap); }
    }));
    public int PushAxisAlignedClip(nint rect, int antialias) => Run(() => PushClip(*(D2DPdfRect*)rect));
    private void PushClip(D2DPdfRect rect)
    {
        _page.Push(); _page.Transform(_transform); _page.Clip(Rect(rect));
        if (!Matrix3x2.Invert(_transform, out var inverse)) throw new InvalidOperationException("Singular native transform.");
        _page.Transform(inverse);
    }
    public int PushLayer(nint parameters, nint layer) => Run(() =>
    {
        var p = *(D2DPdfLayer*)parameters;
        if (p.OpacityBrush != 0 || Math.Abs(p.Opacity - 1) > 0.0001) throw new NotSupportedException("Nontrivial opacity layers are not supported.");
        PushClip(p.Bounds);
        if (p.Mask != 0)
        {
            _page.Transform(p.Transform * _transform);
            var path = D2DPdfInterop.Geometry(p.Mask); _page.Clip(path.ToString(), path.EvenOdd);
            if (!Matrix3x2.Invert(p.Transform * _transform, out var inverse)) throw new InvalidOperationException("Singular layer transform.");
            _page.Transform(inverse);
        }
    });
    public int PopAxisAlignedClip() => Run(_page.Pop);
    public int PopLayer() => Run(_page.Pop);
    public int DrawGdiMetafile(nint metafile, nint offset) => Unsupported("GDI metafile");
    public int FillMesh(nint mesh, nint brush) => Unsupported("mesh");
    public int FillOpacityMask(nint mask, nint brush, nint destination, nint source) => Unsupported("opacity mask");
}
