using System.Globalization;
using System.Text;

namespace MDEditor.Typesetting.Typography;

/// <summary>Document-space typography. Zoom and raster DPI never alter these metrics.</summary>
public sealed record TypographyPreset
{
    public static TypographyPreset Balanced { get; } = new("balanced", "均衡", "Microsoft YaHei", 1.30f);
    public static TypographyPreset Book { get; } = new("book", "书刊", "SimSun", 1.38f);
    public static IReadOnlyList<TypographyPreset> All { get; } = [Balanced, Book];

    public string Id { get; }
    public string Name { get; }
    public string CjkFamily { get; }
    public float LineAdvance { get; }
    public const string CodeFamily = "Cascadia Code";
    public const string MathFamily = "Cambria Math";
    public const float InlineMathEm = 20;
    public const float DisplayMathEm = 26;

    private TypographyPreset(string id, string name, string cjkFamily, float lineAdvance)
    {
        Id = id; Name = name; CjkFamily = cjkFamily; LineAdvance = lineAdvance;
    }

    public string TextFamily(string sourceFamily, bool code)
    {
        if (code) return CodeFamily;
        // The ligature and bidi diagnostic specimens deliberately retain their own faces.
        if (sourceFamily is "Gabriola" or "Segoe UI") return sourceFamily;
        return this == Book ? "Cambria" : sourceFamily;
    }

    public float HeadingScale(int level) => level switch
    {
        1 => 2f, 2 => 1.5f, 3 => 1.25f, 4 => 1f, 5 => 0.875f,
        6 => 0.85f, _ => 1f
    };
}

public readonly record struct FontOverride(int Start, int Length);

/// <summary>
/// Font overrides are whole Unicode grapheme clusters. Latin, Arabic, Hebrew, emoji and
/// unknown scripts remain with DirectWrite so shaping/fallback never splits a cluster.
/// </summary>
public static class ScriptFontPolicy
{
    public static IReadOnlyList<FontOverride> CjkOverrides(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var starts = StringInfo.ParseCombiningCharacters(text);
        var result = new List<FontOverride>();
        for (var i = 0; i < starts.Length; i++)
        {
            var start = starts[i];
            var end = i + 1 < starts.Length ? starts[i + 1] : text.Length;
            if (!IsCjk(text.AsSpan(start, end - start))) continue;
            if (result.Count > 0 && result[^1].Start + result[^1].Length == start)
                result[^1] = result[^1] with { Length = result[^1].Length + end - start };
            else result.Add(new(start, end - start));
        }
        return result;
    }

    private static bool IsCjk(ReadOnlySpan<char> grapheme)
    {
        foreach (var rune in grapheme.EnumerateRunes())
        {
            var scalar = rune.Value;
            if (scalar is >= 0x2E80 and <= 0x9FFF or >= 0xAC00 and <= 0xD7AF or
                >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE6F or
                >= 0xFF01 and <= 0xFF60 or >= 0x20000 and <= 0x323AF or
                >= 0x3040 and <= 0x30FF or >= 0x3100 and <= 0x312F ||
                scalar is 0x3000 or 0x3001 or 0x3002 or 0x2018 or 0x2019 or 0x201C or 0x201D)
                return true;
        }
        return false;
    }
}
