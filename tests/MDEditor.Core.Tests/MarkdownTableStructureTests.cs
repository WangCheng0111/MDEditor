using MDEditor.Core.Markdown;
using MDEditor.Core.Text;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MarkdownTableStructureTests
{
    [TestMethod]
    public void Table_between_display_math_and_step_27_references_stays_a_table()
    {
        var tableOnly = new SourceTextSnapshot(string.Join('\n',
            "| 名称 | 数值 | 说明 |", "| :--- | :---: | :---: |",
            "| 中文 | 42 | a\\|b |", "| `code` | 100 | **加粗** |"), 1);
        Assert.HasCount(1, MarkdownRichTextProjection.Create(tableOnly).Text.Tables.Tables);
        var mathBefore = new SourceTextSnapshot("\\[\\sqrt{x+1}\\]\n\n" + tableOnly.Text, 1);
        Assert.HasCount(1, MarkdownRichTextProjection.Create(mathBefore).Text.Tables.Tables);
        var imageAfter = new SourceTextSnapshot(tableOnly.Text +
            "\n\n![本地示意图](Assets/Tiles/StoreDisplay-300.png)", 1);
        Assert.HasCount(1, MarkdownRichTextProjection.Create(imageAfter).Text.Tables.Tables);
        var source = new SourceTextSnapshot(string.Join('\n',
            "\\[\\sqrt{x+1}\\]", "", "| 名称 | 数值 | 说明 |", "| :--- | :---: | :---: |",
            "| 中文 | 42 | a\\|b |", "| `code` | 100 | **加粗** |", "",
            "![本地示意图](Assets/Tiles/StoreDisplay-300.png)",
            "脚注先引用[^detail]。", "[^detail]: 脚注内容。",
            "$$\\frac{m+1}{n+1}\\label{eq:ratio}$$", "公式\\eqref{eq:ratio}。"), 1);
        var rich = MarkdownRichTextProjection.Create(source);

        Assert.HasCount(1, rich.Text.Tables.Tables);
        Assert.HasCount(4, rich.TableLines);
        Assert.IsFalse(rich.Text.Display.Text.Contains(":---", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Escaped_and_code_pipes_stay_inside_their_cells()
    {
        var source = new SourceTextSnapshot(
            "| Name | Value |\r\n| :--- | ---: |\r\n| a\\|b | `c|d` |\r\n|   | 42 |", 12);
        var table = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source)).Tables.Single();

        Assert.AreEqual(2, table.ColumnCount);
        Assert.AreEqual(3, table.Rows.Length);
        CollectionAssert.AreEqual(new[] { MarkdownTableAlignment.Left, MarkdownTableAlignment.Right },
            table.Alignments.ToArray());
        Assert.AreEqual(" a\\|b ", source.GetText(table.Rows[1].Cells[0].Source));
        Assert.AreEqual(" `c|d` ", source.GetText(table.Rows[1].Cells[1].Source));
        Assert.AreEqual(3, source.GetText(table.Rows[2].Cells[0].Source).Length);
        Assert.AreEqual("\\", source.GetText(table.Rows[1].EscapedSlashes.Single()));
        Assert.AreEqual("| :--- | ---: |", source.GetText(table.DelimiterLine));
    }

    [TestMethod]
    public void Bare_pipes_and_empty_cells_keep_stable_source_offsets()
    {
        var source = new SourceTextSnapshot("A | B\n---|---\nx|\n|y", 1);
        var table = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source)).Tables.Single();
        Assert.AreEqual(2, table.ColumnCount);
        Assert.AreEqual(3, table.Rows.Length);
        Assert.AreEqual(0, table.Rows[1].Cells[1].Source.Length);
        Assert.AreEqual("x", source.GetText(table.Rows[1].Cells[0].Source));
        Assert.AreEqual("y", source.GetText(table.Rows[2].Cells[0].Source));
        Assert.AreEqual(2, table.Rows[2].Cells.Length);
        Assert.AreEqual(0, table.Rows[2].Cells[1].Source.Length);
        var cell = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source))
            .CellAt(source.Text.IndexOf('x'));
        Assert.AreEqual(1, cell!.Value.Row.RowIndex);
    }

    [TestMethod]
    public void Delimiter_colons_define_the_three_visual_alignments()
    {
        var source = new SourceTextSnapshot(
            "| 名称 | 数值 | 说明 |\n| :--- | :---: | ---: |\n| 中文 | 42 | 注释 |", 1);
        var table = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source)).Tables.Single();
        CollectionAssert.AreEqual(new[] { MarkdownTableAlignment.Left, MarkdownTableAlignment.Center,
            MarkdownTableAlignment.Right }, table.Alignments.ToArray());
    }

    [TestMethod]
    public void Grid_edges_belong_to_the_right_cell_and_padding_is_not_editable_content()
    {
        var widths = new[] { 100d, 80d, 120d };
        Assert.AreEqual(0, MarkdownTableCaretPolicy.ColumnAt(10, 10, widths));
        Assert.AreEqual(1, MarkdownTableCaretPolicy.ColumnAt(110, 10, widths));
        Assert.AreEqual(2, MarkdownTableCaretPolicy.ColumnAt(190, 10, widths));
        Assert.AreEqual(2, MarkdownTableCaretPolicy.ColumnAt(310, 10, widths));
        Assert.AreEqual(-1, MarkdownTableCaretPolicy.ColumnAt(311, 10, widths));

        var source = new SourceTextSnapshot("| a\\|b | **加粗** |", 1);
        var cell = new SourceRange(source.Text.IndexOf(" **", StringComparison.Ordinal), 8);
        Assert.AreEqual(cell.Start + 1, MarkdownTableCaretPolicy.EditableOffset(source, cell, cell.Start));
        Assert.AreEqual(cell.End - 1, MarkdownTableCaretPolicy.EditableOffset(source, cell, cell.End));
        var empty = new SourceRange(source.Text.IndexOf(" |", StringComparison.Ordinal), 0);
        Assert.AreEqual(empty.Start, MarkdownTableCaretPolicy.EditableOffset(source, empty, 0));
    }

    [TestMethod]
    public void Clicks_beside_a_table_row_are_consumed_without_a_cell_caret()
    {
        var widths = new[] { 100d, 80d, 120d };
        var left = MarkdownTableCaretPolicy.HitTest(9, 150, 10, 120, 180, widths);
        var right = MarkdownTableCaretPolicy.HitTest(311, 150, 10, 120, 180, widths);
        Assert.IsTrue(left.InRow);
        Assert.AreEqual(-1, left.Column);
        Assert.IsTrue(right.InRow);
        Assert.AreEqual(-1, right.Column);
        Assert.AreEqual(new MarkdownTablePointerHit(true, 0),
            MarkdownTableCaretPolicy.HitTest(10, 150, 10, 120, 180, widths));
        Assert.AreEqual(new MarkdownTablePointerHit(true, 2),
            MarkdownTableCaretPolicy.HitTest(310, 150, 10, 120, 180, widths));
        Assert.IsFalse(MarkdownTableCaretPolicy.HitTest(9, 119, 10, 120, 180, widths).InRow);
        Assert.IsFalse(MarkdownTableCaretPolicy.HitTest(311, 181, 10, 120, 180, widths).InRow);
    }

    [TestMethod]
    public void Inline_code_inside_a_table_cell_keeps_its_code_style_range()
    {
        var source = new SourceTextSnapshot(
            "| 名称 | 说明 |\n|---|---|\n| `code` | **加粗** |", 1);
        var rich = MarkdownRichTextProjection.Create(source);
        var code = rich.TableLines.Single(line => line.RowIndex == 1).Cells[0];
        var strong = rich.TableLines.Single(line => line.RowIndex == 1).Cells[1];
        Assert.IsTrue(rich.Styles.Any(style => (style.Style & MarkdownVisualStyle.Code) != 0 &&
            style.Display.Start < code.End && style.Display.End > code.Start));
        Assert.IsTrue(rich.Styles.Any(style => (style.Style & MarkdownVisualStyle.Strong) != 0 &&
            style.Display.Start < strong.End && style.Display.End > strong.Start));
    }

    [TestMethod]
    public void Fenced_code_with_pipes_is_not_a_table()
    {
        var source = new SourceTextSnapshot("```md\n| A | B |\n|---|---|\n```", 1);
        Assert.AreEqual(0, MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source)).Tables.Length);
    }

    [TestMethod]
    public void Tab_moves_across_cells_and_adds_a_row_at_the_end()
    {
        var source = new SourceTextSnapshot("| A | B |\n| --- | --- |\n| x | y |", 2);
        var structure = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source));
        var x = source.Text.IndexOf('x');
        var y = source.Text.IndexOf('y');

        Assert.AreEqual(y, MarkdownTableCommands.Tab(structure, x, reverse: false)!.Selection.Start);
        Assert.AreEqual(x, MarkdownTableCommands.Tab(structure, y, reverse: true)!.Selection.Start);
        var add = MarkdownTableCommands.Tab(structure, y, reverse: false)!.Edit!;
        var changed = Apply(source, add);
        Assert.AreEqual("| A | B |\n| --- | --- |\n| x | y |\n|  |  |", changed.Text);
        Assert.AreEqual(3, MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(changed))
            .Tables.Single().Rows.Length);
        Assert.IsTrue(add.Selection.Start > source.Length);
    }

    [TestMethod]
    public void Column_edits_keep_existing_cells_and_outside_text()
    {
        var source = new SourceTextSnapshot("before\n\n| A | B |\n| :--- | ---: |\n| x | y |\n\nafter", 2);
        var structure = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source));
        var insert = MarkdownTableCommands.Plan(structure, source.Text.IndexOf('x'),
            MarkdownTableCommand.InsertColumnAfter)!;
        var expanded = Apply(source, insert);
        var table = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(expanded)).Tables.Single();

        Assert.AreEqual(3, table.ColumnCount);
        CollectionAssert.AreEqual(new[] { " A ", "  ", " B " },
            table.Header.Cells.Select(cell => expanded.GetText(cell.Source)).ToArray());
        CollectionAssert.AreEqual(new[] { " x ", "  ", " y " },
            table.Rows[1].Cells.Select(cell => expanded.GetText(cell.Source)).ToArray());
        Assert.IsTrue(expanded.Text.StartsWith("before\n\n"));
        Assert.IsTrue(expanded.Text.EndsWith("\n\nafter"));
        var remove = MarkdownTableCommands.Plan(MarkdownTableStructure.Create(
            MarkdownSyntaxParser.Parse(expanded)), insert.Selection.Start,
            MarkdownTableCommand.DeleteColumn)!;
        Assert.AreEqual(source.Text, Apply(expanded, remove).Text);
    }

    [TestMethod]
    public void Body_row_deletion_preserves_header_delimiter_and_crlf()
    {
        var source = new SourceTextSnapshot("| A | B |\r\n| --- | --- |\r\n| x | y |\r\n| z | q |", 2);
        var structure = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source));
        var plan = MarkdownTableCommands.Plan(structure, source.Text.IndexOf('x'),
            MarkdownTableCommand.DeleteRow)!;
        Assert.AreEqual("| A | B |\r\n| --- | --- |\r\n| z | q |", Apply(source, plan).Text);
        Assert.IsNull(MarkdownTableCommands.Plan(structure, source.Text.IndexOf('A'),
            MarkdownTableCommand.DeleteRow));
    }

    [TestMethod]
    public void Literal_pipe_input_escapes_only_unescaped_delimiters()
    {
        var source = new SourceTextSnapshot("| A | B |\n| --- | --- |\n| x | y |", 1);
        var offset = source.Text.IndexOf('x') + 1;
        Assert.AreEqual("\\|", MarkdownTableCommands.EscapeCellPipes(source, offset, "|"));
        Assert.AreEqual("\\|", MarkdownTableCommands.EscapeCellPipes(source, offset, "\\|"));
        var withSlash = new SourceTextSnapshot(source.Text.Insert(offset, "\\"), 2);
        Assert.AreEqual("|", MarkdownTableCommands.EscapeCellPipes(withSlash, offset + 1, "|"));
        var edited = new SourceTextSnapshot(source.Text.Insert(offset,
            MarkdownTableCommands.EscapeCellPipes(source, offset, "|")), 3);
        var table = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(edited)).Tables.Single();
        Assert.AreEqual(2, table.ColumnCount);
        Assert.AreEqual(" x\\| ", edited.GetText(table.Rows[1].Cells[0].Source));
    }

    [TestMethod]
    public void Render_projection_hides_table_rules_but_maps_escaped_pipe_to_original_source()
    {
        var source = new SourceTextSnapshot("| A | B |\n|---|---|\n| a\\|b | c |", 10);
        var rich = MarkdownRichTextProjection.Create(source);
        var display = rich.Text.Display.Text;
        Assert.IsTrue(display.Contains("a|b"));
        Assert.IsFalse(display.Contains("---"));
        Assert.AreEqual(3, rich.TableLines.Length);
        Assert.AreEqual(0, rich.TableLines[1].Line.Length);
        var start = display.IndexOf("a|b", StringComparison.Ordinal);
        Assert.AreEqual("a\\|b", source.GetText(rich.Text.ToSourceRange(new(start, 3))));
        Assert.AreEqual("\\|", source.GetText(rich.Text.ToSourceRange(new(start + 1, 1))));
        Assert.AreEqual("\\|", source.GetText(rich.Text.BackspaceRange(
            rich.Text.ToSourceOffset(start + 2, ProjectionBoundary.AfterHidden))!.Value));
        Assert.AreSame(source, rich.Text.Source);
    }

    [TestMethod]
    public void Following_fenced_code_is_not_consumed_as_a_table_row()
    {
        var source = new SourceTextSnapshot("| A | B |\n|---|---|\n| x | y |\n```csharp\nint x = 1;\n```", 1);
        var syntax = MarkdownSyntaxParser.Parse(source);
        Assert.AreEqual(2, MarkdownTableStructure.Create(syntax).Tables.Single().Rows.Length);
        Assert.AreEqual(1, syntax.Descendants().Count(node => node.Kind == MarkdownSyntaxKind.FencedCode));
    }

    [TestMethod]
    public void Source_and_visible_cells_remain_synchronized_after_sequential_row_and_column_edits()
    {
        var source = new SourceTextSnapshot(
            "intro\r\n\r\n| H | V |\r\n| :--- | ---: |\r\n| a\\|b | `x|y` |\r\n\r\noutro", 10);
        var originalOutside = (source.Text[..source.Text.IndexOf("| H", StringComparison.Ordinal)],
            source.Text[(source.Text.IndexOf("\r\n\r\noutro", StringComparison.Ordinal))..]);
        var structure = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source));
        var addColumn = MarkdownTableCommands.Plan(structure, source.Text.IndexOf("a\\|b", StringComparison.Ordinal),
            MarkdownTableCommand.InsertColumnAfter)!;
        source = Apply(source, addColumn);
        structure = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source));
        Assert.AreEqual(3, structure.Tables.Single().ColumnCount);
        var addRow = MarkdownTableCommands.Plan(structure, addColumn.Selection.Start,
            MarkdownTableCommand.InsertRow)!;
        source = Apply(source, addRow);
        var rich = MarkdownRichTextProjection.Create(source);
        Assert.AreEqual(4, rich.TableLines.Length);
        Assert.AreEqual(3, rich.Text.Tables.Tables.Single().Rows.Length);
        Assert.IsTrue(rich.Text.Display.Text.Contains("a|b"));
        Assert.IsTrue(rich.Text.Display.Text.Contains("x|y"));
        Assert.IsFalse(rich.Text.Display.Text.Contains(":---"));
        Assert.IsTrue(source.Text.StartsWith(originalOutside.Item1, StringComparison.Ordinal));
        Assert.IsTrue(source.Text.EndsWith(originalOutside.Item2, StringComparison.Ordinal));
        Assert.AreEqual(1, MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source)).Tables.Length);
    }

    [TestMethod]
    public void Empty_table_cell_can_be_targeted_by_tab_and_escaped_pipe_stays_inside_it()
    {
        var source = new SourceTextSnapshot("| A | B |\n|---|---|\n|  |  |", 1);
        var structure = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source));
        var first = structure.Tables.Single().Rows[1].Cells[0];
        var next = MarkdownTableCommands.Tab(structure, first.Source.Start + 1, false)!;
        var second = structure.Tables.Single().Rows[1].Cells[1];
        Assert.AreEqual(second.Source.Start + 1, next.Selection.Start);
        var pipe = MarkdownTableCommands.EscapeCellPipes(source, next.Selection.Start, "|");
        source = new(source.Text.Insert(next.Selection.Start, pipe), source.Version + 1);
        structure = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source));
        Assert.AreEqual(2, structure.Tables.Single().ColumnCount);
        var rich = MarkdownRichTextProjection.Create(source);
        Assert.AreEqual(" | ", rich.Text.Display.GetText(rich.TableLines[2].Cells[1]));
    }

    [TestMethod]
    public void Deleting_a_column_stops_at_one_and_keeps_a_parseable_table()
    {
        var source = new SourceTextSnapshot("| A | B |\n|---|---|\n| x | y |", 1);
        var structure = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source));
        var delete = MarkdownTableCommands.Plan(structure, source.Text.IndexOf('x'),
            MarkdownTableCommand.DeleteColumn)!;
        source = Apply(source, delete);
        structure = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source));
        Assert.AreEqual(1, structure.Tables.Single().ColumnCount);
        Assert.AreEqual("| B |\n|---|\n| y |", source.Text);
        Assert.IsNull(MarkdownTableCommands.Plan(structure, source.Text.IndexOf('y'),
            MarkdownTableCommand.DeleteColumn));
    }

    [TestMethod]
    public void Tables_without_outer_pipes_preserve_their_form_when_adding_a_column()
    {
        var source = new SourceTextSnapshot("before\n\nA | B\n---|---\nx | y\n\nafter", 1);
        var structure = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source));
        var edit = MarkdownTableCommands.Plan(structure, source.Text.IndexOf('x'),
            MarkdownTableCommand.InsertColumnAfter)!;
        source = Apply(source, edit);
        Assert.AreEqual("before\n\nA |  | B\n---| --- |---\nx |  | y\n\nafter", source.Text);
        Assert.AreEqual(3, MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source))
            .Tables.Single().ColumnCount);
    }

    [TestMethod]
    public void Insert_row_and_column_selection_points_inside_the_new_cells()
    {
        const string initial = "| A | B |\n|---|---|\n| x | y |";
        foreach (var cellText in new[] { "A", "B", "x", "y" })
        foreach (var command in new[] { MarkdownTableCommand.InsertRow,
            MarkdownTableCommand.InsertColumnAfter })
        {
            var source = new SourceTextSnapshot(initial, 1);
            var before = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source));
            var caret = source.Text.IndexOf(cellText, StringComparison.Ordinal);
            var hit = before.CellAt(caret)!.Value;
            var edit = MarkdownTableCommands.Plan(before, caret, command)!;
            source = Apply(source, edit);
            source = new(source.Text.Insert(edit.Selection.Start, "Q"), source.Version + 1);
            var table = MarkdownTableStructure.Create(MarkdownSyntaxParser.Parse(source)).Tables.Single();
            var rich = MarkdownRichTextProjection.Create(source);
            if (command == MarkdownTableCommand.InsertRow)
            {
                Assert.AreEqual(3, table.Rows.Length);
                var added = table.Rows[hit.Row.RowIndex + 1];
                Assert.IsTrue(source.GetText(added.Cells[hit.Cell.ColumnIndex].Source).Contains('Q'));
                Assert.IsFalse(source.GetText(table.Rows[hit.Row.RowIndex].Cells[hit.Cell.ColumnIndex].Source)
                    .Contains('Q'));
                Assert.IsTrue(rich.Text.Display.GetText(rich.TableLines.Single(line =>
                    line.RowIndex == added.RowIndex).Cells[hit.Cell.ColumnIndex]).Contains('Q'));
            }
            else
            {
                Assert.AreEqual(3, table.ColumnCount);
                Assert.IsTrue(source.GetText(table.Rows[hit.Row.RowIndex].Cells[hit.Cell.ColumnIndex + 1].Source)
                    .Contains('Q'));
                Assert.IsFalse(source.GetText(table.Rows[hit.Row.RowIndex].Cells[hit.Cell.ColumnIndex].Source)
                    .Contains('Q'));
                Assert.IsTrue(rich.Text.Display.GetText(rich.TableLines.Single(line =>
                    line.RowIndex == hit.Row.RowIndex).Cells[hit.Cell.ColumnIndex + 1]).Contains('Q'));
            }
        }
    }

    private static SourceTextSnapshot Apply(SourceTextSnapshot source, MarkdownSourceEdit edit) =>
        new(source.Text[..edit.Replace.Start] + edit.Text + source.Text[edit.Replace.End..],
            source.Version + 1);
}
