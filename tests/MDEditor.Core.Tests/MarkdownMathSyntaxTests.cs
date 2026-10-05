using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Markdown;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MarkdownMathSyntaxTests
{
    [TestMethod]
    public void Four_delimiters_are_source_mapped_and_classified()
    {
        var source = new SourceTextSnapshot("one $a+b$ and \\(c+d\\)\n$$x+1$$\n\\[y+1\\]", 27);
        var spans = MarkdownMathSyntax.Parse(source);

        CollectionAssert.AreEqual(new[] { MarkdownMathDelimiter.Dollar,
            MarkdownMathDelimiter.Parenthesis, MarkdownMathDelimiter.DoubleDollar,
            MarkdownMathDelimiter.Bracket }, spans.Select(span => span.Delimiter).ToArray());
        CollectionAssert.AreEqual(new[] { MarkdownMathSyntaxKind.Inline, MarkdownMathSyntaxKind.Inline,
            MarkdownMathSyntaxKind.Display, MarkdownMathSyntaxKind.Display },
            spans.Select(span => span.Kind).ToArray());
        CollectionAssert.AreEqual(new[] { "a+b", "c+d", "x+1", "y+1" },
            spans.Select(span => span.GetContent(source)).ToArray());
        Assert.IsTrue(spans.All(span => source.FullRange.Contains(span.Source) &&
            span.Source.Contains(span.Content)));
        Assert.AreEqual(4, MarkdownMathDocument.Parse(source).Math.Length);
    }

    [TestMethod]
    public void Multiline_display_pairs_preserve_line_endings_and_exact_ranges()
    {
        var source = new SourceTextSnapshot("before\r\n$$\r\n\\frac{1}{2}\r\n$$\r\n\\[\r\nx+1\r\n\\]\r\nafter", 3);
        var math = MarkdownMathDocument.Parse(source);

        Assert.AreEqual(2, math.Math.Length);
        Assert.AreEqual("\\frac{1}{2}", math.Math[0].GetContent(source));
        Assert.AreEqual("x+1", math.Math[1].GetContent(source));
        Assert.AreEqual("$$\r\n\\frac{1}{2}\r\n$$", source.GetText(math.Math[0].Source));
        Assert.AreEqual("\\[\r\nx+1\r\n\\]", source.GetText(math.Math[1].Source));
        Assert.AreEqual(2, math.Blocks.Count(block => block.Kind == MDEditor.Typesetting.Markdown.MarkdownBlockKind.DisplayMath));
    }

    [TestMethod]
    public void Code_spans_fenced_code_indented_code_and_escapes_remain_literal()
    {
        var source = new SourceTextSnapshot(
            "`$a$` ``\\(b\\)`` \\$c$ \\\\(d\\) $live$\n" +
            "```tex\n\\[blocked\\]\n$blocked$\n```\n" +
            "    \\(indented\\)\n    $$blocked$$\n    \\[blocked\\]\nvisible \\(ok\\)", 5);
        var math = MarkdownMathSyntax.Parse(source);

        CollectionAssert.AreEqual(new[] { "live", "ok" },
            math.Select(span => span.GetContent(source)).ToArray());
    }

    [TestMethod]
    public void Unclosed_or_empty_delimiters_do_not_consume_following_math()
    {
        var source = new SourceTextSnapshot("$ x$ \\(y \\[\n$$\n$$\nvalid $z$", 1);
        var math = MarkdownMathSyntax.Parse(source);
        Assert.AreEqual(1, math.Length);
        Assert.AreEqual("z", math[0].GetContent(source));
    }

    [TestMethod]
    public void Math_internal_markdown_marks_never_fold_or_style_the_formula()
    {
        var source = new SourceTextSnapshot("**outer** $a*b*c$ and \\(x_1+y_2\\)", 11);
        var projection = MarkdownRichTextProjection.Create(source);

        Assert.AreEqual("outer $a*b*c$ and \\(x_1+y_2\\)", projection.Text.Display.Text);
        foreach (var math in projection.Text.MathSpans)
        {
            Assert.AreEqual(math.Source.Length, projection.Text.ToDisplayRange(math.Source).Length);
            Assert.IsFalse(projection.Text.HiddenRanges.Any(hidden =>
                hidden.Start < math.Source.End && math.Source.Start < hidden.End));
            Assert.IsFalse(projection.Styles.Any(style =>
                style.Display.Start < projection.Text.ToDisplayRange(math.Source).End &&
                projection.Text.ToDisplayRange(math.Source).Start < style.Display.End));
        }
    }

    [TestMethod]
    public void Bracket_display_formula_paste_isolated_from_surrounding_prose()
    {
        var destination = new StyledDocumentBuffer("前后", [new(0, 0)], 1).Capture();
        var pasted = MarkdownMathPaste.Prepare(destination, new(1, 0), "\\[\\sqrt{x+1}\\]");
        Assert.AreEqual("\n\\[\\sqrt{x+1}\\]\n", pasted);
        var result = new SourceTextSnapshot("前" + pasted + "后", 2);
        Assert.AreEqual("\\sqrt{x+1}", MarkdownMathDocument.Parse(result).Math.Single().GetContent(result));
    }

    [TestMethod]
    public void Caret_inside_a_formula_reveals_source_without_mutating_it()
    {
        var source = new SourceTextSnapshot("before **bold** and \\(x+1\\) after", 43);
        var collapsed = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(source));
        var math = collapsed.MathSpans.Single();
        var editing = collapsed.RevealAt(math.Content.Start);

        Assert.AreEqual(math.Source, editing.RevealedMath!.Source);
        Assert.AreSame(source, editing.Source);
        Assert.AreEqual(source.GetText(math.Source), editing.Display.GetText(
            editing.ToDisplayRange(math.Source)));
        Assert.IsNull(editing.RevealAt(0).RevealedMath);
        Assert.AreEqual(43L, editing.Display.Version);
    }

    [TestMethod]
    public void Unclosed_fence_blocks_all_following_math_delimiters()
    {
        var source = new SourceTextSnapshot("```tex\n$x$\n\\(y\\)\n\\[z\\]", 1);
        Assert.AreEqual(0, MarkdownMathSyntax.Parse(source).Length);
    }
}
