using System.Collections.Immutable;
using MDEditor.Core.Text;

namespace MDEditor.Core.Markdown;

public enum MarkdownTableAlignment { Left, Center, Right }

public sealed record MarkdownTableCell(int RowIndex, int ColumnIndex, SourceRange Source);

public sealed record MarkdownTableRow(int RowIndex, SourceRange Line,
    ImmutableArray<MarkdownTableCell> Cells, ImmutableArray<SourceRange> Pipes,
    ImmutableArray<SourceRange> EscapedSlashes, bool LeadingPipe, bool TrailingPipe);

public sealed record MarkdownTable(int Index, SourceRange Source, SourceRange DelimiterLine,
    ImmutableArray<MarkdownTableRow> Rows, ImmutableArray<MarkdownTableAlignment> Alignments,
    ImmutableArray<SourceRange> DelimiterCells, bool DelimiterLeadingPipe,
    bool DelimiterTrailingPipe, string ContainerPrefix)
{
    public int ColumnCount => Alignments.Length;
    public MarkdownTableRow Header => Rows[0];
}

/// <summary>Physical GFM table rows and editable cells in unchanged UTF-16 source coordinates.</summary>
public sealed class MarkdownTableStructure
{
    public SourceTextSnapshot Source { get; }
    public ImmutableArray<MarkdownTable> Tables { get; }

    private MarkdownTableStructure(SourceTextSnapshot source, ImmutableArray<MarkdownTable> tables)
    { Source = source; Tables = tables; }

