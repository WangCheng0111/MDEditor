using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class DocumentViewportTests
{
    [TestMethod]
    [DataRow(50)] [DataRow(75)] [DataRow(100)] [DataRow(125)] [DataRow(150)] [DataRow(200)] [DataRow(300)]
    public void RightEdgeAndRoundTripUseOneZoom(int percent)
    {
        var v = new DocumentViewport(983.375, 612.125, percent, 144);
        Assert.AreEqual((double)(float)((983.375 - 48) / v.Zoom), v.DocumentWidth);
        var edge = v.ToView(new(v.DocumentWidth, 0));
        Assert.AreEqual(983.375 - 24, edge.X, 0.0002);
        var point = new LayoutPoint(73.125, 220.375);
        var back = v.ToDocument(v.ToView(point, 38.25), 38.25);
        Assert.AreEqual(point.X, back.X, 1e-10); Assert.AreEqual(point.Y, back.Y, 1e-10);
        Assert.AreEqual(edge.X * 1.5, v.ToPixels(edge).X, 1e-10);
    }
    [TestMethod]
    [DataRow(50)] [DataRow(100)] [DataRow(125)] [DataRow(300)]
    public void PointerOutsidePagePaddingCannotHitText(int percent)
    {
        var viewport = new DocumentViewport(983.375, 612.125, percent);
        Assert.IsFalse(viewport.ContainsDocumentViewX(DocumentViewport.Padding - 0.01));
        Assert.IsTrue(viewport.ContainsDocumentViewX(DocumentViewport.Padding));
        Assert.IsTrue(viewport.ContainsDocumentViewX(viewport.Width - DocumentViewport.Padding));
        Assert.IsFalse(viewport.ContainsDocumentViewX(viewport.Width - DocumentViewport.Padding + 0.01));
        Assert.IsFalse(viewport.ContainsDocumentViewX(double.NaN));
        Assert.IsFalse(new DocumentViewport(48, 100).ContainsDocumentViewX(24));
    }
    [TestMethod]
    [DataRow(96d)] [DataRow(120d)] [DataRow(144d)] [DataRow(192d)] [DataRow(288d)]
    public void DpiDoesNotChangeLayoutWidth(double dpi)
    {
        var v = new DocumentViewport(822.125, 488.5, 125, dpi);
        Assert.AreEqual(new DocumentViewport(822.125, 900, 125).DocumentWidth, v.DocumentWidth);
        Assert.AreEqual((822.125 - 48) / 1.25, v.DocumentWidth, 0.0001);
    }
    [TestMethod]
    [DataRow(0d, 0d)] [DataRow(20d, 100d)] [DataRow(100d, 48d)] [DataRow(48d, 100d)]
    public void HiddenViewportHasNoLayout(double width, double height)
    {
        var v = new DocumentViewport(width, height);
        Assert.IsFalse(v.IsVisible); Assert.AreEqual(0d, v.DocumentWidth); Assert.AreEqual(0d, v.DocumentHeight);
    }
    [TestMethod]
    public void ScrollIsInDocumentDipAndClamped()
    {
        var v = new DocumentViewport(800, 448, 200);
        Assert.AreEqual(200d, v.DocumentHeight); Assert.AreEqual(800d, v.MaximumScroll(1000));
        Assert.AreEqual(800d, v.ClampScroll(10000, 1000)); Assert.AreEqual(0d, v.ClampScroll(-1, 1000));
        Assert.AreEqual(0d, v.ClampScroll(5, 100));
        Assert.AreEqual(24d, v.ToView(new(0, 800), 800).Y);
    }
    [TestMethod]
    public void NativeBaselineRecoveredBeforeFloatAnchorAddition()
    {
        const double y = 117.15537109374999;
        var range = new SourceRange(0, 1);
        var run = new GlyphRunLayout(0, range, new(0, y + 18.28125), 18, 0, "zh-CN",
            [new(42, 18)], [new(range, 0, 1, 18)]);
        var line = new LineLayout(range, new(0, y, 100, 23), y + 18.28125, 18, [run]);
        var offset = new LayoutPoint(16, (float)(16 - y));
        var actual = GlyphPaintCoordinates.Resolve(line, run, offset);
        Assert.AreEqual((double)(float)((float)(y + offset.Y) + 18.28125f), actual.Y);
    }
    [TestMethod]
    public void NativeRunWidthUsesLocalFloatAccumulationThenLinePen()
    {
        var glyphs = Enumerable.Repeat(new GlyphPlacement(42, 18.422794342041016), 12).ToArray();
        Assert.AreEqual(221.07350158691406f, GlyphPaintCoordinates.AdvancePen(0, glyphs));
        Assert.AreNotEqual((float)glyphs.Sum(g => g.Advance), GlyphPaintCoordinates.AdvancePen(0, glyphs));
        Assert.AreEqual((float)(23.33919334411621f + 221.07350158691406f), GlyphPaintCoordinates.AdvancePen(23.33919334411621f, glyphs));
        Assert.Throws<ArgumentOutOfRangeException>(() => GlyphPaintCoordinates.AdvancePen(float.PositiveInfinity, glyphs));
    }
    [TestMethod]
    [DataRow(double.NaN)] [DataRow(double.PositiveInfinity)] [DataRow(-1d)] [DataRow(50001d)]
    public void InvalidSizesRejected(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentViewport(value, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentViewport(100, value));
    }
    [TestMethod]
    [DataRow(0)] [DataRow(49)] [DataRow(301)] [DataRow(int.MaxValue)]
    public void InvalidZoomRejected(int value) => Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentViewport(100, 100, value));
    [TestMethod]
    [DataRow(double.NaN)] [DataRow(0d)] [DataRow(-96d)] [DataRow(9601d)]
    public void InvalidDpiRejected(double value) => Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentViewport(100, 100, dpi: value));
    [TestMethod]
    public void InvalidScrollRejected()
    {
        var v = new DocumentViewport(100, 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => v.ClampScroll(double.NaN, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => v.MaximumScroll(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => v.ToView(new(0, 0), double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => v.ToDocument(new(0, 0), double.NaN));
    }
    [TestMethod]
    public void LatestResultWinsIncludingHiddenAndClosedSurface()
    {
        var gate = new LayoutRequestGate();
        var old = gate.Next(); var latest = gate.Next();
        Assert.IsFalse(gate.IsCurrent(old)); Assert.IsTrue(gate.IsCurrent(latest));
        gate.Next(); Assert.IsFalse(gate.IsCurrent(latest)); // hidden/device generation revoked
        var last = gate.Next(); gate.Close(); Assert.IsFalse(gate.IsCurrent(last));
        Assert.Throws<ObjectDisposedException>(() => gate.Next());
    }
    [TestMethod]
    public void ThousandsOfOutOfOrderRequestsCannotCommit()
    {
        var gate = new LayoutRequestGate();
        var all = Enumerable.Range(0, 5000).Select(_ => gate.Next()).ToArray();
        Assert.AreEqual(1, all.Reverse().Count(gate.IsCurrent));
        Assert.IsTrue(gate.IsCurrent(all[^1]));
    }
    private static LayoutSnapshot Part(SourceTextSnapshot source, int start, double y, FontFaceDescriptor font)
    {
        var range = new SourceRange(start, 1); var bounds = new LayoutRect(0, y, 10, 20);
        var run = new GlyphRunLayout(0, range, new(0, y + 14), 17, 0, "en-US",
            [new(42, 10)], [new(range, 0, 1, 10)]);
        return new(source, bounds, [font], [new(range, bounds, [new(range, bounds, y + 14, 10, [run])])]);
    }
    [TestMethod]
    public void CompositeRemapsSlotsWithoutNameBasedDeduplication()
    {
        var source = new SourceTextSnapshot("a\nb", 99);
        var f1 = new FontFaceDescriptor("Cambria", "Regular"); var f2 = new FontFaceDescriptor("Cambria", "Regular");
        var a = Part(source, 0, 0, f1); var b = Part(source, 2, 30, f2);
        var result = LayoutSnapshotComposer.Compose(source, new(0, 0, 10, 50), [a, b]);
        Assert.AreEqual(2, result.Fonts.Length); Assert.AreSame(f1, result.Fonts[0]); Assert.AreSame(f2, result.Fonts[1]);
        Assert.AreEqual(0, result.Blocks[0].Lines[0].Runs[0].FontIndex);
        Assert.AreEqual(1, result.Blocks[1].Lines[0].Runs[0].FontIndex);
        Assert.AreEqual(0, b.Blocks[0].Lines[0].Runs[0].FontIndex);
        Assert.AreEqual(new SourceRange(2, 1), result.Blocks[1].Lines[0].Source);
    }
    [TestMethod]
    public void CompositeRejectsForeignSourceEvenIfSameText()
    {
        var s = new SourceTextSnapshot("a", 0); var foreign = new SourceTextSnapshot("a", 0);
        Assert.Throws<ArgumentException>(() => LayoutSnapshotComposer.Compose(s, new(0, 0, 10, 20),
            [Part(foreign, 0, 0, new("Cambria", "Regular"))]));
    }
    [TestMethod]
    public void CompositeRequiresDocumentBoundsAndSupportsEmptyDocument()
    {
        var s = new SourceTextSnapshot("a", 0);
        Assert.Throws<ArgumentException>(() => LayoutSnapshotComposer.Compose(s, new(0, 0, 5, 20), [Part(s, 0, 0, new("Cambria", "Regular"))]));
        Assert.AreEqual(0, LayoutSnapshotComposer.Compose(s, new(0, 0, 0, 0), []).Blocks.Length);
    }
}
