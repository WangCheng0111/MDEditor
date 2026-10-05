using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace MDEditor.Typesetting.Export;

/// <summary>Device-independent vector replay. Coordinates are DIPs, with positive Y down.</summary>
public sealed class PdfDocument
{
    private readonly List<PdfPage> _pages = [];
    private readonly Dictionary<(string Key, string Text), int> _glyphCodes = [];
    private readonly List<(string Path, string Text, double Advance, PdfGlyphBounds Bounds)> _glyphs = [];
    private readonly List<float> _alphas = [1];
    private readonly Dictionary<string, int> _imageIds = [];
    private readonly List<PdfImage> _images = [];
    public int PageCount => _pages.Count;

    public PdfPage AddPage(double width, double height, double scale, PdfColor background)
    {
        Positive(width); Positive(height); Positive(scale);
        var page = new PdfPage(this, width, height, scale, background);
        _pages.Add(page); return page;
    }

    internal (int Font, int Code) Glyph(string key, string path, string text, double advance, PdfGlyphBounds bounds)
    {
        key += ":" + Number(advance);
        if (!_glyphCodes.TryGetValue((key, text), out var index))
        {
            index = _glyphs.Count; _glyphCodes.Add((key, text), index); _glyphs.Add((path, text, advance, bounds));
        }
        return (index / 255, index % 255 + 1);
    }
    internal int Alpha(float alpha)
    {
        if (!float.IsFinite(alpha) || alpha < 0 || alpha > 1) throw new ArgumentOutOfRangeException(nameof(alpha));
        var index = _alphas.IndexOf(alpha);
        if (index < 0) { index = _alphas.Count; _alphas.Add(alpha); }
        return index;
    }

