using System.Collections.Immutable;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MDEditor.Core.Text;

namespace MDEditor.Core.Markdown;

public enum MarkdownSyntaxKind
{
    Document, Paragraph, Heading, Quote, List, ListItem, FencedCode, IndentedCode,
    ThematicBreak, HtmlBlock, Table, TableRow, TableCell, Text, Emphasis, Strong,
    Strikethrough, CodeSpan, Link, Image, TaskMarker, SoftBreak, HardBreak,
    HtmlInline, Entity, Other
}

/// <summary>
/// A parser node with a half-open UTF-16 range into the unchanged source snapshot.
/// Delimiters and trivia remain in the source even when child ranges do not cover them.
/// </summary>
public sealed record MarkdownSyntaxNode(MarkdownSyntaxKind Kind, SourceRange Source,
    ImmutableArray<MarkdownSyntaxNode> Children);

public sealed class MarkdownSyntaxDocument
{
    public SourceTextSnapshot Source { get; }
    public MarkdownSyntaxNode Root { get; }

    internal MarkdownSyntaxDocument(SourceTextSnapshot source, MarkdownSyntaxNode root)
    {
        Source = source;
        Root = root;
    }

    public IEnumerable<MarkdownSyntaxNode> Descendants()
    {
        var stack = new Stack<MarkdownSyntaxNode>();
        for (var index = Root.Children.Length - 1; index >= 0; index--)
            stack.Push(Root.Children[index]);
        while (stack.Count != 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var index = node.Children.Length - 1; index >= 0; index--)
                stack.Push(node.Children[index]);
        }
    }
}

