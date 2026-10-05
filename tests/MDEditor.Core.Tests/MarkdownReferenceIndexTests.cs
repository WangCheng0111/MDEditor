using MDEditor.Core.Markdown;
using MDEditor.Core.Text;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MarkdownReferenceIndexTests
{
    [TestMethod]
    public void Footnotes_number_in_first_reference_order_and_follow_edited_definitions()
    {
        var source = new SourceTextSnapshot(
            "先[^b]，再[^a]和[^b]。\n\n[^a]: 甲\n[^b]: 乙", 1);
        var projection = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(source));
        Assert.AreEqual("先¹，再²和¹。\n\n² 甲\n¹ 乙", projection.Display.Text);
        Assert.AreEqual(2, projection.References.Footnotes.Length);
        Assert.AreEqual("B", projection.References.Footnotes[0].Identifier);
        Assert.AreEqual(2, projection.References.Footnotes[0].References.Length);

        var changed = new SourceTextSnapshot(source.Text.Replace("[^b]: 乙", "[^b]: 改"), 2);
        var updated = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(changed));
        Assert.IsTrue(updated.Display.Text.Contains("¹ 改"));
        Assert.AreEqual("先¹，再²和¹。", updated.Display.Text.Split('\n')[0]);
    }

    [TestMethod]
    public void Equation_references_update_when_labels_move_or_disappear()
    {
        var source = new SourceTextSnapshot(
            "见\\eqref{eq:b}和\\ref{eq:a}。\n\n$$a+b\\label{eq:a}$$\n\n$$c+d\\label{eq:b}$$", 1);
        var projection = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(source));
        Assert.IsTrue(projection.Display.Text.StartsWith("见(2)和1。"));
        Assert.AreEqual("a+b", projection.References.LayoutContent(
            projection.MathSpans.Single(span => span.GetContent(source).Contains("eq:a"))));
        var missing = new SourceTextSnapshot(source.Text.Replace("\\label{eq:b}", ""), 2);
        var updated = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(missing));
        Assert.IsTrue(updated.Display.Text.StartsWith("见(??)和1。"));
        Assert.IsTrue(updated.References.Issues.Any(issue => issue.Message.Contains("eq:b")));
    }

    [TestMethod]
    public void Reordering_numbered_equations_updates_all_visible_cross_references()
    {
        const string first = "$$a\\label{eq:a}$$";
        const string second = "$$b\\label{eq:b}$$";
        var original = new SourceTextSnapshot($"{first}\n\n{second}\n\n\\eqref{{eq:a}} \\eqref{{eq:b}}", 1);
        var moved = new SourceTextSnapshot($"{second}\n\n{first}\n\n\\eqref{{eq:a}} \\eqref{{eq:b}}", 2);

        Assert.IsTrue(MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(original))
            .Display.Text.EndsWith("(1) (2)", StringComparison.Ordinal));
        Assert.IsTrue(MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(moved))
            .Display.Text.EndsWith("(2) (1)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Removing_a_footnote_definition_preserves_the_reference_as_an_explicit_error()
    {
        var source = new SourceTextSnapshot("正文[^missing]。", 1);
        var projection = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(source));

        Assert.AreEqual("正文[?]。", projection.Display.Text);
        Assert.IsTrue(projection.References.Issues.Single().Message.Contains("没有定义"));
        Assert.AreEqual(source.Text, projection.Source.Text);
    }

    [TestMethod]
    public void Image_is_one_object_atom_but_copy_and_delete_keep_original_source()
    {
        var source = new SourceTextSnapshot("图 ![替代文字](Assets/Tiles/AppList.targetsize-48.png) 后", 1);
        var projection = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(source));
        var image = projection.References.Images.Single();
        Assert.AreEqual("替代文字", image.Alternative);
        Assert.AreEqual("Assets/Tiles/AppList.targetsize-48.png", image.Target);
        Assert.AreEqual("图 \uFFFC 后", projection.Display.Text);
        var visible = projection.ToDisplayRange(image.Source);
        Assert.AreEqual(image.Source, projection.ToSourceRange(visible));
        var revealed = projection.RevealAt(image.Source.Start);
        Assert.AreEqual(source.Text, revealed.Display.Text);
    }

    [TestMethod]
    public void Folded_image_caret_moves_only_between_its_two_source_edges()
    {
        var source = new SourceTextSnapshot("A![图](Assets/Tiles/StoreDisplay-300.png)B", 1);
        var projection = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(source));
        var image = projection.References.Images.Single();
        var visible = projection.ToDisplayRange(image.Source);

        Assert.AreEqual(image.Source.Start, projection.ToNavigationSourceOffset(
            visible.Start, ProjectionBoundary.AfterHidden));
        Assert.AreEqual(image.Source.End, projection.ToNavigationSourceOffset(
            visible.End, ProjectionBoundary.BeforeHidden));
        Assert.AreEqual(new SourceRange(image.Source.Start, 0), projection.ToSourceRange(new(visible.Start, 0)));
        Assert.AreEqual(new SourceRange(image.Source.End, 0), projection.ToSourceRange(new(visible.End, 0)));
        Assert.AreEqual(image.Source.Start, projection.MoveSourceCaret(image.Source.End, -1));
        Assert.AreEqual(image.Source.End, projection.MoveSourceCaret(image.Source.Start, 1));
    }

    [TestMethod]
    public void Multiple_folded_images_keep_independent_caret_edges()
    {
        var source = new SourceTextSnapshot("甲![一](one.png)乙![二](two.png)丙", 1);
        var projection = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(source));

        Assert.AreEqual(2, projection.References.Images.Length);
        foreach (var image in projection.References.Images)
        {
            var visible = projection.ToDisplayRange(image.Source);
            Assert.AreEqual(image.Source.Start, projection.ToNavigationSourceOffset(
                visible.Start, ProjectionBoundary.AfterHidden));
            Assert.AreEqual(image.Source.End, projection.ToNavigationSourceOffset(
                visible.End, ProjectionBoundary.BeforeHidden));
        }
    }

    [TestMethod]
    public void Reference_style_image_resolves_its_target_without_changing_source()
    {
        var source = new SourceTextSnapshot(
            "![图标][asset]\n\n[asset]: Assets/Tiles/StoreDisplay-300.png", 1);
        var projection = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(source));

        var image = projection.References.Images.Single();
        Assert.AreEqual("图标", image.Alternative);
        Assert.AreEqual("Assets/Tiles/StoreDisplay-300.png", image.Target);
        Assert.IsTrue(projection.Display.Text.StartsWith("\uFFFC", StringComparison.Ordinal));
        Assert.AreEqual(source.Text, projection.Source.Text);
    }

    [TestMethod]
    public void Code_and_math_syntax_do_not_create_false_references()
    {
        var source = new SourceTextSnapshot(
            "`[^fake]` 和 $\\ref{eq:fake}$\n\n```md\n![x](missing.png)\n[^bad]: 123\n```", 1);
        var projection = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(source));
        Assert.AreEqual(0, projection.References.Footnotes.Length);
        Assert.AreEqual(0, projection.References.Images.Length);
        Assert.AreEqual(0, projection.References.Issues.Length);
    }
}
