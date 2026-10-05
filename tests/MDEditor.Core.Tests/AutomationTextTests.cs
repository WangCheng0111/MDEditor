using MDEditor.Core.Text;
using MDEditor.Core.Markdown;
using MDEditor.Typesetting.Layout;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class AutomationTextTests
{
    private static AutomationTextDocument Text(string text) => new(new(text, 3));

    [TestMethod]
    public void Malformed_utf16_remains_readable_and_navigable()
    {
        var document = Text("a\uD800 中\uDC00");
        Assert.AreEqual(document.Source.Text, document.GetText(document.Source.FullRange));
        Assert.IsTrue(document.MoveEndpoint(0, AutomationTextUnit.Word, 1).Offset > 0);
        Assert.AreEqual(document.Source.Length, document.MoveEndpoint(0, AutomationTextUnit.Character, 100).Offset);
    }

    [TestMethod]
    public void Full_text_preserves_markdown_math_and_exact_newlines()
    {
        var source = "# 中文\r\n**abc** $\\frac{x}{y}$\n\tcode\r";
        var document = Text(source);
        Assert.AreEqual(source, document.GetText(document.Source.FullRange));
        Assert.AreEqual("中文", document.GetText(new(2, 2)));
    }

    [TestMethod]
    [DataRow("😀X", 1, "")]
    [DataRow("e\u0301X", 1, "")]
    [DataRow("\r\nX", 1, "")]
    [DataRow("👩‍💻X", 4, "")]
    [DataRow("中文", 1, "中")]
    [DataRow("😀X", 2, "😀")]
    [DataRow("abc", 0, "")]
    public void Text_length_limit_never_splits_graphemes(string source, int maximum, string expected)
    {
        var document = Text(source);
        Assert.AreEqual(expected, document.GetText(document.Source.FullRange, maximum));
    }

    [TestMethod]
    public void Invalid_text_queries_are_rejected()
    {
        var document = Text("abc");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => document.GetText(new(0, 4)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => document.GetText(new(0, 1), -2));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => document.MoveEndpoint(4, AutomationTextUnit.Character, 1));
        Assert.ThrowsExactly<ArgumentException>(() => document.FindText(new(0, 3), ""));
    }

    [TestMethod]
    public void Character_units_keep_emoji_combining_marks_and_crlf_whole()
    {
        var document = Text("A😀e\u0301\r\n👩‍💻中");
        var offset = 0;
        var found = new List<string>();
        while (offset < document.Source.Length)
        {
            var moved = document.MoveEndpoint(offset, AutomationTextUnit.Character, 1);
            Assert.AreEqual(1, moved.Moved);
            found.Add(document.GetText(new(offset, moved.Offset - offset)));
            offset = moved.Offset;
        }
        CollectionAssert.AreEqual(new[] { "A", "😀", "e\u0301", "\r\n", "👩‍💻", "中" }, found.ToArray());
        var previous = document.MoveEndpoint(5, AutomationTextUnit.Character, -1);
        Assert.AreEqual(3, previous.Offset);
    }

    [TestMethod]
    public void Safe_selection_expands_to_complete_graphemes()
    {
        var document = Text("A😀e\u0301B");
        Assert.AreEqual(new SourceRange(1, 4), document.SafeRange(new(2, 2)));
        Assert.AreEqual(new SourceRange(1, 0), document.SafeRange(new(2, 0)));
        Assert.AreEqual(3, document.Snap(2, true));
    }

    [TestMethod]
    public void Endpoint_counts_clamp_at_document_edges_without_integer_overflow()
    {
        var document = Text("abc");
        Assert.AreEqual((3, 2), document.MoveEndpoint(1, AutomationTextUnit.Character, int.MaxValue));
        Assert.AreEqual((0, -2), document.MoveEndpoint(2, AutomationTextUnit.Character, int.MinValue));
        Assert.AreEqual((0, 0), document.MoveEndpoint(0, AutomationTextUnit.Character, -1));
        Assert.AreEqual((3, 0), document.MoveEndpoint(3, AutomationTextUnit.Character, 1));
    }

    [TestMethod]
    public void Nondegenerate_move_normalizes_to_one_unit_and_reports_actual_count()
    {
        var document = Text("one two three");
        var moved = document.Move(new(1, 9), AutomationTextUnit.Word, 1);
        Assert.AreEqual(new SourceRange(4, 4), moved.Range);
        Assert.AreEqual(1, moved.Moved);
        Assert.AreEqual(new SourceRange(8, 5), document.Move(moved.Range, AutomationTextUnit.Word, 100).Range);
        Assert.AreEqual(new SourceRange(1, 9), document.Move(new(1, 9), AutomationTextUnit.Word, 0).Range);
    }

    [TestMethod]
    public void Word_navigation_handles_han_latin_punctuation_and_whitespace()
    {
        var document = Text("中文 abc,def 42");
        Assert.AreEqual("中", document.GetText(document.Enclosing(new(0, 0), AutomationTextUnit.Word)));
        Assert.AreEqual("文 ", document.GetText(document.Enclosing(new(1, 0), AutomationTextUnit.Word)));
        Assert.AreEqual("abc", document.GetText(document.Enclosing(new(4, 0), AutomationTextUnit.Word)));
        Assert.AreEqual(",", document.GetText(document.Enclosing(new(6, 0), AutomationTextUnit.Word)));
        Assert.AreEqual("def ", document.GetText(document.Enclosing(new(8, 0), AutomationTextUnit.Word)));
    }

    [TestMethod]
    public void Visual_lines_and_source_paragraphs_are_independent()
    {
        var source = new SourceTextSnapshot("abcdef\r\ngh", 1);
        var document = new AutomationTextDocument(source, [0, 3, 8]);
        Assert.AreEqual("def\r\n", document.GetText(document.Enclosing(new(4, 0), AutomationTextUnit.Line)));
        Assert.AreEqual("abcdef\r\n", document.GetText(document.Enclosing(new(4, 0), AutomationTextUnit.Paragraph)));
        Assert.AreEqual("gh", document.GetText(document.Enclosing(new(source.Length, 0), AutomationTextUnit.Line)));
        Assert.AreEqual(source.FullRange, document.Enclosing(new(4, 2), AutomationTextUnit.Document));
    }

    [TestMethod]
    public void Empty_document_provides_a_valid_degenerate_document_range()
    {
        var document = Text("");
        foreach (var unit in Enum.GetValues<AutomationTextUnit>())
        {
            Assert.AreEqual(new SourceRange(0, 0), document.Enclosing(new(0, 0), unit));
            Assert.AreEqual((0, 0), document.MoveEndpoint(0, unit, int.MaxValue));
            Assert.AreEqual((new SourceRange(0, 0), 0), document.Move(new(0, 0), unit, -1));
        }
        Assert.AreEqual("", document.GetText(new(0, 0)));
    }

    [TestMethod]
    public void Find_text_respects_range_direction_case_and_math_source()
    {
        var document = Text("Abc 中文 abc $x+1$ abc");
        var range = document.Source.FullRange;
        Assert.AreEqual(new SourceRange(0, 3), document.FindText(range, "abc", ignoreCase: true));
        Assert.AreEqual(new SourceRange(7, 3), document.FindText(range, "abc"));
        Assert.AreEqual(new SourceRange(17, 3), document.FindText(range, "abc", backward: true));
        Assert.AreEqual(new SourceRange(12, 3), document.FindText(range, "x+1"));
        Assert.IsNull(document.FindText(new(0, 3), "中文"));
    }

    [TestMethod]
    public void Retained_ranges_follow_insertions_before_the_selected_text()
    {
        var document = Text("abc def");
        Assert.AreEqual(new SourceRange(6, 3), document.Rebase(new(4, 3), new("XXabc def", 4)));
        Assert.AreEqual(new SourceRange(6, 3), document.Rebase(new(4, 3), new("abc XXdef", 4)));
        Assert.AreEqual(new SourceRange(0, 3), document.Rebase(new(0, 3), new("abcXX def", 4)));
        Assert.AreEqual(new SourceRange(5, 0), document.Rebase(new(3, 0), new("abcXX def", 4)));
    }

    [TestMethod]
    public void Retained_ranges_clip_deleted_content_and_follow_replacements()
    {
        var document = Text("abc def ghi");
        Assert.AreEqual(new SourceRange(4, 0), document.Rebase(new(4, 3), new("abc  ghi", 4)));
        Assert.AreEqual(new SourceRange(4, 5), document.Rebase(new(4, 3), new("abc 你好😀x ghi", 4)));
        var rebased = Text("😀x").Rebase(new(0, 2), new("😁x", 4));
        Assert.AreEqual(new SourceRange(0, 2), rebased);
    }

    private static TextInteractionMap Geometry() => new(new("abcd\nefgh", 1), TextSurface.Body,
    [
        new(new(0, 4), new(0, 0, 40, 20), Enumerable.Range(0, 4)
            .Select(i => new TextInteractionSpan(new(i, 1), i * 10, (i + 1) * 10))),
        new(new(5, 4), new(0, 40, 40, 20), Enumerable.Range(0, 4)
            .Select(i => new TextInteractionSpan(new(5 + i, 1), i * 10, (i + 1) * 10)))
    ]);

    [TestMethod]
    public void Visible_ranges_exclude_offscreen_lines_and_include_partial_lines()
    {
        var geometry = new AutomationTextGeometry(Geometry());
        CollectionAssert.AreEqual(new[] { new SourceRange(0, 4) }, geometry.VisibleRanges(new(200, 50), 0).ToArray());
        CollectionAssert.AreEqual(new[] { new SourceRange(5, 4) }, geometry.VisibleRanges(new(200, 50), 65).ToArray());
        Assert.AreEqual(0, geometry.VisibleRanges(new(200, 50), 200).Count);
    }

    [TestMethod]
    public void Bounding_rectangles_follow_zoom_scroll_and_clip_to_viewport()
    {
        var geometry = new AutomationTextGeometry(Geometry());
        var viewport = new DocumentViewport(200, 70, 200, 144);
        var rectangles = geometry.Rectangles(new(0, 9), viewport, 15);
        CollectionAssert.AreEqual(new[] { new LayoutRect(24, 0, 80, 34) }, rectangles.ToArray());
        Assert.AreEqual(0, geometry.Rectangles(new(0, 0), viewport, 15).Count);
        Assert.AreEqual(0, geometry.Rectangles(new(0, 4), viewport, 200).Count);
    }

    [TestMethod]
    public void Caret_geometry_is_distinct_from_degenerate_range_rectangles()
    {
        var geometry = new AutomationTextGeometry(Geometry());
        var viewport = new DocumentViewport(200, 100);
        Assert.AreEqual(new LayoutRect(44, 24, 1, 20), geometry.Caret(2, viewport, 0));
        Assert.AreEqual(0, geometry.Rectangles(new(2, 0), viewport, 0).Count);
        Assert.IsNull(geometry.Caret(2, viewport, 100));
    }

    [TestMethod]
    public void Projection_rectangles_use_source_ranges_without_selecting_hidden_markers()
    {
        var source = new SourceTextSnapshot("**ab**", 1);
        var projection = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(source));
        var display = new TextInteractionMap(projection.Display, TextSurface.Body,
        [new(new(0, 2), new(0, 0, 20, 20), [new(new(0, 1), 0, 10), new(new(1, 1), 10, 20)])]);
        var geometry = new AutomationTextGeometry(new MarkdownProjectionInteractionMap(projection, display));
        var viewport = new DocumentViewport(200, 100);
        Assert.AreEqual(0, geometry.Rectangles(new(0, 2), viewport, 0).Count);
        CollectionAssert.AreEqual(new[] { new LayoutRect(24, 24, 20, 20) }, geometry.Rectangles(new(2, 2), viewport, 0).ToArray());
    }

    [TestMethod]
    public void Dpi_only_changes_screen_conversion_not_text_or_view_geometry()
    {
        var geometry = new AutomationTextGeometry(Geometry());
        var first = geometry.Rectangles(new(0, 2), new(200, 100, 100, 96), 0);
        var second = geometry.Rectangles(new(0, 2), new(200, 100, 100, 192), 0);
        CollectionAssert.AreEqual(first.ToArray(), second.ToArray());
        Assert.AreEqual(new LayoutRect(48, 48, 40, 40), new(first[0].X * 2, first[0].Y * 2, first[0].Width * 2, first[0].Height * 2));
    }

    [TestMethod]
    public void Geometry_clipping_rejects_touching_or_outside_edges()
    {
        var viewport = new LayoutRect(0, 0, 100, 50);
        Assert.IsNull(AutomationTextGeometry.Clip(new(100, 0, 10, 10), viewport));
        Assert.AreEqual(new LayoutRect(0, 0, 5, 5), AutomationTextGeometry.Clip(new(-5, -5, 10, 10), viewport));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AutomationTextGeometry(Geometry()).Rectangles(new(0, 100), new(200, 100), 0));
    }
}