/// <summary>
/// CommonMark plus the agreed GFM syntax extensions: pipe tables, task markers,
/// double-tilde strikethrough and bare URL autolinks. Rendering/HTML policy is separate.
/// This parser does not enable Markdig's unrelated AdvancedExtensions or math parser.
/// </summary>
public static class MarkdownSyntaxParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables(new PipeTableOptions { UseHeaderForColumnCount = true })
        .UseTaskLists()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .UsePreciseSourceLocation()
        .Build();

    public static MarkdownSyntaxDocument Parse(SourceTextSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var parsed = Markdig.Markdown.Parse(source.Text, Pipeline);
        var lines = new Lazy<DocumentLineMap>(() => DocumentLineMap.Create(source));
        var children = ConvertChildren(parsed, source.FullRange, source, lines);
        return new(source, new(MarkdownSyntaxKind.Document, source.FullRange, children));
    }

    private static ImmutableArray<MarkdownSyntaxNode> ConvertChildren(MarkdownObject parent,
        SourceRange parentRange, SourceTextSnapshot source, Lazy<DocumentLineMap> lines)
    {
        var result = ImmutableArray.CreateBuilder<MarkdownSyntaxNode>();
        var tableCellSlots = parent is TableRow ? TableCellSlots(source.Text, parentRange) : [];
        var tableCellIndex = 0;
        var tableRowIndex = 0;
        foreach (var child in Children(parent))
        {
            // Reference definitions are attached to the document as lookup metadata.
            // Their synthetic span is not the definition's source location; the raw
            // definition remains in Source.Text, while reference links have real spans.
            if (child is LinkReferenceDefinitionGroup) continue;
            if (child.GetType() == typeof(ContainerInline))
            {
                result.AddRange(ConvertChildren(child, parentRange, source, lines));
                continue;
            }
            var hasRange = TryRange(child.Span, parentRange, source.Length, out var range);
            if (child is TableRow && parent is Table)
            {
                // Table rows are contiguous physical lines, with the delimiter line
                // between header and first body row. Empty rows can carry a default
                // Markdig span/line pointing at the document start, so derive the
                // physical line from table order rather than trusting row.Line.
                var headerLine = lines.Value.FindLine(parentRange.Start);
                var lineIndex = headerLine + (tableRowIndex == 0 ? 0 : tableRowIndex + 1);
                tableRowIndex++;
                if (lineIndex < lines.Value.Count)
                {
                    var line = lines.Value.Lines[lineIndex].Content;
                    var containerPrefix = parentRange.Start - lines.Value.Lines[headerLine].Content.Start;
                    var start = Math.Max(parentRange.Start, line.Start + containerPrefix);
                    var end = Math.Min(parentRange.End, line.End);
                    if (start <= end) { range = new(start, end - start); hasRange = true; }
                }
            }
            if (child is TableCell)
            {
                if (!hasRange && tableCellIndex < tableCellSlots.Length)
                { range = tableCellSlots[tableCellIndex]; hasRange = true; }
                else if (!hasRange)
                { range = new(parentRange.End, 0); hasRange = true; }
                tableCellIndex++;
            }
            if (!hasRange)
            {
                // Some parser bookkeeping nodes have no source span. Keep any real
                // descendants instead of inventing coordinates for the missing node.
                result.AddRange(ConvertChildren(child, parentRange, source, lines));
                continue;
            }
            // Markdig reports only the opening fence for an unclosed fenced block.
            // CommonMark treats the rest of its containing block as code, so the
            // syntax node must also cover that source to remain safe for editing.
            if (child is FencedCodeBlock { ClosingFencedCharCount: 0 })
                range = new SourceRange(range.Start, parentRange.End - range.Start);
            result.Add(new(KindOf(child), range, ConvertChildren(child, range, source, lines)));
        }
        return result.ToImmutable();
    }

    private static SourceRange[] TableCellSlots(string text, SourceRange row)
    {
        var separators = new List<int>();
        for (var index = row.Start; index < row.End; index++)
        {
            if (text[index] == '\\' && index + 1 < row.End && text[index + 1] is '|' or '`' or '\\')
            { index++; continue; }
            if (text[index] == '`')
            {
                var openingEnd = index + 1;
                while (openingEnd < row.End && text[openingEnd] == '`') openingEnd++;
                var matchingEnd = FindClosingBackticks(text, openingEnd, openingEnd - index, row.End);
                if (matchingEnd >= 0) { index = matchingEnd - 1; continue; }
                index = openingEnd - 1;
                continue;
            }
            if (text[index] == '|') separators.Add(index);
        }

        var first = row.Start;
        while (first < row.End && char.IsWhiteSpace(text[first])) first++;
        var last = row.End - 1;
        while (last >= first && char.IsWhiteSpace(text[last])) last--;
        var contentStart = row.Start;
        var contentEnd = row.End;
        if (separators.Count > 0 && separators[0] == first)
        { contentStart = separators[0] + 1; separators.RemoveAt(0); }
        if (separators.Count > 0 && separators[^1] == last)
        { contentEnd = separators[^1]; separators.RemoveAt(separators.Count - 1); }

        var slots = new List<SourceRange>(separators.Count + 1);
        var start = contentStart;
        foreach (var separator in separators)
        {
            if (separator < start || separator > contentEnd) continue;
            slots.Add(new(start, separator - start));
            start = separator + 1;
        }
        slots.Add(new(start, Math.Max(0, contentEnd - start)));
        return slots.ToArray();
    }

    private static int FindClosingBackticks(string text, int start, int count, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (text[index] != '`') continue;
            var runEnd = index + 1;
            while (runEnd < end && text[runEnd] == '`') runEnd++;
            if (runEnd - index == count) return runEnd;
            index = runEnd - 1;
        }
        return -1;
    }

    private static IEnumerable<MarkdownObject> Children(MarkdownObject node)
    {
        if (node is ContainerBlock block)
            foreach (var child in block) yield return child;
        if (node is LeafBlock { Inline: { } inline }) yield return inline;
        if (node is ContainerInline container)
            for (var child = container.FirstChild; child is not null; child = child.NextSibling)
                yield return child;
    }

    private static bool TryRange(SourceSpan span, SourceRange parent, int sourceLength,
        out SourceRange range)
    {
        range = default;
        if (span.Start < 0 || span.End < span.Start || span.End >= sourceLength) return false;
        range = new(span.Start, checked(span.End - span.Start + 1));
        return parent.Contains(range);
    }

    private static MarkdownSyntaxKind KindOf(MarkdownObject node) => node switch
    {
        FencedCodeBlock => MarkdownSyntaxKind.FencedCode,
        CodeBlock => MarkdownSyntaxKind.IndentedCode,
        ParagraphBlock => MarkdownSyntaxKind.Paragraph,
        HeadingBlock => MarkdownSyntaxKind.Heading,
        QuoteBlock => MarkdownSyntaxKind.Quote,
        ListBlock => MarkdownSyntaxKind.List,
        ListItemBlock => MarkdownSyntaxKind.ListItem,
        ThematicBreakBlock => MarkdownSyntaxKind.ThematicBreak,
        HtmlBlock => MarkdownSyntaxKind.HtmlBlock,
        Table => MarkdownSyntaxKind.Table,
        TableRow => MarkdownSyntaxKind.TableRow,
        TableCell => MarkdownSyntaxKind.TableCell,
        LiteralInline => MarkdownSyntaxKind.Text,
        EmphasisInline emphasis when emphasis.DelimiterChar == '~' => MarkdownSyntaxKind.Strikethrough,
        EmphasisInline emphasis when emphasis.DelimiterCount >= 2 => MarkdownSyntaxKind.Strong,
        EmphasisInline => MarkdownSyntaxKind.Emphasis,
        CodeInline => MarkdownSyntaxKind.CodeSpan,
        AutolinkInline => MarkdownSyntaxKind.Link,
        LinkInline link when link.IsImage => MarkdownSyntaxKind.Image,
        LinkInline => MarkdownSyntaxKind.Link,
        TaskList => MarkdownSyntaxKind.TaskMarker,
        LineBreakInline lineBreak when lineBreak.IsHard => MarkdownSyntaxKind.HardBreak,
        LineBreakInline => MarkdownSyntaxKind.SoftBreak,
        HtmlInline => MarkdownSyntaxKind.HtmlInline,
        HtmlEntityInline => MarkdownSyntaxKind.Entity,
        _ => MarkdownSyntaxKind.Other
    };
}
