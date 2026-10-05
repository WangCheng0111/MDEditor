using MDEditor.Core.Markdown;
using MDEditor.Core.Text;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MarkdownIncrementalParserTests
{
    [TestMethod]
    public void Isolated_inline_edit_reuses_prefix_and_suffix_with_exact_utf16_ranges()
    {
        var first = new SourceTextSnapshot("# 标题\r\n\r\n中文 **bold** and `code`\r\n\r\n尾段 😀\r\n", 1);
        var old = MarkdownSyntaxParser.Parse(first);
        var next = new SourceTextSnapshot(first.Text.Replace("bold", "加粗😀"), 2);
        var update = MarkdownIncrementalParser.Update(old, next);

        Assert.AreEqual(MarkdownParseScope.IsolatedParagraph, update.Scope);
        Assert.AreSame(old.Root.Children[0], update.Syntax.Root.Children[0]);
        Assert.AreEqual(new SourceRange(next.Text.IndexOf("中文", StringComparison.Ordinal),
            "中文 **加粗😀** and `code`".Length), update.NewRange);
        AssertTreeEquals(MarkdownSyntaxParser.Parse(next), update.Syntax);
        var expected = MarkdownRichTextProjection.Create(next);
        var actual = MarkdownRichTextProjection.FromSyntax(update.Syntax);
        Assert.AreEqual(expected.Text.Display.Text, actual.Text.Display.Text);
        CollectionAssert.AreEqual(expected.Text.HiddenRanges.ToArray(), actual.Text.HiddenRanges.ToArray());
        CollectionAssert.AreEqual(expected.Styles.ToArray(), actual.Styles.ToArray());
    }

    [TestMethod]
    public void Completing_unfinished_inline_syntax_updates_only_the_isolated_paragraph()
    {
        var old = MarkdownSyntaxParser.Parse(new SourceTextSnapshot("前段\n\n这里 **未闭合\n\n后段", 1));
        var next = new SourceTextSnapshot("前段\n\n这里 **加粗**\n\n后段", 2);
        var update = MarkdownIncrementalParser.Update(old, next);

        Assert.AreEqual(MarkdownParseScope.IsolatedParagraph, update.Scope);
        AssertTreeEquals(MarkdownSyntaxParser.Parse(next), update.Syntax);
    }

    [TestMethod]
    [DataRow("正文\n\n第二段", "正文\n```cs\n第二段", "fence")]
    [DataRow("正文\n\n第二段", "正文\n\n- 第二段", "list")]
    [DataRow("前段\n\n[id]: old\n\n[名称][id]", "前段\n\n[id]: new\n\n[名称][id]", "definition")]
    [DataRow("| a | b |\n|---|---|\n| 1 | 2 |", "| a | b |\n|---|---|\n| 1 | 3 |", "table")]
    public void Structural_and_reference_edits_fall_back_without_stale_nodes(string before,
        string after, string reason)
    {
        var old = MarkdownSyntaxParser.Parse(new SourceTextSnapshot(before, 1));
        var next = new SourceTextSnapshot(after, 2);
        var update = MarkdownIncrementalParser.Update(old, next);

        Assert.AreEqual(MarkdownParseScope.FullDocument, update.Scope, reason);
        AssertTreeEquals(MarkdownSyntaxParser.Parse(next), update.Syntax);
    }

    [TestMethod]
    public void Reference_dependent_paragraph_falls_back_even_if_physically_isolated()
    {
        var old = MarkdownSyntaxParser.Parse(new SourceTextSnapshot(
            "[名称][id]\n\n第二段\n\n[id]: https://old.example", 1));
        var next = new SourceTextSnapshot("[改名][id]\n\n第二段\n\n[id]: https://old.example", 2);
        var update = MarkdownIncrementalParser.Update(old, next);

        Assert.AreEqual(MarkdownParseScope.FullDocument, update.Scope);
        AssertTreeEquals(MarkdownSyntaxParser.Parse(next), update.Syntax);
    }

    [TestMethod]
    public void Changing_a_distant_image_definition_invalidates_the_reference_target()
    {
        const string before = "![图][id]\n\n中间正文\n\n[id]: old.png";
        var old = MarkdownSyntaxParser.Parse(new SourceTextSnapshot(before, 1));
        var next = new SourceTextSnapshot(before.Replace("old.png", "new.png"), 2);
        var update = MarkdownIncrementalParser.Update(old, next);
        var projection = MarkdownRichTextProjection.FromSyntax(update.Syntax);

        Assert.AreEqual(MarkdownParseScope.FullDocument, update.Scope);
        Assert.AreEqual("new.png", projection.Text.References.Images.Single().Target);
        AssertTreeEquals(MarkdownSyntaxParser.Parse(next), update.Syntax);
    }

    [TestMethod]
    public void Same_text_new_version_keeps_syntax_but_binds_the_new_snapshot()
    {
        var old = MarkdownSyntaxParser.Parse(new SourceTextSnapshot("中 **文**", 1));
        var next = new SourceTextSnapshot(old.Source.Text, 2);
        var update = MarkdownIncrementalParser.Update(old, next);

        Assert.AreEqual(MarkdownParseScope.Unchanged, update.Scope);
        Assert.AreSame(next, update.Syntax.Source);
        AssertTreeEquals(MarkdownSyntaxParser.Parse(next), update.Syntax);
    }

    [TestMethod]
    public void Local_parse_matches_full_parse_across_inline_and_block_boundary_edits()
    {
        const string before = "前段\n\nabc 中文 **bold** 尾\n\n后段";
        var old = MarkdownSyntaxParser.Parse(new SourceTextSnapshot(before, 1));
        var first = before.IndexOf("abc", StringComparison.Ordinal);
        var fragments = new[] { "x", "😀", "*", "**", "`", "[id]", "# ",
            "- ", "|", "\n", "\n\n", "~~~", "\\(", "&amp;", "" };
        var local = 0;
        var version = 2L;
        foreach (var fragment in fragments)
            for (var position = first; position <= first + 3; position++)
            {
                var text = before[..position] + fragment + before[position..];
                var next = new SourceTextSnapshot(text, version++);
                var update = MarkdownIncrementalParser.Update(old, next);
                if (update.Scope == MarkdownParseScope.IsolatedParagraph) local++;
                AssertTreeEquals(MarkdownSyntaxParser.Parse(next), update.Syntax);
            }
        Assert.IsTrue(local > 10, $"Only {local} edits took the local path.");
    }

    [TestMethod]
    public void Consecutive_local_edits_rebase_the_successive_snapshots()
    {
        var syntax = MarkdownSyntaxParser.Parse(new SourceTextSnapshot("前段\n\nabc\n\n尾段", 1));
        foreach (var (text, version) in new[]
        {
            ("前段\n\nabc😀\n\n尾段", 2L),
            ("前段\n\nabc😀\n\n**尾段**", 3L),
            ("前段\n\nabc\n\n**尾段**", 4L)
        })
        {
            var next = new SourceTextSnapshot(text, version);
            var update = MarkdownIncrementalParser.Update(syntax, next);
            Assert.AreEqual(MarkdownParseScope.IsolatedParagraph, update.Scope);
            AssertTreeEquals(MarkdownSyntaxParser.Parse(next), update.Syntax);
            syntax = update.Syntax;
        }
    }

    private static void AssertTreeEquals(MarkdownSyntaxDocument expected, MarkdownSyntaxDocument actual)
    {
        static IEnumerable<string> Describe(MarkdownSyntaxNode node, string prefix)
        {
            yield return $"{prefix}{node.Kind}@{node.Source.Start}:{node.Source.Length}";
            foreach (var child in node.Children)
                foreach (var line in Describe(child, prefix + " ")) yield return line;
        }
        CollectionAssert.AreEqual(Describe(expected.Root, "").ToArray(),
            Describe(actual.Root, "").ToArray());
    }
}