    internal int Image(PdfImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0 || image.Alpha.LongLength != (long)image.Width * image.Height ||
            image.Rgb.LongLength != (long)image.Width * image.Height * 3)
            throw new ArgumentException("Invalid PDF image dimensions or pixel buffers.", nameof(image));
        if (!_imageIds.TryGetValue(image.Key, out var id))
        {
            id = _images.Count; _imageIds.Add(image.Key, id); _images.Add(image);
        }
        return id;
    }

    public byte[] ToBytes(CancellationToken cancellationToken = default)
    {
        if (_pages.Count == 0) throw new InvalidOperationException("The PDF has no pages.");
        var objects = new PdfObjects();
        var catalog = objects.Reserve(); var pageTree = objects.Reserve();
        var fonts = new List<int>();
        for (var begin = 0; begin < _glyphs.Count; begin += 255)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(255, _glyphs.Count - begin);
            var chars = new StringBuilder(); var differences = new StringBuilder("1 ");
            var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /MDEditorUnicode def /CMapType 2 def\n1 begincodespacerange <00> <FF> endcodespacerange\n");
            var mapped = Enumerable.Range(0, count).Where(i => _glyphs[begin + i].Text.Length > 0).ToArray();
            for (var block = 0; block < mapped.Length; block += 100)
            {
                var group = mapped.Skip(block).Take(100).ToArray();
                cmap.Append(group.Length).Append(" beginbfchar\n");
                foreach (var i in group)
                    cmap.Append('<').Append((i + 1).ToString("X2")).Append("> <")
                        .Append(UnicodeHex(_glyphs[begin + i].Text, false)).Append(">\n");
                cmap.Append("endbfchar\n");
            }
            cmap.Append("endcmap CMapName currentdict /CMap defineresource pop end end\n");
            var mapId = objects.Stream("", Encoding.ASCII.GetBytes(cmap.ToString()));
            for (var i = 0; i < count; i++)
            {
                var charId = objects.Stream("", Encoding.ASCII.GetBytes(Number(_glyphs[begin + i].Advance) + " 0 d0\nq 1 0 0 -1 0 0 cm\n" + _glyphs[begin + i].Path + "Q\n"));
                chars.Append("/g").Append(i + 1).Append(' ').Append(charId).Append(" 0 R ");
                differences.Append("/g").Append(i + 1).Append(' ');
            }
            var bounds = _glyphs.Skip(begin).Take(count).Select(g => g.Bounds).ToArray();
            var box = string.Join(' ', new[] { bounds.Min(b => b.Left), bounds.Min(b => b.Bottom),
                bounds.Max(b => b.Right), bounds.Max(b => b.Top) }.Select(Number));
            fonts.Add(objects.Add($"<< /Type /Font /Subtype /Type3 /Name /F{fonts.Count} /FontBBox [{box}] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << {chars} >> /Encoding << /Type /Encoding /Differences [{differences}] >> /FirstChar 1 /LastChar {count} /Widths [{string.Join(' ', Enumerable.Range(0, count).Select(i => Number(_glyphs[begin + i].Advance)))}] /Resources << >> /ToUnicode {mapId} 0 R >>"));
        }
        var images = new List<int>();
        foreach (var image in _images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mask = objects.Stream($"/Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8", image.Alpha);
            images.Add(objects.Stream($"/Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask {mask} 0 R", image.Rgb));
        }
        var resource = $"<< /Font << {string.Join(' ', fonts.Select((id, i) => $"/F{i} {id} 0 R"))} >> /XObject << {string.Join(' ', images.Select((id, i) => $"/I{i} {id} 0 R"))} >> /ExtGState << {string.Join(' ', _alphas.Select((alpha, i) => $"/A{i} << /Type /ExtGState /ca {Number(alpha)} /CA {Number(alpha)} >>"))} >> >>";
        var pageIds = new List<int>();
        foreach (var page in _pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = objects.Stream("", Encoding.ASCII.GetBytes(page.Finish()));
            pageIds.Add(objects.Add($"<< /Type /Page /Parent {pageTree} 0 R /MediaBox [0 0 {Number(page.Width)} {Number(page.Height)}] /Resources {resource} /Contents {content} 0 R >>"));
        }
        objects.Set(pageTree, $"<< /Type /Pages /Count {pageIds.Count} /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] >>");
        objects.Set(catalog, $"<< /Type /Catalog /Pages {pageTree} 0 R >>");
        return objects.Finish(catalog, cancellationToken);
    }

    internal static string Number(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        return value.ToString("0.#########", CultureInfo.InvariantCulture);
    }
    internal static string UnicodeHex(string text, bool bom) => (bom ? "FEFF" : "") + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text));
    internal static void Positive(double value) { if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); }

    private sealed class PdfObjects
    {
        private readonly List<byte[]?> _items = [];
        public int Reserve() { _items.Add(null); return _items.Count; }
        public int Add(string value) { var id = Reserve(); Set(id, value); return id; }
        public void Set(int id, string value) => _items[id - 1] = Encoding.ASCII.GetBytes(value);
        public int Stream(string properties, byte[] data)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZLibStream(buffer, CompressionLevel.Optimal, true)) zip.Write(data);
            var compressed = buffer.ToArray();
            using var result = new MemoryStream();
            result.Write(Encoding.ASCII.GetBytes($"<< {properties} /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n"));
            result.Write(compressed); result.Write(Encoding.ASCII.GetBytes("\nendstream"));
            _items.Add(result.ToArray()); return _items.Count;
        }
        public byte[] Finish(int catalog, CancellationToken token)
        {
            using var output = new MemoryStream();
            void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
            Write("%PDF-1.7\n%MDEditor vector export\n");
            var offsets = new List<long> { 0 };
            for (var i = 0; i < _items.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                offsets.Add(output.Position); Write($"{i + 1} 0 obj\n");
                output.Write(_items[i] ?? throw new InvalidOperationException("Unresolved PDF object."));
                Write("\nendobj\n");
            }
            var xref = output.Position; Write($"xref\n0 {_items.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets.Skip(1)) Write($"{offset:0000000000} 00000 n \n");
            Write($"trailer\n<< /Size {_items.Count + 1} /Root {catalog} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return output.ToArray();
        }
    }
}

