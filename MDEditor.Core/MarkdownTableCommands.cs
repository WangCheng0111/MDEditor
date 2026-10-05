using System.Text;
using MDEditor.Core.Text;

namespace MDEditor.Core.Markdown;

public enum MarkdownTableCommand { InsertRow, DeleteRow, InsertColumnAfter, DeleteColumn }
public sealed record MarkdownTableNavigation(SourceRange Selection, MarkdownSourceEdit? Edit);

/// <summary>Single-transaction table edits that preserve every source byte outside the affected table.</summary>
public static class MarkdownTableCommands
{
    public static MarkdownSourceEdit? Plan(MarkdownTableStructure structure, int caret,
        MarkdownTableCommand command)
    {
        ArgumentNullException.ThrowIfNull(structure);
        if (structure.CellAt(caret) is not { } hit) return null;
        return command switch
        {
            MarkdownTableCommand.InsertRow => InsertRow(structure.Source, hit.Table, hit.Row,
                hit.Cell.ColumnIndex),
            MarkdownTableCommand.DeleteRow => DeleteRow(structure.Source, hit.Table, hit.Row),
            MarkdownTableCommand.InsertColumnAfter => RewriteColumn(structure.Source,
                hit.Table, hit.Row.RowIndex, hit.Cell.ColumnIndex, insert: true),
            MarkdownTableCommand.DeleteColumn => RewriteColumn(structure.Source,
                hit.Table, hit.Row.RowIndex, hit.Cell.ColumnIndex, insert: false),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
    }

    public static MarkdownTableNavigation? Tab(MarkdownTableStructure structure, int caret, bool reverse)
    {
        ArgumentNullException.ThrowIfNull(structure);
        if (structure.CellAt(caret) is not { } hit) return null;
        var column = hit.Cell.ColumnIndex + (reverse ? -1 : 1);
        var row = hit.Row.RowIndex;
        if (column < 0) { column = hit.Table.ColumnCount - 1; row--; }
        else if (column >= hit.Table.ColumnCount) { column = 0; row++; }
        if (row < 0) return new(new(CellEditPosition(structure.Source, hit.Table.Rows[0].Cells[0]), 0), null);
        if (row >= hit.Table.Rows.Length)
        {
            var inserted = InsertRow(structure.Source, hit.Table, hit.Row, 0);
            return inserted is null ? null : new(inserted.Selection, inserted);
        }
        return new(new(CellEditPosition(structure.Source, hit.Table.Rows[row].Cells[column]), 0), null);
    }

    /// <summary>Escapes only pipes which would otherwise split the current cell.</summary>
    public static string EscapeCellPipes(SourceTextSnapshot source, int insertionOffset, string text)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(text);
        if (insertionOffset < 0 || insertionOffset > source.Length)
            throw new ArgumentOutOfRangeException(nameof(insertionOffset));
        if (!text.Contains('|')) return text;
        var prefixSlashes = 0;
        for (var index = insertionOffset - 1; index >= 0 && source.Text[index] == '\\'; index--)
            prefixSlashes++;
        var result = new StringBuilder(text.Length + 2);
        var consecutiveSlashes = prefixSlashes;
        foreach (var character in text)
        {
            if (character == '|')
            {
                if ((consecutiveSlashes & 1) == 0) result.Append('\\');
                result.Append('|');
                consecutiveSlashes = 0;
            }
            else
            {
                result.Append(character);
                consecutiveSlashes = character == '\\' ? consecutiveSlashes + 1 : 0;
            }
        }
        return result.ToString();
    }

    private static MarkdownSourceEdit? InsertRow(SourceTextSnapshot source,
        MarkdownTable table, MarkdownTableRow current, int selectedColumn)
    {
        var after = current.RowIndex == 0 ? table.DelimiterLine : current.Line;
        var lines = DocumentLineMap.Create(source);
        var lineIndex = lines.FindLine(after.Start);
        var ending = lines.Lines[lineIndex].Break.Length > 0 ?
            source.GetText(lines.Lines[lineIndex].Break) :
            lineIndex > 0 && lines.Lines[lineIndex - 1].Break.Length > 0 ?
                source.GetText(lines.Lines[lineIndex - 1].Break) : "\n";
        var cells = Enumerable.Repeat("  ", table.ColumnCount).ToArray();
        var (line, offsets) = RenderRow(table.ContainerPrefix, cells,
            table.Header.LeadingPipe, table.Header.TrailingPipe);
        var at = after.End;
        return new(new(at, 0), ending + line,
            new(at + ending.Length + offsets[selectedColumn] + 1, 0));
    }

