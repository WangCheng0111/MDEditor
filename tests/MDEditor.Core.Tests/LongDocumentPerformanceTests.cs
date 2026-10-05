using System.Diagnostics;
using System.Text;
using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class LongDocumentPerformanceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Fixed_long_document_projects_and_queries_visible_range()
    {
        const int paragraphCount = 1024;
        var text = new StringBuilder(paragraphCount * 96);
        for (var index = 0; index < paragraphCount; index++)
            text.Append("Paragraph ").Append(index).Append(
                " has **bold** and `code` with $x_i$ and 中文混排。\n\n");
        var source = new SourceTextSnapshot(text.ToString(), 1);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        var syntax = MarkdownSyntaxParser.Parse(source);
        var parseMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var editProjection = MarkdownEditProjection.Create(syntax);
        var editProjectionMs = timer.Elapsed.TotalMilliseconds;
        Assert.AreEqual(source.Length, editProjection.Source.Length);
        timer.Restart();
        var projection = MarkdownRichTextProjection.FromSyntax(syntax);
        var projectionMs = timer.Elapsed.TotalMilliseconds;
        Assert.AreEqual(paragraphCount * 2, projection.Text.Display.Text.Count(c => c == '\n'));

        var editAt = source.Text.IndexOf("Paragraph 512", StringComparison.Ordinal) +
            "Paragraph 512".Length;
        var edited = new SourceTextSnapshot(source.Text.Insert(editAt, "X"), 2);
        timer.Restart();
        var update = MarkdownIncrementalParser.Update(syntax, edited);
        var incrementalParseMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var editedProjection = MarkdownRichTextProjection.FromSyntax(update.Syntax);
        var incrementalProjectionMs = timer.Elapsed.TotalMilliseconds;
        Assert.AreEqual(MarkdownParseScope.IsolatedParagraph, update.Scope);
        StringAssert.Contains(editedProjection.Text.Display.Text, "Paragraph 512X");

        var entries = Enumerable.Range(0, paragraphCount)
            .Select(index => (index, Top: index * 30.0, Bottom: (index + 1) * 30.0));
        timer.Restart();
        var viewport = new ViewportIntervalIndex<int>(entries);
        for (var offset = 0; offset < paragraphCount * 30; offset += 30)
        {
            var (first, end) = viewport.CandidateRange(offset, offset + 600);
            Assert.IsTrue(end - first is >= 1 and <= 22);
            Assert.IsTrue(first <= offset / 30 && end > offset / 30);
        }
        var metrics = $"Fixed document: {paragraphCount} paragraphs, {source.Length} UTF-16 units; " +
            $"parse {parseMs:F1} ms; edit projection {editProjectionMs:F1} ms; " +
            $"rich projection {projectionMs:F1} ms; incremental parse {incrementalParseMs:F1} ms; " +
            $"incremental rich projection {incrementalProjectionMs:F1} ms; " +
            $"{paragraphCount} viewport queries {timer.Elapsed.TotalMilliseconds:F1} ms; " +
            $"allocated {(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 1_048_576.0:F1} MiB.";
        TestContext.WriteLine(metrics);
        Console.Error.WriteLine(metrics);
    }
}
