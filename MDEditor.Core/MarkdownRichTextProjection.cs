using System.Collections.Immutable;
using MDEditor.Core.Text;

namespace MDEditor.Core.Markdown;

[Flags]
public enum MarkdownVisualStyle
{
    None = 0,
    Strong = 1,
    Emphasis = 2,
    Strikethrough = 4,
    Code = 8,
    Link = 16
}

/// <summary>A half-open range in the projected display snapshot, never in the source.</summary>
public readonly record struct MarkdownStyleSpan(SourceRange Display, MarkdownVisualStyle Style);

public readonly record struct MarkdownHeadingSpan(SourceRange Display, int Level);

public readonly record struct MarkdownBlockDisplay(SourceRange Line, MarkdownBlockKind Kind,
    int QuoteDepth, int ListDepth, string Marker, bool PrefixHidden);
public readonly record struct MarkdownCodeDisplay(SourceRange Line, int BlockIndex,
    MarkdownCodeLineRole Role, string Language, int QuoteDepth, bool FenceHidden);
public readonly record struct MarkdownTableDisplay(SourceRange Line, int TableIndex,
    int RowIndex, ImmutableArray<SourceRange> Cells,
    ImmutableArray<MarkdownTableAlignment> Alignments);
public readonly record struct MarkdownThematicBreakDisplay(SourceRange Line,
    int QuoteDepth, int ListDepth, bool MarkerHidden);

/// <summary>
/// Immutable presentation data for one source version and one reveal state. Parsing and
/// styling never replace or normalize the Markdown source used by editing and history.
/// </summary>
public sealed class MarkdownRichTextProjection
{
    private readonly Dictionary<int, MarkdownCodeDisplay> _codeByLineStart;
    private readonly Dictionary<int, MarkdownTableDisplay> _tableByLineStart;
    private readonly Dictionary<int, MarkdownBlockDisplay> _blockByLineStart;
    private readonly Dictionary<int, MarkdownThematicBreakDisplay> _thematicBreakByLineStart;
    public MarkdownEditProjection Text { get; }
    public MarkdownSyntaxDocument Syntax { get; }
    public ImmutableArray<MarkdownStyleSpan> Styles { get; }
    public ImmutableArray<MarkdownHeadingSpan> Headings { get; }
    public ImmutableArray<MarkdownBlockDisplay> Blocks { get; }
    public ImmutableArray<MarkdownCodeDisplay> CodeLines { get; }
    public ImmutableArray<MarkdownTableDisplay> TableLines { get; }
    public ImmutableArray<MarkdownThematicBreakDisplay> ThematicBreaks { get; }

