using System.Collections.Immutable;
using MDEditor.Core.Text;

namespace MDEditor.Core.Markdown;

public enum MarkdownMathDelimiter { Dollar, DoubleDollar, Parenthesis, Bracket }
public enum MarkdownMathSyntaxKind { Inline, Display }

/// <summary>All ranges address the unchanged Markdown source, including both delimiters.</summary>
public sealed record MarkdownMathSpan(SourceRange Source, SourceRange Content,
    MarkdownMathSyntaxKind Kind, MarkdownMathDelimiter Delimiter)
{
    public string GetContent(SourceTextSnapshot source) => source.GetText(Content);
}

/// <summary>
/// Recognizes the four TeX-in-Markdown forms without interpreting formula contents as Markdown.
/// Incomplete and escaped constructs are literal; fenced/indented code and code spans are excluded.
/// Display delimiters must occupy their own physical line (or enclose one entire line).
/// </summary>
public static class MarkdownMathSyntax
{
    public static ImmutableArray<MarkdownMathSpan> Parse(SourceTextSnapshot source,
        MarkdownSyntaxDocument? syntax = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        syntax ??= MarkdownSyntaxParser.Parse(source);
        if (!ReferenceEquals(syntax.Source, source))
            throw new ArgumentException("Markdown syntax must own the source snapshot.", nameof(syntax));
        var code = new CodeRangeIndex(syntax.Descendants().Where(node => node.Kind is MarkdownSyntaxKind.CodeSpan or
            MarkdownSyntaxKind.FencedCode or MarkdownSyntaxKind.IndentedCode)
            .Select(node => node.Source).OrderBy(range => range.Start).ToArray());
        var lines = DocumentLineMap.Create(source).Lines;
        var result = ImmutableArray.CreateBuilder<MarkdownMathSpan>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var trimmed = Trim(source.Text, line.Content);
            if (TryDisplay(source.Text, trimmed, code, out var single))
            {
                result.Add(single);
                continue;
            }
            if (TryOpening(source.Text, trimmed, code, out var delimiter))
            {
                var closingText = delimiter == MarkdownMathDelimiter.DoubleDollar ? "$$" : "\\]";
                var consumed = false;
                for (var closing = index + 1; closing < lines.Count; closing++)
                {
                    var end = Trim(source.Text, lines[closing].Content);
                    if (!Matches(source.Text, end, closingText) || code.Intersects(end)) continue;
                    var contentStart = line.Break.End;
                    var contentEnd = lines[closing - 1].Content.End;
                    if (contentEnd > contentStart &&
                        !source.Text.AsSpan(contentStart, contentEnd - contentStart).Trim().IsEmpty &&
                        !IntersectsFencedCode(syntax, new(trimmed.Start, end.End - trimmed.Start)))
                    {
                        result.Add(new(new(trimmed.Start, end.End - trimmed.Start),
                            new(contentStart, contentEnd - contentStart),
                            MarkdownMathSyntaxKind.Display, delimiter));
                        index = closing;
                        consumed = true;
                    }
                    break;
                }
                if (consumed) continue;
            }
            ScanInline(source.Text, line.Content, code, result);
        }
        return result.ToImmutable();
    }

    private static bool TryDisplay(string text, SourceRange line, CodeRangeIndex code,
        out MarkdownMathSpan span)
    {
        span = null!;
        if (line.Length < 5 || code.Intersects(line)) return false;
        var delimiter = MarkdownMathDelimiter.DoubleDollar;
        var opening = "$$";
        var closing = "$$";
        if (!text.AsSpan(line.Start, line.Length).StartsWith(opening))
        {
            delimiter = MarkdownMathDelimiter.Bracket;
            opening = "\\["; closing = "\\]";
        }
        if (!text.AsSpan(line.Start, line.Length).StartsWith(opening) ||
            !text.AsSpan(line.Start, line.Length).EndsWith(closing) ||
            Escaped(text, line.Start)) return false;
        var content = new SourceRange(line.Start + opening.Length,
            line.Length - opening.Length - closing.Length);
        if (content.Length <= 0 || text.AsSpan(content.Start, content.Length).Trim().IsEmpty)
            return false;
        span = new(line, content, MarkdownMathSyntaxKind.Display, delimiter);
        return true;
    }

    private static bool TryOpening(string text, SourceRange line, CodeRangeIndex code,
        out MarkdownMathDelimiter delimiter)
    {
        delimiter = MarkdownMathDelimiter.DoubleDollar;
        if (code.Intersects(line)) return false;
        if (Matches(text, line, "$$")) return true;
        delimiter = MarkdownMathDelimiter.Bracket;
        return Matches(text, line, "\\[") && !Escaped(text, line.Start);
    }

    private static void ScanInline(string text, SourceRange line, CodeRangeIndex code,
        ImmutableArray<MarkdownMathSpan>.Builder result)
    {
        for (var position = line.Start; position < line.End; position++)
        {
            if (code.Intersects(new(position, 1))) continue;
            var parenthesis = position + 1 < line.End && text[position] == '\\' &&
                text[position + 1] == '(' && !Escaped(text, position);
            var dollar = text[position] == '$' && !Escaped(text, position) &&
                (position == line.Start || text[position - 1] != '$') &&
                (position + 1 == line.End || text[position + 1] != '$');
            if (!parenthesis && !dollar) continue;
            var openingLength = parenthesis ? 2 : 1;
            var contentStart = position + openingLength;
            if (contentStart >= line.End || char.IsWhiteSpace(text[contentStart])) continue;
            for (var closing = contentStart + 1; closing < line.End; closing++)
            {
                var closingLength = parenthesis ? 2 : 1;
                if (parenthesis ? closing + 1 >= line.End || text[closing] != '\\' ||
                        text[closing + 1] != ')' || Escaped(text, closing) :
                    text[closing] != '$' || Escaped(text, closing) ||
                        closing + 1 < line.End && text[closing + 1] == '$') continue;
                if (char.IsWhiteSpace(text[closing - 1])) continue;
                var source = new SourceRange(position, closing + closingLength - position);
                if (code.Intersects(source)) continue;
                result.Add(new(source, new(contentStart, closing - contentStart),
                    MarkdownMathSyntaxKind.Inline,
                    parenthesis ? MarkdownMathDelimiter.Parenthesis : MarkdownMathDelimiter.Dollar));
                position = source.End - 1;
                break;
            }
        }
    }

    private static SourceRange Trim(string text, SourceRange range)
    {
        var start = range.Start; var end = range.End;
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return new(start, end - start);
    }

    private static bool Matches(string text, SourceRange range, string value) =>
        range.Length == value.Length && text.AsSpan(range.Start, range.Length).SequenceEqual(value);

    private readonly struct CodeRangeIndex
    {
        private readonly SourceRange[] _ranges;
        private readonly int[] _prefixMaxEnd;

        public CodeRangeIndex(SourceRange[] ranges)
        {
            _ranges = ranges;
            _prefixMaxEnd = new int[ranges.Length];
            for (var index = 0; index < ranges.Length; index++)
                _prefixMaxEnd[index] = Math.Max(index == 0 ? 0 : _prefixMaxEnd[index - 1],
                    ranges[index].End);
        }

        public bool Intersects(SourceRange candidate)
        {
            var low = 0;
            var high = _ranges.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (_prefixMaxEnd[middle] <= candidate.Start) low = middle + 1;
                else high = middle;
            }
            return low < _ranges.Length && _ranges[low].Start < candidate.End;
        }
    }

    private static bool IntersectsFencedCode(MarkdownSyntaxDocument syntax, SourceRange candidate) =>
        syntax.Descendants().Any(node => node.Kind == MarkdownSyntaxKind.FencedCode &&
            node.Source.Start < candidate.End && candidate.Start < node.Source.End);

    private static bool Escaped(string text, int position)
    {
        var backslashes = 0;
        for (var index = position - 1; index >= 0 && text[index] == '\\'; index--) backslashes++;
        return (backslashes & 1) != 0;
    }
}
