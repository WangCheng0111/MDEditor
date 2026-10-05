using System.Collections.Immutable;
using MDEditor.Core.Text;

namespace MDEditor.Core.Markdown;

public enum MarkdownBlockKind { None, Quote, BulletList, OrderedList, TaskList }

/// <summary>One physical source line's editable container prefix. Ranges are UTF-16 source ranges.</summary>
public sealed record MarkdownBlockLine(int LineIndex, SourceRange Line, SourceRange Prefix,
    SourceRange QuotePrefix, SourceRange ListPrefix, SourceRange TaskMarker,
    MarkdownBlockKind Kind, int QuoteDepth, int ListIndent, char Bullet, char OrderedDelimiter,
    int OrderedNumber, bool TaskChecked)
{
    public SourceRange Content => new(Prefix.End, Line.End - Prefix.End);
    public int ListDepth => Kind is MarkdownBlockKind.BulletList or MarkdownBlockKind.OrderedList or
        MarkdownBlockKind.TaskList ? ListIndent / 2 + 1 : 0;
    public string Marker => Kind switch
    {
        MarkdownBlockKind.BulletList => Bullet.ToString(),
        MarkdownBlockKind.OrderedList => $"{OrderedNumber}{OrderedDelimiter}",
        MarkdownBlockKind.TaskList => TaskChecked ? "☑" : "☐",
        _ => ""
    };
}

/// <summary>
/// Physical-line structure for editing, independent of rendered line wraps. Only recognized
/// container markers are projected; code blocks and thematic breaks remain untouched.
/// </summary>
public sealed class MarkdownBlockStructure
{
    public SourceTextSnapshot Source { get; }
    public DocumentLineMap Lines { get; }
    public ImmutableArray<MarkdownBlockLine> Blocks { get; }

    private MarkdownBlockStructure(SourceTextSnapshot source, MarkdownSyntaxDocument syntax)
    {
        Source = source;
        Lines = DocumentLineMap.Create(source);
        var protectedRanges = syntax.Descendants().Where(node => node.Kind is
            MarkdownSyntaxKind.FencedCode or MarkdownSyntaxKind.IndentedCode or
            MarkdownSyntaxKind.ThematicBreak).Select(node => node.Source).ToArray();
        var listItemStarts = syntax.Descendants().Where(node => node.Kind == MarkdownSyntaxKind.ListItem)
            .Select(node => node.Source.Start).ToHashSet();
        var result = ImmutableArray.CreateBuilder<MarkdownBlockLine>(Lines.Count);
        for (var index = 0; index < Lines.Count; index++)
        {
            var line = Lines.Lines[index].Content;
            var protectedLine = protectedRanges.Any(range => range.Start < line.End && line.Start < range.End);
            result.Add(ParseLine(source.Text, index, line, protectedLine, listItemStarts));
        }
        Blocks = result.MoveToImmutable();
    }

    public static MarkdownBlockStructure Create(SourceTextSnapshot source,
        MarkdownSyntaxDocument? syntax = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        syntax ??= MarkdownSyntaxParser.Parse(source);
        if (!ReferenceEquals(source, syntax.Source))
            throw new ArgumentException("Syntax must belong to this source snapshot.", nameof(syntax));
        return new(source, syntax);
    }

    public MarkdownBlockLine At(int sourceOffset) => Blocks[Lines.FindLine(sourceOffset)];

    private static MarkdownBlockLine ParseLine(string text, int index, SourceRange line, bool protectedLine,
        HashSet<int> listItemStarts)
    {
        var empty = new SourceRange(line.Start, 0);
        if (protectedLine) return new(index, line, empty, empty, empty, empty,
            MarkdownBlockKind.None, 0, 0, '\0', '\0', 0, false);
        var position = line.Start;
        var quotes = 0;
        var quoteEnd = position;
        while (position < line.End)
        {
            var probe = position;
            while (probe < line.End && probe - position < 3 && text[probe] == ' ') probe++;
            if (probe >= line.End || text[probe] != '>') break;
            position = probe + 1;
            if (position < line.End && text[position] is ' ' or '\t') position++;
            quoteEnd = position;
            quotes++;
        }
        var quotePrefix = new SourceRange(line.Start, quoteEnd - line.Start);
        var listStart = position;
        while (position < line.End && text[position] == ' ') position++;
        var indent = position - listStart;
        if (position >= line.End) return QuoteOrPlain(index, line, quotePrefix, quotes);
        var markerStart = position;
        var kind = MarkdownBlockKind.None;
        var bullet = '\0'; var delimiter = '\0'; var number = 0;
        if (text[position] is '-' or '+' or '*')
        {
            bullet = text[position++];
            if (position == line.End || text[position] is ' ' or '\t') kind = MarkdownBlockKind.BulletList;
        }
        else if (char.IsAsciiDigit(text[position]))
        {
            while (position < line.End && position - markerStart < 9 && char.IsAsciiDigit(text[position])) position++;
            if (position < line.End && text[position] is '.' or ')' &&
                (position + 1 == line.End || text[position + 1] is ' ' or '\t'))
            {
                delimiter = text[position++];
                number = int.Parse(text.AsSpan(markerStart, position - markerStart - 1),
                    System.Globalization.CultureInfo.InvariantCulture);
                kind = MarkdownBlockKind.OrderedList;
            }
        }
        if (kind == MarkdownBlockKind.None || !listItemStarts.Contains(markerStart))
            return QuoteOrPlain(index, line, quotePrefix, quotes);
        if (position < line.End && text[position] is ' ' or '\t') position++;
        var listPrefix = new SourceRange(listStart, position - listStart);
        var task = new SourceRange(position, 0);
        var checkedTask = false;
        if (position + 2 < line.End && text[position] == '[' &&
            text[position + 1] is ' ' or 'x' or 'X' && text[position + 2] == ']' &&
            (position + 3 == line.End || text[position + 3] is ' ' or '\t'))
        {
            checkedTask = text[position + 1] is 'x' or 'X';
            var start = position;
            position += 3;
            if (position < line.End && text[position] is ' ' or '\t') position++;
            task = new(start, position - start);
            kind = MarkdownBlockKind.TaskList;
        }
        return new(index, line, new(line.Start, position - line.Start), quotePrefix,
            listPrefix, task, kind, quotes, indent, bullet, delimiter, number, checkedTask);
    }

    private static MarkdownBlockLine QuoteOrPlain(int index, SourceRange line,
        SourceRange quotePrefix, int depth)
    {
        var empty = new SourceRange(quotePrefix.End, 0);
        return new(index, line, quotePrefix, quotePrefix, empty, empty,
            depth == 0 ? MarkdownBlockKind.None : MarkdownBlockKind.Quote,
            depth, 0, '\0', '\0', 0, false);
    }
}
