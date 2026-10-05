using System.IO.Compression;
using System.Numerics;
using System.Text;
using MDEditor.Typesetting.Export;
using MDEditor.Typesetting.Layout;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class PdfExportTests
{
    private static PdfDocument Document() { var pdf = new PdfDocument(); pdf.AddPage(595, 842, 0.75, new(1, 1, 1)); return pdf; }
    private static string Expanded(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes); var result = new StringBuilder(text);
        for (var at = 0; (at = text.IndexOf("stream\n", at, StringComparison.Ordinal)) >= 0;)
        {
            if (at > 0 && text[at - 1] == 'd') { at += 7; continue; }
            var start = at + 7; var end = text.IndexOf("\nendstream", start, StringComparison.Ordinal);
            using var input = new MemoryStream(bytes, start, end - start);
            using var zip = new ZLibStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(zip); result.Append(reader.ReadToEnd()); at = end + 10;
        }
        return result.ToString();
    }
    [TestMethod] public void EmptyPageIsValidPdf() { var bytes = Document().ToBytes(); StringAssert.StartsWith(Encoding.ASCII.GetString(bytes), "%PDF-1.7"); StringAssert.Contains(Expanded(bytes), "/Count 1"); }
    [TestMethod] public void DocumentRequiresPages() => Assert.Throws<InvalidOperationException>(() => new PdfDocument().ToBytes());
    [TestMethod] public void PageRejectsNonfiniteSize() => Assert.Throws<ArgumentOutOfRangeException>(() => new PdfDocument().AddPage(double.NaN, 10, 1, new(1, 1, 1)));
    [TestMethod] public void GlyphsCarryUnicodeAndVectorPaths()
    {
        var pdf = Document(); var page = pdf.AddPage(595, 842, 0.75, new(1, 1, 1));
        page.Glyph("cjk", "0 -700 m 500 -700 l 500 0 l h f\n", "中", 24, 40, 20, 20, new(0, 0, 0));
        var text = Expanded(pdf.ToBytes()); StringAssert.Contains(text, "/Subtype /Type3");
        StringAssert.Contains(text, "<01> <4E2D>"); StringAssert.Contains(text, "0 -700 m"); StringAssert.Contains(text, "1000 0 d0");
    }
    [TestMethod] public void LigaturesAndSupplementaryCharactersKeepFullUnicode()
    {
        var pdf = Document(); var page = pdf.AddPage(595, 842, 1, new(1, 1, 1));
        page.Glyph("ffi", "", "ffi", 0, 0, 20, 15, new(0, 0, 0));
        page.Glyph("emoji", "", "😀", 20, 0, 20, 20, new(0, 0, 0));
        var text = Expanded(pdf.ToBytes()); StringAssert.Contains(text, "006600660069"); StringAssert.Contains(text, "D83DDE00");
    }
    [TestMethod] public void MoreThan255GlyphsCreateAnotherEmbeddedFont()
    {
        var pdf = Document(); var page = pdf.AddPage(595, 842, 1, new(1, 1, 1));
        for (var i = 0; i < 300; i++) page.Glyph(i.ToString(), "", "字", 0, 0, 20, 20, new(0, 0, 0));
        var text = Expanded(pdf.ToBytes()); StringAssert.Contains(text, "/F1 20 Tf"); StringAssert.Contains(text, "/LastChar 45");
    }
    [TestMethod] public void TransparencyUsesGraphicsState() { var pdf = Document(); var page = pdf.AddPage(595, 842, 1, new(1, 1, 1)); page.Path("0 0 10 10 re\n", new(0, 0, 0, 0.2f)); StringAssert.Contains(Expanded(pdf.ToBytes()), "/ca 0.2"); }
    [TestMethod] public void BitmapHasSoftMaskNotOpaqueBlackBackground()
    {
        var pdf = Document(); var page = pdf.AddPage(595, 842, 1, new(1, 1, 1));
        page.Bitmap(new("sample", 1, 1, [255, 0, 0], [128]), 24, 24, 10, 10);
        var text = Expanded(pdf.ToBytes()); StringAssert.Contains(text, "/SMask"); StringAssert.Contains(text, "/DeviceRGB");
    }
    [TestMethod] public void UnbalancedStateIsRejected() { var pdf = Document(); var page = pdf.AddPage(595, 842, 1, new(1, 1, 1)); page.Push(); Assert.Throws<InvalidOperationException>(() => pdf.ToBytes()); }
    [TestMethod] public void NativeGlyphBoundsAreEmbedded()
    {
        var pdf = Document(); var page = pdf.AddPage(595, 842, 1, new(1, 1, 1));
        page.Glyph("a", "", "A", 24, 40, 20, 10, new(0, 0, 0), new(-20, -200, 900, 850));
        StringAssert.Contains(Expanded(pdf.ToBytes()), "/FontBBox [-20 -200 900 850]");
    }
    [TestMethod] public void InvalidImageBuffersAreRejected()
    {
        var pdf = Document(); var page = pdf.AddPage(595, 842, 1, new(1, 1, 1));
        Assert.Throws<ArgumentException>(() => page.Bitmap(new("bad", 2, 1, [255, 0, 0], [255]), 0, 0, 10, 10));
    }
    [TestMethod] public void LogicalRtlTextIsMarkedAndBalanced()
    {
        var pdf = Document(); var page = pdf.AddPage(595, 842, 1, new(1, 1, 1));
        page.ActualText("עברית"); page.EndActualText();
        StringAssert.Contains(Expanded(pdf.ToBytes()), "/ActualText <FEFF05E205D105E805D905EA>");
        Assert.Throws<InvalidOperationException>(page.EndActualText);
        page.ActualText("unclosed"); Assert.Throws<InvalidOperationException>(() => pdf.ToBytes());
    }
    [TestMethod] public void ExcessivePopIsRejected() { var pdf = Document(); var page = pdf.AddPage(595, 842, 1, new(1, 1, 1)); Assert.Throws<InvalidOperationException>(page.Pop); }
    [TestMethod] public void CancellationStopsSerialization() { using var token = new CancellationTokenSource(); token.Cancel(); Assert.Throws<OperationCanceledException>(() => Document().ToBytes(token.Token)); }
    [TestMethod] public void OutputIsCultureIndependent()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try { System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR"); StringAssert.Contains(Expanded(Document().ToBytes()), "0.75 0 0 -0.75"); }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }
    [TestMethod] public void PaginationKeepsLineBandsIntact()
    {
        var slices = PdfPagePlanner.Plan(300, 100, [new(0, 90, 30, 30), new(0, 185, 30, 30)]);
        Assert.AreEqual(90d, slices[0].Height); Assert.AreEqual(185d, slices[2].Top);
        Assert.AreEqual(300d, slices[^1].Top + slices[^1].Height);
    }
    [TestMethod] public void OversizedAtomUsesTallPageRatherThanClipping()
    {
        var slices = PdfPagePlanner.Plan(500, 100, [new(0, 0, 30, 250)]);
        Assert.AreEqual(250d, slices[0].Height); Assert.AreEqual(250d, slices[1].Top);
    }
    [TestMethod] public void EmptyDocumentStillHasOnePage() => Assert.HasCount(1, PdfPagePlanner.Plan(0, 100, []));
    [TestMethod] public void PaginationRejectsInvalidCapacity() => Assert.Throws<ArgumentOutOfRangeException>(() => PdfPagePlanner.Plan(100, 0, []));
    [TestMethod] public void OverlappingProtectedBandsConverge()
    {
        var slices = PdfPagePlanner.Plan(200, 100, [new(0, 80, 30, 30), new(0, 70, 30, 20)]);
        Assert.AreEqual(70d, slices[0].Height); Assert.AreEqual(200d, slices[^1].Top + slices[^1].Height);
    }
}
