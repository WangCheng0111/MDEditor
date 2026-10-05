using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MarkdownProjectionInteractionMapTests
{
    [TestMethod]
    public void Pointer_hit_on_projected_glyph_returns_the_original_source_position()
    {
        var (projection, interaction) = OneLine("a **b** c");

        Assert.AreEqual("a b c", projection.Display.Text);
        Assert.AreEqual(2, interaction.HitTest(new(21, 10))!.Value.Offset);
        Assert.AreEqual(4, interaction.HitTest(new(21, 10), ProjectionBoundary.AfterHidden)!.Value.Offset);
        Assert.AreEqual(5, interaction.HitTest(new(29, 10))!.Value.Offset);
        Assert.AreEqual(new LayoutRect(20, 0, 0, 20),
            interaction.Resolve(new(TextSurface.Body, projection.Source.Version, 4, CaretAffinity.Downstream)));
        Assert.AreEqual(new LayoutRect(20, 0, 0, 20),
            interaction.Resolve(new(TextSurface.Body, projection.Source.Version, 2, CaretAffinity.Downstream)));
    }

    [TestMethod]
    public void Visual_arrows_skip_hidden_markers_but_preserve_source_affinity()
    {
        var (projection, interaction) = OneLine("a **b** c");
        var beforeB = new TextCaret(TextSurface.Body, projection.Source.Version, 4, CaretAffinity.Downstream);

        var afterB = interaction.MoveHorizontal(beforeB, 1)!.Value;
        Assert.AreEqual(5, afterB.Offset);
        Assert.AreEqual(3, projection.ToDisplayOffset(afterB.Offset));
        Assert.AreEqual(4, interaction.MoveHorizontal(afterB, -1)!.Value.Offset);
    }

    [TestMethod]
    public void Forward_and_reverse_source_selections_highlight_the_same_visible_glyphs()
    {
        var (projection, interaction) = OneLine("a **bold** z");
        var first = new TextCaret(TextSurface.Body, projection.Source.Version, 2, CaretAffinity.Downstream);
        var last = new TextCaret(TextSurface.Body, projection.Source.Version, 10, CaretAffinity.Upstream);

        var forward = interaction.SelectionRects(new(first, last));
        var reverse = interaction.SelectionRects(new(last, first));
        CollectionAssert.AreEqual(new[] { new LayoutRect(20, 0, 40, 20) }, forward.ToArray());
        CollectionAssert.AreEqual(forward.ToArray(), reverse.ToArray());
        Assert.HasCount(0, interaction.SelectionRects(new(first, first with { Offset = 4 })));
    }

    [TestMethod]
    public void Expanding_syntax_reprojects_the_same_source_caret_without_rewriting_it()
    {
        var (collapsed, compactMap) = OneLine("**bold**", 31);
        var expanded = collapsed.RevealAt(3);
        var expandedMap = OneLine(expanded).Interaction;
        var sourceCaret = new TextCaret(TextSurface.Body, 31, 3, CaretAffinity.Downstream);

        Assert.AreEqual(new LayoutRect(10, 0, 0, 20), compactMap.Resolve(sourceCaret));
        Assert.AreEqual(new LayoutRect(30, 0, 0, 20), expandedMap.Resolve(sourceCaret));
        Assert.AreEqual(3, sourceCaret.Offset);
    }

    [TestMethod]
    public void A_display_map_from_another_projection_is_rejected_even_at_the_same_version()
    {
        var first = Project("**bold**", 7);
        var second = Project("**bold**", 7);
        var stale = OneLine(first).DisplayMap;

        Assert.ThrowsExactly<ArgumentException>(() =>
            new MarkdownProjectionInteractionMap(second, stale));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new MarkdownProjectionInteractionMap(first, new TextInteractionMap(first.Display,
                TextSurface.MarkdownMath, [])));
    }

    [TestMethod]
    public void Invalid_versions_or_surfaces_never_resolve_or_highlight_stale_geometry()
    {
        var (projection, interaction) = OneLine("**bold**", 4);
        var stale = new TextCaret(TextSurface.Body, 3, 2, CaretAffinity.Downstream);
        var wrongSurface = stale with { SourceVersion = 4, Surface = TextSurface.MarkdownMath };

        Assert.IsNull(interaction.Resolve(stale));
        Assert.IsNull(interaction.Resolve(wrongSurface));
        Assert.IsNull(interaction.MoveHorizontal(stale, 1));
        Assert.HasCount(0, interaction.SelectionRects(new(stale, stale with { Offset = 6 })));
        Assert.AreEqual(4L, projection.Source.Version);
    }

    [TestMethod]
    public void Heading_projection_maps_a_click_on_the_next_line_back_past_hidden_markers()
    {
        var projection = Project("# A\nB", 9);
        Assert.AreEqual("A\nB", projection.Display.Text);
        var displayMap = new TextInteractionMap(projection.Display, TextSurface.Body,
        [
            new(new(0, 1), new(0, 0, 10, 20), [new(new(0, 1), 0, 10)]),
            new(new(2, 1), new(0, 25, 10, 20), [new(new(2, 1), 0, 10)])
        ]);
        var interaction = new MarkdownProjectionInteractionMap(projection, displayMap);

        Assert.AreEqual(4, interaction.HitTest(new(1, 35))!.Value.Offset);
        Assert.AreEqual(new LayoutRect(0, 25, 0, 20),
            interaction.Resolve(new(TextSurface.Body, 9, 4, CaretAffinity.Downstream)));
    }

    [TestMethod]
    public void Source_facing_interface_keeps_line_geometry_but_exposes_original_offsets()
    {
        var (projection, bridge) = OneLine("a **bold** z", 12);
        ITextInteractionMap map = bridge;

        Assert.AreSame(projection.Source, map.Source);
        Assert.AreEqual(TextSurface.Body, map.Surface);
        Assert.AreEqual(projection.Source.FullRange, map.Lines[0].Source);
        Assert.AreEqual(new SourceRange(4, 1), map.Lines[0].Spans[2].Source);
        Assert.AreEqual(2, map.HitTest(new(21, 10))!.Value.Offset);
        Assert.AreEqual(0, map.DistanceToY(10));
        Assert.AreEqual(new LayoutRect(20, 0, 0, 20),
            map.Resolve(new(TextSurface.Body, 12, 2, CaretAffinity.Downstream)));
    }

    [TestMethod]
    public void Editing_click_on_either_side_of_one_formatted_glyph_enters_its_wrapper()
    {
        var (projection, bridge) = OneLine("a **b** c");
        var left = bridge.HitTestForEditing(new(21, 10))!.Value;
        var right = bridge.HitTestForEditing(new(29, 10))!.Value;

        Assert.AreEqual(4, left.Offset);
        Assert.AreEqual(5, right.Offset);
        Assert.AreEqual(projection.Source.Text, projection.RevealAt(left.Offset).Display.Text);
        Assert.AreEqual(projection.Source.Text, projection.RevealAt(right.Offset).Display.Text);
    }

    [TestMethod]
    public void Editing_click_enters_nested_wrapper_when_hidden_prefixes_merge()
    {
        var (projection, bridge) = OneLine("***x***");
        var caret = bridge.HitTestForEditing(new(1, 10))!.Value;

        Assert.AreEqual(3, caret.Offset);
        Assert.AreEqual("***x***", projection.RevealAt(caret.Offset).Display.Text);
    }

    [TestMethod]
    public void Entering_a_folded_list_line_by_arrow_click_or_home_stays_after_its_marker()
    {
        var projection = Project("a\n- item", 8);
        Assert.AreEqual("a\nitem", projection.Display.Text);
        var display = new TextInteractionMap(projection.Display, TextSurface.Body,
        [
            new(new(0, 1), new(0, 0, 10, 20), [new(new(0, 1), 0, 10)]),
            new(new(2, 4), new(0, 25, 40, 20), Enumerable.Range(2, 4)
                .Select(index => new TextInteractionSpan(new(index, 1), (index - 2) * 10, (index - 1) * 10)))
        ]);
        var bridge = new MarkdownProjectionInteractionMap(projection, display);
        var previousEnd = new TextCaret(TextSurface.Body, 8, 1, CaretAffinity.Upstream);

        Assert.AreEqual(4, bridge.Lines[1].Source.Start);
        Assert.AreEqual(4, bridge.HitTest(new(0, 35))!.Value.Offset);
        Assert.AreEqual(4, bridge.MoveHorizontal(previousEnd, 1)!.Value.Offset);
        Assert.AreEqual(4, projection.MoveSourceCaret(1, 1));
    }

    [TestMethod]
    public void Empty_table_cell_padding_maps_to_one_source_side_without_negative_length()
    {
        var projection = Project("| A | B |\n|---|---|\n|  |  |", 13);
        var cell = projection.Tables.Tables.Single().Rows[1].Cells[1];
        var point = projection.ToDisplayOffset(cell.Source.Start);
        var line = projection.ToDisplayRange(projection.Tables.Tables.Single().Rows[1].Line);
        var display = new TextInteractionMap(projection.Display, TextSurface.Body,
        [
            new(line, new(0, 0, 100, 30), [new TextInteractionSpan(new(point, 0), 60, 95)])
        ]);

        var bridge = new MarkdownProjectionInteractionMap(projection, display);
        Assert.AreEqual(0, bridge.Lines[0].Spans[0].Source.Length);
        Assert.AreEqual(projection.ToSourceOffset(point, ProjectionBoundary.AfterHidden),
            bridge.Lines[0].Spans[0].Source.Start);
        Assert.IsNotNull(bridge.HitTest(new(80, 15)));
    }

    [TestMethod]
    public void Every_glyph_of_a_multi_character_reference_maps_to_its_whole_source_token()
    {
        var projection = Project("$$x\\label{eq:x}$$\nSee \\eqref{eq:x}.\n[^n]: note\nUse[^n].");
        foreach (var replacement in projection.ActiveReplacements.Where(item =>
            item.Kind is MarkdownReplacementKind.EquationReference or
                MarkdownReplacementKind.FootnoteDefinition))
        {
            var visible = projection.ToDisplayRange(replacement.Source);
            Assert.IsGreaterThan(1, visible.Length);
            var spans = Enumerable.Range(visible.Start, visible.Length)
                .Select(index => new TextInteractionSpan(new(index, 1),
                    (index - visible.Start) * 10, (index - visible.Start + 1) * 10));
            var display = new TextInteractionMap(projection.Display, TextSurface.Body,
                [new(visible, new(0, 0, visible.Length * 10, 20), spans)]);

            var bridge = new MarkdownProjectionInteractionMap(projection, display);
            foreach (var span in bridge.Lines[0].Spans)
                Assert.AreEqual(replacement.Source, span.Source);
        }
    }

    [TestMethod]
    public void Clicking_either_half_of_a_folded_image_never_targets_its_url()
    {
        var (projection, bridge) = OneLine("A![图](local.png)B");
        var image = projection.References.Images.Single();

        Assert.AreEqual(image.Source.Start, bridge.HitTestForEditing(new(11, 10))!.Value.Offset);
        Assert.AreEqual(image.Source.End, bridge.HitTestForEditing(new(19, 10))!.Value.Offset);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Formula_glue_has_no_phantom_step_after_mapping_past_hidden_bold_markers(bool literalSpaces)
    {
        var source = literalSpaces ? "**公式** $x$ 仍然" : "**公式**$x$仍然";
        var projection = Project(source, 27);
        var math = projection.MathSpans.Single();
        var displayMath = projection.ToDisplayRange(math.Source);
        var spans = new List<TextInteractionSpan>();
        var x = 0d;
        for (var offset = 0; offset < projection.Display.Length;)
        {
            if (offset == displayMath.Start)
            {
                if (!literalSpaces) x += 4;
                spans.Add(new(displayMath, x, x + 60));
                x += literalSpaces ? 60 : 64;
                offset = displayMath.End;
            }
            else
            {
                var advance = projection.Display.Text[offset] == ' ' ? 5 : 16;
                spans.Add(new(new(offset++, 1), x, x + advance));
                x += advance;
            }
        }
        var display = new TextInteractionMap(projection.Display, TextSurface.Body,
            [new(projection.Display.FullRange, new(0, 0, x, 24), spans)]);
        var bridge = new MarkdownProjectionInteractionMap(projection, display);
        var before = new TextCaret(TextSurface.Body, projection.Source.Version, math.Source.Start,
            CaretAffinity.Upstream);
        var after = bridge.MoveHorizontal(before, 1)!.Value;

        Assert.AreEqual(math.Source.End, after.Offset);
        Assert.AreEqual(math.Source.Start,
            bridge.MoveHorizontal(after with { Affinity = CaretAffinity.Downstream }, -1)!.Value.Offset);
        var afterNext = bridge.MoveHorizontal(after, 1)!.Value;
        Assert.AreEqual(math.Source.End + 1, afterNext.Offset);
        Assert.AreEqual(literalSpaces ? " " : "仍",
            TextEditingOperations.SelectedText(projection.Source, new(after, afterNext)));
        Assert.AreEqual(source, projection.Source.Text);
    }

    private static MarkdownEditProjection Project(string source, long version = 0) =>
        MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(new SourceTextSnapshot(source, version)));

    private static (MarkdownEditProjection Projection, MarkdownProjectionInteractionMap Interaction) OneLine(
        string source, long version = 0)
    {
        var projection = Project(source, version);
        return (projection, OneLine(projection).Interaction);
    }

    private static (TextInteractionMap DisplayMap, MarkdownProjectionInteractionMap Interaction) OneLine(
        MarkdownEditProjection projection)
    {
        var spans = Enumerable.Range(0, projection.Display.Length)
            .Select(index => new TextInteractionSpan(new(index, 1), index * 10, (index + 1) * 10));
        var map = new TextInteractionMap(projection.Display, TextSurface.Body,
            [new(projection.Display.FullRange, new(0, 0, projection.Display.Length * 10, 20), spans)]);
        return (map, new MarkdownProjectionInteractionMap(projection, map));
    }
}
