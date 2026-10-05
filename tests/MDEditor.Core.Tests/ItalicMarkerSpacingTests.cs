using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Typography;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class ItalicMarkerSpacingTests
{
    [TestMethod]
    public void Only_upright_emphasis_markers_after_italic_ink_are_corrected()
    {
        Assert.IsTrue(ItalicMarkerSpacing.IsBoundary('*', true, false));
        Assert.IsTrue(ItalicMarkerSpacing.IsBoundary('_', true, false));
        Assert.IsFalse(ItalicMarkerSpacing.IsBoundary('*', false, false));
        Assert.IsFalse(ItalicMarkerSpacing.IsBoundary('*', true, true));
        Assert.IsFalse(ItalicMarkerSpacing.IsBoundary(' ', true, false));
        Assert.IsFalse(ItalicMarkerSpacing.IsBoundary('文', true, false));
    }

    [TestMethod]
    public void Correction_accounts_for_ink_overhang_and_scales_with_the_font()
    {
        Assert.AreEqual(4.6, ItalicMarkerSpacing.AdditionalAdvance(23, 20, 20), 1e-9);
        Assert.AreEqual(9.2, ItalicMarkerSpacing.AdditionalAdvance(46, 40, 40), 1e-9);
        Assert.AreEqual(0, ItalicMarkerSpacing.AdditionalAdvance(18, 22, 20));
        Assert.AreEqual(2.6, ItalicMarkerSpacing.AdditionalAdvance(23, 20, 20, 2), 1e-9);
        Assert.AreEqual(0, ItalicMarkerSpacing.AdditionalAdvance(23, 20, 20, 5));
    }

    [TestMethod]
    public void Revealing_and_folding_chinese_emphasis_never_inserts_spaces_or_changes_source_offsets()
    {
        var source = new SourceTextSnapshot("前*强调*后 $x+1$", 7);
        var revealed = MarkdownRichTextProjection.Create(source, 3);
        Assert.AreEqual(source.Text, revealed.Text.Display.Text);
        Assert.AreSame(source, revealed.Text.Source);
        Assert.AreEqual(new SourceRange(2, 2), revealed.Styles.Single().Display);
        Assert.AreEqual(MarkdownVisualStyle.Emphasis, revealed.Styles.Single().Style);
        Assert.AreEqual(4, revealed.Text.ToSourceOffset(4, ProjectionBoundary.AfterHidden));
        var folded = MarkdownRichTextProjection.Create(source);
        Assert.AreEqual("前强调后 $x+1$", folded.Text.Display.Text);
        Assert.AreEqual("前*强调*后 $x+1$", source.Text);
    }

    [TestMethod]
    public void Invalid_ink_metrics_cannot_reach_native_character_spacing()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ItalicMarkerSpacing.AdditionalAdvance(double.NaN, 0, 20));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ItalicMarkerSpacing.AdditionalAdvance(0, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ItalicMarkerSpacing.AdditionalAdvance(0, 0, 20, -1));
    }
}
