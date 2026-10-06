using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using MDEditor.Core.Text;

namespace MDEditor.Core.Markdown;

/// <summary>Which side of invisible source syntax owns one display caret boundary.</summary>
public enum ProjectionBoundary { BeforeHidden, AfterHidden }

/// <summary>A source syntax node and only the delimiter ranges this projection may hide.</summary>
public sealed record MarkdownProjectionUnit(MarkdownSyntaxKind Kind, SourceRange Source,
    ImmutableArray<SourceRange> Delimiters);

/// <summary>
/// Immutable text projection over an unchanged Markdown snapshot. It removes only supported
/// delimiters; it neither renders styles nor rewrites escaped text, entities, math or files.
/// Every UTF-16 display boundary maps to both legal sides of any collapsed source syntax.
/// </summary>
public sealed class MarkdownEditProjection
{
    private readonly MarkdownSyntaxDocument _syntax;
    private readonly int[] _sourceToDisplay;
    private readonly int[] _displayBefore;
    private readonly int[] _displayAfter;
    private readonly int[] _displayGraphemeStops;
    private readonly Dictionary<int, int> _objectNavigationBoundaries;
    private readonly HashSet<int> _foldedContainerStarts;
    private readonly int[] _syntaxUnitPrefixMaxEnd;

    public SourceTextSnapshot Source => _syntax.Source;
    public SourceTextSnapshot Display { get; }
    public bool IsSourceMode { get; }
    public int? RevealedAtSourceOffset { get; }
    public MarkdownBlockStructure Blocks { get; }
    public MarkdownCodeBlockStructure CodeBlocks { get; }
    public MarkdownTableStructure Tables { get; }
    public ImmutableArray<MarkdownThematicBreak> ThematicBreaks { get; }
    public ImmutableArray<MarkdownMathSpan> MathSpans { get; }
    public MarkdownReferenceIndex References { get; }
    public ImmutableArray<MarkdownDisplayReplacement> ActiveReplacements { get; }
    public MarkdownMathSpan? RevealedMath => RevealedAtSourceOffset is { } offset ?
        MathSpans.FirstOrDefault(span => span.Source.Start < offset && offset < span.Source.End) : null;
    public ImmutableArray<MarkdownProjectionUnit> SyntaxUnits { get; }
    public ImmutableArray<SourceRange> HiddenRanges { get; }

