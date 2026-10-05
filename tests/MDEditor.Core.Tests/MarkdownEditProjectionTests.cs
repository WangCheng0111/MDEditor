using MDEditor.Core.Markdown;
using MDEditor.Core.Text;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MarkdownEditProjectionTests
{
    [TestMethod]
    public void Strong_delimiters_hide_without_changing_source_or_version()
    {
        var parsed = Parse("a **bold** z", 23);
        var projection = MarkdownEditProjection.Create(parsed);

        Assert.AreSame(parsed.Source, projection.Source);
        Assert.AreEqual(23L, projection.Display.Version);
        Assert.AreEqual("a **bold** z", projection.Source.Text);
        Assert.AreEqual("a bold z", projection.Display.Text);
        CollectionAssert.AreEqual(new[] { new SourceRange(2, 2), new SourceRange(8, 2) },
            projection.HiddenRanges.ToArray());
        Assert.AreEqual(2, projection.ToSourceOffset(2, ProjectionBoundary.BeforeHidden));
        Assert.AreEqual(4, projection.ToSourceOffset(2, ProjectionBoundary.AfterHidden));
        Assert.AreEqual(8, projection.ToSourceOffset(6, ProjectionBoundary.BeforeHidden));
        Assert.AreEqual(10, projection.ToSourceOffset(6, ProjectionBoundary.AfterHidden));
        Assert.AreEqual(6, projection.ToDisplayOffset(10));
    }

    [TestMethod]
    public void Nested_emphasis_and_strong_have_disjoint_delimiter_ranges()
    {
        var projection = Project("***mix***");

        Assert.AreEqual("mix", projection.Display.Text);
        CollectionAssert.AreEqual(new[] { new SourceRange(0, 3), new SourceRange(6, 3) },
            projection.HiddenRanges.ToArray());
        Assert.AreEqual(new SourceRange(3, 3), projection.ToSourceRange(new(0, 3)));
        Assert.AreEqual(new SourceRange(0, 9), projection.ToSourceRange(new(0, 3), true));
    }

    [TestMethod]
    public void Links_hide_only_the_markup_and_preserve_the_label()
    {
        var projection = Project("go [here](https://x.test) now");

        Assert.AreEqual("go here now", projection.Display.Text);
        Assert.AreEqual("here", projection.Source.GetText(projection.ToSourceRange(new(3, 4))));
        Assert.AreEqual("[here](https://x.test)",
            projection.Source.GetText(projection.ToSourceRange(new(3, 4), true)));
        Assert.AreEqual("go [here](https://x.test) now", projection.Source.Text);
    }

    [TestMethod]
    public void Angle_autolinks_hide_brackets_but_bare_urls_are_unchanged()
    {
        var projection = Project("<https://example.com> https://example.org");

        Assert.AreEqual("https://example.com https://example.org", projection.Display.Text);
        Assert.AreEqual("<https://example.com>",
            projection.Source.GetText(projection.ToSourceRange(new(0, "https://example.com".Length), true)));
    }

    [TestMethod]
    public void Atx_and_setext_headings_hide_their_markers_without_losing_newlines()
    {
        var projection = Project("# Head\r\n\r\nTitle\r\n=====\r\n");

        Assert.AreEqual("Head\r\n\r\nTitle\r\n", projection.Display.Text);
        Assert.AreEqual("# Head\r\n\r\nTitle\r\n=====\r\n", projection.Source.Text);
        AssertMappingInvariants(projection);
    }

    [TestMethod]
    public void Code_spans_hide_only_backticks_and_do_not_parse_inner_emphasis()
    {
        var projection = Project("a `**not-bold**` b");

        Assert.AreEqual("a **not-bold** b", projection.Display.Text);
        Assert.AreEqual(1, projection.SyntaxUnits.Count(unit => unit.Kind == MarkdownSyntaxKind.CodeSpan));
        Assert.IsFalse(projection.SyntaxUnits.Any(unit => unit.Kind == MarkdownSyntaxKind.Strong));
        Assert.AreEqual("`**not-bold**`", projection.Source.GetText(
            projection.ToSourceRange(new(2, "**not-bold**".Length), true)));
    }

    [TestMethod]
    public void Entering_a_construct_reveals_its_source_without_mutating_the_collapsed_projection()
    {
        var collapsed = Project("a **bold** z");
        var expanded = collapsed.RevealAt(5);

        Assert.AreEqual("a bold z", collapsed.Display.Text);
        Assert.AreEqual("a **bold** z", expanded.Display.Text);
        Assert.HasCount(0, expanded.HiddenRanges);
        Assert.AreSame(collapsed.Source, expanded.Source);
        Assert.AreEqual(5, expanded.ToDisplayOffset(5));
        Assert.AreSame(expanded, expanded.RevealAt(5));
        Assert.AreEqual("a bold z", collapsed.RevealAt(0).Display.Text);
        Assert.AreEqual("# **A**", Project("# **A**").RevealAt(4).Display.Text);
    }

    [TestMethod]
    public void Moving_over_collapsed_syntax_never_stops_inside_hidden_delimiters()
    {
        var projection = Project("a **b** c");
        var beforeA = 0;
        var afterA = projection.MoveSourceCaret(beforeA, 1);
        var beforeB = projection.MoveSourceCaret(afterA, 1);
        var afterB = projection.MoveSourceCaret(beforeB, 1);

        Assert.AreEqual(1, projection.ToDisplayOffset(afterA));
        Assert.AreEqual(2, projection.ToDisplayOffset(beforeB));
        Assert.AreEqual(3, projection.ToDisplayOffset(afterB));
        Assert.IsFalse(projection.HiddenRanges.Any(range => range.Start < beforeB && beforeB < range.End));
        Assert.IsFalse(projection.HiddenRanges.Any(range => range.Start < afterB && afterB < range.End));
        Assert.AreEqual(4, projection.MoveSourceCaret(afterB, -1));
        Assert.AreEqual(projection.ToDisplayOffset(beforeB),
            projection.ToDisplayOffset(projection.MoveSourceCaret(afterB, -1)));
    }

    [TestMethod]
    public void Deleting_the_only_visible_character_removes_its_entire_wrapper()
    {
        var projection = Project("a **b** c");

        Assert.AreEqual(new SourceRange(2, 5), projection.DeleteForwardRange(4));
        Assert.AreEqual(new SourceRange(2, 5), projection.BackspaceRange(7));
        Assert.AreEqual("**b**", projection.Source.GetText(projection.DeleteForwardRange(4)!.Value));
    }

    [TestMethod]
    public void Applying_a_projection_delete_to_the_buffer_keeps_markdown_editable()
    {
        var buffer = new DocumentTextBuffer("a **b** c", 10);
        var projection = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(buffer.CaptureSnapshot()));
        buffer.Delete(projection.BackspaceRange(7)!.Value);

        Assert.AreEqual("a  c", buffer.CaptureSnapshot().Text);
        Assert.AreEqual("a  c", MarkdownEditProjection.Create(
            MarkdownSyntaxParser.Parse(buffer.CaptureSnapshot())).Display.Text);

        var second = new DocumentTextBuffer("**bold**", 10);
        var secondProjection = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(second.CaptureSnapshot()));
        second.Delete(secondProjection.BackspaceRange(3)!.Value);
        Assert.AreEqual("**old**", second.CaptureSnapshot().Text);
        Assert.AreEqual("old", MarkdownEditProjection.Create(
            MarkdownSyntaxParser.Parse(second.CaptureSnapshot())).Display.Text);
    }

    [TestMethod]
    public void Partial_deletion_keeps_the_surrounding_markup()
    {
        var projection = Project("**bold**");

        Assert.AreEqual(new SourceRange(2, 1), projection.BackspaceRange(3));
        Assert.AreEqual(new SourceRange(5, 1), projection.BackspaceRange(6));
        Assert.AreEqual(new SourceRange(3, 1), projection.DeleteForwardRange(3));
        Assert.IsNull(projection.BackspaceRange(0));
        Assert.IsNull(projection.DeleteForwardRange(projection.Source.Length));
    }

    [TestMethod]
    public void Selection_can_preserve_visible_text_or_include_complete_syntax_for_edits()
    {
        var projection = Project("a **bold** z");

        Assert.AreEqual(new SourceRange(4, 4), projection.ToSourceRange(new(2, 4)));
        Assert.AreEqual(new SourceRange(2, 8), projection.ToSourceRange(new(2, 4), true));
        Assert.AreEqual(new SourceRange(2, 4), projection.ToDisplayRange(new(2, 8)));
        Assert.AreEqual(new SourceRange(4, 0), projection.ToSourceRange(new(2, 0)));
    }

    [TestMethod]
    public void Selecting_all_content_in_a_revealed_wrapper_still_removes_its_markers()
    {
        var expanded = Project("a **bold** z").RevealAt(5);

        Assert.AreEqual(new SourceRange(2, 8), expanded.ToSourceRange(new(4, 4), true));
        Assert.AreEqual(new SourceRange(2, 8), expanded.ToSourceRange(new(2, 8), true));
        Assert.AreEqual(new SourceRange(4, 2), expanded.ToSourceRange(new(4, 2), true));
    }

    [TestMethod]
    public void Two_source_sides_of_one_display_boundary_choose_inside_or_outside_insertion()
    {
        var projection = Project("**bold**");
        var outside = projection.ToSourceOffset(0, ProjectionBoundary.BeforeHidden);
        var inside = projection.ToSourceOffset(0, ProjectionBoundary.AfterHidden);

        Assert.AreEqual(0, outside);
        Assert.AreEqual(2, inside);
        var outsideBuffer = new DocumentTextBuffer(projection.Source.Text);
        outsideBuffer.Insert(outside, "X");
        var insideBuffer = new DocumentTextBuffer(projection.Source.Text);
        insideBuffer.Insert(inside, "X");
        Assert.AreEqual("X**bold**", outsideBuffer.CaptureSnapshot().Text);
        Assert.AreEqual("**Xbold**", insideBuffer.CaptureSnapshot().Text);
    }

    [TestMethod]
    public void Deletion_across_adjacent_constructs_expands_only_fully_selected_nodes()
    {
        var projection = Project("**a** + `bc`");

        Assert.AreEqual("a + bc", projection.Display.Text);
        Assert.AreEqual("**a**", projection.Source.GetText(projection.ToSourceRange(new(0, 1), true)));
        Assert.AreEqual("c", projection.Source.GetText(projection.ToSourceRange(new(5, 1), true)));
        Assert.AreEqual(projection.Source.FullRange, projection.ToSourceRange(projection.Display.FullRange, true));
    }

    [TestMethod]
    public void Grapheme_movement_and_deletion_preserve_surrogates_combining_marks_and_crlf()
    {
        var projection = Project("**😀e\u0301**\r\nX");

        Assert.AreEqual("😀e\u0301\r\nX", projection.Display.Text);
        Assert.AreEqual(new SourceRange(2, 2), projection.DeleteForwardRange(2));
        Assert.AreEqual(4, projection.MoveSourceCaret(2, 1));
        Assert.AreEqual(6, projection.MoveSourceCaret(4, 1));
        Assert.AreEqual(new SourceRange(8, 2), projection.DeleteForwardRange(6));
    }

    [TestMethod]
    public void Incomplete_syntax_stays_visible_until_an_edit_completes_it()
    {
        var buffer = new DocumentTextBuffer("**unfinished", 7);
        var before = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(buffer.CaptureSnapshot()));
        Assert.AreEqual("**unfinished", before.Display.Text);

        buffer.Insert(buffer.Length, "**");
        var after = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(buffer.CaptureSnapshot()));
        Assert.AreEqual("unfinished", after.Display.Text);
        Assert.AreEqual("**unfinished", before.Display.Text);
        Assert.AreEqual(8L, after.Source.Version);
    }

    [TestMethod]
    public void List_quote_and_table_structure_fold_without_changing_math_or_image_source()
    {
        var text = "> quote\n- item\n\n![alt](image.png) $x+1$\n\n| a | b |\n|---|---|\n| x | y |";
        var projection = Project(text);

        Assert.AreEqual("quote\nitem\n\n\uFFFC $x+1$\n\n a  b \n\n x  y ",
            projection.Display.Text);
        Assert.IsTrue(projection.HiddenRanges.Length > 2);
        Assert.AreEqual(text, projection.Source.Text);
        AssertMappingInvariants(projection);
    }

    [TestMethod]
    public void Every_source_and_display_boundary_obeys_the_two_sided_roundtrip_contract()
    {
        foreach (var sample in new[]
        {
            "", "plain", "a **bold** z", "***nested***", "# Title\nnext", "[a](url) and `b`",
            "😀 **e\u0301**\r\n<https://example.com>", "**incomplete", "[](url)"
        })
            AssertMappingInvariants(Project(sample));
    }

    [TestMethod]
    public void Mixed_incomplete_markdown_keeps_mapping_and_delete_ranges_bounded()
    {
        var random = new Random(21092026);
        var pieces = new[] { "a", "中", "😀", "e\u0301", " ", "\r\n", "**", "*", "~~",
            "`", "[", "](", "url)", "# ", "<https://x.test>", "$x$" };
        for (var sample = 0; sample < 180; sample++)
        {
            var text = string.Concat(Enumerable.Range(0, random.Next(1, 9))
                .Select(_ => pieces[random.Next(pieces.Length)]));
            var projection = Project(text);
            AssertMappingInvariants(projection);
            for (var source = 0; source <= text.Length; source++)
            {
                var backward = projection.BackspaceRange(source);
                var forward = projection.DeleteForwardRange(source);
                Assert.IsTrue(backward is null || projection.Source.FullRange.Contains(backward.Value),
                    $"Bad Backspace range for sample {sample}: {text}.");
                Assert.IsTrue(forward is null || projection.Source.FullRange.Contains(forward.Value),
                    $"Bad Delete range for sample {sample}: {text}.");
                Assert.IsTrue(projection.Source.FullRange.Contains(new(
                    projection.MoveSourceCaret(source, -1), 0)));
                Assert.IsTrue(projection.Source.FullRange.Contains(new(
                    projection.MoveSourceCaret(source, 1), 0)));
            }
        }
    }

    [TestMethod]
    public void Invalid_offsets_and_ranges_are_rejected_without_changing_the_projection()
    {
        var projection = Project("**bold**");

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => projection.ToDisplayOffset(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => projection.ToSourceOffset(5, ProjectionBoundary.BeforeHidden));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => projection.ToSourceRange(new(3, 2)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => projection.RevealAt(9));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => projection.MoveSourceCaret(0, 0));
        Assert.AreEqual("bold", projection.Display.Text);
    }

    private static MarkdownSyntaxDocument Parse(string text, long version = 0) =>
        MarkdownSyntaxParser.Parse(new SourceTextSnapshot(text, version));

    private static MarkdownEditProjection Project(string text) => MarkdownEditProjection.Create(Parse(text));

    private static void AssertMappingInvariants(MarkdownEditProjection projection)
    {
        for (var source = 0; source <= projection.Source.Length; source++)
        {
            var display = projection.ToDisplayOffset(source);
            var before = projection.ToSourceOffset(display, ProjectionBoundary.BeforeHidden);
            var after = projection.ToSourceOffset(display, ProjectionBoundary.AfterHidden);
            Assert.IsTrue(before <= source && source <= after, $"Source {source}, display {display}.");
        }
        for (var display = 0; display <= projection.Display.Length; display++)
        {
            Assert.AreEqual(display, projection.ToDisplayOffset(
                projection.ToSourceOffset(display, ProjectionBoundary.BeforeHidden)));
            Assert.AreEqual(display, projection.ToDisplayOffset(
                projection.ToSourceOffset(display, ProjectionBoundary.AfterHidden)));
        }
        for (var index = 1; index < projection.HiddenRanges.Length; index++)
            Assert.IsTrue(projection.HiddenRanges[index - 1].End < projection.HiddenRanges[index].Start);
    }
}
