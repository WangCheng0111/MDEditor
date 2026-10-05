using System.Globalization;
using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Typesetting.LineBreaking;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class LiteralLineBreakerTests
{
    [TestMethod]
    [DataRow(" ")]
    [DataRow("    ")]
    [DataRow("                ")]
    [DataRow("    print (i,j,k)")]
    [DataRow("    print (i,j,k)    ")]
    [DataRow("  // 中文注释 😀  ")]
    [DataRow("```python")]
    [DataRow("```")]
    public void Every_code_character_including_margin_spaces_remains_in_one_unadjusted_line(string text)
    {
        var source = new SourceTextSnapshot("prefix\n" + text + "\nsuffix", 2);
        var range = new SourceRange(7, text.Length);
        var map = Map(source, range);
        var measurement = new LineMeasurement(map.Clusters.Sum(cluster => cluster.Advance), 10, 0);
        var result = LiteralLineBreaker.Break(map, measurement.NaturalWidth + 8, measurement);

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, result.Lines);
        var line = result.Lines[0];
        var display = map.GetLine(new(line.StartItemIndex, line.EndItemIndex,
            line.BreakItemIndex, line.BreakWidth, line.IsParagraphEnd));
        Assert.AreEqual(range, display.Source);
        Assert.AreEqual(text, display.GetText(source));
        Assert.AreEqual(0d, line.AdjustmentRatio);
        Assert.AreEqual(measurement.NaturalWidth, line.ActualWidth);
        Assert.AreEqual("", display.Suffix);
    }

    [TestMethod]
    public void Empty_fence_line_keeps_the_existing_empty_paragraph_path()
    {
        var source = new SourceTextSnapshot("", 1);
        var result = LiteralLineBreaker.Break(Map(source, source.FullRange), 300, new(0, 0, 0));
        Assert.IsTrue(result.IsSuccess);
        Assert.IsEmpty(result.Lines);
    }

    [TestMethod]
    public void Narrow_width_cannot_discard_indentation_or_wrap_the_code()
    {
        var source = new SourceTextSnapshot("    print(1)    ", 1);
        var map = Map(source, source.FullRange);
        var measurement = new LineMeasurement(map.Clusters.Sum(cluster => cluster.Advance), 20, 10);
        var result = LiteralLineBreaker.Break(map, measurement.NaturalWidth - 1, measurement);
        Assert.AreEqual(LineBreakStatus.NoFeasibleBreaks, result.Status);
        Assert.IsEmpty(result.Lines);
    }

    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    public void Pasted_python_with_a_space_only_blank_line_preserves_every_physical_code_line(string newline)
    {
        string[] code = ["#!/usr/bin/python", "# -*- coding: UTF-8 -*-", " ",
            "for i in range(1,5):", "    for j in range(1,5):", "        for k in range(1,5):",
            "            if( i != k ) and (i != j) and (j != k):", "                print (i,j,k)"];
        var source = new SourceTextSnapshot("```python" + newline + string.Join(newline, code) + newline +
            "```" + newline + newline + "| a | b |" + newline + "|---|---|" + newline + "| 1 | 2 |", 3);
        var rich = MarkdownRichTextProjection.Create(source);
        Assert.HasCount(1, rich.Text.CodeBlocks.Blocks);
        var content = rich.CodeLines.Where(line => line.Role == MarkdownCodeLineRole.Content).ToArray();
        Assert.HasCount(code.Length, content);
        for (var index = 0; index < content.Length; index++)
        {
            var map = Map(rich.Text.Display, content[index].Line);
            var measurement = new LineMeasurement(map.Clusters.Sum(cluster => cluster.Advance), 0, 0);
            var result = LiteralLineBreaker.Break(map, measurement.NaturalWidth + 8, measurement);
            Assert.HasCount(1, result.Lines);
            var line = result.Lines[0];
            var display = map.GetLine(new(line.StartItemIndex, line.EndItemIndex,
                line.BreakItemIndex, line.BreakWidth, line.IsParagraphEnd));
            Assert.AreEqual(code[index], display.GetText(rich.Text.Display));
        }
        Assert.HasCount(1, rich.Text.Tables.Tables);
        Assert.AreEqual(source.Text, rich.Text.Source.Text);
    }

    private static ParagraphItemMap Map(SourceTextSnapshot source, SourceRange range)
    {
        var text = source.GetText(range);
        var boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToArray();
        var clusters = boundaries.Zip(boundaries.Skip(1), (start, end) => new MeasuredTextCluster(
            new(range.Start + start, end - start), 4, text[end - 1] == ' '));
        return ParagraphItemMap.Create(source, range, clusters, 16);
    }
}
