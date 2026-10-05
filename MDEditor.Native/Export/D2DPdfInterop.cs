using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using MDEditor.Typesetting.Export;
using Microsoft.Graphics.Canvas;
using WinRT;

namespace MDEditor.Native.Export;

// ABI declarations are confined to this exporter. No pointers escape a synchronous Stream call.
internal static unsafe class D2DPdfInterop
{
    internal static nint Native(object wrapper, Guid iid)
    {
        using var value = MarshalInspectable<object>.CreateMarshaler(wrapper);
        var wrapperIid = new Guid("5f10688d-ea55-4d55-a3b0-4ddb55c0c20a");
        nint nativeWrapper = 0, resource = 0;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(value.ThisPtr, in wrapperIid, out nativeWrapper));
        try
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, float, Guid*, nint*, int>)V(nativeWrapper, 3))(nativeWrapper, 0, 96, &iid, &resource));
            return resource;
        }
        finally { Marshal.Release(nativeWrapper); }
    }
    internal static void* V(nint pointer, int index) => (*(void***)pointer)[index];
    internal static void Close(CanvasCommandList list)
    {
        var native = Native(list, new("b4f34a19-2383-4d76-94f6-ec343657c3dc"));
        try { Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)V(native, 5))(native)); }
        finally { Marshal.Release(native); }
    }
    internal static void Stream(CanvasCommandList list, D2DPdfSink sink)
    {
        var native = Native(list, new("b4f34a19-2383-4d76-94f6-ec343657c3dc"));
        var target = Marshal.GetComInterfaceForObject<D2DPdfSink, ID2DPdfCommandSink>(sink);
        try
        {
            var result = ((delegate* unmanaged[Stdcall]<nint, nint, int>)V(native, 4))(native, target);
            if (sink.Error is not null) throw new InvalidOperationException($"PDF 矢量导出失败：{sink.Error.Message}", sink.Error);
            Marshal.ThrowExceptionForHR(result);
        }
        finally { Marshal.Release(target); Marshal.Release(native); GC.KeepAlive(sink); }
    }
    internal static PdfPath Geometry(nint geometry)
    {
        var path = new PdfPath();
        var target = Marshal.GetComInterfaceForObject<PdfPath, ID2DPdfGeometrySink>(path);
        try
        {
            // ID2D1Geometry::Simplify(CUBICS_AND_LINES). Preserve curves, do not flatten glyphs.
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, nint, float, nint, int>)V(geometry, 9))(geometry, 0, 0, 0.01f, target));
            path.Check(); return path;
        }
        finally { Marshal.Release(target); GC.KeepAlive(path); }
    }
    internal static PdfPath Glyph(nint face, ushort index)
    {
        var path = new PdfPath();
        var target = Marshal.GetComInterfaceForObject<PdfPath, ID2DPdfGeometrySink>(path);
        float advance = 0;
        try
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, float, ushort*, float*, nint, uint, int, int, nint, int>)V(face, 14))(face, 1000, &index, &advance, 0, 1, 0, 0, target));
            path.Check(); return path;
        }
        finally { Marshal.Release(target); GC.KeepAlive(path); }
    }
    internal static T BorrowWrapper<T>(CanvasDevice device, nint resource)
    {
        // ICanvasFactoryNative::GetOrCreate returns an IInspectable through an explicit out pointer
        // on every architecture. Do not invoke C++ aggregate-return methods directly from C#.
        var factoryIid = new Guid("695c440d-04b3-4edd-bfd9-63e51e9f7202");
        nint result = 0;
        using var deviceValue = MarshalInspectable<object>.CreateMarshaler(device);
        const string className = "Microsoft.Graphics.Canvas.CanvasDevice";
        // CsWinRT also resolves the bundled Win2D DLL for non-packaged/native test hosts.
        using var factory = ActivationFactory.Get(className, factoryIid);
        try
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, nint, float, nint*, int>)V(factory.ThisPtr, 6))(factory.ThisPtr, deviceValue.ThisPtr, resource, 96, &result));
            // Borrowed from the Win2D resource manager: never dispose the editor's resource.
            return MarshalInspectable<T>.FromAbi(result);
        }
        finally { if (result != 0) Marshal.Release(result); }
    }
    internal static PdfImage Bitmap(CanvasDevice device, nint bitmap)
    {
        var image = BorrowWrapper<CanvasBitmap>(device, bitmap);
        var size = image.SizeInPixels;
        var width = checked((int)size.Width); var height = checked((int)size.Height);
        var bytes = image.GetPixelBytes();
        var rgb = new byte[checked(width * height * 3)]; var alpha = new byte[checked(width * height)];
        for (var i = 0; i < alpha.Length; i++)
        {
            var a = bytes[i * 4 + 3]; alpha[i] = a;
            for (var c = 0; c < 3; c++) rgb[i * 3 + c] = a == 0 ? (byte)0 : (byte)Math.Min(255, (bytes[i * 4 + 2 - c] * 255 + a / 2) / a);
        }
        return new(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), width, height, rgb, alpha);
    }
}

