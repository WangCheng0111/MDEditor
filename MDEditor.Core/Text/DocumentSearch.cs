using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MDEditor.Core.Text;

public sealed record DocumentSearchOptions(bool MatchCase = false, bool WholeWord = false,
    bool RegularExpression = false);

/// <summary>Matches always use the original, immutable UTF-16 source, including hidden Markdown.</summary>
public sealed record DocumentSearchResult(SourceTextSnapshot Source, string Query,
    DocumentSearchOptions Options, ImmutableArray<SourceRange> Matches,
    bool Truncated = false, string? Error = null)
{
    public int FindIndex(int offset, bool reverse = false)
    {
        if (offset < 0 || offset > Source.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        if (Matches.IsEmpty) return -1;
        var low = 0; var high = Matches.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (Matches[middle].Start < offset) low = middle + 1;
            else high = middle;
        }
        return reverse ? (low > 0 ? low - 1 : Matches.Length - 1) :
            low < Matches.Length ? low : 0;
    }
}

public sealed record DocumentReplacementPlan(SourceRange Range, string Text,
    ImmutableArray<DocumentStyleMarker> Styles, int CaretOffset, int Count);

public static class DocumentSearch
{
    public const int MaximumMatches = 100_000;
    private const int MaximumOutputLength = 16 * 1024 * 1024;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>Ctrl+F/H adopts the current document selection even when find is already open.
    /// Focus inside the search widget keeps its query instead of reusing the old document match.</summary>
    public static string QueryFromSelection(SourceTextSnapshot source, SourceRange? selection,
        string currentQuery, bool searchHasFocus)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(currentQuery);
        if (searchHasFocus || selection is not { Length: > 0 and < 512 } range) return currentQuery;
        var text = source.GetText(range);
        return text.Contains('\r') || text.Contains('\n') ? currentQuery : text;
    }

    public static DocumentSearchResult Find(SourceTextSnapshot source, string query,
        DocumentSearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(query);
        options ??= new();
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Length == 0) return new(source, query, options, []);
        if (query.Length > 4096) return new(source, query, options, [], Error: "搜索表达式超过 4096 字符");
        var matches = ImmutableArray.CreateBuilder<SourceRange>();
        bool Add(int start, int length)
        {
            if (SplitsScalar(source.Text, start) || SplitsScalar(source.Text, start + length) ||
                options.WholeWord && !WholeWord(source.Text, start, start + length)) return true;
            if (matches.Count == MaximumMatches) return false;
            matches.Add(new(start, length));
            return true;
        }
        try
        {
            if (options.RegularExpression)
            {
                var regex = CreateRegex(query, options);
                for (var match = regex.Match(source.Text); match.Success; match = match.NextMatch())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Add(match.Index, match.Length)) return new(source, query, options, matches.ToImmutable(), true);
                }
            }
            else
            {
                var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                for (var offset = 0; offset <= source.Length - query.Length;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var found = source.Text.IndexOf(query, offset, comparison);
                    if (found < 0) break;
                    if (!Add(found, query.Length)) return new(source, query, options, matches.ToImmutable(), true);
                    offset = found + query.Length;
                }
            }
            return new(source, query, options, matches.ToImmutable());
        }
        catch (RegexMatchTimeoutException) { return new(source, query, options, [], Error: "正则搜索超时，请简化表达式"); }
        catch (ArgumentException error) { return new(source, query, options, [], Error: $"正则表达式无效：{error.Message}"); }
    }

    /// <summary>One history edit, with every unaffected style anchor preserved. No stale-source replay.</summary>
    public static DocumentReplacementPlan? PlanReplace(StyledDocumentSnapshot snapshot,
        DocumentSearchResult result, string replacement, int? matchIndex = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!ReferenceEquals(snapshot.Source, result.Source))
            throw new InvalidOperationException("搜索结果已过期，请重新搜索");
        if (result.Error is not null || result.Truncated && matchIndex is null)
            throw new InvalidOperationException(result.Error ?? "结果超过 100000 项，请缩小范围后全部替换");
        if (matchIndex is { } index && (index < 0 || index >= result.Matches.Length))
            throw new ArgumentOutOfRangeException(nameof(matchIndex));
        var ranges = matchIndex is { } selected ? [result.Matches[selected]] : result.Matches;
        if (ranges.IsEmpty) return null;
        var regex = result.Options.RegularExpression ? CreateRegex(result.Query, result.Options) : null;
        var edits = new List<(SourceRange Range, string Text)>();
        long newLength = snapshot.Source.Length;
        foreach (var range in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = regex is null ? replacement : regex.Match(result.Source.Text, range.Start).Result(replacement);
            if (text == result.Source.GetText(range)) continue;
            newLength += (long)text.Length - range.Length;
            if (newLength > MaximumOutputLength) throw new InvalidOperationException("替换后的文档超过 16 Mi 字符限制");
            edits.Add((range, text));
        }
        if (edits.Count == 0) return null;
        var first = edits[0].Range.Start; var last = edits[^1].Range.End;
        var builder = new StringBuilder(); var cursor = first;
        foreach (var edit in edits)
        {
            builder.Append(snapshot.Source.Text.AsSpan(cursor, edit.Range.Start - cursor));
            builder.Append(edit.Text); cursor = edit.Range.End;
        }
        var styles = ImmutableArray.CreateBuilder<DocumentStyleMarker>();
        var editIndex = 0; var delta = 0;
        foreach (var marker in snapshot.Styles)
        {
            while (editIndex < edits.Count && (edits[editIndex].Range.End < marker.Offset ||
                edits[editIndex].Range.End == marker.Offset && edits[editIndex].Range.Length > 0))
            {
                delta += edits[editIndex].Text.Length - edits[editIndex].Range.Length;
                editIndex++;
            }
            if (marker.Offset > 0 && editIndex < edits.Count &&
                edits[editIndex].Range.Start < marker.Offset && marker.Offset < edits[editIndex].Range.End) continue;
            var offset = marker.Offset == 0 ? 0 : marker.Offset + delta;
            if (styles.Count == 0 || styles[^1].Offset != offset) styles.Add(marker with { Offset = offset });
        }
        return new(new(first, last - first), builder.ToString(), styles.ToImmutable(),
            first + builder.Length, edits.Count);
    }

    private static Regex CreateRegex(string query, DocumentSearchOptions options) => new(query,
        RegexOptions.CultureInvariant | RegexOptions.Multiline |
        (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase), RegexTimeout);

    private static bool SplitsScalar(string text, int offset) => offset > 0 && offset < text.Length &&
        char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]);
    private static bool WholeWord(string text, int start, int end) =>
        (start == 0 || !WordAt(text, char.IsLowSurrogate(text[start - 1]) && start > 1 ? start - 2 : start - 1)) &&
        (end == text.Length || !WordAt(text, end));
    private static bool WordAt(string text, int offset) => Rune.TryGetRuneAt(text, offset, out var rune) &&
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
        UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or
        UnicodeCategory.DecimalDigitNumber or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
        UnicodeCategory.ConnectorPunctuation or UnicodeCategory.LetterNumber;
}