    private MarkdownEditProjection(MarkdownSyntaxDocument syntax, int? revealedAtSourceOffset, bool sourceMode = false,
        bool revealThematicBreakBoundaries = true)
    {
        _syntax = syntax;
        IsSourceMode = sourceMode;
        if (revealedAtSourceOffset is { } offset && (offset < 0 || offset > Source.Length))
            throw new ArgumentOutOfRangeException(nameof(revealedAtSourceOffset));
        RevealedAtSourceOffset = revealedAtSourceOffset;
        Blocks = MarkdownBlockStructure.Create(Source, syntax);
        CodeBlocks = MarkdownCodeBlockStructure.Create(syntax);
        Tables = MarkdownTableStructure.Create(syntax);
        ThematicBreaks = MarkdownThematicBreak.Collect(syntax, Blocks.Lines);
        MathSpans = MarkdownMathSyntax.Parse(Source, syntax);
        References = MarkdownReferenceIndex.Create(Source, syntax, MathSpans);
        if (sourceMode)
        {
            // Identity projection: syntax, CRLF, escapes and formula delimiters are all editable.
            Display = Source;
            MathSpans = [];
            ActiveReplacements = []; SyntaxUnits = []; HiddenRanges = [];
            _syntaxUnitPrefixMaxEnd = [];
            _sourceToDisplay = Enumerable.Range(0, Source.Length + 1).ToArray();
            _displayBefore = _sourceToDisplay; _displayAfter = _sourceToDisplay;
            _objectNavigationBoundaries = new(); _foldedContainerStarts = new();
            _displayGraphemeStops = GraphemeStops(Source.Text);
            return;
        }
        ActiveReplacements = References.Replacements.Concat(ThematicBreaks.Select(rule =>
            new MarkdownDisplayReplacement(rule.Source, "\uFFFC", MarkdownReplacementKind.ThematicBreak)))
            .Where(replacement =>
            RevealedAtSourceOffset is not { } reveal ||
            reveal < replacement.Source.Start || reveal > replacement.Source.End ||
            !revealThematicBreakBoundaries && replacement.Kind == MarkdownReplacementKind.ThematicBreak &&
                (reveal == replacement.Source.Start || reveal == replacement.Source.End))
            .OrderBy(replacement => replacement.Source.Start)
            .ToImmutableArray();
        var structural = Blocks.Blocks.Where(block => block.Kind != MarkdownBlockKind.None &&
            block.Prefix.Length > 0).Select(block => new MarkdownProjectionUnit(
                block.ListDepth > 0 ? MarkdownSyntaxKind.ListItem : MarkdownSyntaxKind.Quote,
                block.Line, ImmutableArray.Create(block.Prefix)));
        SyntaxUnits = syntax.Descendants().Select(TryCreateUnit).OfType<MarkdownProjectionUnit>()
            .Concat(structural)
            .Concat(CodeBlocks.Blocks.Select(block =>
                new MarkdownProjectionUnit(block.FenceLength > 0 ? MarkdownSyntaxKind.FencedCode :
                    MarkdownSyntaxKind.IndentedCode, block.Source,
                    block.Lines.SelectMany(line => line.Role is
                        MarkdownCodeLineRole.OpeningFence or MarkdownCodeLineRole.ClosingFence
                        ? new[] { line.Line } : line.OuterPrefix.Length > 0
                            ? new[] { line.OuterPrefix } : []).ToImmutableArray()))
                .Where(unit => unit.Delimiters.Length > 0))
            .OrderBy(unit => unit.Source.Start).ThenByDescending(unit => unit.Source.Length).ToImmutableArray();
        _syntaxUnitPrefixMaxEnd = new int[SyntaxUnits.Length];
        for (var index = 0; index < SyntaxUnits.Length; index++)
            _syntaxUnitPrefixMaxEnd[index] = Math.Max(index == 0 ? 0 :
                _syntaxUnitPrefixMaxEnd[index - 1], SyntaxUnits[index].Source.End);

        var hidden = new bool[Source.Length];
        foreach (var unit in SyntaxUnits)
        {
            if (IsRevealed(unit)) continue;
            foreach (var delimiter in unit.Delimiters)
                for (var position = delimiter.Start; position < delimiter.End; position++)
                    if (!MathContains(position))
                        hidden[position] = true;
        }
        foreach (var table in Tables.Tables)
        {
            for (var position = table.DelimiterLine.Start; position < table.DelimiterLine.End; position++)
                hidden[position] = true;
            foreach (var row in table.Rows)
            {
                foreach (var pipe in row.Pipes)
                    hidden[pipe.Start] = true;
                foreach (var slash in row.EscapedSlashes)
                    hidden[slash.Start] = true;
            }
        }
        var ranges = ImmutableArray.CreateBuilder<SourceRange>();
        for (var position = 0; position < hidden.Length;)
        {
            if (!hidden[position]) { position++; continue; }
            var start = position;
            while (position < hidden.Length && hidden[position]) position++;
            ranges.Add(new(start, position - start));
        }
        HiddenRanges = ranges.ToImmutable();

        _sourceToDisplay = new int[Source.Length + 1];
        var display = new StringBuilder(Source.Length);
        var replacementIndex = 0;
        for (var position = 0; position < Source.Length;)
        {
            if (replacementIndex < ActiveReplacements.Length &&
                ActiveReplacements[replacementIndex].Source.Start == position)
            {
                var replacement = ActiveReplacements[replacementIndex++];
                var displayStart = display.Length;
                for (var index = position; index < replacement.Source.End; index++)
                    _sourceToDisplay[index] = displayStart;
                display.Append(replacement.Display);
                position = replacement.Source.End;
                continue;
            }
            _sourceToDisplay[position] = display.Length;
            if (!hidden[position]) display.Append(Source.Text[position]);
            position++;
        }
        _sourceToDisplay[Source.Length] = display.Length;
        Display = new(display.ToString(), Source.Version);

        _displayBefore = Enumerable.Repeat(-1, Display.Length + 1).ToArray();
        _displayAfter = new int[Display.Length + 1];
        for (var position = 0; position <= Source.Length; position++)
        {
            var projected = _sourceToDisplay[position];
            if (_displayBefore[projected] < 0) _displayBefore[projected] = position;
            _displayAfter[projected] = position;
        }
        _objectNavigationBoundaries = new();
        foreach (var replacement in ActiveReplacements)
        {
            var first = ToDisplayOffset(replacement.Source.Start);
            var last = ToDisplayOffset(replacement.Source.End);
            if (replacement.Kind is MarkdownReplacementKind.Image or MarkdownReplacementKind.ThematicBreak)
            {
                _objectNavigationBoundaries.TryAdd(first, replacement.Source.Start);
                _objectNavigationBoundaries.TryAdd(last, replacement.Source.End);
            }
            for (var index = first + 1; index < last; index++)
            {
                _displayBefore[index] = replacement.Source.Start;
                _displayAfter[index] = replacement.Source.End;
            }
        }
        _foldedContainerStarts = new();
        foreach (var block in Blocks.Blocks)
            if (block.Kind != MarkdownBlockKind.None && block.Prefix.Length > 0 &&
                IsHiddenRange(block.Prefix))
                _foldedContainerStarts.Add(ToDisplayOffset(block.Line.Start));
        foreach (var line in CodeBlocks.Lines)
            if (line.Role is MarkdownCodeLineRole.OpeningFence or MarkdownCodeLineRole.ClosingFence
                ? IsHiddenRange(line.Line)
                : line.OuterPrefix.Length > 0 && IsHiddenRange(line.OuterPrefix))
                _foldedContainerStarts.Add(ToDisplayOffset(line.Line.Start));
        _displayGraphemeStops = GraphemeStops(Display.Text);
    }

