using MDEditor.Core.Markdown;
using MDEditor.Core.Text;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MarkdownSyntaxParserTests
{
    [TestMethod]
    public void Empty_document_keeps_an_empty_root_and_the_input_snapshot()
    {
        var source = new SourceTextSnapshot("", 17);
        var parsed = MarkdownSyntaxParser.Parse(source);

        Assert.AreSame(source, parsed.Source);
        Assert.AreEqual(17L, parsed.Source.Version);
        Assert.AreEqual(MarkdownSyntaxKind.Document, parsed.Root.Kind);
        Assert.AreEqual(new SourceRange(0, 0), parsed.Root.Source);
        Assert.AreEqual(0, parsed.Descendants().Count());
    }

    [TestMethod]
    public void CommonMark_block_forms_have_distinct_nodes()
    {
        const string markdown = "# Title\n\n> quoted\n\n- first\n- second\n\n```cs\nx\n```\n\n    indented\n\n---\n";
        var parsed = Parse(markdown);

        foreach (var kind in new[] { MarkdownSyntaxKind.Heading, MarkdownSyntaxKind.Quote,
            MarkdownSyntaxKind.List, MarkdownSyntaxKind.ListItem, MarkdownSyntaxKind.FencedCode,
            MarkdownSyntaxKind.IndentedCode, MarkdownSyntaxKind.ThematicBreak })
            Assert.IsTrue(parsed.Descendants().Any(node => node.Kind == kind), $"Missing {kind}.");
        Assert.AreEqual(2, parsed.Descendants().Count(node => node.Kind == MarkdownSyntaxKind.ListItem));
        AssertSourceEquals(parsed, MarkdownSyntaxKind.Heading, "# Title");
        AssertSourceEquals(parsed, MarkdownSyntaxKind.FencedCode, "```cs\nx\n```");
        AssertTreeRanges(parsed);
    }

    [TestMethod]
    public void CommonMark_inline_forms_preserve_delimiter_ranges()
    {
        const string markdown = "Text **bold** *italic* [link](https://example.com) ![alt](a.png) `code`  \nnext";
        var parsed = Parse(markdown);

        AssertSourceContains(parsed, MarkdownSyntaxKind.Strong, "**bold**");
        AssertSourceContains(parsed, MarkdownSyntaxKind.Emphasis, "*italic*");
        AssertSourceContains(parsed, MarkdownSyntaxKind.Link, "[link](https://example.com)");
        AssertSourceContains(parsed, MarkdownSyntaxKind.Image, "![alt](a.png)");
        AssertSourceContains(parsed, MarkdownSyntaxKind.CodeSpan, "`code`");
        Assert.IsTrue(parsed.Descendants().Any(node => node.Kind == MarkdownSyntaxKind.HardBreak));
        AssertTreeRanges(parsed);
    }

    [TestMethod]
    public void Gfm_tables_keep_table_row_and_cell_source_ranges()
    {
        const string markdown = "| Name | Value |\n| :--- | ---: |\n| 中 | 42 |\n";
        var parsed = Parse(markdown);

        Assert.AreEqual(1, parsed.Descendants().Count(node => node.Kind == MarkdownSyntaxKind.Table));
        Assert.AreEqual(2, parsed.Descendants().Count(node => node.Kind == MarkdownSyntaxKind.TableRow));
        Assert.AreEqual(4, parsed.Descendants().Count(node => node.Kind == MarkdownSyntaxKind.TableCell));
        AssertSourceEquals(parsed, MarkdownSyntaxKind.TableCell, " Name ");
        AssertSourceEquals(parsed, MarkdownSyntaxKind.TableCell, " 42 ");
        AssertTreeRanges(parsed);
    }

    [TestMethod]
    public void Gfm_tasks_strikethrough_and_bare_urls_are_explicitly_enabled()
    {
        const string markdown = "- [x] done\n- [ ] next\n\n~~gone~~ https://example.com/path\n";
        var parsed = Parse(markdown);

        Assert.AreEqual(2, parsed.Descendants().Count(node => node.Kind == MarkdownSyntaxKind.TaskMarker));
        AssertSourceEquals(parsed, MarkdownSyntaxKind.TaskMarker, "[x]");
        AssertSourceEquals(parsed, MarkdownSyntaxKind.TaskMarker, "[ ]");
        AssertSourceEquals(parsed, MarkdownSyntaxKind.Strikethrough, "~~gone~~");
        AssertSourceEquals(parsed, MarkdownSyntaxKind.Link, "https://example.com/path");
        AssertTreeRanges(parsed);
    }

    [TestMethod]
    public void Reference_links_setext_headings_and_soft_breaks_remain_commonmark()
    {
        const string markdown = "Heading\n=======\n\n[first][id]\nsecond line\n\n[id]: https://example.com \"Title\"\n";
        var parsed = Parse(markdown);

        Assert.IsTrue(parsed.Descendants().Any(node => node.Kind == MarkdownSyntaxKind.Heading));
        AssertSourceEquals(parsed, MarkdownSyntaxKind.Link, "[first][id]");
        Assert.IsTrue(parsed.Descendants().Any(node => node.Kind == MarkdownSyntaxKind.SoftBreak));
        Assert.AreEqual(markdown, parsed.Source.Text);
        AssertTreeRanges(parsed);
    }

    [TestMethod]
    public void Angle_autolinks_and_extended_www_links_are_recognized()
    {
        const string markdown = "<https://example.com> www.example.org\n";
        var parsed = Parse(markdown);

        AssertSourceEquals(parsed, MarkdownSyntaxKind.Link, "<https://example.com>");
        AssertSourceEquals(parsed, MarkdownSyntaxKind.Link, "www.example.org");
        AssertTreeRanges(parsed);
    }

    [TestMethod]
    public void Entities_escapes_and_raw_html_keep_their_original_source_text()
    {
        const string markdown = "A &amp; B \\* C <b>raw</b>\n";
        var parsed = Parse(markdown);

        AssertSourceEquals(parsed, MarkdownSyntaxKind.Entity, "&amp;");
        AssertSourceEquals(parsed, MarkdownSyntaxKind.HtmlInline, "<b>");
        AssertSourceEquals(parsed, MarkdownSyntaxKind.HtmlInline, "</b>");
        Assert.IsFalse(parsed.Descendants().Any(node => node.Kind == MarkdownSyntaxKind.Emphasis));
        Assert.AreEqual(markdown, parsed.Source.Text);
        AssertTreeRanges(parsed);
    }

    [TestMethod]
    public void Empty_table_cells_remain_structural_cells_without_invented_text()
    {
        const string markdown = "| A | B |\n| --- | --- |\n|   | x |\n";
        var parsed = Parse(markdown);

        Assert.AreEqual(4, parsed.Descendants().Count(node => node.Kind == MarkdownSyntaxKind.TableCell),
            Describe(parsed));
        AssertSourceEquals(parsed, MarkdownSyntaxKind.TableCell, "   ");
        AssertTreeRanges(parsed);
    }

    [TestMethod]
    public void Table_cell_slots_ignore_escaped_pipes_and_pipes_inside_code_spans()
    {
        const string markdown = "| A | B |\n| --- | --- |\n| a\\|b |   |\n| `c|d` |   |\n";
        var parsed = Parse(markdown);

        Assert.AreEqual(6, parsed.Descendants().Count(node => node.Kind == MarkdownSyntaxKind.TableCell),
            Describe(parsed));
        AssertSourceEquals(parsed, MarkdownSyntaxKind.TableCell, " a\\|b ");
        AssertSourceEquals(parsed, MarkdownSyntaxKind.TableCell, "`c|d`");
        Assert.AreEqual(2, parsed.Descendants().Count(node => node.Kind == MarkdownSyntaxKind.TableCell &&
            parsed.Source.GetText(node.Source) == "   "));
        AssertTreeRanges(parsed);
    }

    [TestMethod]
    public void An_entirely_empty_table_row_still_has_two_locatable_cells()
    {
        const string markdown = "| A | B |\r\n| --- | --- |\r\n|   |   |\r\n";
        var parsed = Parse(markdown);
        var rows = parsed.Descendants().Where(node => node.Kind == MarkdownSyntaxKind.TableRow).ToArray();
        Assert.IsTrue(rows.Length >= 2, Describe(parsed));
        var cells = rows[^1].Children.Where(node => node.Kind == MarkdownSyntaxKind.TableCell).ToArray();

        Assert.AreEqual(2, cells.Length);
        Assert.AreEqual("   ", parsed.Source.GetText(cells[0].Source), Describe(parsed));
        Assert.AreEqual("   ", parsed.Source.GetText(cells[1].Source), Describe(parsed));
        AssertTreeRanges(parsed);
    }

    [TestMethod]
    public void A_table_inside_a_quote_keeps_empty_cells_in_the_quoted_line()
    {
        const string markdown = "> | A | B |\n> | --- | --- |\n> |   | x |\n";
        var parsed = Parse(markdown);
        var cells = parsed.Descendants().Where(node => node.Kind == MarkdownSyntaxKind.TableCell).ToArray();

        Assert.AreEqual(4, cells.Length, Describe(parsed));
        Assert.IsTrue(cells.Any(cell => parsed.Source.GetText(cell.Source) == "   "), Describe(parsed));
        AssertTreeRanges(parsed);
    }

    [TestMethod]
    public void Unrelated_advanced_extensions_are_not_enabled()
    {
        const string markdown = "~not subscript~ and ^not superscript^ and ==not mark==\n";
        var parsed = Parse(markdown);

        Assert.IsFalse(parsed.Descendants().Any(node => node.Kind is MarkdownSyntaxKind.Emphasis
            or MarkdownSyntaxKind.Strong or MarkdownSyntaxKind.Strikethrough));
        Assert.AreEqual(markdown, parsed.Source.Text);
        AssertTreeRanges(parsed);
    }

    [TestMethod]
    public void Ranges_are_utf16_offsets_into_unchanged_crlf_and_unicode_source()
    {
        const string markdown = "😀中\r\n\r\n**cafe\u0301** and `字`\r\n";
        var source = new SourceTextSnapshot(markdown, 93);
        var parsed = MarkdownSyntaxParser.Parse(source);
        var strong = parsed.Descendants().Single(node => node.Kind == MarkdownSyntaxKind.Strong);
        var code = parsed.Descendants().Single(node => node.Kind == MarkdownSyntaxKind.CodeSpan);

        Assert.AreSame(source, parsed.Source);
        Assert.AreEqual(93L, parsed.Source.Version);
        Assert.AreEqual(new SourceRange(markdown.IndexOf("**cafe", StringComparison.Ordinal),
            "**cafe\u0301**".Length), strong.Source);
        Assert.AreEqual(new SourceRange(markdown.IndexOf("`字`", StringComparison.Ordinal), 3), code.Source);
        Assert.AreEqual("**cafe\u0301**", source.GetText(strong.Source));
        AssertTreeRanges(parsed);
    }

    [TestMethod]
    public void Incomplete_inline_syntax_remains_editable_and_can_be_completed()
    {
        foreach (var (unfinished, completion, expectedKind) in new[]
        {
            ("**未闭合", "**", MarkdownSyntaxKind.Strong),
            ("[link](", "url)", MarkdownSyntaxKind.Link),
            ("`code", "`", MarkdownSyntaxKind.CodeSpan)
        })
        {
            var buffer = new DocumentTextBuffer(unfinished, 5);
            var before = MarkdownSyntaxParser.Parse(buffer.CaptureSnapshot());
            Assert.AreEqual(unfinished, before.Source.Text);
            Assert.AreEqual(new SourceRange(0, unfinished.Length), before.Root.Source);
            AssertTreeRanges(before);

            buffer.Insert(buffer.Length, completion);
            var after = MarkdownSyntaxParser.Parse(buffer.CaptureSnapshot());
            Assert.AreEqual(unfinished + completion, after.Source.Text);
            Assert.AreEqual(6L, after.Source.Version);
            Assert.IsTrue(after.Descendants().Any(node => node.Kind == expectedKind),
                $"Closing {unfinished} should produce {expectedKind}.");
            Assert.AreEqual(unfinished, before.Source.Text);
            AssertTreeRanges(after);
        }
    }

    [TestMethod]
    public void Unclosed_fence_is_preserved_and_reparses_after_edit()
    {
        var buffer = new DocumentTextBuffer("```cs\ncode\n", 2);
        var before = MarkdownSyntaxParser.Parse(buffer.CaptureSnapshot());
        Assert.IsTrue(before.Descendants().Any(node => node.Kind == MarkdownSyntaxKind.FencedCode),
            $"Parsed nodes: {Describe(before)}");
        AssertSourceEquals(before, MarkdownSyntaxKind.FencedCode, "```cs\ncode\n");
        AssertTreeRanges(before);

        buffer.Insert(buffer.Length, "```\n");
        var after = MarkdownSyntaxParser.Parse(buffer.CaptureSnapshot());
        AssertSourceEquals(after, MarkdownSyntaxKind.FencedCode, "```cs\ncode\n```");
        Assert.AreEqual(3L, after.Source.Version);
        AssertTreeRanges(after);
    }

    [TestMethod]
    public void An_unclosed_fence_inside_a_quote_stops_at_the_quote_boundary()
    {
        const string markdown = "> ```\n> code\noutside\n";
        var parsed = Parse(markdown);
        var quote = parsed.Descendants().Single(node => node.Kind == MarkdownSyntaxKind.Quote);
        var fence = parsed.Descendants().Single(node => node.Kind == MarkdownSyntaxKind.FencedCode);

        Assert.IsTrue(quote.Source.Contains(fence.Source));
        Assert.IsFalse(parsed.Source.GetText(fence.Source).Contains("outside", StringComparison.Ordinal));
        Assert.IsTrue(parsed.Descendants().Any(node => node.Kind == MarkdownSyntaxKind.Paragraph &&
            parsed.Source.GetText(node.Source).Contains("outside", StringComparison.Ordinal)));
        AssertTreeRanges(parsed);
    }

    [TestMethod]
    public void Every_reported_node_is_nested_within_its_parent_across_syntax_samples()
    {
        foreach (var markdown in new[]
        {
            "## a\r\n\r\nb *c* and **d**\r\n",
            "> - [x] task\n> - [ ] next\n",
            "| a | b |\n| --- | --- |\n| 1 | 2 |\n",
            "<span>html</span>  \nnext\n",
            "a\\*b &amp; c ![图](p.png)\n",
            "😀 **bold** ~~gone~~ https://example.com\n",
            "**unfinished [link](\n",
            "```\nunfinished"
        })
            AssertTreeRanges(Parse(markdown));
    }

    [TestMethod]
    public void Every_typing_prefix_keeps_an_editable_source_and_bounded_nodes()
    {
        const string intended = "# 标题\r\n\r\nText **bold** [link][ref]\r\n\r\n[ref]: https://example.com\r\n\r\n| A | B |\r\n| --- | --- |\r\n|   | x |\r\n\r\n```cs\r\ncode\r\n```";
        var buffer = new DocumentTextBuffer();
        for (var index = 0; index < intended.Length; index++)
        {
            if (intended[index] == '\r')
            {
                buffer.Insert(buffer.Length, "\r\n");
                index++;
            }
            else buffer.Insert(buffer.Length, intended[index].ToString());
            var snapshot = buffer.CaptureSnapshot();
            var parsed = MarkdownSyntaxParser.Parse(snapshot);
            Assert.AreSame(snapshot, parsed.Source);
            Assert.AreEqual(snapshot.Text, parsed.Source.GetText(parsed.Root.Source));
            AssertTreeRanges(parsed);
        }
        Assert.AreEqual(intended, buffer.CaptureSnapshot().Text);
    }

    private static MarkdownSyntaxDocument Parse(string markdown) =>
        MarkdownSyntaxParser.Parse(new SourceTextSnapshot(markdown, 0));

    private static void AssertSourceContains(MarkdownSyntaxDocument document,
        MarkdownSyntaxKind kind, string sourceText)
    {
        Assert.IsTrue(document.Descendants().Where(node => node.Kind == kind)
            .Any(node => document.Source.GetText(node.Source).Contains(sourceText, StringComparison.Ordinal)),
            $"No {kind} node includes {sourceText}. Parsed nodes: {Describe(document)}");
    }

    private static void AssertSourceEquals(MarkdownSyntaxDocument document,
        MarkdownSyntaxKind kind, string sourceText)
    {
        Assert.IsTrue(document.Descendants().Where(node => node.Kind == kind)
            .Any(node => document.Source.GetText(node.Source) == sourceText),
            $"No {kind} node equals {sourceText}. Parsed nodes: {Describe(document)}");
    }

    private static string Describe(MarkdownSyntaxDocument document) => string.Join("; ",
        document.Descendants().Select(node => $"{node.Kind} {node.Source}: " +
            document.Source.GetText(node.Source).Replace("\n", "\\n", StringComparison.Ordinal)));

    private static void AssertTreeRanges(MarkdownSyntaxDocument document)
    {
        Assert.AreEqual(document.Source.FullRange, document.Root.Source);
        Visit(document.Root, document.Source);

        void Visit(MarkdownSyntaxNode parent, SourceTextSnapshot source)
        {
            var previousStart = parent.Source.Start;
            foreach (var child in parent.Children)
            {
                Assert.IsTrue(parent.Source.Contains(child.Source),
                    $"{child.Kind} {child.Source} is outside {parent.Kind} {parent.Source}.");
                Assert.IsTrue(child.Source.Start >= previousStart,
                    $"{child.Kind} is out of source order. Parsed nodes: {Describe(document)}");
                previousStart = child.Source.Start;
                _ = source.GetText(child.Source);
                Visit(child, source);
            }
        }
    }
}
