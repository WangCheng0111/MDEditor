using MDEditor.Core.Markdown;
using MDEditor.Core.Text;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MarkdownCodeBlockTests
{
    [TestMethod]
    public void Closed_fence_folds_only_syntax_and_reveals_without_changing_source()
    {
        var source = new SourceTextSnapshot("before\n```csharp title=demo\nint answer = 42;\n```\nafter", 24);
        var folded = MarkdownRichTextProjection.Create(source);
        var active = MarkdownRichTextProjection.Create(source, source.Text.IndexOf("answer", StringComparison.Ordinal));

        Assert.AreEqual("before\n\nint answer = 42;\n\nafter", folded.Text.Display.Text);
        Assert.AreEqual(source.Text, active.Text.Display.Text);
        Assert.AreSame(source, folded.Text.Source);
        Assert.AreEqual(24L, folded.Text.Display.Version);
        Assert.HasCount(1, folded.Text.CodeBlocks.Blocks);
        Assert.AreEqual("csharp", folded.Text.CodeBlocks.Blocks[0].Language);
        Assert.IsTrue(folded.Text.CodeBlocks.Blocks[0].IsClosed);
        Assert.HasCount(3, folded.CodeLines);
        Assert.AreEqual(MarkdownCodeLineRole.OpeningFence, folded.CodeLines[0].Role);
        Assert.AreEqual(MarkdownCodeLineRole.ClosingFence, folded.CodeLines[2].Role);
        Assert.IsTrue(folded.CodeLines[0].FenceHidden);
        var codeWord = folded.Text.Display.Text.IndexOf("answer", StringComparison.Ordinal);
        Assert.IsTrue(folded.IsCodeAtDisplayOffset(codeWord));
        Assert.IsFalse(folded.IsCodeAtDisplayOffset(0));
        Assert.IsNotNull(folded.CodeLineAt(folded.CodeLines[1].Line));
        Assert.AreEqual(source.FullRange, folded.Text.ToSourceRange(folded.Text.Display.FullRange, true));
        var word = folded.Text.Display.Text.IndexOf("answer", StringComparison.Ordinal);
        Assert.AreEqual("answer", source.GetText(folded.Text.ToSourceRange(new(word, 6), true)));
        var opening = folded.Text.CodeBlocks.Blocks[0].Lines[0];
        var openingDisplay = folded.Text.ToDisplayOffset(opening.Line.Start);
        Assert.AreEqual(opening.Line.End,
            folded.Text.ToNavigationSourceOffset(openingDisplay, ProjectionBoundary.BeforeHidden));
    }

    [TestMethod]
    public void Unclosed_fence_is_code_to_end_and_reveals_at_eof()
    {
        var source = new SourceTextSnapshot("~~~python\nprint(\"hi\")\n**literal**", 1);
        var folded = MarkdownRichTextProjection.Create(source);
        var active = MarkdownRichTextProjection.Create(source, source.Length);

        Assert.AreEqual("\nprint(\"hi\")\n**literal**", folded.Text.Display.Text);
        Assert.AreEqual(source.Text, active.Text.Display.Text);
        Assert.IsFalse(folded.Text.CodeBlocks.Blocks[0].IsClosed);
        Assert.HasCount(3, folded.CodeLines);
        Assert.IsEmpty(folded.Styles);
        Assert.AreEqual("python", folded.CodeLines[1].Language);
    }

    [TestMethod]
    public void Quoted_fence_hides_outer_prefix_but_preserves_all_source_coordinates()
    {
        var source = new SourceTextSnapshot("> ```js\n> let x = 1;\n> ```\nnext", 2);
        var folded = MarkdownRichTextProjection.Create(source);
        var active = MarkdownRichTextProjection.Create(source, source.Text.IndexOf("let", StringComparison.Ordinal));

        Assert.HasCount(1, folded.Text.CodeBlocks.Blocks);
        Assert.AreEqual("\nlet x = 1;\n\nnext", folded.Text.Display.Text);
        Assert.AreEqual("js", folded.CodeLines[1].Language);
        Assert.AreEqual(1, folded.CodeLines[1].QuoteDepth);
        Assert.AreEqual(source.Text, active.Text.Display.Text);
        Assert.AreEqual(source.FullRange, folded.Text.ToSourceRange(folded.Text.Display.FullRange, true));
    }

    [TestMethod]
    public void Syntax_color_ranges_are_bounded_and_source_preserving()
    {
        var source = new SourceTextSnapshot("```csharp\n// note\nint answer = 42;\nvar text = \"hello\";\n```", 3);
        var rich = MarkdownRichTextProjection.Create(source);
        var inputs = MarkdownCodeHighlighter.CreateInputs(rich.Text.CodeBlocks);
        Assert.IsFalse(inputs.Truncated);
        Assert.AreEqual("// note\nint answer = 42;\nvar text = \"hello\";\n", inputs.Blocks[0].Text);
        var input = inputs.Blocks[0];
        var mapped = MarkdownCodeHighlighter.MapTokens(input,
            [new(new(input.Text.IndexOf("int", StringComparison.Ordinal), 3), MarkdownCodeTokenKind.Keyword)]);
        var display = rich.Text.ToDisplayRange(mapped[0].Source);
        Assert.AreEqual("int", rich.Text.Display.GetText(display));
        Assert.AreEqual(MarkdownCodeTokenKind.Keyword,
            MarkdownCodeHighlighter.KindAt(new(source, mapped, false), rich.Text, display.Start));
        Assert.AreEqual(source.Text, rich.Text.Source.Text);
    }

    [TestMethod]
    public void Language_labels_are_delegated_and_budget_stops_large_highlight_pass()
    {
        var unknown = MarkdownRichTextProjection.Create(new("```mystery\nfoo = 42\n```", 0));
        Assert.AreEqual("mystery", MarkdownCodeHighlighter.CreateInputs(unknown.Text.CodeBlocks).Blocks[0].Language);

        var source = new SourceTextSnapshot("```python\n" + new string('a', MarkdownCodeHighlighter.MaximumBlockCharacters + 1) + "\n```", 0);
        var structure = MarkdownCodeBlockStructure.Create(MarkdownSyntaxParser.Parse(source));
        var highlight = MarkdownCodeHighlighter.CreateInputs(structure);
        Assert.IsTrue(highlight.Truncated);
        Assert.IsEmpty(highlight.Blocks);
    }

    [TestMethod]
    public void Closing_an_unclosed_fence_restores_following_markdown()
    {
        var before = MarkdownRichTextProjection.Create(new("~~~python\nprint(1)\n**bold**", 0));
        var after = MarkdownRichTextProjection.Create(new("~~~python\nprint(1)\n~~~\n**bold**", 1));

        Assert.IsFalse(before.Text.CodeBlocks.Blocks[0].IsClosed);
        Assert.IsEmpty(before.Styles);
        Assert.IsTrue(after.Text.CodeBlocks.Blocks[0].IsClosed);
        Assert.AreEqual("\nprint(1)\n\nbold", after.Text.Display.Text);
        Assert.IsTrue(after.Styles.Any(span => span.Style == MarkdownVisualStyle.Strong));
    }

    [TestMethod]
    public void Demo_adjacent_closed_and_unclosed_blocks_follow_a_task_item()
    {
        var rich = MarkdownRichTextProjection.Create(new(
            "- [x] 已完成任务\n```csharp\n// 中文注释：原生字形，不丢源码\nint answer = 42;\n" +
            "Console.WriteLine(\"hello\");\n```\n~~~python\nprint(\"你好\") # 未闭合围栏仍是代码", 0));

        Assert.HasCount(2, rich.Text.CodeBlocks.Blocks);
        Assert.HasCount(7, rich.CodeLines);
        Assert.IsTrue(rich.Text.CodeBlocks.Blocks[0].IsClosed);
        Assert.IsFalse(rich.Text.CodeBlocks.Blocks[1].IsClosed);
        Assert.AreEqual("csharp", rich.CodeLines[2].Language);
        Assert.AreEqual("python", rich.CodeLines[^1].Language);
    }

    [TestMethod]
    public void Indented_code_keeps_its_literal_spaces_and_uses_code_presentation()
    {
        var source = new SourceTextSnapshot("    let x = 1;\n    let y = 2;", 0);
        var rich = MarkdownRichTextProjection.Create(source);

        Assert.AreEqual(source.Text, rich.Text.Display.Text);
        Assert.HasCount(1, rich.Text.CodeBlocks.Blocks);
        Assert.HasCount(2, rich.CodeLines);
        Assert.IsTrue(rich.CodeLines.All(line => line.Role == MarkdownCodeLineRole.IndentedContent));
        Assert.IsEmpty(MarkdownCodeHighlighter.CreateInputs(rich.Text.CodeBlocks).Blocks);
    }

    [TestMethod]
    public void Leading_spaces_inside_a_fence_remain_part_of_a_colored_code_line()
    {
        var rich = MarkdownRichTextProjection.Create(new("```cs\n    int value = 7;\n```", 0));
        var keyword = rich.Text.Display.Text.IndexOf("int", StringComparison.Ordinal);

        Assert.IsTrue(rich.IsCodeAtDisplayOffset(keyword));
        var input = MarkdownCodeHighlighter.CreateInputs(rich.Text.CodeBlocks).Blocks[0];
        Assert.AreEqual("    int value = 7;\n", input.Text);
        var mapped = MarkdownCodeHighlighter.MapTokens(input, [new(new(4, 3), MarkdownCodeTokenKind.Keyword)]);
        Assert.AreEqual(keyword, rich.Text.ToDisplayRange(mapped[0].Source).Start);
        Assert.AreEqual(MarkdownCodeTokenKind.Keyword,
            MarkdownCodeHighlighter.KindAt(new(rich.Text.Source, mapped, false), rich.Text, keyword));
    }
}