    public static MarkdownEditProjection Create(MarkdownSyntaxDocument syntax, int? revealAtSourceOffset = null,
        bool revealThematicBreakBoundaries = true) =>
        new(syntax ?? throw new ArgumentNullException(nameof(syntax)), revealAtSourceOffset,
            revealThematicBreakBoundaries: revealThematicBreakBoundaries);

    public static MarkdownEditProjection CreateSource(MarkdownSyntaxDocument syntax) =>
        new(syntax ?? throw new ArgumentNullException(nameof(syntax)), null, true);

    public MarkdownEditProjection RevealAt(int sourceOffset) =>
        IsSourceMode || RevealedAtSourceOffset == sourceOffset ? this : Create(_syntax, sourceOffset);

    public int ToDisplayOffset(int sourceOffset)
    {
        if (sourceOffset < 0 || sourceOffset > Source.Length)
            throw new ArgumentOutOfRangeException(nameof(sourceOffset));
        return _sourceToDisplay[sourceOffset];
    }

    public int ToSourceOffset(int displayOffset, ProjectionBoundary boundary)
    {
        if (displayOffset < 0 || displayOffset > Display.Length)
            throw new ArgumentOutOfRangeException(nameof(displayOffset));
        return boundary switch
        {
            ProjectionBoundary.BeforeHidden => _displayBefore[displayOffset],
            ProjectionBoundary.AfterHidden => _displayAfter[displayOffset],
            _ => throw new ArgumentOutOfRangeException(nameof(boundary))
        };
    }

    /// <summary>Entering a folded container line lands after its hidden marker, never before it.</summary>
    public int ToNavigationSourceOffset(int displayOffset, ProjectionBoundary boundary)
    {
        // A folded image or rule is one visible object, not an editable character inside
        // its Markdown syntax. Navigation and insertion must land on either side of
        // the entire source token; raw interior positions remain available only
        // after the user enters the expanded source representation.
        if (_objectNavigationBoundaries.TryGetValue(displayOffset, out var objectEdge)) return objectEdge;
        if (boundary == ProjectionBoundary.BeforeHidden &&
            _foldedContainerStarts.Contains(displayOffset))
            boundary = ProjectionBoundary.AfterHidden;
        return ToSourceOffset(displayOffset, boundary);
    }

    public SourceRange ToDisplayRange(SourceRange sourceRange)
    {
        if (!Source.FullRange.Contains(sourceRange)) throw new ArgumentOutOfRangeException(nameof(sourceRange));
        var start = ToDisplayOffset(sourceRange.Start);
        return new(start, ToDisplayOffset(sourceRange.End) - start);
    }

