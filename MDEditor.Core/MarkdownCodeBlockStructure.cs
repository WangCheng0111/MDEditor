using System.Collections.Immutable;
using MDEditor.Core.Text;

namespace MDEditor.Core.Markdown;

public enum MarkdownCodeLineRole { OpeningFence, Content, ClosingFence, IndentedContent }

/// <summary>One physical source line belonging to a CommonMark code block.</summary>
public sealed record MarkdownCodeLine(int BlockIndex, int LineIndex, SourceRange Line,
    SourceRange OuterPrefix, SourceRange Content, MarkdownCodeLineRole Role,
    string Language, int QuoteDepth);

public sealed record MarkdownCodeBlock(SourceRange Source, string Language, char Fence,
    int FenceLength, bool IsClosed, ImmutableArray<MarkdownCodeLine> Lines);

/// <summary>Fenced/indented code boundaries from the syntax tree, including unclosed fences.</summary>
public sealed class MarkdownCodeBlockStructure
{
    public SourceTextSnapshot Source { get; }
    public DocumentLineMap LineMap { get; }
    public ImmutableArray<MarkdownCodeBlock> Blocks { get; }
    public ImmutableArray<MarkdownCodeLine> Lines { get; }

    private MarkdownCodeBlockStructure(MarkdownSyntaxDocument syntax)
    {
        Source = syntax.Source;
        var map = LineMap = DocumentLineMap.Create(Source);
        var found = new List<(MarkdownSyntaxNode Node, int Quotes)>();
        void Visit(MarkdownSyntaxNode node, int quotes)
        {
            if (node.Kind == MarkdownSyntaxKind.Quote) quotes++;
            if (node.Kind is MarkdownSyntaxKind.FencedCode or MarkdownSyntaxKind.IndentedCode)
                found.Add((node, quotes));
            foreach (var child in node.Children) Visit(child, quotes);
        }
        Visit(syntax.Root, 0);
        found.Sort((a, b) => a.Node.Source.Start.CompareTo(b.Node.Source.Start));
        var blocks = ImmutableArray.CreateBuilder<MarkdownCodeBlock>();
        var lines = ImmutableArray.CreateBuilder<MarkdownCodeLine>();
        foreach (var (node, quotes) in found)
        {
            if (node.Source.Length == 0) continue;
            var first = map.FindLine(node.Source.Start);
            var last = map.FindLine(node.Source.End - 1);
            var opening = map.Lines[first].Content;
            var openAt = Math.Max(node.Source.Start, SkipQuotePrefix(Source.Text, opening, quotes));
            if (node.Kind == MarkdownSyntaxKind.IndentedCode)
            {
                var items = ImmutableArray.CreateBuilder<MarkdownCodeLine>();
                for (var index = first; index <= last; index++)
                {
                    var line = map.Lines[index].Content;
                    var contentStart = SkipQuotePrefix(Source.Text, line, quotes);
                    var prefix = new SourceRange(line.Start, contentStart - line.Start);
                    var item = new MarkdownCodeLine(blocks.Count, index, line, prefix,
                        new(contentStart, line.End - contentStart),
                        MarkdownCodeLineRole.IndentedContent, "", quotes);
                    items.Add(item); lines.Add(item);
                }
                blocks.Add(new(new(map.Lines[first].Content.Start,
                    node.Source.End - map.Lines[first].Content.Start), "", '\0', 0, true,
                    items.ToImmutable()));
                continue;
            }
            if (!TryFence(Source.Text, opening, openAt, '\0', 3, closing: false,
                out var fence, out var length, out var afterFence)) continue;
            var info = Source.Text.AsSpan(afterFence, opening.End - afterFence).Trim();
            var languageEnd = 0;
            while (languageEnd < info.Length && !char.IsWhiteSpace(info[languageEnd])) languageEnd++;
            var language = info[..languageEnd].ToString();
            var ending = map.Lines[last].Content;
            var closeAt = SkipQuotePrefix(Source.Text, ending, quotes);
            var closed = last > first && TryFence(Source.Text, ending, closeAt,
                fence, length, closing: true, out _, out _, out _);
            var contentLines = ImmutableArray.CreateBuilder<MarkdownCodeLine>();
            for (var index = first; index <= last; index++)
            {
                var line = map.Lines[index].Content;
                var prefixEnd = SkipQuotePrefix(Source.Text, line, quotes);
                if (index != first && index != last || index == last && !closed)
                {
                    // A nested fence de-indents only its container indentation. Additional
                    // indentation belongs to the code and is never normalized in Source.
                    var openingIndent = Math.Max(0, openAt - prefixEnd);
                    for (var consumed = 0; consumed < openingIndent && prefixEnd < line.End &&
                        Source.Text[prefixEnd] == ' '; consumed++) prefixEnd++;
                }
                var role = index == first ? MarkdownCodeLineRole.OpeningFence :
                    index == last && closed ? MarkdownCodeLineRole.ClosingFence :
                    MarkdownCodeLineRole.Content;
                var prefix = new SourceRange(line.Start, prefixEnd - line.Start);
                var item = new MarkdownCodeLine(blocks.Count, index, line, prefix,
                    new(prefixEnd, line.End - prefixEnd), role, language, quotes);
                contentLines.Add(item); lines.Add(item);
            }
            var blockEnd = closed ? ending.End : node.Source.End;
            blocks.Add(new(new(opening.Start, blockEnd - opening.Start),
                language, fence, length, closed, contentLines.ToImmutable()));
        }
        Blocks = blocks.ToImmutable();
        Lines = lines.OrderBy(line => line.LineIndex).ToImmutableArray();
    }

    public static MarkdownCodeBlockStructure Create(MarkdownSyntaxDocument syntax) =>
        new(syntax ?? throw new ArgumentNullException(nameof(syntax)));

    public MarkdownCodeLine? At(int sourceOffset)
    {
        if (sourceOffset < 0 || sourceOffset > Source.Length)
            throw new ArgumentOutOfRangeException(nameof(sourceOffset));
        var line = LineMap.FindLine(sourceOffset);
        return Lines.FirstOrDefault(item => item.LineIndex == line);
    }

    private static int SkipQuotePrefix(string text, SourceRange line, int depth)
    {
        var position = line.Start;
        for (var level = 0; level < depth; level++)
        {
            var probe = position;
            while (probe < line.End && probe - position < 3 && text[probe] == ' ') probe++;
            if (probe >= line.End || text[probe] != '>') break;
            position = probe + 1;
            if (position < line.End && text[position] is ' ' or '\t') position++;
        }
        return position;
    }

    private static bool TryFence(string text, SourceRange line, int start, char expected,
        int minimum, bool closing, out char fence, out int length, out int after)
    {
        fence = '\0'; length = 0; after = start;
        while (after < line.End && after - start < 3 && text[after] == ' ') after++;
        if (after >= line.End || text[after] is not ('`' or '~') ||
            expected != '\0' && text[after] != expected) return false;
        fence = text[after];
        while (after < line.End && text[after] == fence) { length++; after++; }
        if (length < minimum) return false;
        return !closing || text.AsSpan(after, line.End - after).Trim().IsEmpty;
    }
}
