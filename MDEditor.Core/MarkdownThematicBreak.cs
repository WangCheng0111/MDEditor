using System.Collections.Immutable;
using MDEditor.Core.Text;

namespace MDEditor.Core.Markdown;

/// <summary>A parser-confirmed rule and its unchanged physical source line.</summary>
public readonly record struct MarkdownThematicBreak(SourceRange Source, int QuoteDepth, int ListDepth)
{
    internal static ImmutableArray<MarkdownThematicBreak> Collect(MarkdownSyntaxDocument syntax, DocumentLineMap lines)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(lines);
        if (!ReferenceEquals(syntax.Source, lines.Source))
            throw new ArgumentException("Line map must belong to this source snapshot.", nameof(lines));
        var result = ImmutableArray.CreateBuilder<MarkdownThematicBreak>();
        var pending = new Stack<(MarkdownSyntaxNode Node, int Quotes, int Lists)>();
        pending.Push((syntax.Root, 0, 0));
        while (pending.TryPop(out var entry))
        {
            var (node, quotes, lists) = entry;
            if (node.Kind == MarkdownSyntaxKind.ThematicBreak)
                result.Add(new(lines.Lines[lines.FindLine(node.Source.Start)].Content, quotes, lists));
            if (node.Kind == MarkdownSyntaxKind.Quote) quotes++;
            if (node.Kind == MarkdownSyntaxKind.ListItem) lists++;
            for (var index = node.Children.Length - 1; index >= 0; index--)
                pending.Push((node.Children[index], quotes, lists));
        }
        return result.ToImmutable();
    }
}
