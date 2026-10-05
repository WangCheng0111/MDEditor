using MDEditor.Typesetting.Layout;
using MDEditor.Typesetting.LineBreaking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class ViewportReflowIntegrationTests
{
    [TestMethod]
    [DataRow(50)] [DataRow(75)] [DataRow(100)] [DataRow(125)] [DataRow(150)] [DataRow(200)] [DataRow(300)]
    public void FixedViewWidthZoomRebreaksRatherThanFitScaling(int zoom)
    {
        var viewport = new DocumentViewport(728.375, 500, zoom);
        var items = Enumerable.Range(0, 50).SelectMany(_ => new[]
        {
            LineBreakItem.Box(20), LineBreakItem.Glue(5, 20, 3)
        }).Append(LineBreakItem.Penalty(0, -LineBreakItem.InfinitePenalty)).ToArray();
        var result = KnuthPlassLineBreaker.Break(items, viewport.DocumentWidth);
        Assert.IsTrue(result.IsSuccess);
        foreach (var line in result.Lines)
            Assert.AreEqual(viewport.DocumentWidth, line.TargetWidth);
        var density = new DocumentViewport(728.375, 750, zoom, 192);
        var other = KnuthPlassLineBreaker.Break(items, density.DocumentWidth);
        Assert.IsTrue(result.Lines.SequenceEqual(other.Lines));
        if (zoom == 200)
        {
            var normal = KnuthPlassLineBreaker.Break(items, new DocumentViewport(728.375, 500).DocumentWidth);
            Assert.IsTrue(result.Lines.Length > normal.Lines.Length);
        }
    }
}