    private static MarkdownSourceEdit? DeleteRow(SourceTextSnapshot source,
        MarkdownTable table, MarkdownTableRow current)
    {
        if (current.RowIndex == 0) return null; // The header anchors the table's column schema.
        var lines = DocumentLineMap.Create(source);
        var lineIndex = lines.FindLine(current.Line.Start);
        var start = lines.Lines[lineIndex - 1].Break.Start;
        var range = new SourceRange(start, current.Line.End - start);
        var survivor = current.RowIndex + 1 < table.Rows.Length ?
            table.Rows[current.RowIndex + 1] : table.Rows[current.RowIndex - 1];
        var column = Math.Min(table.ColumnCount - 1, current.Cells.Length - 1);
        var at = CellEditPosition(source, survivor.Cells[column]);
        if (at > range.End) at -= range.Length;
        else if (at >= range.Start) at = range.Start;
        return new(range, "", new(at, 0));
    }

    private static MarkdownSourceEdit? RewriteColumn(SourceTextSnapshot source,
        MarkdownTable table, int selectedRow, int selectedColumn, bool insert)
    {
        if (!insert && table.ColumnCount <= 1) return null;
        var targetColumn = insert ? selectedColumn + 1 :
            Math.Min(selectedColumn, table.ColumnCount - 2);
        var map = DocumentLineMap.Create(source);
        var first = map.FindLine(table.Source.Start);
        var last = map.FindLine(table.Source.End - 1);
        var output = new StringBuilder(table.Source.Length + table.Rows.Length * 5);
        var selected = -1;
        for (var lineIndex = first; lineIndex <= last; lineIndex++)
        {
            var line = map.Lines[lineIndex].Content;
            var row = table.Rows.FirstOrDefault(row => row.Line.Start == line.Start);
            string rewritten;
            int[] offsets;
            if (row is not null)
            {
                var cells = row.Cells.Select(cell => source.GetText(cell.Source)).ToList();
                if (insert) cells.Insert(selectedColumn + 1, "  ");
                else cells.RemoveAt(selectedColumn);
                var prefixEnd = row.LeadingPipe && row.Pipes.Length > 0 ?
                    row.Pipes[0].Start : row.Cells[0].Source.Start;
                var prefix = source.Text[line.Start..prefixEnd];
                (rewritten, offsets) = RenderRow(prefix, cells,
                    row.LeadingPipe, row.TrailingPipe);
                if (row.RowIndex == selectedRow)
                    selected = table.Source.Start + output.Length + offsets[targetColumn] +
                        Math.Min(1, cells[targetColumn].Length);
            }
            else if (line.Start == table.DelimiterLine.Start)
            {
                var cells = table.DelimiterCells.Select(source.GetText).ToList();
                while (cells.Count < table.ColumnCount) cells.Add(" --- ");
                if (insert) cells.Insert(selectedColumn + 1, " --- ");
                else cells.RemoveAt(selectedColumn);
                var prefixEnd = table.DelimiterLeadingPipe ?
                    table.DelimiterLine.Start + table.ContainerPrefix.Length :
                    table.DelimiterCells[0].Start;
                var prefix = source.Text[line.Start..prefixEnd];
                (rewritten, offsets) = RenderRow(prefix, cells,
                    table.DelimiterLeadingPipe, table.DelimiterTrailingPipe);
            }
            else (rewritten, offsets) = (source.GetText(line), []);
            output.Append(rewritten);
            if (lineIndex < last) output.Append(source.GetText(map.Lines[lineIndex].Break));
        }
        if (selected < 0) return null;
        return new(table.Source, output.ToString(), new(selected, 0));
    }

    private static (string Text, int[] CellOffsets) RenderRow(string prefix,
        IReadOnlyList<string> cells, bool leadingPipe, bool trailingPipe)
    {
        var result = new StringBuilder(prefix);
        if (leadingPipe) result.Append('|');
        var offsets = new int[cells.Count];
        for (var index = 0; index < cells.Count; index++)
        {
            if (index > 0) result.Append('|');
            offsets[index] = result.Length;
            result.Append(cells[index]);
        }
        if (trailingPipe) result.Append('|');
        return (result.ToString(), offsets);
    }

    private static int CellEditPosition(SourceTextSnapshot source, MarkdownTableCell cell)
    {
        var position = cell.Source.Start;
        while (position < cell.Source.End && char.IsWhiteSpace(source.Text[position])) position++;
        return position < cell.Source.End ? position :
            cell.Source.Start + Math.Min(1, cell.Source.Length);
    }
}
