using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MDEditor.Core.Text;

namespace MDEditor.Core.Markdown;

public enum MarkdownParseScope { Unchanged, IsolatedParagraph, FullDocument }

/// <summary>Source ranges are in their respective immutable UTF-16 snapshots.</summary>
public sealed record MarkdownParseUpdate(MarkdownSyntaxDocument Syntax, MarkdownParseScope Scope,
    SourceRange OldRange, SourceRange NewRange);

/// <summary>
/// Reparse an isolated paragraph and rebase unchanged suffix nodes. Block constructs and
/// reference-dependent inline syntax deliberately fall back to Markdig's full document
/// parse: an edit must never leave a stale fence, list, table or link definition behind.
/// </summary>
public static class MarkdownIncrementalParser
{
    private static readonly Regex Definition = new(@"(?m)^[ ]{0,3}\[(?:\^)?[^\]\r\n]+\]:",
        RegexOptions.Compiled);

    public static MarkdownParseUpdate Update(MarkdownSyntaxDocument previous, SourceTextSnapshot next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        var old = previous.Source.Text;
        var fresh = next.Text;
        if (old == fresh)
            return new(new(next, new(MarkdownSyntaxKind.Document, next.FullRange,
                previous.Root.Children)), MarkdownParseScope.Unchanged, new(0, 0), new(0, 0));

        var start = 0;
        while (start < old.Length && start < fresh.Length && old[start] == fresh[start]) start++;
        var oldEnd = old.Length;
        var newEnd = fresh.Length;
        while (oldEnd > start && newEnd > start && old[oldEnd - 1] == fresh[newEnd - 1])
        { oldEnd--; newEnd--; }
        var changedOld = new SourceRange(start, oldEnd - start);
        var changedNew = new SourceRange(start, newEnd - start);

        var children = previous.Root.Children;
        var blockIndex = -1;
        for (var index = 0; index < children.Length; index++)
            if (children[index].Kind == MarkdownSyntaxKind.Paragraph &&
                children[index].Source.Start <= changedOld.Start &&
                changedOld.End <= children[index].Source.End)
            { blockIndex = index; break; }
        if (blockIndex >= 0)
        {
            var block = children[blockIndex];
            var delta = fresh.Length - old.Length;
            var nextEnd = (long)block.Source.End + delta;
            if (nextEnd >= block.Source.Start && nextEnd <= fresh.Length)
            {
                var nextRange = new SourceRange(block.Source.Start, (int)nextEnd - block.Source.Start);
                var oldLines = DocumentLineMap.Create(previous.Source);
                var newLines = DocumentLineMap.Create(next);
                var containsReferenceSyntax = old.AsSpan(block.Source.Start, block.Source.Length).IndexOf('[') >= 0 ||
                    fresh.AsSpan(nextRange.Start, nextRange.Length).IndexOf('[') >= 0;
                if (Isolated(oldLines, block.Source) && Isolated(newLines, nextRange) &&
                    !(containsReferenceSyntax && (Definition.IsMatch(old) || Definition.IsMatch(fresh))))
                {
                    var fragment = MarkdownSyntaxParser.Parse(new SourceTextSnapshot(
                        next.GetText(nextRange), next.Version));
                    if (fragment.Root.Children is [{ Kind: MarkdownSyntaxKind.Paragraph } parsed] &&
                        parsed.Source == new SourceRange(0, nextRange.Length))
                    {
                        var builder = ImmutableArray.CreateBuilder<MarkdownSyntaxNode>(children.Length);
                        for (var index = 0; index < children.Length; index++)
                            builder.Add(index < blockIndex ? children[index] : index == blockIndex
                                ? Shift(parsed, nextRange.Start) : Shift(children[index], delta));
                        var syntax = new MarkdownSyntaxDocument(next,
                            new(MarkdownSyntaxKind.Document, next.FullRange, builder.MoveToImmutable()));
                        return new(syntax, MarkdownParseScope.IsolatedParagraph, block.Source, nextRange);
                    }
                }
            }
        }
        return new(MarkdownSyntaxParser.Parse(next), MarkdownParseScope.FullDocument,
            previous.Source.FullRange, next.FullRange);
    }

    private static bool Isolated(DocumentLineMap lines, SourceRange block)
    {
        var first = lines.FindLine(block.Start);
        var last = lines.FindLine(block.End);
        if (first > 0 && lines.Lines[first - 1].Content.Length != 0) return false;
        if (last + 1 < lines.Count && lines.Lines[last + 1].Content.Length != 0) return false;
        return lines.Lines[first].Content.Start == block.Start &&
            lines.Lines[last].Content.End == block.End;
    }

    private static MarkdownSyntaxNode Shift(MarkdownSyntaxNode node, int delta) =>
        new(node.Kind, new SourceRange(checked(node.Source.Start + delta), node.Source.Length),
            node.Children.Select(child => Shift(child, delta)).ToImmutableArray());
}
