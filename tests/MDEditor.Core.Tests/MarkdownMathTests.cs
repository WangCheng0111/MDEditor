using MDEditor.Core.Text;
using MDEditor.Typesetting.Markdown;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MarkdownMathTests
{
    [TestMethod]
    public void Inline_math_preserves_delimiters_content_and_surrounding_source()
    {
        var source = new SourceTextSnapshot("中文 $E=mc^2$ and $\\frac{1}{x}$ 结尾", 14);
        var document = MarkdownMathDocument.Parse(source);
        Assert.AreEqual(1, document.Blocks.Length);
        Assert.AreEqual(2, document.Math.Length);
        CollectionAssert.AreEqual(new[] { "E=mc^2", "\\frac{1}{x}" },
            document.Math.Select(node => node.GetContent(source)).ToArray());
        Assert.AreEqual(source.Text, string.Concat(document.Blocks[0].Inlines.Select(node => source.GetText(node.Source))));
    }

    [TestMethod]
    public void Only_an_exact_formula_selection_qualifies_for_rendered_copy()
    {
        var source = new SourceTextSnapshot("before $\\frac{x+1}{\\sqrt{y+1}}$ after", 2);
        var document = MarkdownMathDocument.Parse(source);
        var formula = document.Math.Single();
        Assert.AreSame(formula, document.FindExactMath(formula.Source));
        Assert.IsNull(document.FindExactMath(formula.Content));
        Assert.IsNull(document.FindExactMath(new SourceRange(formula.Source.Start - 1, formula.Source.Length + 1)));
        Assert.IsNull(document.FindExactMath(new SourceRange(0, source.Length)));
    }

    [TestMethod]
    public void Mixed_text_and_formula_copy_reparses_at_the_destination_offsets()
    {
        const string formula = "$\\frac{x+1}{\\sqrt{y+1}}$";
        var original = new SourceTextSnapshot($"前文 根式 {formula} 后文", 7);
        var selection = new SourceRange(3, 3 + formula.Length + 3);
        var copied = original.GetText(selection);
        var pasted = new SourceTextSnapshot("目标开头 " + copied + " 目标结尾", 8);
        var document = MarkdownMathDocument.Parse(pasted);
        var math = document.Math.Single();
        Assert.AreEqual(formula, pasted.GetText(math.Source));
        Assert.AreEqual("\\frac{x+1}{\\sqrt{y+1}}", math.GetContent(pasted));
        Assert.AreEqual(pasted.Text.IndexOf(formula, StringComparison.Ordinal), math.Source.Start);
        Assert.AreEqual(pasted.Text, string.Concat(document.Blocks.Single().Inlines.Select(node =>
            pasted.GetText(node.Source))));
    }

    [TestMethod]
    public void Pasting_a_display_formula_inside_prose_creates_a_standalone_block()
    {
        const string formula = "$$\\frac{x+1}{\\sqrt{y+1}}$$";
        var destination = new StyledDocumentBuffer("前后", [new(0, 0)], 1).Capture();
        var inserted = MarkdownMathPaste.Prepare(destination, new SourceRange(1, 0), formula);
        Assert.AreEqual("\n" + formula + "\n", inserted);
        var source = new SourceTextSnapshot("前" + inserted + "后", 2);
        Assert.AreEqual(MarkdownMathKind.Display, MarkdownMathDocument.Parse(source).Math.Single().Kind);
    }

    [TestMethod]
    public void Display_formula_paste_uses_the_existing_crlf_line_ending()
    {
        const string formula = "$$x+1$$";
        var destination = new StyledDocumentBuffer("前\r\n后", [new(0, 0)], 1).Capture();
        var inserted = MarkdownMathPaste.Prepare(destination, new SourceRange(1, 0), formula);
        Assert.AreEqual("\r\n" + formula, inserted);
        var source = new SourceTextSnapshot("前" + inserted + "\r\n后", 2);
        Assert.AreEqual(MarkdownMathKind.Display, MarkdownMathDocument.Parse(source).Math.Single().Kind);
    }

    [TestMethod]
    public void Display_formula_paste_adds_only_the_missing_trailing_line_break()
    {
        var destination = new StyledDocumentBuffer("前。", [new(0, 0)], 1).Capture();
        var inserted = MarkdownMathPaste.Prepare(destination, new SourceRange(1, 0), "\n$$x+1$$");
        Assert.AreEqual("\n$$x+1$$\n", inserted);
    }

    [TestMethod]
    public void Inline_math_and_nonstandalone_double_dollars_are_not_rewritten_on_paste()
    {
        var destination = new StyledDocumentBuffer("前后", [new(0, 0)], 1).Capture();
        Assert.AreEqual("文字 $x+1$ 结尾", MarkdownMathPaste.Prepare(destination,
            new SourceRange(1, 0), "文字 $x+1$ 结尾"));
        Assert.AreEqual("$$x+1$$。", MarkdownMathPaste.Prepare(destination,
            new SourceRange(1, 0), "$$x+1$$。"));
    }

    [TestMethod]
    public void Escaped_dollars_and_code_spans_remain_text()
    {
        var source = new SourceTextSnapshot(@"price \$5, `$ignored$`, ``$also$`` and $x+1$", 1);
        var document = MarkdownMathDocument.Parse(source);
        Assert.AreEqual(1, document.Math.Length);
        Assert.AreEqual("x+1", document.Math[0].GetContent(source));
    }

    [TestMethod]
    public void Whitespace_boundaries_and_unclosed_formulas_are_literal_text()
    {
        var source = new SourceTextSnapshot("$ x$ and $x $ and $never", 1);
        var document = MarkdownMathDocument.Parse(source);
        Assert.AreEqual(0, document.Math.Length);
        Assert.AreEqual(source.Text, source.GetText(document.Blocks.Single().Inlines.Single().Source));
    }

    [TestMethod]
    public void Standalone_display_block_has_exact_source_ranges()
    {
        var source = new SourceTextSnapshot("before\n\n$$\n\\sqrt{1+x}\n$$\n\nafter", 8);
        var document = MarkdownMathDocument.Parse(source);
        Assert.AreEqual(3, document.Blocks.Length);
        var block = document.Blocks[1];
        Assert.AreEqual(MarkdownBlockKind.DisplayMath, block.Kind);
        Assert.AreEqual(MarkdownMathKind.Display, block.DisplayMath!.Kind);
        Assert.AreEqual("\\sqrt{1+x}", block.DisplayMath.GetContent(source));
        Assert.AreEqual("$$\n\\sqrt{1+x}\n$$", source.GetText(block.Source));
    }

    [TestMethod]
    public void Single_line_display_math_is_recognized_but_empty_pair_is_not()
    {
        var source = new SourceTextSnapshot("  $$\\sum_{i=1}^{n}i$$  \n$$$$", 3);
        var document = MarkdownMathDocument.Parse(source);
        Assert.AreEqual(MarkdownBlockKind.DisplayMath, document.Blocks[0].Kind);
        Assert.AreEqual("\\sum_{i=1}^{n}i", document.Math.Single().GetContent(source));
        Assert.AreEqual(MarkdownBlockKind.Paragraph, document.Blocks[1].Kind);
    }

    [TestMethod]
    public void Blank_lines_are_not_blocks_and_block_order_is_stable()
    {
        var source = new SourceTextSnapshot("a\r\n\r\n b $x$\r\n", 2);
        var document = MarkdownMathDocument.Parse(source);
        Assert.AreEqual(2, document.Blocks.Length);
        Assert.IsTrue(document.Blocks[0].Source.End < document.Blocks[1].Source.Start);
        Assert.AreEqual("x", document.Math.Single().GetContent(source));
    }

    [TestMethod]
    public void Fenced_code_does_not_create_math_nodes()
    {
        var source = new SourceTextSnapshot("```tex\n$x$\n$$y$$\n```\nvisible $z$", 1);
        var document = MarkdownMathDocument.Parse(source);
        Assert.AreEqual(1, document.Math.Length);
        Assert.AreEqual("z", document.Math[0].GetContent(source));
    }

    [TestMethod]
    public void Empty_display_delimiters_remain_literal()
    {
        var source = new SourceTextSnapshot("$$\n$$\nnext $x$", 1);
        var document = MarkdownMathDocument.Parse(source);
        Assert.AreEqual(1, document.Math.Length);
        Assert.AreEqual("x", document.Math[0].GetContent(source));
    }
}