    private MarkdownRichTextProjection(MarkdownSyntaxDocument syntax, int? revealAtSourceOffset, bool sourceMode = false)
    {
        Syntax = syntax;
        if (sourceMode)
        {
            Text = MarkdownEditProjection.CreateSource(syntax);
            Styles = []; Headings = []; Blocks = []; CodeLines = []; TableLines = [];
            ThematicBreaks = [];
            _thematicBreakByLineStart = new();
            _codeByLineStart = new(); _tableByLineStart = new(); _blockByLineStart = new();
            return;
        }
        Text = MarkdownEditProjection.Create(syntax, revealAtSourceOffset);
        var flags = new MarkdownVisualStyle[Text.Display.Length];
        var mathDisplayRanges = Text.MathSpans.Select(span => Text.ToDisplayRange(span.Source)).ToArray();
        var units = new Dictionary<(MarkdownSyntaxKind Kind, SourceRange Source), MarkdownProjectionUnit>();
        foreach (var unit in Text.SyntaxUnits)
            if (unit.Delimiters.Length >= 2)
                units.TryAdd((unit.Kind, unit.Source), unit);
        var headings = ImmutableArray.CreateBuilder<MarkdownHeadingSpan>();
        foreach (var node in syntax.Descendants())
        {
            if (node.Kind == MarkdownSyntaxKind.Heading)
            {
                var range = Text.ToDisplayRange(node.Source);
                if (range.Length > 0) headings.Add(new(range, HeadingLevel(syntax.Source, node)));
            }
            var style = node.Kind switch
            {
                MarkdownSyntaxKind.Strong => MarkdownVisualStyle.Strong,
                MarkdownSyntaxKind.Emphasis => MarkdownVisualStyle.Emphasis,
                MarkdownSyntaxKind.Strikethrough => MarkdownVisualStyle.Strikethrough,
                MarkdownSyntaxKind.CodeSpan => MarkdownVisualStyle.Code,
                MarkdownSyntaxKind.Link => MarkdownVisualStyle.Link,
                _ => MarkdownVisualStyle.None
            };
            if (style == MarkdownVisualStyle.None) continue;
            if (ContainsRange(Text.MathSpans, node.Source)) continue;
            units.TryGetValue((node.Kind, node.Source), out var unit);
            var content = node.Children.Length > 0
                ? new SourceRange(node.Children[0].Source.Start,
                    node.Children[^1].Source.End - node.Children[0].Source.Start)
                : unit is not null ? new SourceRange(unit.Delimiters[0].End,
                    unit.Delimiters[^1].Start - unit.Delimiters[0].End) : node.Source;
            var visible = Text.ToDisplayRange(content);
            for (var position = visible.Start; position < visible.End; position++)
                if (!ContainsPosition(mathDisplayRanges, position))
                    flags[position] |= style;
        }
        foreach (var replacement in Text.ActiveReplacements)
        {
            if (replacement.Kind is not (MarkdownReplacementKind.FootnoteReference or
                MarkdownReplacementKind.EquationReference)) continue;
            var visible = Text.ToDisplayRange(replacement.Source);
            for (var position = visible.Start; position < visible.End; position++)
                flags[position] |= MarkdownVisualStyle.Link;
        }
        var spans = ImmutableArray.CreateBuilder<MarkdownStyleSpan>();
        for (var position = 0; position < flags.Length;)
        {
            var style = flags[position];
            var start = position++;
            while (position < flags.Length && flags[position] == style) position++;
            if (style != MarkdownVisualStyle.None)
                spans.Add(new(new(start, position - start), style));
        }
        Styles = spans.ToImmutable();
        Headings = headings.ToImmutable();
        Blocks = Text.Blocks.Blocks.Where(block => block.Kind != MarkdownBlockKind.None)
            .Select(block => new MarkdownBlockDisplay(Text.ToDisplayRange(block.Line), block.Kind,
                block.QuoteDepth, block.ListDepth, block.Marker,
                ContainsRange(Text.HiddenRanges, block.Prefix))).ToImmutableArray();
        _blockByLineStart = Blocks.ToDictionary(block => block.Line.Start);
        CodeLines = Text.CodeBlocks.Lines.Select(line => new MarkdownCodeDisplay(
            Text.ToDisplayRange(line.Line), line.BlockIndex, line.Role, line.Language,
            line.QuoteDepth, (line.Role is MarkdownCodeLineRole.OpeningFence or
                MarkdownCodeLineRole.ClosingFence) &&
                ContainsRange(Text.HiddenRanges, line.Line))).ToImmutableArray();
        _codeByLineStart = CodeLines.ToDictionary(line => line.Line.Start);
        TableLines = Text.Tables.Tables.SelectMany(table =>
            table.Rows.Select(row => new MarkdownTableDisplay(Text.ToDisplayRange(row.Line),
                table.Index, row.RowIndex,
                row.Cells.Select(cell => Text.ToDisplayRange(cell.Source)).ToImmutableArray(),
                table.Alignments)).Append(new MarkdownTableDisplay(
                Text.ToDisplayRange(table.DelimiterLine), table.Index, -1, [], table.Alignments)))
            .OrderBy(line => line.Line.Start).ToImmutableArray();
        _tableByLineStart = TableLines.ToDictionary(line => line.Line.Start);
        var foldedRules = Text.ActiveReplacements.Where(replacement =>
            replacement.Kind == MarkdownReplacementKind.ThematicBreak)
            .Select(replacement => replacement.Source).ToHashSet();
        ThematicBreaks = Text.ThematicBreaks.Select(rule => new MarkdownThematicBreakDisplay(
            Text.ToDisplayRange(rule.Source), rule.QuoteDepth, rule.ListDepth,
            foldedRules.Contains(rule.Source))).ToImmutableArray();
        _thematicBreakByLineStart = ThematicBreaks.ToDictionary(rule => rule.Line.Start);
    }

    public static MarkdownRichTextProjection Create(SourceTextSnapshot source,
        int? revealAtSourceOffset = null) =>
        new(MarkdownSyntaxParser.Parse(source ?? throw new ArgumentNullException(nameof(source))),
            revealAtSourceOffset);

    public static MarkdownRichTextProjection FromSyntax(MarkdownSyntaxDocument syntax,
        int? revealAtSourceOffset = null, bool sourceMode = false) =>
        new(syntax ?? throw new ArgumentNullException(nameof(syntax)), revealAtSourceOffset, sourceMode);

