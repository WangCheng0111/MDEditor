using System.Globalization;
using System.Text;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;
using MDEditor.Typesetting.LineBreaking;

namespace MDEditor.Typesetting.Typography;

public enum CjkProhibitionLevel { Basic, Strict }
public enum CjkSpacingKind { WordSpace, InterCharacter, MixedScript, Punctuation }

/// <summary>Immutable horizontal Chinese typography profile. Legacy preserves the step 7 baseline.</summary>
public sealed class CjkTypographyOptions
{
    public static CjkTypographyOptions Legacy { get; } = new(enabled: false);
    public static CjkTypographyOptions Refined { get; } = new();
    public bool Enabled { get; }
    public CjkProhibitionLevel ProhibitionLevel { get; }
    public bool CompressPunctuation { get; }
    public double MixedNaturalEm { get; }
    public double MixedMinimumEm { get; }
    public double MixedMaximumEm { get; }
    public double InterCharacterMaximumEm { get; }
    public double InkClearanceEm { get; }
    public CjkTypographyOptions(bool enabled = true, CjkProhibitionLevel prohibitionLevel = CjkProhibitionLevel.Basic,
        bool compressPunctuation = true, double mixedNaturalEm = 0.25, double mixedMinimumEm = 0.125,
        double mixedMaximumEm = 0.5, double interCharacterMaximumEm = 0.24, double inkClearanceEm = 0.04)
    {
        if (!Enum.IsDefined(prohibitionLevel)) throw new ArgumentOutOfRangeException(nameof(prohibitionLevel));
        foreach (var value in new[] { mixedNaturalEm, mixedMinimumEm, mixedMaximumEm, interCharacterMaximumEm, inkClearanceEm })
            if (!double.IsFinite(value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(value));
        if (mixedMinimumEm > mixedNaturalEm || mixedNaturalEm > mixedMaximumEm)
            throw new ArgumentException("Mixed-script spacing must satisfy minimum <= natural <= maximum.");
        Enabled = enabled; ProhibitionLevel = prohibitionLevel; CompressPunctuation = compressPunctuation;
        MixedNaturalEm = mixedNaturalEm; MixedMinimumEm = mixedMinimumEm; MixedMaximumEm = mixedMaximumEm;
        InterCharacterMaximumEm = interCharacterMaximumEm; InkClearanceEm = inkClearanceEm;
    }
}

public readonly record struct ClusterInkMargins
{
    public SourceRange Source { get; }
    public double Leading { get; }
    public double Trailing { get; }
    public ClusterInkMargins(SourceRange source, double leading, double trailing)
    {
        if (source.Length == 0) throw new ArgumentException("Ink metrics require a nonempty cluster.", nameof(source));
        LayoutValidation.Nonnegative(leading, nameof(leading)); LayoutValidation.Nonnegative(trailing, nameof(trailing));
        Source = source; Leading = leading; Trailing = trailing;
    }
}

public static class CjkTypographyRules
{
    private const string Opening = "（《〈【〔［｛「『“‘﹙﹛﹝([{‹«";
    private const string Closing = "）》〉】〕］｝」』”’﹚﹜﹞)]}›»";
    private const string Stops = "，。、；：？！﹐﹑﹒﹔﹕﹖﹗";
    private const string OtherNoStart = ",.;:!?％%‰℃℉°·／/‐‑–";

    public static bool IsOpening(string text) => Single(text, Opening);
    public static bool IsClosing(string text) => Single(text, Closing);
    public static bool IsStop(string text) => Single(text, Stops);
    public static bool IsPunctuation(string text) => IsOpening(text) || IsClosing(text) || IsStop(text);
    private static bool Single(string text, string set) => text.Length == 1 && set.Contains(text[0]);
    private static Rune First(string text) => text.EnumerateRunes().FirstOrDefault();
    private static Rune LastBase(string text) => text.EnumerateRunes().LastOrDefault(r =>
        Rune.GetUnicodeCategory(r) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark));
    private static bool Western(Rune rune) => rune.Value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' ||
        rune.Value is >= 0xC0 and <= 0x24F && Rune.IsLetter(rune) ||
        rune.Value is >= 0x1E00 and <= 0x1EFF && Rune.IsLetter(rune);
    public static bool IsMixedBoundary(string left, string right) =>
        TextSpacingPolicy.IsCjk(left) && Western(First(right)) ||
        Western(LastBase(left)) && TextSpacingPolicy.IsCjk(right);
    public static bool IsInterCharacterBoundary(string left, string right) =>
        TextSpacingPolicy.IsCjk(left) && TextSpacingPolicy.IsCjk(right) ||
        IsClosing(left) && TextSpacingPolicy.IsCjk(right) || IsStop(left) && TextSpacingPolicy.IsCjk(right) ||
        TextSpacingPolicy.IsCjk(left) && IsOpening(right);

    /// <summary>Additional Chinese tailoring. Platform opportunities and glyph/grapheme safety are still required.</summary>
    public static bool MayBreak(SourceTextSnapshot source, int boundary, CjkTypographyOptions options)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(options);
        if (boundary < 0 || boundary > source.Length) throw new ArgumentOutOfRangeException(nameof(boundary));
        if (!options.Enabled || boundary == 0 || boundary == source.Length) return true;
        var left = boundary - 1; var right = boundary;
        while (left >= 0 && source.Text[left] == ' ') left--;
        while (right < source.Length && source.Text[right] == ' ') right++;
        if (left < 0 || right == source.Length) return true;
        var a = source.Text[left]; var b = source.Text[right];
        if (Opening.Contains(a) || Closing.Contains(b) || Stops.Contains(b) || OtherNoStart.Contains(b)) return false;
        if (options.ProhibitionLevel == CjkProhibitionLevel.Strict &&
            (b is '…' or '—' || a is '/' or '／')) return false;
        if (right == left + 1 && a == b && (a is '…' or '—'))
        {
            var start = left;
            while (start > 0 && source.Text[start - 1] == a) start--;
            if ((left - start) % 2 == 0) return false; // Treat each two-character mark as a single unit.
        }
        if (right == left + 1 && (char.IsAsciiDigit(a) && char.IsAsciiDigit(b) ||
            char.IsAsciiDigit(a) && "%％‰°℃℉".Contains(b) ||
            "+-−±¥￥$€£".Contains(a) && char.IsAsciiDigit(b))) return false;
        return true;
    }

    public static bool LineBoundariesValid(SourceTextSnapshot source, SourceRange paragraph, SourceRange line,
        CjkTypographyOptions options)
    {
        if (!paragraph.Contains(line)) return false;
        return (line.Start == paragraph.Start || source.GetText(new(paragraph.Start, line.Start - paragraph.Start)).All(c => c == ' ') ||
                MayBreak(source, line.Start, options)) &&
            (line.End == paragraph.End || source.GetText(new(line.End, paragraph.End - line.End)).All(c => c == ' ') ||
                MayBreak(source, line.End, options));
    }
}
