using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Export;
using MDEditor.Typesetting.Layout;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MarkdownThematicBreakTests
{
    [TestMethod]
    [DataRow("---")]
    [DataRow("***")]
    [DataRow("___")]
    [DataRow("- - -")]
    [DataRow(" * * * ")]
    [DataRow("   ____   ")]
    public void Recognized_rules_fold_to_one_object_without_changing_source(string marker)
    {
        var source = new SourceTextSnapshot($"before\r\n\r\n{marker}\r\n\r\nafter", 19);
        var rich = MarkdownRichTextProjection.Create(source);
        Assert.AreEqual("before\r\n\r\n\uFFFC\r\n\r\nafter", rich.Text.Display.Text);
        Assert.AreSame(source, rich.Text.Source);
        Assert.AreEqual(1, rich.ThematicBreaks.Length);
        var rule = rich.ThematicBreaks[0];
        Assert.IsTrue(rule.MarkerHidden);
        Assert.AreEqual(rule, rich.ThematicBreakAt(rule.Line));
        var replacement = rich.Text.ActiveReplacements.Single();
        Assert.AreEqual(MarkdownReplacementKind.ThematicBreak, replacement.Kind);
        Assert.AreEqual(marker, source.GetText(replacement.Source));
        Assert.AreEqual(replacement.Source, rich.Text.ToSourceRange(rule.Line, true));
        Assert.AreEqual(19L, rich.Text.Display.Version);
    }

    [TestMethod]
    public void Clicking_either_edge_reveals_source_and_leaving_folds_it_again()
    {
        var source = new SourceTextSnapshot("before\n\n---\n\nafter", 4);
        var folded = MarkdownRichTextProjection.Create(source);
        var rule = folded.Text.ThematicBreaks.Single();
        foreach (var offset in new[] { rule.Source.Start, rule.Source.Start + 1, rule.Source.End })
        {
            var entered = MarkdownRichTextProjection.Create(source, offset);
            Assert.AreEqual(source.Text, entered.Text.Display.Text);
            Assert.IsFalse(entered.ThematicBreaks.Single().MarkerHidden);
        }
        var left = MarkdownRichTextProjection.Create(source, source.Length);
        Assert.IsTrue(left.ThematicBreaks.Single().MarkerHidden);
        Assert.AreEqual(folded.Text.Display.Text, left.Text.Display.Text);
    }

    [TestMethod]
    [DataRow("Title\n---")]
    [DataRow("```\n---\n```")]
    [DataRow("    ---")]
    [DataRow("`---`")]
    [DataRow("a---b")]
    [DataRow("--")]
    [DataRow("| A | B |\n| --- | --- |\n| x | y |")]
    public void Headings_code_tables_and_incomplete_markers_are_not_rules(string text)
    {
        var rich = MarkdownRichTextProjection.Create(new(text, 1));
        Assert.AreEqual(0, rich.ThematicBreaks.Length);
        Assert.IsFalse(rich.Text.ActiveReplacements.Any(replacement =>
            replacement.Kind == MarkdownReplacementKind.ThematicBreak));
    }

    [TestMethod]
    [DataRow("> ---", 1, 0)]
    [DataRow("> > ***", 2, 0)]
    [DataRow("- item\n\n  ***", 0, 0)]
    [DataRow("- item\n\n    ***", 0, 1)]
    public void Container_depth_and_complete_prefix_survive_fold_and_reveal(string text, int quotes, int lists)
    {
        var source = new SourceTextSnapshot(text, 7);
        var folded = MarkdownRichTextProjection.Create(source);
        var rule = folded.ThematicBreaks.Single();
        Assert.AreEqual(quotes, rule.QuoteDepth);
        Assert.AreEqual(lists, rule.ListDepth, string.Join("; ", folded.Syntax.Descendants()
            .Select(node => $"{node.Kind}:{node.Source}")));
        var original = folded.Text.ThematicBreaks.Single();
        var entered = MarkdownRichTextProjection.Create(source, original.Source.Start);
        Assert.AreEqual(source.GetText(original.Source), entered.Text.Display.GetText(entered.ThematicBreaks.Single().Line));
        Assert.AreEqual("\uFFFC", folded.Text.Display.GetText(rule.Line));
    }

    [TestMethod]
    public void Source_mode_keeps_literal_markers_and_line_endings()
    {
        var source = new SourceTextSnapshot("---\r\n\r\n***\r\n", 6);
        var rich = MarkdownRichTextProjection.FromSyntax(MarkdownSyntaxParser.Parse(source), sourceMode: true);
        Assert.AreSame(source, rich.Text.Display);
        Assert.AreEqual(0, rich.ThematicBreaks.Length);
        Assert.AreEqual(0, rich.Text.ActiveReplacements.Length);
    }

    [TestMethod]
    public void Arrows_delete_and_selection_own_the_whole_folded_rule()
    {
        var rich = MarkdownRichTextProjection.Create(new("before\n\n- - -\n\nafter", 2));
        var rule = rich.ThematicBreaks.Single();
        var raw = rich.Text.ThematicBreaks.Single().Source;
        Assert.AreEqual(raw.End, rich.Text.MoveSourceCaret(raw.Start, 1));
        Assert.AreEqual(raw.Start, rich.Text.MoveSourceCaret(raw.End, -1));
        Assert.AreEqual(raw, rich.Text.DeleteForwardRange(raw.Start));
        Assert.AreEqual(raw, rich.Text.BackspaceRange(raw.End));
        var line = MarkdownThematicBreakLayout.Create(rule.Line, 600, 24, 16);
        var map = new MarkdownProjectionInteractionMap(rich.Text,
            new TextInteractionMap(rich.Text.Display, TextSurface.Body, [line]));
        var start = map.HitTestForEditing(new(0, 26))!.Value;
        var end = map.HitTestForEditing(new(600, 26))!.Value;
        Assert.AreEqual(raw.Start, start.Offset);
        Assert.AreEqual(raw.End, end.Offset);
        Assert.AreEqual(raw.End, map.MoveHorizontal(start, 1)!.Value.Offset);
        Assert.AreEqual(raw.Start, map.MoveHorizontal(end, -1)!.Value.Offset);
        Assert.AreEqual(line.Bounds, map.SelectionRects(new(start, end)).Single());
        Assert.IsNotNull(map.Resolve(start));
        Assert.IsNotNull(map.Resolve(end));
    }

    [TestMethod]
    public void Github_geometry_tracks_em_size_container_indent_and_available_width()
    {
        var line = MarkdownThematicBreakLayout.Create(new(0, 1), 600, 24, 16);
        Assert.AreEqual(new LayoutRect(0, 24, 600, 4), line.Bounds);
        Assert.AreEqual(28.0, line.Bounds.Bottom);
        var nested = MarkdownThematicBreakLayout.Create(new(0, 1), 300, 100, 20, 52);
        Assert.AreEqual(new LayoutRect(52, 100, 248, 5), nested.Bounds);
    }

    [TestMethod]
    public void Pagination_never_splits_a_rule_band()
    {
        var rule = MarkdownThematicBreakLayout.Create(new(0, 1), 600, 98, 16);
        var pages = PdfPagePlanner.Plan(180, 100, [rule.Bounds]);
        Assert.AreEqual(98.0, pages[0].Height);
        Assert.AreEqual(98.0, pages[1].Top);
        Assert.IsTrue(pages[1].Height >= rule.Bounds.Height);
    }

    [TestMethod]
    public void Incremental_edits_create_and_remove_rules_without_stale_metadata()
    {
        var first = new SourceTextSnapshot("before\n\n--\n\nafter", 1);
        var syntax = MarkdownSyntaxParser.Parse(first);
        var next = new SourceTextSnapshot("before\n\n---\n\nafter", 2);
        var added = MarkdownIncrementalParser.Update(syntax, next).Syntax;
        Assert.AreEqual(1, MarkdownRichTextProjection.FromSyntax(added).ThematicBreaks.Length);
        var last = new SourceTextSnapshot("before\n\n--Q\n\nafter", 3);
        var removed = MarkdownIncrementalParser.Update(added, last).Syntax;
        Assert.AreEqual(0, MarkdownRichTextProjection.FromSyntax(removed).ThematicBreaks.Length);
    }

    [TestMethod]
    public void Adjacent_rules_have_distinct_display_lines_and_keep_final_empty_line()
    {
        var rich = MarkdownRichTextProjection.Create(new("---\n***\n___\n", 1));
        Assert.AreEqual("\uFFFC\n\uFFFC\n\uFFFC\n", rich.Text.Display.Text);
        Assert.AreEqual(3, rich.ThematicBreaks.Length);
        Assert.AreEqual(4, DocumentLineMap.Create(rich.Text.Display).Count);
        foreach (var rule in rich.ThematicBreaks)
            Assert.AreEqual(rule, rich.ThematicBreakAt(rule.Line));
    }
}