    public int HeadingLevelAt(SourceRange displayParagraph)
    {
        if (!Text.Display.FullRange.Contains(displayParagraph))
            throw new ArgumentOutOfRangeException(nameof(displayParagraph));
        var low = 0; var high = Headings.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (Headings[middle].Display.Start <= displayParagraph.Start) low = middle + 1;
            else high = middle;
        }
        if (low > 0 && displayParagraph.Start <= Headings[low - 1].Display.End)
            return Headings[low - 1].Level;
        return 0;
    }

    public MarkdownBlockDisplay? BlockAt(SourceRange displayParagraph)
    {
        if (!Text.Display.FullRange.Contains(displayParagraph))
            throw new ArgumentOutOfRangeException(nameof(displayParagraph));
        return _blockByLineStart.TryGetValue(displayParagraph.Start, out var block) ? block : null;
    }

    public MarkdownCodeDisplay? CodeLineAt(SourceRange displayParagraph)
    {
        if (!Text.Display.FullRange.Contains(displayParagraph))
            throw new ArgumentOutOfRangeException(nameof(displayParagraph));
        return _codeByLineStart.TryGetValue(displayParagraph.Start, out var line) ? line : null;
    }

    public MarkdownTableDisplay? TableLineAt(SourceRange displayParagraph)
    {
        if (!Text.Display.FullRange.Contains(displayParagraph))
            throw new ArgumentOutOfRangeException(nameof(displayParagraph));
        return _tableByLineStart.TryGetValue(displayParagraph.Start, out var line) ? line : null;
    }

    public bool IsCodeAtDisplayOffset(int offset)
    {
        if (offset < 0 || offset > Text.Display.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        var low = 0; var high = CodeLines.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (CodeLines[middle].Line.Start <= offset) low = middle + 1;
            else high = middle - 1;
        }
        return high >= 0 && offset <= CodeLines[high].Line.End;
    }

    public MarkdownThematicBreakDisplay? ThematicBreakAt(SourceRange displayParagraph)
    {
        if (!Text.Display.FullRange.Contains(displayParagraph))
            throw new ArgumentOutOfRangeException(nameof(displayParagraph));
        return _thematicBreakByLineStart.TryGetValue(displayParagraph.Start, out var rule) ? rule : null;
    }

    /// <summary>Style ranges relative to a projected paragraph; safe to rebind when its offset moves.</summary>
    public ImmutableArray<MarkdownStyleSpan> ParagraphStyles(SourceRange displayParagraph)
    {
        if (!Text.Display.FullRange.Contains(displayParagraph))
            throw new ArgumentOutOfRangeException(nameof(displayParagraph));
        var result = ImmutableArray.CreateBuilder<MarkdownStyleSpan>();
        var low = 0; var high = Styles.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (Styles[middle].Display.End <= displayParagraph.Start) low = middle + 1;
            else high = middle;
        }
        for (var index = low; index < Styles.Length && Styles[index].Display.Start < displayParagraph.End; index++)
        {
            var span = Styles[index];
            var start = Math.Max(span.Display.Start, displayParagraph.Start);
            var end = Math.Min(span.Display.End, displayParagraph.End);
            if (start < end)
                result.Add(new(new(start - displayParagraph.Start, end - start), span.Style));
        }
        return result.ToImmutable();
    }

    private static bool ContainsPosition(SourceRange[] ranges, int position)
    {
        var low = 0;
        var high = ranges.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (ranges[middle].Start <= position) low = middle + 1;
            else high = middle;
        }
        return low > 0 && position < ranges[low - 1].End;
    }

    private static bool ContainsRange(ImmutableArray<SourceRange> ranges, SourceRange target)
    {
        var low = 0;
        var high = ranges.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (ranges[middle].Start <= target.Start) low = middle + 1;
            else high = middle;
        }
        return low > 0 && ranges[low - 1].Contains(target);
    }

    private static bool ContainsRange(ImmutableArray<MarkdownMathSpan> ranges, SourceRange target)
    {
        var low = 0;
        var high = ranges.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (ranges[middle].Source.Start <= target.Start) low = middle + 1;
            else high = middle;
        }
        return low > 0 && ranges[low - 1].Source.Contains(target);
    }

    private static int HeadingLevel(SourceTextSnapshot source, MarkdownSyntaxNode node)
    {
        var text = source.GetText(node.Source).AsSpan();
        var index = 0;
        while (index < text.Length && text[index] == ' ' && index < 3) index++;
        var start = index;
        while (index < text.Length && text[index] == '#' && index - start < 6) index++;
        if (index > start && (index == text.Length || char.IsWhiteSpace(text[index])))
            return index - start;
        var lastLine = text.LastIndexOfAny('\r', '\n');
        if (lastLine < 0) return 2;
        var underline = text[(lastLine + 1)..].Trim();
        return underline.Length > 0 && underline[0] == '=' ? 1 : 2;
    }
}