public readonly record struct PdfColor(float R, float G, float B, float A = 1);
// Bounds use PDF font coordinates (1000 units per em, positive Y up).
public readonly record struct PdfGlyphBounds(double Left, double Bottom, double Right, double Top);
public sealed record PdfImage(string Key, int Width, int Height, byte[] Rgb, byte[] Alpha);

public sealed class PdfPage
{
    private readonly PdfDocument _document;
    private readonly StringBuilder _commands = new();
    private int _depth;
    private int _textDepth;
    public double Width { get; }
    public double Height { get; }
    internal PdfPage(PdfDocument document, double width, double height, double scale, PdfColor background)
    {
        _document = document; Width = width; Height = height;
        _commands.Append($"q\n{PdfDocument.Number(scale)} 0 0 {PdfDocument.Number(-scale)} 0 {PdfDocument.Number(height)} cm\n");
        Path($"0 0 {PdfDocument.Number(width / scale)} {PdfDocument.Number(height / scale)} re\n", background);
    }
    public void Push() { _commands.Append("q\n"); _depth++; }
    public void Pop() { if (_depth == 0) throw new InvalidOperationException("Unbalanced PDF graphics state."); _commands.Append("Q\n"); _depth--; }
    public void Transform(Matrix3x2 matrix) => _commands.Append($"{N(matrix.M11)} {N(matrix.M12)} {N(matrix.M21)} {N(matrix.M22)} {N(matrix.M31)} {N(matrix.M32)} cm\n");
    public void Clip(string path, bool evenOdd = false) => _commands.Append(path).Append(evenOdd ? "W* n\n" : "W n\n");
    public void StrokeStyle(int cap, int join, double miter) => _commands.Append($"{cap} J {join} j {N(miter)} M\n");
    public void Path(string path, PdfColor color, double strokeWidth = 0, bool evenOdd = false)
    {
        if (color.A <= 0) return;
        Paint(color, strokeWidth > 0); _commands.Append(path);
        if (strokeWidth > 0) _commands.Append(N(strokeWidth)).Append(" w S\n");
        else _commands.Append(evenOdd ? "f*\n" : "f\n");
    }
    public void Glyph(string key, string outline, string text, double x, double y, double size, double advance, PdfColor color,
        PdfGlyphBounds? bounds = null)
    {
        PdfDocument.Positive(size);
        var (font, code) = _document.Glyph(key, outline, text, advance * 1000 / size,
            bounds ?? new(-1000, -250, 2000, 1250));
        Paint(color, false);
        _commands.Append($"BT /F{font} {N(size)} Tf 1 0 0 -1 {N(x)} {N(y)} Tm <{code:X2}> Tj ET\n");
    }
    public void Bitmap(PdfImage image, double x, double y, double width, double height)
    {
        var id = _document.Image(image);
        _commands.Append($"q {N(width)} 0 0 {N(-height)} {N(x)} {N(y + height)} cm /I{id} Do Q\n");
    }
    public void ActualText(string text) { _commands.Append("/Span << /ActualText <").Append(PdfDocument.UnicodeHex(text, true)).Append("> >> BDC\n"); _textDepth++; }
    public void EndActualText() { if (_textDepth == 0) throw new InvalidOperationException("Unbalanced PDF marked content."); _commands.Append("EMC\n"); _textDepth--; }
    private void Paint(PdfColor color, bool stroke) => _commands.Append($"/A{_document.Alpha(color.A)} gs {N(color.R)} {N(color.G)} {N(color.B)} {(stroke ? "RG" : "rg")}\n");
    internal string Finish() => _depth == 0 && _textDepth == 0 ? _commands.ToString() + "Q\n" : throw new InvalidOperationException("Unbalanced PDF graphics or marked-content state.");
    private static string N(double value) => PdfDocument.Number(value);
}