    /// <summary>
    /// Map a display selection to source. Whole visible constructs can include their hidden
    /// wrappers, so cutting or deleting all of a link/strong/code node never leaves orphan marks.
    /// A zero-length selection chooses the downstream side for insertion.
    /// </summary>
    public SourceRange ToSourceRange(SourceRange displayRange, bool includeCompleteSyntax = false)
    {
        if (!Display.FullRange.Contains(displayRange)) throw new ArgumentOutOfRangeException(nameof(displayRange));
        if (displayRange.Length == 0)
            return new(ToNavigationSourceOffset(displayRange.Start, ProjectionBoundary.AfterHidden), 0);
        var start = ToSourceOffset(displayRange.Start, ProjectionBoundary.AfterHidden);
        var end = ToSourceOffset(displayRange.End, ProjectionBoundary.BeforeHidden);
        foreach (var replacement in ActiveReplacements)
        {
            var visible = ToDisplayRange(replacement.Source);
            if (visible.Start < displayRange.End && displayRange.Start < visible.End)
            {
                start = Math.Min(start, replacement.Source.Start);
                end = Math.Max(end, replacement.Source.End);
            }
        }
        // In a GFM cell, a visible escaped character owns its hidden preceding
        // backslash. Deleting just the displayed pipe must not leave a stray '\'.
        foreach (var slash in Tables.Tables.SelectMany(table => table.Rows)
            .SelectMany(row => row.EscapedSlashes))
        {
            var visible = ToDisplayOffset(slash.End);
            if (displayRange.Start <= visible && visible < displayRange.End)
                start = Math.Min(start, slash.Start);
        }
        if (includeCompleteSyntax)
        {
            var low = 0;
            var high = _syntaxUnitPrefixMaxEnd.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (_syntaxUnitPrefixMaxEnd[middle] < start) low = middle + 1;
                else high = middle;
            }
            for (var index = low; index < SyntaxUnits.Length &&
                SyntaxUnits[index].Source.Start <= end; index++)
            {
                var unit = SyntaxUnits[index];
                if (unit.Delimiters.Length == 0) continue;
                if (unit.Kind is MarkdownSyntaxKind.FencedCode or MarkdownSyntaxKind.IndentedCode)
                {
                    var visibleBlock = ToDisplayRange(unit.Source);
                    if (visibleBlock.Length > 0 && displayRange.Contains(visibleBlock))
                    {
                        start = Math.Min(start, unit.Source.Start);
                        end = Math.Max(end, unit.Source.End);
                    }
                    continue;
                }
                var first = unit.Delimiters[0];
                var last = unit.Delimiters[^1];
                var contentStart = first.Start == unit.Source.Start ? first.End : unit.Source.Start;
                var contentEnd = last.End == unit.Source.End ? last.Start : unit.Source.End;
                if (contentEnd < contentStart) continue;
                var content = new SourceRange(contentStart, contentEnd - contentStart);
                var visible = ToDisplayRange(content);
                if (visible.Length == 0 || !displayRange.Contains(visible)) continue;
                start = Math.Min(start, unit.Source.Start);
                end = Math.Max(end, unit.Source.End);
            }
        }
        return new(start, end - start);
    }

    /// <summary>Move over one displayed grapheme, never into an invisible delimiter.</summary>
    public int MoveSourceCaret(int sourceOffset, int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var displayOffset = ToDisplayOffset(sourceOffset);
        var index = Array.BinarySearch(_displayGraphemeStops, displayOffset);
        if (index < 0) index = ~index;
        var next = direction < 0 ? Math.Max(0, index - 1) :
            Math.Min(_displayGraphemeStops.Length - 1, index +
                (_displayGraphemeStops[index] == displayOffset ? 1 : 0));
        if (_displayGraphemeStops[next] == displayOffset) return sourceOffset;
        return ToNavigationSourceOffset(_displayGraphemeStops[next], direction < 0 ?
            ProjectionBoundary.AfterHidden : ProjectionBoundary.BeforeHidden);
    }

    public SourceRange? BackspaceRange(int sourceCaret) => AdjacentDeleteRange(sourceCaret, -1);
    public SourceRange? DeleteForwardRange(int sourceCaret) => AdjacentDeleteRange(sourceCaret, 1);

    private SourceRange? AdjacentDeleteRange(int sourceCaret, int direction)
    {
        var displayOffset = ToDisplayOffset(sourceCaret);
        var index = Array.BinarySearch(_displayGraphemeStops, displayOffset);
        if (index < 0)
        {
            // An externally restored caret inside a grapheme deletes that whole grapheme.
            index = ~index;
            return ToSourceRange(new(_displayGraphemeStops[index - 1],
                _displayGraphemeStops[index] - _displayGraphemeStops[index - 1]), true);
        }
        var next = index + direction;
        if (next < 0 || next >= _displayGraphemeStops.Length) return null;
        var start = Math.Min(_displayGraphemeStops[index], _displayGraphemeStops[next]);
        return ToSourceRange(new(start,
            Math.Abs(_displayGraphemeStops[next] - _displayGraphemeStops[index])), true);
    }

    private bool MathContains(int position)
    {
        var low = 0;
        var high = MathSpans.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (MathSpans[middle].Source.Start <= position) low = middle + 1;
            else high = middle;
        }
        return low > 0 && position < MathSpans[low - 1].Source.End;
    }

    private bool MathContains(SourceRange range)
    {
        var low = 0;
        var high = MathSpans.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (MathSpans[middle].Source.Start <= range.Start) low = middle + 1;
            else high = middle;
        }
        return low > 0 && MathSpans[low - 1].Source.Contains(range);
    }

    private bool IsHiddenRange(SourceRange range)
    {
        var low = 0;
        var high = HiddenRanges.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (HiddenRanges[middle].Start <= range.Start) low = middle + 1;
            else high = middle;
        }
        return low > 0 && HiddenRanges[low - 1].Contains(range);
    }

    private bool IsRevealed(MarkdownProjectionUnit unit) => RevealedAtSourceOffset is { } offset &&
        (unit.Kind is MarkdownSyntaxKind.ListItem or MarkdownSyntaxKind.Quote or
            MarkdownSyntaxKind.FencedCode or MarkdownSyntaxKind.IndentedCode
            ? unit.Source.Start < offset && offset <= unit.Source.End
            : unit.Source.Start < offset && offset < unit.Source.End);

    private MarkdownProjectionUnit? TryCreateUnit(MarkdownSyntaxNode node)
    {
        if (node.Source.Length == 0) return null;
        if (MathContains(node.Source)) return null;
        var delimiters = ImmutableArray.CreateBuilder<SourceRange>();
        if (node.Kind == MarkdownSyntaxKind.CodeSpan)
        {
            var content = Source.GetText(node.Source);
            var opening = 0;
            while (opening < content.Length && content[opening] == '`') opening++;
            var closing = 0;
            while (closing < content.Length && content[content.Length - closing - 1] == '`') closing++;
            if (opening > 0 && opening == closing && opening * 2 < content.Length)
            {
                delimiters.Add(new(node.Source.Start, opening));
                delimiters.Add(new(node.Source.End - closing, closing));
            }
        }
        else if (node.Kind == MarkdownSyntaxKind.Link && node.Children.Length == 0)
        {
            var text = Source.GetText(node.Source);
            if (text.Length > 2 && text[0] == '<' && text[^1] == '>')
            {
                delimiters.Add(new(node.Source.Start, 1));
                delimiters.Add(new(node.Source.End - 1, 1));
            }
        }
        else if (node.Children.Length > 0 && node.Kind is
            (MarkdownSyntaxKind.Heading or MarkdownSyntaxKind.Emphasis or MarkdownSyntaxKind.Strong or
            MarkdownSyntaxKind.Strikethrough or MarkdownSyntaxKind.Link))
        {
            var first = node.Children[0].Source;
            var last = node.Children[^1].Source;
            var prefix = new SourceRange(node.Source.Start, first.Start - node.Source.Start);
            var suffix = new SourceRange(last.End, node.Source.End - last.End);
            if (node.Kind is MarkdownSyntaxKind.Emphasis or MarkdownSyntaxKind.Strong or MarkdownSyntaxKind.Strikethrough)
            {
                var delimiter = Source.Text[node.Source.Start];
                if (delimiter is not ('*' or '_' or '~') || prefix.Length == 0 ||
                    prefix.Length != suffix.Length || !AllCharacters(prefix, delimiter) ||
                    !AllCharacters(suffix, delimiter)) return null;
            }
            if (prefix.Length > 0) delimiters.Add(prefix);
            if (suffix.Length > 0) delimiters.Add(suffix);
        }
        return delimiters.Count == 0 ? null : new(node.Kind, node.Source, delimiters.ToImmutable());
    }

    private bool AllCharacters(SourceRange range, char character) =>
        Source.Text.AsSpan(range.Start, range.Length).IndexOfAnyExcept(character) < 0;

    private static int[] GraphemeStops(string text)
    {
        var boundaries = StringInfo.ParseCombiningCharacters(text)
            .Where(index => index == 0 || index >= text.Length ||
                text[index - 1] != '\r' || text[index] != '\n').ToList();
        if (boundaries.Count == 0 || boundaries[0] != 0) boundaries.Insert(0, 0);
        if (boundaries[^1] != text.Length) boundaries.Add(text.Length);
        return boundaries.ToArray();
    }
}