[ComVisible(true), Guid("2cd9069e-12e2-11dc-9fed-001143a055f9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ID2DPdfGeometrySink
{
    [PreserveSig] void SetFillMode(int mode);
    [PreserveSig] void SetSegmentFlags(int flags);
    [PreserveSig] void BeginFigure(Vector2 start, int begin);
    [PreserveSig] void AddLines(nint points, uint count);
    [PreserveSig] void AddBeziers(nint curves, uint count);
    [PreserveSig] void EndFigure(int end);
    [PreserveSig] int Close();
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed unsafe class PdfPath : ID2DPdfGeometrySink
{
    private readonly StringBuilder _path = new();
    private Exception? _error;
    private Vector2 _minimum = Vector2.Zero, _maximum = Vector2.Zero;
    public PdfGlyphBounds Bounds => new(_minimum.X, -_maximum.Y, _maximum.X, -_minimum.Y);
    public bool EvenOdd { get; private set; }
    public override string ToString() => _path.ToString();
    public void Check() { if (_error is not null) throw new InvalidOperationException("Invalid native path.", _error); }
    private void Run(Action action) { try { action(); } catch (Exception error) { _error ??= error; } }
    private static string N(float value) => PdfDocumentNumber(value);
    internal static string PdfDocumentNumber(double value) => double.IsFinite(value) ? value.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(nameof(value));
    public void SetFillMode(int mode) => EvenOdd = mode == 0;
    public void SetSegmentFlags(int flags) { }
    private void Include(Vector2 point) { _minimum = Vector2.Min(_minimum, point); _maximum = Vector2.Max(_maximum, point); }
    public void BeginFigure(Vector2 start, int begin) => Run(() => { Include(start); _path.Append($"{N(start.X)} {N(start.Y)} m\n"); });
    public void AddLines(nint points, uint count) => Run(() =>
    { for (var i = 0; i < count; i++) { var p = ((Vector2*)points)[i]; Include(p); _path.Append($"{N(p.X)} {N(p.Y)} l\n"); } });
    public void AddBeziers(nint curves, uint count) => Run(() =>
    { for (var i = 0; i < count; i++) { var p = ((Vector2*)curves) + i * 3; Include(p[0]); Include(p[1]); Include(p[2]); _path.Append($"{N(p[0].X)} {N(p[0].Y)} {N(p[1].X)} {N(p[1].Y)} {N(p[2].X)} {N(p[2].Y)} c\n"); } });
    public void EndFigure(int end) { if (end == 1) _path.Append("h\n"); }
    public int Close() => _error?.HResult ?? 0;
}

[StructLayout(LayoutKind.Sequential)] public struct D2DPdfRect { public float Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] internal struct D2DPdfGlyphRun
{ public nint Face; public float Size; public uint Count; public nint Indices, Advances, Offsets; public int Sideways; public uint Bidi; }
[StructLayout(LayoutKind.Sequential)] internal struct D2DPdfRunDescription
{ public nint Locale, Text; public uint Length; public nint Clusters; public uint TextPosition; }
[StructLayout(LayoutKind.Sequential)] internal struct D2DPdfLayer
{ public D2DPdfRect Bounds; public nint Mask; public int Antialias; public Matrix3x2 Transform; public float Opacity; public nint OpacityBrush; public int Options; }

[ComVisible(true), Guid("54d7898a-a061-40a7-bec7-e465bcba2c4f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ID2DPdfCommandSink
{
    [PreserveSig] int BeginDraw();
    [PreserveSig] int EndDraw();
    [PreserveSig] int SetAntialiasMode(int mode);
    [PreserveSig] int SetTags(ulong tag1, ulong tag2);
    [PreserveSig] int SetTextAntialiasMode(int mode);
    [PreserveSig] int SetTextRenderingParams(nint parameters);
    [PreserveSig] int SetTransform(nint transform);
    [PreserveSig] int SetPrimitiveBlend(int blend);
    [PreserveSig] int SetUnitMode(int mode);
    [PreserveSig] int Clear(nint color);
    [PreserveSig] int DrawGlyphRun(Vector2 origin, nint run, nint description, nint brush, int measuring);
    [PreserveSig] int DrawLine(Vector2 start, Vector2 end, nint brush, float width, nint style);
    [PreserveSig] int DrawGeometry(nint geometry, nint brush, float width, nint style);
    [PreserveSig] int DrawRectangle(nint rect, nint brush, float width, nint style);
    [PreserveSig] int DrawBitmap(nint bitmap, nint destination, float opacity, int interpolation, nint source, nint perspective);
    [PreserveSig] int DrawImage(nint image, nint offset, nint rect, int interpolation, int composite);
    [PreserveSig] int DrawGdiMetafile(nint metafile, nint offset);
    [PreserveSig] int FillMesh(nint mesh, nint brush);
    [PreserveSig] int FillOpacityMask(nint mask, nint brush, nint destination, nint source);
    [PreserveSig] int FillGeometry(nint geometry, nint brush, nint opacityBrush);
    [PreserveSig] int FillRectangle(nint rect, nint brush);
    [PreserveSig] int PushAxisAlignedClip(nint rect, int antialias);
    [PreserveSig] int PushLayer(nint parameters, nint layer);
    [PreserveSig] int PopAxisAlignedClip();
    [PreserveSig] int PopLayer();
}
