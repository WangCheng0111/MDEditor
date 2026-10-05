using System.Globalization;
using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Typesetting.LineBreaking;
using MDEditor.Typesetting.Layout;
using MDEditor.Typesetting.Typography;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class SourceModeTests
{
    [TestMethod]
    [DataRow("# **中*文*** `code` [link](https://x.test) $\\sqrt{x}$\r\n")]
    [DataRow("| a | b |\n|---|---|\n| a\\|b | `code` |")]
    [DataRow("![image](local.png) [^note] \\eqref{eq:a}\n\n[^note]: note\n\n$$x\\label{eq:a}$$")]
    [DataRow("```csharp\r\n    int a = 1;  \r\n```\r\n")]
    [DataRow("> - [x] 中文 😀 café cafe\u0301\n")]
    [DataRow("")]
    public void Identity_projection_keeps_every_boundary_and_disables_visual_atoms(string text)
    {
        var source = new SourceTextSnapshot(text, 23);
        var syntax = MarkdownSyntaxParser.Parse(source);
        var rich = MarkdownRichTextProjection.FromSyntax(syntax, sourceMode: true);
        Assert.IsTrue(rich.Text.IsSourceMode);
        Assert.AreSame(source, rich.Text.Display);
        Assert.AreSame(source, rich.Text.Source);
        Assert.IsEmpty(rich.Text.HiddenRanges);
        Assert.IsEmpty(rich.Text.ActiveReplacements);
        Assert.IsEmpty(rich.Text.MathSpans);
        Assert.IsEmpty(rich.Styles); Assert.IsEmpty(rich.Headings);
        Assert.IsEmpty(rich.Blocks); Assert.IsEmpty(rich.TableLines); Assert.IsEmpty(rich.CodeLines);
        for (var offset = 0; offset <= text.Length; offset++)
        {
            Assert.AreEqual(offset, rich.Text.ToDisplayOffset(offset));
            Assert.AreEqual(offset, rich.Text.ToSourceOffset(offset, ProjectionBoundary.BeforeHidden));
            Assert.AreEqual(offset, rich.Text.ToNavigationSourceOffset(offset, ProjectionBoundary.AfterHidden));
        }
        Assert.AreSame(rich.Text, rich.Text.RevealAt(0));
        var preview = MarkdownRichTextProjection.FromSyntax(syntax);
        Assert.IsFalse(preview.Text.IsSourceMode);
        Assert.AreSame(source, preview.Text.Source);
    }

    [TestMethod]
    public void Source_navigation_and_delete_are_literal_grapheme_safe_not_atomic_math()
    {
        var source = new SourceTextSnapshot("$\\sqrt{x}$ **bold** 😀 cafe\u0301", 1);
        var projection = MarkdownEditProjection.CreateSource(MarkdownSyntaxParser.Parse(source));
        Assert.AreEqual(new SourceRange(0, 1), projection.DeleteForwardRange(0));
        Assert.AreEqual(new SourceRange(source.Text.IndexOf("**", StringComparison.Ordinal), 1),
            projection.DeleteForwardRange(source.Text.IndexOf("**", StringComparison.Ordinal)));
        var emoji = source.Text.IndexOf("😀", StringComparison.Ordinal);
        Assert.AreEqual(new SourceRange(emoji, 2), projection.DeleteForwardRange(emoji));
        Assert.AreEqual(new SourceRange(source.Length - 2, 2), projection.BackspaceRange(source.Length));
    }

    [TestMethod]
    [DataRow("    abc  中文 😀 cafe\u0301    end  ")]
    [DataRow("$\\frac{x+1}{\\sqrt{y+1}}$")]
    [DataRow("                 ")]
    [DataRow("abc")]
    [DataRow("")]
    public void Source_softwrap_preserves_all_spaces_clusters_and_never_generates_text(string text)
    {
        var source = new SourceTextSnapshot(text, 0);
        var boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToArray();
        var clusters = boundaries.Zip(boundaries.Skip(1), (a, b) => new MeasuredTextCluster(new(a, b - a), 4, false));
        var map = ParagraphItemMap.Create(source, source.FullRange, clusters, 14, CjkTypographyOptions.Legacy,
            mergeSpaces: false);
        LineMeasurement? Measure(LineMeasureRequest request)
        {
            var range = map.GetSource(request);
            return new(map.Clusters.Where(c => range.Contains(c.Source)).Sum(c => c.Advance), 0, 0);
        }
        foreach (var width in new[] { 1d, 8d, 20d, 500d })
        {
            var result = LiteralLineBreaker.BreakWrapped(map, width, Measure);
            Assert.IsTrue(result.IsSuccess);
            var displays = result.Lines.Select(line => map.GetLine(new(line.StartItemIndex, line.EndItemIndex,
                line.BreakItemIndex, 0, line.IsParagraphEnd))).ToArray();
            Assert.AreEqual(text, string.Concat(displays.Select(d => d.GetText(source))));
            Assert.IsTrue(displays.All(d => d.Suffix.Length == 0));
            Assert.IsTrue(result.Lines.All(line => line.AdjustmentRatio == 0));
            Assert.IsTrue(result.Lines.All(line => line.NaturalWidth <= Math.Max(4, width)));
        }
    }

    [TestMethod]
    public void Mode_switch_does_not_change_snapshot_history_or_styles()
    {
        var buffer = new StyledDocumentBuffer("**bold**", [new(0, 4)], 5);
        var history = new TextEditHistory(buffer);
        var snapshot = buffer.Capture();
        var caret = new TextCaret(TextSurface.Body, 5, 8, CaretAffinity.Downstream);
        var edit = TextEditingOperations.Replace(buffer, new(caret, caret), "!");
        history.Record(snapshot, new(caret, caret), edit, HistoryEditKind.Typing, DateTimeOffset.UtcNow);
        var after = buffer.Capture();
        var syntax = MarkdownSyntaxParser.Parse(after.Source);
        for (var index = 0; index < 10; index++)
            _ = MarkdownRichTextProjection.FromSyntax(syntax, sourceMode: index % 2 == 0);
        Assert.AreSame(after, buffer.Capture());
        Assert.AreEqual("**bold**!", buffer.Capture().Source.Text);
        Assert.IsNotNull(history.Undo());
        Assert.AreEqual("**bold**", buffer.Capture().Source.Text);
        Assert.IsNotNull(history.Redo());
    }

    [TestMethod]
    public void Source_caret_selection_and_viewport_anchor_stay_in_original_coordinates()
    {
        var source = new SourceTextSnapshot("**bold** $x$", 17);
        var projection = MarkdownEditProjection.CreateSource(MarkdownSyntaxParser.Parse(source));
        var spans = Enumerable.Range(0, source.Length).Select(index =>
            new TextInteractionSpan(new(index, 1), index * 10, (index + 1) * 10));
        var display = new TextInteractionMap(source, TextSurface.Body,
            [new(source.FullRange, new(0, 0, source.Length * 10, 20), spans)]);
        var bridge = new MarkdownProjectionInteractionMap(projection, display);
        var caret = new TextCaret(TextSurface.Body, 17, 0, CaretAffinity.Downstream);
        Assert.AreEqual(1, bridge.MoveHorizontal(caret, 1)!.Value.Offset);
        var selection = new TextSelection(caret with { Offset = 2 }, caret with { Offset = 6 });
        Assert.AreEqual(new LayoutRect(20, 0, 40, 20), bridge.SelectionRects(selection).Single());
        var preview = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(source));
        Assert.AreEqual(10, ViewportAnchorMap.Map(preview.Source, projection.Source, 10));
        Assert.AreEqual(10, ViewportAnchorMap.Map(projection.Source, preview.Source, 10));
    }
}
