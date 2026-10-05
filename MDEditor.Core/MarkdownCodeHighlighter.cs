using System.Collections.Immutable;
using System.Text;
using MDEditor.Core.Text;

namespace MDEditor.Core.Markdown;

// starry-night's PrettyLights classes, not a hand-written language lexer.
public enum MarkdownCodeTokenKind
{
    Comment, Constant, Entity, Plain, Tag, Keyword, String, Variable,
    Unmatched, Illegal, CarriageReturn, Regex, List, Heading, Deleted,
    Inserted, Changed, Ignored, DiffRange, Angle, Gutter, Reference
}
public readonly record struct MarkdownCodeToken(SourceRange Source, MarkdownCodeTokenKind Kind);
public sealed record MarkdownCodeHighlight(SourceTextSnapshot Source,
    ImmutableArray<MarkdownCodeToken> Tokens, bool Truncated);
public readonly record struct MarkdownCodeSegment(SourceRange Input, SourceRange Source);
public sealed record MarkdownCodeInput(int Id, string Language, string Text,
    ImmutableArray<MarkdownCodeSegment> Segments);
public sealed record MarkdownCodeInputs(SourceTextSnapshot Source,
    ImmutableArray<MarkdownCodeInput> Blocks, bool Truncated);

/// <summary>Extracts and maps UTF-16 ranges only. The offline worker tokenizes code.</summary>
public static class MarkdownCodeHighlighter
{
    public const int MaximumBlockCharacters = 262_144;
    public const int MaximumDocumentCharacters = 1_048_576;
    public const int MaximumBlocks = 128;

    public static MarkdownCodeInputs CreateInputs(MarkdownCodeBlockStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        var source = structure.Source;
        var blocks = ImmutableArray.CreateBuilder<MarkdownCodeInput>();
        var total = 0;
        var truncated = false;
        foreach (var block in structure.Blocks)
        {
            if (string.IsNullOrWhiteSpace(block.Language)) continue;
            if (block.Language.Length > 256) { truncated = true; continue; }
            var lines = block.Lines.Where(line => line.Role is MarkdownCodeLineRole.Content or
                MarkdownCodeLineRole.IndentedContent).ToArray();
            var length = lines.Sum(line => (long)line.Content.Length +
                structure.LineMap.Lines[line.LineIndex].Break.Length);
            if (length > MaximumBlockCharacters || length + total > MaximumDocumentCharacters ||
                blocks.Count >= MaximumBlocks)
            { truncated = true; continue; }
            var text = new StringBuilder((int)length);
            var segments = ImmutableArray.CreateBuilder<MarkdownCodeSegment>();
            foreach (var line in lines)
            {
                if (line.Content.Length > 0)
                    segments.Add(new(new(text.Length, line.Content.Length), line.Content));
                text.Append(source.Text, line.Content.Start, line.Content.Length);
                var newline = structure.LineMap.Lines[line.LineIndex].Break;
                text.Append(source.Text, newline.Start, newline.Length);
            }
            total += text.Length;
            blocks.Add(new(blocks.Count, block.Language, text.ToString(), segments.ToImmutable()));
        }
        return new(source, blocks.ToImmutable(), truncated);
    }

    public static ImmutableArray<MarkdownCodeToken> MapTokens(MarkdownCodeInput input,
        IEnumerable<MarkdownCodeToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(tokens);
        var mapped = ImmutableArray.CreateBuilder<MarkdownCodeToken>();
        var previousEnd = 0;
        var segmentIndex = 0;
        foreach (var token in tokens)
        {
            if (token.Source.Start < previousEnd || token.Source.Length <= 0 ||
                token.Source.End > input.Text.Length || !Enum.IsDefined(token.Kind))
                throw new ArgumentException("Invalid starry-night token range.", nameof(tokens));
            previousEnd = token.Source.End;
            while (segmentIndex < input.Segments.Length &&
                input.Segments[segmentIndex].Input.End <= token.Source.Start) segmentIndex++;
            for (var index = segmentIndex; index < input.Segments.Length; index++)
            {
                var segment = input.Segments[index];
                if (segment.Input.Start >= token.Source.End) break;
                var first = Math.Max(segment.Input.Start, token.Source.Start);
                var last = Math.Min(segment.Input.End, token.Source.End);
                if (last > first)
                    mapped.Add(new(new(segment.Source.Start + first - segment.Input.Start,
                        last - first), token.Kind));
            }
        }
        return mapped.ToImmutable();
    }

    /// <summary>Resolve only the visible glyph being painted; never remap a whole document on the UI thread.</summary>
    public static MarkdownCodeTokenKind? KindAt(MarkdownCodeHighlight? highlight,
        MarkdownEditProjection projection, int displayOffset)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (highlight is null || !ReferenceEquals(highlight.Source, projection.Source)) return null;
        var offset = projection.ToSourceOffset(displayOffset, ProjectionBoundary.AfterHidden);
        var low = 0;
        var high = highlight.Tokens.Length - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var token = highlight.Tokens[middle];
            if (offset < token.Source.Start) high = middle - 1;
            else if (offset >= token.Source.End) low = middle + 1;
            else return token.Kind;
        }
        return null;
    }
}
