using System.Collections.Immutable;
using MDEditor.Core.Text;

namespace MDEditor.Typesetting.Hyphenation;

/// <summary>Explicit en-US prose policy. Exclusions are absolute UTF-16 ranges, not display indices.</summary>
public sealed class HyphenationOptions
{
    public static HyphenationOptions None { get; } = new(enabled: false);
    public static HyphenationOptions EnglishUs { get; } = new();
    public bool Enabled { get; }
    public int LeftMinimum { get; }
    public int RightMinimum { get; }
    public int Penalty { get; }
    public string Hyphen { get; }
    public bool CapitalizedWords { get; }
    public ImmutableArray<SourceRange> ExcludedRanges { get; }

    public HyphenationOptions(bool enabled = true, int leftMinimum = 2, int rightMinimum = 3,
        int penalty = 50, string hyphen = "\u2010", bool capitalizedWords = true,
        IEnumerable<SourceRange>? excludedRanges = null)
    {
        if (leftMinimum < 2 || leftMinimum > 64) throw new ArgumentOutOfRangeException(nameof(leftMinimum));
        if (rightMinimum < 3 || rightMinimum > 64) throw new ArgumentOutOfRangeException(nameof(rightMinimum));
        if (penalty < 0 || penalty >= 10000) throw new ArgumentOutOfRangeException(nameof(penalty));
        if (hyphen is not "-" and not "\u2010") throw new ArgumentException("Only one hyphen glyph is supported.", nameof(hyphen));
        Enabled = enabled; LeftMinimum = leftMinimum; RightMinimum = rightMinimum; Penalty = penalty;
        Hyphen = hyphen; CapitalizedWords = capitalizedWords;
        ExcludedRanges = (excludedRanges ?? []).ToArray().ToImmutableArray();
    }
}

/// <summary>A conditional display suffix; the original source range is never enlarged.</summary>
public readonly record struct DiscretionaryLine
{
    public SourceRange Source { get; }
    private readonly string? _suffix;
    public string Suffix => _suffix ?? "";
    public DiscretionaryLine(SourceRange source, string suffix)
    {
        if (suffix is not "" and not "-" and not "\u2010") throw new ArgumentException("Invalid discretionary suffix.", nameof(suffix));
        Source = source; _suffix = suffix;
    }
    public string GetText(SourceTextSnapshot source) => source.GetText(Source) + Suffix;
    public SourceRange MapDisplayRange(SourceRange displayRange)
    {
        if (displayRange.End > Source.Length + Suffix.Length) throw new ArgumentOutOfRangeException(nameof(displayRange));
        if (displayRange.Start < Source.Length && displayRange.End > Source.Length)
            throw new ArgumentException("A glyph cluster crosses the source/generated boundary.", nameof(displayRange));
        return new(checked(Source.Start + Math.Min(displayRange.Start, Source.Length)),
            Math.Max(0, Math.Min(displayRange.End, Source.Length) - Math.Min(displayRange.Start, Source.Length)));
    }
}
