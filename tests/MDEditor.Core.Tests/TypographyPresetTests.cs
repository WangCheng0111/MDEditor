using System.Globalization;
using MDEditor.Typesetting.Typography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class TypographyPresetTests
{
    [TestMethod]
    public void Presets_keep_math_and_code_faces_while_changing_text_and_leading()
    {
        Assert.AreEqual("Arial", TypographyPreset.Balanced.TextFamily("Arial", false));
        Assert.AreEqual("Cambria", TypographyPreset.Book.TextFamily("Arial", false));
        Assert.AreEqual("Gabriola", TypographyPreset.Book.TextFamily("Gabriola", false));
        Assert.AreEqual(TypographyPreset.CodeFamily,
            TypographyPreset.Book.TextFamily("Arial", true));
        Assert.AreEqual("Microsoft YaHei", TypographyPreset.Balanced.CjkFamily);
        Assert.AreEqual("SimSun", TypographyPreset.Book.CjkFamily);
        Assert.IsTrue(TypographyPreset.Book.LineAdvance > TypographyPreset.Balanced.LineAdvance);
        Assert.AreEqual(TypographyPreset.Balanced.HeadingScale(1),
            TypographyPreset.Book.HeadingScale(1));
    }

    [TestMethod]
    public void Github_heading_scale_matches_supplied_markdown_styles()
    {
        var expected = new[] { 2f, 1.5f, 1.25f, 1f, 0.875f, 0.85f };
        for (var level = 1; level <= expected.Length; level++)
        {
            Assert.AreEqual(expected[level - 1], TypographyPreset.Balanced.HeadingScale(level));
            Assert.AreEqual(expected[level - 1], TypographyPreset.Book.HeadingScale(level));
        }
    }

    [TestMethod]
    public void Cjk_overrides_cover_whole_graphemes_without_touching_other_scripts()
    {
        const string text = "中a\u0308\u0301文 ffi 👩‍💻 العربية 😀 日本語 𠀀";
        var spans = ScriptFontPolicy.CjkOverrides(text);
        var cjk = string.Concat(spans.Select(span => text.Substring(span.Start, span.Length)));
        Assert.AreEqual("中文日本語𠀀", cjk);
        var boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToHashSet();
        foreach (var span in spans)
        {
            Assert.IsTrue(boundaries.Contains(span.Start));
            Assert.IsTrue(boundaries.Contains(span.Start + span.Length));
        }
        Assert.IsEmpty(ScriptFontPolicy.CjkOverrides("office cafe\u0301 👩‍💻 العربية"));
    }

    [TestMethod]
    public void Consecutive_cjk_punctuation_is_one_override_without_source_changes()
    {
        const string source = "《中文》与 office，测试";
        var spans = ScriptFontPolicy.CjkOverrides(source);
        Assert.AreEqual("《中文》与", source.Substring(spans[0].Start, spans[0].Length));
        Assert.AreEqual("，测试", source.Substring(spans[1].Start, spans[1].Length));
        Assert.IsTrue(spans[0].Start + spans[0].Length <= spans[1].Start);
    }
}
