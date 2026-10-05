using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using MDEditor.Core.Text;

namespace MDEditor.Typesetting.Hyphenation;

public static class EnglishWordBreaks
{
    public static ImmutableHashSet<int> Find(SourceTextSnapshot source, SourceRange paragraph,
        IEnumerable<int> safeClusterEnds, HyphenationOptions options)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(safeClusterEnds);
        ArgumentNullException.ThrowIfNull(options);
        var text = source.GetText(paragraph);
        foreach (var range in options.ExcludedRanges)
            if (!source.FullRange.Contains(range)) throw new ArgumentException("Exclusion lies outside source.", nameof(options));
        if (!options.Enabled) return [];
        var safe = safeClusterEnds.ToHashSet();
        var graphemes = StringInfo.ParseCombiningCharacters(text).Select(i => paragraph.Start + i).ToHashSet();
        var result = ImmutableHashSet.CreateBuilder<int>();
        static bool Letter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
        static bool Joined(string text, int index)
        {
            if (index < 0 || index >= text.Length) return false;
            if (!Rune.TryGetRuneAt(text, index, out var rune)) return true;
            if (MDEditor.Typesetting.LineBreaking.TextSpacingPolicy.IsCjk(text[index..])) return false;
            var category = Rune.GetUnicodeCategory(rune);
            return Rune.IsLetterOrDigit(rune) || category is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark ||
                rune.Value is '_' or '@' or '/' or '\\' or ':' or '=' or '\'' or 0x2019 or '-' or 0x2010;
        }
        for (var i = 0; i < text.Length;)
        {
            if (!Letter(text[i])) { i++; continue; }
            var start = i; while (i < text.Length && Letter(text[i])) i++;
            var end = i; var word = text[start..end];
            if (Joined(text, start - 1) || Joined(text, end)) continue;
            if (start >= 2 && text[start - 1] == '.' && Letter(text[start - 2]) ||
                end + 1 < text.Length && text[end] == '.' && Letter(text[end + 1])) continue;
            // Do not treat camelCase, ALLCAPS, backtick code, URLs or emails as prose.
            if (word.Skip(1).Any(char.IsUpper) || !options.CapitalizedWords && char.IsUpper(word[0])) continue;
            var tokenStart = start; while (tokenStart > 0 && !char.IsWhiteSpace(text[tokenStart - 1])) tokenStart--;
            var tokenEnd = end; while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd])) tokenEnd++;
            var token = text[tokenStart..tokenEnd];
            if (token.Contains('@') || token.Contains("://", StringComparison.Ordinal) || token.Contains('`') || token.Contains('\\')) continue;
            var range = new SourceRange(paragraph.Start + start, word.Length);
            if (options.ExcludedRanges.Any(r => r.Start < range.End && range.Start < r.End)) continue;
            foreach (var offset in LiangHyphenator.EnglishUs.GetBreakOffsets(word, options.LeftMinimum, options.RightMinimum))
            {
                var absolute = range.Start + offset;
                if (safe.Contains(absolute) && graphemes.Contains(absolute)) result.Add(absolute);
            }
        }
        return result.ToImmutable();
    }
}