    public static MarkdownTableStructure Create(MarkdownSyntaxDocument syntax)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        var source = syntax.Source;
        var lines = DocumentLineMap.Create(source);
        var tables = ImmutableArray.CreateBuilder<MarkdownTable>();
        foreach (var node in syntax.Descendants().Where(node => node.Kind == MarkdownSyntaxKind.Table))
        {
            if (node.Source.Length == 0) continue;
            var first = lines.FindLine(node.Source.Start);
            var last = lines.FindLine(node.Source.End - 1);
            if (last <= first + 0) continue;
            var headerLine = lines.Lines[first].Content;
            var prefix = source.Text[headerLine.Start..Math.Clamp(node.Source.Start,
                headerLine.Start, headerLine.End)];
            SourceRange TableContent(int lineIndex)
            {
                var line = lines.Lines[lineIndex].Content;
                return new(line.Start + (source.Text.AsSpan(line.Start, line.Length).StartsWith(prefix) ?
                    prefix.Length : 0), line.Length - (source.Text.AsSpan(line.Start, line.Length)
                    .StartsWith(prefix) ? prefix.Length : 0));
            }
            var header = Split(source.Text, TableContent(first), 0);
            var delimiter = lines.Lines[first + 1].Content;
            var delimiterRow = Split(source.Text, TableContent(first + 1), -1);
            var delimiterCells = delimiterRow.Cells;
            var rows = ImmutableArray.CreateBuilder<MarkdownTableRow>();
            rows.Add(header with { Line = headerLine });
            for (var lineIndex = first + 2; lineIndex <= last; lineIndex++)
                rows.Add(Split(source.Text, TableContent(lineIndex), rows.Count) with
                { Line = lines.Lines[lineIndex].Content });
            var count = Math.Max(1, rows.Max(row => row.Cells.Length));
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                if (row.Cells.Length >= count) continue;
                var cells = row.Cells.ToBuilder();
                while (cells.Count < count)
                    cells.Add(new(row.RowIndex, cells.Count, new(row.Line.End, 0)));
                rows[rowIndex] = row with { Cells = cells.ToImmutable() };
            }
            var alignments = ImmutableArray.CreateBuilder<MarkdownTableAlignment>(count);
            for (var column = 0; column < count; column++)
            {
                var marker = column < delimiterCells.Length ? source.GetText(delimiterCells[column].Source).Trim() : "";
                alignments.Add(marker.StartsWith(':') && marker.EndsWith(':') ?
                    MarkdownTableAlignment.Center : marker.EndsWith(':') ?
                    MarkdownTableAlignment.Right : MarkdownTableAlignment.Left);
            }
            var range = new SourceRange(headerLine.Start,
                lines.Lines[last].Content.End - headerLine.Start);
            tables.Add(new(tables.Count, range, delimiter, rows.ToImmutable(),
                alignments.ToImmutable(), delimiterCells.Select(cell => cell.Source).ToImmutableArray(),
                delimiterRow.LeadingPipe, delimiterRow.TrailingPipe, prefix));
        }
        return new(source, tables.ToImmutable());
    }

    public (MarkdownTable Table, MarkdownTableRow Row, MarkdownTableCell Cell)? CellAt(int offset)
    {
        if (offset < 0 || offset > Source.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        foreach (var table in Tables)
            foreach (var row in table.Rows)
            {
                if (offset < row.Line.Start || offset > row.Line.End) continue;
                var cell = row.Cells.FirstOrDefault(cell =>
                    cell.Source.Start <= offset && offset <= cell.Source.End);
                if (cell is not null) return (table, row, cell);
                // A click on a hidden separator belongs to its nearest cell.
                if (row.Cells.Length > 0)
                {
                    cell = row.Cells.MinBy(cell => offset < cell.Source.Start ?
                        cell.Source.Start - offset : offset > cell.Source.End ?
                        offset - cell.Source.End : 0)!;
                    return (table, row, cell);
                }
            }
        return null;
    }

    private static MarkdownTableRow Split(string text, SourceRange line, int rowIndex)
    {
        var separators = ImmutableArray.CreateBuilder<SourceRange>();
        var hidden = ImmutableArray.CreateBuilder<SourceRange>();
        for (var index = line.Start; index < line.End; index++)
        {
            if (text[index] == '`')
            {
                var end = index + 1;
                while (end < line.End && text[end] == '`') end++;
                var count = end - index;
                var closing = FindClosingBackticks(text, end, count, line.End);
                if (closing >= 0) { index = closing - 1; continue; }
                index = end - 1;
                continue;
            }
            if (text[index] == '\\')
            {
                var end = index + 1;
                while (end < line.End && text[end] == '\\') end++;
                if (end < line.End && text[end] == '|')
                {
                    for (var slash = index; slash + 1 < end; slash += 2)
                        hidden.Add(new(slash, 1));
                    if (((end - index) & 1) != 0)
                    { hidden.Add(new(end - 1, 1)); index = end; continue; }
                }
                index = end - 1;
                continue;
            }
            if (text[index] == '|') separators.Add(new(index, 1));
        }
        var first = line.Start;
        while (first < line.End && char.IsWhiteSpace(text[first])) first++;
        var last = line.End - 1;
        while (last >= first && char.IsWhiteSpace(text[last])) last--;
        var leading = separators.Count > 0 && separators[0].Start == first;
        var trailing = separators.Count > 0 && separators[^1].Start == last &&
            (!leading || separators.Count > 1);
        var start = leading ? separators[0].End : line.Start;
        var endOfCells = trailing ? separators[^1].Start : line.End;
        var cells = ImmutableArray.CreateBuilder<MarkdownTableCell>();
        foreach (var separator in separators)
        {
            if (separator.Start < start || separator.Start >= endOfCells) continue;
            cells.Add(new(rowIndex, cells.Count, new(start, separator.Start - start)));
            start = separator.End;
        }
        cells.Add(new(rowIndex, cells.Count, new(start, Math.Max(0, endOfCells - start))));
        return new(rowIndex, line, cells.ToImmutable(), separators.ToImmutable(),
            hidden.ToImmutable(), leading, trailing);
    }

    private static int FindClosingBackticks(string text, int start, int count, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (text[index] != '`') continue;
            var run = index + 1;
            while (run < end && text[run] == '`') run++;
            if (run - index == count) return run;
            index = run - 1;
        }
        return -1;
    }
}
