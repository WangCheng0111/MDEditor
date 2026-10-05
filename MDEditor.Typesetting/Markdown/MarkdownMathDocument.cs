using System.Collections.Immutable;
using MDEditor.Core.Markdown;
using MDEditor.Core.Text;

namespace MDEditor.Typesetting.Markdown;

public enum MarkdownMathKind { Inline, Display }
public enum MarkdownBlockKind { Paragraph, DisplayMath }

public abstract record MarkdownInlineNode(SourceRange Source);
public sealed record MarkdownTextNode(SourceRange Source) : MarkdownInlineNode(Source);
public sealed record MarkdownMathNode(SourceRange Source, SourceRange Content, MarkdownMathKind Kind)
    : MarkdownInlineNode(Source)
{
    public string GetContent(SourceTextSnapshot snapshot) => snapshot.GetText(Content);
}

public sealed record MarkdownBlock(SourceRange Source, MarkdownBlockKind Kind,
    ImmutableArray<MarkdownInlineNode> Inlines)
{
    public MarkdownMathNode? DisplayMath => Kind == MarkdownBlockKind.DisplayMath
        ? Inlines.OfType<MarkdownMathNode>().Single()
        : null;
}

/// <summary>
/// A source-preserving view of the four supported Markdown math delimiters. Unknown syntax remains text;
/// no character is deleted and every node points into the original UTF-16 snapshot.
/// </summary>
public sealed class MarkdownMathDocument
{
    public SourceTextSnapshot Source { get; }
    public ImmutableArray<MarkdownBlock> Blocks { get; }
    public ImmutableArray<MarkdownMathNode> Math { get; }

    private MarkdownMathDocument(SourceTextSnapshot source, IEnumerable<MarkdownBlock> blocks)
    {
        Source = source;
        Blocks = blocks.ToImmutableArray();
        Math = Blocks.SelectMany(block => block.Inlines).OfType<MarkdownMathNode>().ToImmutableArray();
        ValidateCoverage();
    }

    /// <summary>Recognizes an atomic formula selection, without treating surrounding prose as a formula.</summary>
    public MarkdownMathNode? FindExactMath(SourceRange selection) =>
        Math.FirstOrDefault(node => node.Source == selection);

    public static MarkdownMathDocument Parse(SourceTextSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var lines = DocumentLineMap.Create(source);
        var math = MarkdownMathSyntax.Parse(source);
        var blocks = new List<MarkdownBlock>();
        var mathIndex = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines.Lines[index].Content;
            if (source.Text.AsSpan(line.Start, line.Length).Trim().IsEmpty) continue;
            while (mathIndex < math.Length && math[mathIndex].Source.End <= line.Start) mathIndex++;
            if (mathIndex < math.Length && math[mathIndex] is { Kind: MarkdownMathSyntaxKind.Display } display &&
                line.Start <= display.Source.Start && display.Source.Start < line.End)
            {
                var closing = lines.FindLine(display.Source.End);
                var range = new SourceRange(line.Start, lines.Lines[closing].Content.End - line.Start);
                blocks.Add(new(range, MarkdownBlockKind.DisplayMath, [ToNode(display)]));
                index = closing;
                mathIndex++;
                continue;
            }
            var nodes = ImmutableArray.CreateBuilder<MarkdownInlineNode>();
            var cursor = line.Start;
            while (mathIndex < math.Length && math[mathIndex].Source.Start < line.End)
            {
                var span = math[mathIndex];
                if (span.Kind != MarkdownMathSyntaxKind.Inline || !line.Contains(span.Source)) break;
                if (cursor < span.Source.Start)
                    nodes.Add(new MarkdownTextNode(new(cursor, span.Source.Start - cursor)));
                nodes.Add(ToNode(span));
                cursor = span.Source.End;
                mathIndex++;
            }
            if (cursor < line.End) nodes.Add(new MarkdownTextNode(new(cursor, line.End - cursor)));
            blocks.Add(new(line, MarkdownBlockKind.Paragraph, nodes.ToImmutable()));
        }
        return new(source, blocks);
    }

    private static MarkdownMathNode ToNode(MarkdownMathSpan span) =>
        new(span.Source, span.Content, span.Kind == MarkdownMathSyntaxKind.Display ?
            MarkdownMathKind.Display : MarkdownMathKind.Inline);

    private void ValidateCoverage()
    {
        var previousEnd = 0;
        foreach (var block in Blocks)
        {
            if (!Source.FullRange.Contains(block.Source) || block.Source.Start < previousEnd)
                throw new InvalidOperationException("Markdown blocks are not ordered source ranges.");
            previousEnd = block.Source.End;
            if (block.Inlines.IsEmpty) throw new InvalidOperationException("A Markdown block cannot be empty.");
            foreach (var node in block.Inlines)
            {
                if (!block.Source.Contains(node.Source)) throw new InvalidOperationException("Inline node is outside its block.");
                if (node is MarkdownMathNode math && (!math.Source.Contains(math.Content) || math.Content.Length == 0))
                    throw new InvalidOperationException("Math content is outside its delimiter range.");
            }
            if (block.Kind != MarkdownBlockKind.Paragraph) continue;
            var cursor = block.Source.Start;
            foreach (var node in block.Inlines)
            {
                if (node.Source.Start != cursor) throw new InvalidOperationException("Inline nodes do not preserve source coverage.");
                cursor = node.Source.End;
            }
            if (cursor != block.Source.End) throw new InvalidOperationException("Inline nodes do not cover their paragraph.");
        }
    }

}
