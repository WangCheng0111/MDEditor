using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class TextInteractionMapTests
{
    [TestMethod]
    [DataRow(" ")]
    [DataRow("   ")]
    public void Whitespace_only_lines_keep_every_real_source_caret(string text)
    {
        var source = new SourceTextSnapshot(text, 3);
        var bounds = new LayoutRect(20, 0, 100, 24);
        var empty = new LineLayout(source.FullRange, new(20, 0, 0, 24), 16, 0, []);
        var snapshot = new LayoutSnapshot(source, bounds, [],
            [new BlockLayout(source.FullRange, bounds, [empty])]);
        var map = TextInteractionMap.FromSnapshot(snapshot);
        for (var offset = 0; offset <= text.Length; offset++)
            Assert.AreEqual(new LayoutRect(20, 0, 0, 24),
                map.Resolve(new(TextSurface.Body, 3, offset, CaretAffinity.Downstream)));
    }

    [TestMethod]
    public void Omitted_boundary_spaces_remain_editable_without_adding_glyphs()
    {
        var source = new SourceTextSnapshot("     ", 3);
        var bounds = new LayoutRect(20, 0, 100, 24);
        var empty = new LineLayout(new(2, 0), new(20, 0, 0, 24), 16, 0, []);
        var snapshot = new LayoutSnapshot(source, bounds, [],
            [new BlockLayout(source.FullRange, bounds, [empty])]);
        var map = TextInteractionMap.FromSnapshot(snapshot);
        Assert.AreEqual(source.FullRange, map.Lines.Single().Source);
        for (var offset = 0; offset <= source.Length; offset++)
            Assert.IsNotNull(map.Resolve(new(TextSurface.Body, 3, offset, CaretAffinity.Downstream)));
        Assert.IsTrue(snapshot.Blocks.Single().Lines.Single().Runs.IsEmpty);
    }

    private static TextInteractionMap TwoLines() => new(new("abcd efgh", 17), TextSurface.Body,
    [
        new(new(0, 4), new(0, 0, 40, 20),
            Enumerable.Range(0, 4).Select(index => new TextInteractionSpan(new(index, 1), index * 10, (index + 1) * 10))),
        new(new(5, 4), new(0, 25, 40, 20),
            Enumerable.Range(5, 4).Select(index => new TextInteractionSpan(new(index, 1), (index - 5) * 10, (index - 4) * 10)))
    ]);

    [TestMethod]
    public void Click_and_drag_across_soft_line_preserves_source_offsets()
    {
        var map = TwoLines();
        var anchor = map.HitTest(new(12, 10))!.Value;
        var focus = map.HitTest(new(33, 35))!.Value;
        Assert.AreEqual(1, anchor.Offset);
        Assert.AreEqual(8, focus.Offset);
        var selection = new TextSelection(anchor, focus);
        Assert.AreEqual(new SourceRange(1, 7), selection.Range);
        Assert.AreEqual("bcd efg", map.Source.GetText(selection.Range));
        CollectionAssert.AreEqual(new[]
        {
            new LayoutRect(10, 0, 30, 20), new LayoutRect(0, 25, 30, 20)
        }, map.SelectionRects(selection).ToArray());
    }

    [TestMethod]
    public void Reverse_selection_has_same_range_and_visible_rectangles()
    {
        var map = TwoLines();
        var first = map.HitTest(new(10, 10))!.Value;
        var last = map.HitTest(new(30, 35))!.Value;
        var forward = new TextSelection(first, last);
        var backward = new TextSelection(last, first);
        Assert.AreEqual(forward.Range, backward.Range);
        CollectionAssert.AreEqual(map.SelectionRects(forward).ToArray(), map.SelectionRects(backward).ToArray());
    }

    [TestMethod]
    public void Resize_reprojects_offset_onto_new_line_geometry()
    {
        var oldMap = TwoLines();
        var position = oldMap.HitTest(new(21, 10))!.Value;
        var newMap = new TextInteractionMap(oldMap.Source, TextSurface.Body,
            [new(new(0, 9), new(0, 100, 90, 24),
                Enumerable.Range(0, 9).Select(index => new TextInteractionSpan(new(index, 1), index * 10, (index + 1) * 10)))]);
        Assert.AreEqual(2, position.Offset);
        Assert.AreEqual(new LayoutRect(20, 100, 0, 24), newMap.Resolve(position));
    }

    [TestMethod]
    public void Zoom_scroll_and_dpi_use_the_presented_viewport_inverse()
    {
        var map = TwoLines();
        foreach (var zoom in new[] { 50, 100, 175, 300 })
        foreach (var dpi in new[] { 96d, 144d, 192d })
        {
            var viewport = new DocumentViewport(850, 500, zoom, dpi);
            var view = viewport.ToView(new(27, 35), 12);
            var document = viewport.ToDocument(view, 12);
            Assert.AreEqual(8, map.HitTest(document)!.Value.Offset);
        }
    }

    [TestMethod]
    public void Soft_wrap_affinity_chooses_the_clicked_line_after_reflow()
    {
        var source = new SourceTextSnapshot("abcd", 4);
        var map = new TextInteractionMap(source, TextSurface.Body,
        [
            new(new(0, 2), new(0, 0, 20, 20), [new(new(0, 1), 0, 10), new(new(1, 1), 10, 20)]),
            new(new(2, 2), new(0, 25, 20, 20), [new(new(2, 1), 0, 10), new(new(3, 1), 10, 20)])
        ]);
        Assert.AreEqual(new LayoutRect(20, 0, 0, 20),
            map.Resolve(new(TextSurface.Body, 4, 2, CaretAffinity.Upstream)));
        Assert.AreEqual(new LayoutRect(0, 25, 0, 20),
            map.Resolve(new(TextSurface.Body, 4, 2, CaretAffinity.Downstream)));
    }

    [TestMethod]
    public void Version_and_surface_mismatch_never_draw_stale_selection()
    {
        var map = TwoLines();
        var wrong = new TextCaret(TextSurface.Body, 18, 2, CaretAffinity.Downstream);
        Assert.IsNull(map.Resolve(wrong));
        Assert.HasCount(0, map.SelectionRects(new(wrong, wrong with { Offset = 5 })));
        Assert.IsNull(map.Resolve(new(TextSurface.MarkdownMath, 17, 2, CaretAffinity.Downstream)));
    }

    [TestMethod]
    public void Combining_marks_surrogates_and_zwj_do_not_gain_internal_caret_stops()
    {
        var source = new SourceTextSnapshot("a\u0308😀👩‍👩‍👧", 1);
        var spans = new List<TextInteractionSpan>();
        TextInteractionMap.AddGraphemes(spans, source, source.FullRange, 0, 40);
        Assert.AreEqual(3, spans.Count);
        CollectionAssert.AreEqual(new[] { 0, 2, 4 }, spans.Select(span => span.Source.Start).ToArray());
        var map = new TextInteractionMap(source, TextSurface.Body,
            [new(source.FullRange, new(0, 0, 40, 20), spans)]);
        foreach (var x in Enumerable.Range(0, 41))
            Assert.IsTrue(new[] { 0, 2, 4, source.Length }.Contains(map.HitTest(new(x, 10))!.Value.Offset));
    }

    [TestMethod]
    public void Formula_is_an_atomic_source_box_including_delimiters()
    {
        var source = new SourceTextSnapshot("$x_i^2$", 14);
        var map = new TextInteractionMap(source, TextSurface.MarkdownMath,
            [new(source.FullRange, new(0, 0, 30, 25), [new(source.FullRange, 0, 30)])]);
        Assert.AreEqual(0, map.HitTest(new(3, 12))!.Value.Offset);
        Assert.AreEqual(source.Length, map.HitTest(new(27, 12))!.Value.Offset);
        Assert.AreEqual(new SourceRange(0, source.Length),
            new TextSelection(map.HitTest(new(3, 12))!.Value, map.HitTest(new(27, 12))!.Value).Range);
    }

    [TestMethod]
    public void Empty_visual_line_still_has_a_visible_caret()
    {
        var source = new SourceTextSnapshot("", 0);
        var map = new TextInteractionMap(source, TextSurface.Body,
            [new(source.FullRange, new(5, 8, 40, 20), [])]);
        var hit = map.HitTest(new(20, 12))!.Value;
        Assert.AreEqual(0, hit.Offset);
        Assert.AreEqual(new LayoutRect(5, 8, 0, 20), map.Resolve(hit));
    }

    [TestMethod]
    public void Rtl_run_uses_right_edge_as_source_start()
    {
        var source = new SourceTextSnapshot("אב", 2);
        var run = new GlyphRunLayout(0, source.FullRange, new(20, 15), 14, 1, "he-IL",
            [new GlyphPlacement(10, 10), new GlyphPlacement(11, 10)],
            [new(new(0, 1), 1, 1, 10), new(new(1, 1), 0, 1, 10)]);
        var line = new LineLayout(source.FullRange, new(0, 0, 20, 20), 15, 20, [run]);
        var snapshot = new LayoutSnapshot(source, new(0, 0, 20, 20),
            [new FontFaceDescriptor("Segoe UI", "Regular")],
            [new BlockLayout(source.FullRange, new(0, 0, 20, 20), [line])]);
        var map = TextInteractionMap.FromSnapshot(snapshot);
        Assert.AreEqual(0, map.HitTest(new(19, 10))!.Value.Offset);
        Assert.AreEqual(2, map.HitTest(new(1, 10))!.Value.Offset);
        Assert.AreEqual(new LayoutRect(20, 0, 0, 20), map.Resolve(new(TextSurface.Body, 2, 0, CaretAffinity.Downstream)));
        Assert.AreEqual(1, map.MoveHorizontal(new(TextSurface.Body, 2, 0, CaretAffinity.Downstream), -1)!.Value.Offset);
        Assert.AreEqual(0, map.MoveHorizontal(new(TextSurface.Body, 2, 1, CaretAffinity.Upstream), 1)!.Value.Offset);
    }

    [TestMethod]
    public void Visual_arrow_crosses_soft_wrap_without_losing_source_mapping()
    {
        var map = TwoLines();
        var end = new TextCaret(TextSurface.Body, 17, 4, CaretAffinity.Upstream);
        var next = map.MoveHorizontal(end, 1)!.Value;
        Assert.AreEqual(5, next.Offset);
        Assert.AreEqual(new LayoutRect(0, 25, 0, 20), map.Resolve(next));
    }

    [TestMethod]
    public void Downstream_caret_after_math_glue_moves_into_following_text()
    {
        var source = new SourceTextSnapshot("A$\\frac{x}{y}$中", 19);
        var math = new SourceRange(1, source.Text.IndexOf('中') - 1);
        var next = math.End;
        var map = new TextInteractionMap(source, TextSurface.Body,
        [
            new(source.FullRange, new(0, 0, 80, 24),
            [
                new(new(0, 1), 0, 10),
                new(math, 10, 50),
                new(new(next, 1), 54, 74)
            ])
        ]);
        var afterPaste = new TextCaret(TextSurface.Body, source.Version, next, CaretAffinity.Downstream);

        Assert.AreEqual(new LayoutRect(54, 0, 0, 24), map.Resolve(afterPaste));
        Assert.AreEqual(next + 1, map.MoveHorizontal(afterPaste, 1)!.Value.Offset);
        Assert.AreEqual(new LayoutRect(50, 0, 0, 24),
            map.Resolve(afterPaste with { Affinity = CaretAffinity.Upstream }));
    }

    [TestMethod]
    [DataRow(2d)]
    [DataRow(4d)]
    [DataRow(6d)]
    [DataRow(18d)]
    public void Generated_formula_glue_adds_no_arrow_steps_in_either_direction(double gap)
    {
        const string text = "原生两端对齐及公式$\\frac{2x+1}{\\sqrt{y+1}}$仍然保留";
        var (map, math) = FormulaMap(text, gap);
        var expected = Enumerable.Range(0, math.Start + 1)
            .Concat(Enumerable.Range(math.End, map.Source.Length - math.End + 1)).ToArray();
        var caret = new TextCaret(TextSurface.Body, map.Source.Version, 0, CaretAffinity.Downstream);

        foreach (var offset in expected.Skip(1))
        {
            caret = map.MoveHorizontal(caret, 1)!.Value;
            Assert.AreEqual(offset, caret.Offset);
            Assert.IsNotNull(map.Resolve(caret));
        }
        Assert.AreEqual(caret, map.MoveHorizontal(caret, 1));
        foreach (var offset in expected.Reverse().Skip(1))
        {
            caret = map.MoveHorizontal(caret, -1)!.Value;
            Assert.AreEqual(offset, caret.Offset);
            Assert.IsNotNull(map.Resolve(caret));
        }
        Assert.AreEqual(caret, map.MoveHorizontal(caret, -1));
        Assert.AreEqual(text, map.Source.Text);
    }

    [TestMethod]
    [DataRow(CaretAffinity.Upstream)]
    [DataRow(CaretAffinity.Downstream)]
    public void Either_visual_side_of_formula_glue_advances_to_a_different_source_offset(CaretAffinity affinity)
    {
        var (map, math) = FormulaMap("公式$x$仍然", 4);
        var before = new TextCaret(TextSurface.Body, map.Source.Version, math.Start, affinity);
        var after = before with { Offset = math.End };

        Assert.AreEqual(math.End, map.MoveHorizontal(before, 1)!.Value.Offset);
        Assert.AreEqual(math.Start - 1, map.MoveHorizontal(before, -1)!.Value.Offset);
        Assert.AreEqual(math.End + 1, map.MoveHorizontal(after, 1)!.Value.Offset);
        Assert.AreEqual(math.Start, map.MoveHorizontal(after, -1)!.Value.Offset);
    }

    [TestMethod]
    public void Shift_arrow_selects_and_deletes_formula_without_a_phantom_space_step()
    {
        const string text = "公式$\\frac{2x+1}{\\sqrt{y+1}}$仍然";
        var (map, math) = FormulaMap(text, 4);
        var first = new TextCaret(TextSurface.Body, map.Source.Version, math.Start, CaretAffinity.Upstream);
        var last = map.MoveHorizontal(first, 1)!.Value;
        var forward = new TextSelection(first, last);
        var reverseStart = last with { Affinity = CaretAffinity.Downstream };
        var reverse = new TextSelection(reverseStart, map.MoveHorizontal(reverseStart, -1)!.Value);

        Assert.AreEqual(math, forward.Range);
        Assert.AreEqual(math, reverse.Range);
        Assert.AreEqual(map.Source.GetText(math), TextEditingOperations.SelectedText(map.Source, forward));
        CollectionAssert.AreEqual(new[] { new LayoutRect(36, 0, 60, 24) },
            map.SelectionRects(forward).ToArray());
        CollectionAssert.AreEqual(map.SelectionRects(forward).ToArray(), map.SelectionRects(reverse).ToArray());
        var buffer = new StyledDocumentBuffer(text, [new(0, 0)], map.Source.Version);
        TextEditingOperations.Replace(buffer, forward, "");
        Assert.AreEqual("公式仍然", buffer.Capture().Source.Text);
    }

    [TestMethod]
    public void Literal_spaces_beside_formula_remain_navigable_selectable_and_deletable()
    {
        const string text = "公式 $x$ 仍然";
        var (map, math) = FormulaMap(text, 0);
        var beforeSpace = new TextCaret(TextSurface.Body, map.Source.Version, math.Start - 1,
            CaretAffinity.Upstream);
        var beforeMath = map.MoveHorizontal(beforeSpace, 1)!.Value;
        var afterMath = map.MoveHorizontal(beforeMath, 1)!.Value;
        var afterSpace = map.MoveHorizontal(afterMath, 1)!.Value;

        Assert.AreEqual(math.Start, beforeMath.Offset);
        Assert.AreEqual(math.End, afterMath.Offset);
        Assert.AreEqual(math.End + 1, afterSpace.Offset);
        Assert.AreEqual(" ", TextEditingOperations.SelectedText(map.Source, new(beforeSpace, beforeMath)));
        Assert.AreEqual(" ", TextEditingOperations.SelectedText(map.Source, new(afterMath, afterSpace)));
        var backwardBuffer = new StyledDocumentBuffer(text, [new(0, 0)], map.Source.Version);
        TextEditingOperations.Backspace(backwardBuffer, new(beforeMath, beforeMath));
        Assert.AreEqual("公式$x$ 仍然", backwardBuffer.Capture().Source.Text);
        var forwardBuffer = new StyledDocumentBuffer(text, [new(0, 0)], map.Source.Version);
        TextEditingOperations.DeleteForward(forwardBuffer, new(afterMath, afterMath));
        Assert.AreEqual("公式 $x$仍然", forwardBuffer.Capture().Source.Text);
        Assert.AreEqual(text, map.Source.Text);
    }

    [TestMethod]
    public void Cjk_justification_glue_is_not_a_second_stop_at_one_source_boundary()
    {
        var source = new SourceTextSnapshot("中文", 23);
        var map = new TextInteractionMap(source, TextSurface.Body,
            [new(source.FullRange, new(0, 0, 36, 24), [new(new(0, 1), 0, 16), new(new(1, 1), 20, 36)])]);
        var start = new TextCaret(TextSurface.Body, source.Version, 0, CaretAffinity.Downstream);
        var middle = map.MoveHorizontal(start, 1)!.Value;
        var end = map.MoveHorizontal(middle, 1)!.Value;

        Assert.AreEqual(1, middle.Offset);
        Assert.AreEqual(2, end.Offset);
        var backwardMiddle = map.MoveHorizontal(end, -1)!.Value;
        Assert.AreEqual(1, backwardMiddle.Offset);
        Assert.AreEqual(0, map.MoveHorizontal(backwardMiddle, -1)!.Value.Offset);
    }

    [TestMethod]
    public void Real_rtl_glyph_between_two_sides_of_one_offset_remains_navigable()
    {
        var source = new SourceTextSnapshot("aא", 24);
        var map = new TextInteractionMap(source, TextSurface.Body,
            [new(source.FullRange, new(0, 0, 20, 24), [new(new(0, 1), 0, 10), new(new(1, 1), 20, 10)])]);
        var beforeRtl = new TextCaret(TextSurface.Body, source.Version, 1, CaretAffinity.Upstream);
        var moved = map.MoveHorizontal(beforeRtl, 1)!.Value;

        Assert.AreEqual(1, moved.Offset);
        Assert.AreEqual(CaretAffinity.Downstream, moved.Affinity);
        Assert.AreEqual(new LayoutRect(20, 0, 0, 24), map.Resolve(moved));
    }

    [TestMethod]
    public void Source_free_glue_between_rtl_glyphs_is_skipped_in_both_directions()
    {
        var source = new SourceTextSnapshot("אב", 25);
        var map = new TextInteractionMap(source, TextSurface.Body,
            [new(source.FullRange, new(0, 0, 24, 24), [new(new(0, 1), 24, 14), new(new(1, 1), 10, 0)])]);
        var right = new TextCaret(TextSurface.Body, source.Version, 2, CaretAffinity.Upstream);
        var rightMiddle = map.MoveHorizontal(right, 1)!.Value;
        Assert.AreEqual(1, rightMiddle.Offset);
        Assert.AreEqual(0, map.MoveHorizontal(rightMiddle, 1)!.Value.Offset);
        var left = right with { Offset = 0, Affinity = CaretAffinity.Downstream };
        var leftMiddle = map.MoveHorizontal(left, -1)!.Value;
        Assert.AreEqual(1, leftMiddle.Offset);
        Assert.AreEqual(2, map.MoveHorizontal(leftMiddle, -1)!.Value.Offset);
    }

    [TestMethod]
    public void Shared_source_offset_on_different_soft_lines_keeps_its_line_transition()
    {
        var source = new SourceTextSnapshot("abcd", 26);
        var map = new TextInteractionMap(source, TextSurface.Body,
        [
            new(new(0, 2), new(0, 0, 20, 20), [new(new(0, 1), 0, 10), new(new(1, 1), 10, 20)]),
            new(new(2, 2), new(0, 25, 20, 20), [new(new(2, 1), 0, 10), new(new(3, 1), 10, 20)])
        ]);
        var oldLineEnd = new TextCaret(TextSurface.Body, source.Version, 2, CaretAffinity.Upstream);
        var newLineStart = map.MoveHorizontal(oldLineEnd, 1)!.Value;

        Assert.AreEqual(2, newLineStart.Offset);
        Assert.AreEqual(new LayoutRect(0, 25, 0, 20), map.Resolve(newLineStart));
        Assert.AreEqual(oldLineEnd, map.MoveHorizontal(newLineStart, -1));
        Assert.AreEqual(3, map.MoveHorizontal(newLineStart, 1)!.Value.Offset);
    }

    private static (TextInteractionMap Map, SourceRange Math) FormulaMap(string text, double gap)
    {
        var source = new SourceTextSnapshot(text, 22);
        var math = new SourceRange(text.IndexOf('$'), text.LastIndexOf('$') - text.IndexOf('$') + 1);
        var spans = new List<TextInteractionSpan>();
        var x = 0d;
        for (var offset = 0; offset < source.Length;)
        {
            if (offset == math.Start)
            {
                x += gap;
                spans.Add(new(math, x, x + 60));
                x += 60 + gap;
                offset = math.End;
            }
            else
            {
                var advance = text[offset] == ' ' ? 5 : 16;
                spans.Add(new(new(offset++, 1), x, x + advance));
                x += advance;
            }
        }
        return (new(source, TextSurface.Body, [new(source.FullRange, new(0, 0, x, 24), spans)]), math);
    }

    [TestMethod]
    public void Selection_across_formula_and_justification_gap_is_one_continuous_band()
    {
        var source = new SourceTextSnapshot("甲$x$乙", 20);
        var map = new TextInteractionMap(source, TextSurface.Body,
        [
            new(source.FullRange, new(0, 0, 80, 24),
            [
                new(new(0, 1), 0, 16),
                new(new(1, 3), 20, 54),
                new(new(4, 1), 59, 75)
            ])
        ]);
        var first = new TextCaret(TextSurface.Body, source.Version, 0, CaretAffinity.Downstream);
        var last = first with { Offset = source.Length, Affinity = CaretAffinity.Upstream };

        CollectionAssert.AreEqual(new[] { new LayoutRect(0, 0, 75, 24) },
            map.SelectionRects(new(first, last)).ToArray());
    }

    [TestMethod]
    public void Visual_selection_does_not_bridge_unselected_bidi_content()
    {
        var source = new SourceTextSnapshot("abcd", 21);
        var map = new TextInteractionMap(source, TextSurface.Body,
        [
            new(source.FullRange, new(0, 0, 40, 20),
            [
                new(new(0, 1), 0, 10),
                new(new(3, 1), 10, 20),
                new(new(1, 1), 20, 30),
                new(new(2, 1), 30, 40)
            ])
        ]);
        var first = new TextCaret(TextSurface.Body, source.Version, 0, CaretAffinity.Downstream);
        var last = first with { Offset = 3, Affinity = CaretAffinity.Upstream };

        CollectionAssert.AreEqual(new[]
        {
            new LayoutRect(0, 0, 10, 20), new LayoutRect(20, 0, 20, 20)
        }, map.SelectionRects(new(first, last)).ToArray());
    }
}
