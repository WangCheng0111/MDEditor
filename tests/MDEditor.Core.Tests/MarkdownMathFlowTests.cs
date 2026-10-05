using MDEditor.Core.Text;
using MDEditor.Typesetting.LineBreaking;
using MDEditor.Typesetting.Markdown;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MarkdownMathFlowTests
{
    [TestMethod]
    public void Formula_is_one_unbreakable_box_with_source_and_baseline()
    {
        var atoms = new[]
        {
            new MathFlowAtom(new(0, 4), MathFlowAtomKind.Text, 38, 13, 4),
            new MathFlowAtom(new(4, 1), MathFlowAtomKind.Space, 5, 0, 0, 10, 2),
            new MathFlowAtom(new(5, 15), MathFlowAtomKind.Math, 45, 28, 8),
            new MathFlowAtom(new(20, 1), MathFlowAtomKind.Space, 5, 0, 0, 10, 2),
            new MathFlowAtom(new(21, 4), MathFlowAtomKind.Text, 38, 13, 4)
        };
        var flow = MarkdownMathFlow.Compose(atoms, 92);
        Assert.IsTrue(flow.IsSuccess);
        Assert.IsTrue(flow.Lines.Length >= 2);
        var math = flow.Lines.SelectMany(line => line.Placements).Single(item => item.Kind == MathFlowAtomKind.Math);
        Assert.AreEqual(new SourceRange(5, 15), math.Source);
        Assert.AreEqual(45d, math.Width, 1e-9);
        Assert.IsTrue(flow.Lines.Any(line => line.Baseline - math.Top == 28));
    }

    [TestMethod]
    public void Nonfinal_line_reaches_edge_and_last_line_is_natural()
    {
        var atoms = new List<MathFlowAtom>();
        for (var index = 0; index < 8; index++)
        {
            atoms.Add(new(new(index * 5, 4), index == 3 ? MathFlowAtomKind.Math : MathFlowAtomKind.Text,
                25, index == 3 ? 22 : 12, index == 3 ? 7 : 3));
            if (index < 7) atoms.Add(new(new(index * 5 + 4, 1), MathFlowAtomKind.Space, 5, 0, 0, 8, 2));
        }
        var flow = MarkdownMathFlow.Compose(atoms, 95);
        Assert.IsTrue(flow.IsSuccess);
        Assert.IsTrue(flow.Lines.Length > 1);
        foreach (var line in flow.Lines.SkipLast(1)) Assert.AreEqual(95d, line.Advance, 1e-6);
        Assert.IsTrue(flow.Lines[^1].Advance < 95);
    }

    [TestMethod]
    public void Overwide_formula_reports_infeasible_instead_of_scaling_or_splitting()
    {
        var flow = MarkdownMathFlow.Compose([new(new(0, 8), MathFlowAtomKind.Math, 240, 40, 10)], 100);
        Assert.AreEqual(LineBreakStatus.NoFeasibleBreaks, flow.Status);
        Assert.AreEqual(0, flow.Lines.Length);
    }

    [TestMethod]
    public void One_typed_character_next_to_an_image_box_keeps_valid_flow_indices()
    {
        var before = MarkdownMathFlow.Compose([
            new(new(0, 1), MathFlowAtomKind.Text, 11, 15, 4),
            new(new(1, 48), MathFlowAtomKind.Math, 175, 175, 0)
        ], 1100);
        var after = MarkdownMathFlow.Compose([
            new(new(0, 48), MathFlowAtomKind.Math, 175, 175, 0),
            new(new(48, 1), MathFlowAtomKind.Text, 11, 15, 4)
        ], 1100);

        Assert.IsTrue(before.IsSuccess);
        Assert.IsTrue(after.IsSuccess);
        Assert.AreEqual(2, before.Lines[0].Placements.Length);
        Assert.AreEqual(2, after.Lines[0].Placements.Length);
    }

    [TestMethod]
    public void Image_atom_with_no_text_does_not_throw_when_typing_next_to_it()
    {
        var source = new SourceTextSnapshot("\uFFFC1", 1);
        var image = new MathFlowAtom(new(0, 1), MathFlowAtomKind.Math, 175, 175, 0);
        var digit = new MathFlowAtom(new(1, 1), MathFlowAtomKind.Text, 11, 15, 4);

        Assert.IsFalse(TextSpacingPolicy.IsCjk(""));
        Assert.IsFalse(MarkdownMathSpacing.NeedsFlexibleGap(source, image, "", digit, "1"));
    }

    [TestMethod]
    public void Width_and_source_validation_rejects_impossible_atoms()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new MathFlowAtom(new(0, 1), MathFlowAtomKind.Math, double.NaN, 10, 1));
        Assert.ThrowsExactly<ArgumentException>(() => MarkdownMathFlow.Compose([
            new(new(5, 2), MathFlowAtomKind.Text, 10, 10, 2),
            new(new(6, 2), MathFlowAtomKind.Text, 10, 10, 2)
        ], 100));
    }

    [TestMethod]
    public void Flexible_math_glue_obeys_chinese_punctuation_prohibitions()
    {
        var source = new SourceTextSnapshot("中文，文字$x$中文（注）", 14);
        MathFlowAtom Box(int start, int length, MathFlowAtomKind kind = MathFlowAtomKind.Text) =>
            new(new(start, length), kind, 12, 12, 3);
        Assert.IsFalse(MarkdownMathSpacing.NeedsFlexibleGap(source,
            Box(1, 1), "文", Box(2, 1), "，"));
        Assert.IsTrue(MarkdownMathSpacing.NeedsFlexibleGap(source,
            Box(2, 1), "，", Box(3, 1), "文"));
        Assert.IsTrue(MarkdownMathSpacing.NeedsFlexibleGap(source,
            Box(4, 1), "字", Box(5, 3, MathFlowAtomKind.Math), "x"));
        Assert.IsTrue(MarkdownMathSpacing.NeedsFlexibleGap(source,
            Box(5, 3, MathFlowAtomKind.Math), "x", Box(8, 1), "中"));
        Assert.IsTrue(MarkdownMathSpacing.NeedsFlexibleGap(source,
            Box(9, 1), "文", Box(10, 1), "（"));
        Assert.IsFalse(MarkdownMathSpacing.NeedsFlexibleGap(source,
            Box(10, 1), "（", Box(11, 1), "注"));
    }

    [TestMethod]
    public void Pasting_inline_math_does_not_add_natural_spaces_between_unrelated_chinese_glyphs()
    {
        // A formula elsewhere in the paragraph activates this flow for every Chinese pair.
        var source = new SourceTextSnapshot("$x$“引号”需要；中西文如Windows", 15);
        MathFlowAtom Box(int start, int length = 1) =>
            new(new(start, length), MathFlowAtomKind.Text, 20, 16, 4);
        const double size = 20;
        var need = source.Text.IndexOf("需要", StringComparison.Ordinal);
        var mixed = source.Text.IndexOf("中西", StringComparison.Ordinal);
        foreach (var (start, left, right) in new[]
        {
            (need - 1, "”", "需"), (need, "需", "要"), (mixed, "中", "西")
        })
        {
            var gap = MarkdownMathSpacing.CreateFlexibleGap(source, Box(start), left,
                Box(start + 1), right, size);
            Assert.IsNotNull(gap);
            Assert.AreEqual(0d, gap.Width);
            Assert.AreEqual(size * 0.24, gap.Stretch, 1e-9);
            Assert.AreEqual(0d, gap.Shrink);
        }
        var western = source.Text.IndexOf("Windows", StringComparison.Ordinal);
        var mixedGap = MarkdownMathSpacing.CreateFlexibleGap(source, Box(western - 1), "如",
            Box(western, "Windows".Length), "Windows", size);
        Assert.IsNotNull(mixedGap);
        Assert.AreEqual(size * 0.25, mixedGap.Width, 1e-9);
        Assert.AreEqual(size * 0.25, mixedGap.Stretch, 1e-9);
        Assert.AreEqual(size * 0.125, mixedGap.Shrink, 1e-9);
    }

    [TestMethod]
    public void Inline_formula_keeps_its_own_gap_without_spacing_Chinese_on_the_last_line()
    {
        var source = new SourceTextSnapshot("文$x$需要", 16);
        var before = new MathFlowAtom(new(0, 1), MathFlowAtomKind.Text, 20, 16, 4);
        var formula = new MathFlowAtom(new(1, 3), MathFlowAtomKind.Math, 35, 24, 6);
        var after = new MathFlowAtom(new(4, 1), MathFlowAtomKind.Text, 20, 16, 4);
        var final = new MathFlowAtom(new(5, 1), MathFlowAtomKind.Text, 20, 16, 4);
        var beforeGap = MarkdownMathSpacing.CreateFlexibleGap(source, before, "文", formula, "x", 20);
        var afterGap = MarkdownMathSpacing.CreateFlexibleGap(source, formula, "x", after, "需", 20);
        var chineseGap = MarkdownMathSpacing.CreateFlexibleGap(source, after, "需", final, "要", 20);

        Assert.IsNotNull(beforeGap);
        Assert.IsNotNull(afterGap);
        Assert.IsNotNull(chineseGap);
        Assert.AreEqual(4d, beforeGap.Width);
        Assert.AreEqual(4d, afterGap.Width);
        Assert.AreEqual(0d, chineseGap.Width);
        var flow = MarkdownMathFlow.Compose([before, beforeGap, formula, afterGap, after, chineseGap, final], 200);
        Assert.IsTrue(flow.IsSuccess);
        Assert.AreEqual(1, flow.Lines.Length);
        Assert.AreEqual(103d, flow.Lines[0].Advance, 1e-9);
    }

    [TestMethod]
    public void Markdown_body_keeps_literal_spaces_around_math_without_adding_more()
    {
        var source = new SourceTextSnapshot("设 $x$ 是", 17);
        var before = new MathFlowAtom(new(0, 1), MathFlowAtomKind.Text, 20, 16, 4);
        var firstSpace = new MathFlowAtom(new(1, 1), MathFlowAtomKind.Space, 5, 0, 0, 8, 2);
        var formula = new MathFlowAtom(new(2, 3), MathFlowAtomKind.Math, 35, 24, 6);
        var secondSpace = new MathFlowAtom(new(5, 1), MathFlowAtomKind.Space, 5, 0, 0, 8, 2);
        var after = new MathFlowAtom(new(6, 1), MathFlowAtomKind.Text, 20, 16, 4);

        Assert.IsNull(MarkdownMathSpacing.CreateFlexibleGap(source, before, "设", firstSpace, " ", 20));
        Assert.IsNull(MarkdownMathSpacing.CreateFlexibleGap(source, firstSpace, " ", formula, "x", 20));
        Assert.IsNull(MarkdownMathSpacing.CreateFlexibleGap(source, formula, "x", secondSpace, " ", 20));
        Assert.IsNull(MarkdownMathSpacing.CreateFlexibleGap(source, secondSpace, " ", after, "是", 20));
        var flow = MarkdownMathFlow.Compose([before, firstSpace, formula, secondSpace, after], 200);
        Assert.IsTrue(flow.IsSuccess);
        Assert.AreEqual(1, flow.Lines.Length);
        Assert.AreEqual(85d, flow.Lines[0].Advance, 1e-9);
    }

    [TestMethod]
    public void Chinese_math_flow_prefers_a_fuller_line_over_half_space_like_justification()
    {
        var source = new SourceTextSnapshot(new string('文', 80), 18);
        var atoms = new List<MathFlowAtom>();
        for (var index = 0; index < source.Length; index++)
        {
            var current = new MathFlowAtom(new(index, 1), MathFlowAtomKind.Text, 18, 15, 3);
            if (atoms.Count > 0)
            {
                var previous = atoms[^1];
                var gap = MarkdownMathSpacing.CreateFlexibleGap(source, previous, "文", current, "文", 18);
                Assert.IsNotNull(gap);
                atoms.Add(gap);
            }
            atoms.Add(current);
        }

        var flow = MarkdownMathFlow.Compose(atoms, 1150);
        Assert.IsTrue(flow.IsSuccess);
        Assert.AreEqual(2, flow.Lines.Length);
        var first = flow.Lines[0];
        Assert.IsTrue(first.Source.Length >= 62, $"First line kept only {first.Source.Length} of 80 Chinese glyphs.");
        Assert.IsTrue(first.Placements.Where(p => p.Kind == MathFlowAtomKind.Gap)
            .All(p => p.Width < 2), "Visible intercharacter gaps should stay well below half an em.");
    }
}
