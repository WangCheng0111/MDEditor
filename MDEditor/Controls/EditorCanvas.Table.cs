using System.Linq;
using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;

namespace MDEditor.Controls;

public sealed partial class EditorCanvas
{
    private void ApplyMarkdownTable(MarkdownTableCommand command)
    {
        if (_imeComposing) return;
        // Toolbar clicks do not pass through the TextBox key handler, which normally
        // flushes and clears its TSF scratch text before a structural operation.
        // Retaining that old fragment anchors the next keystroke to the previous cell.
        MirrorPlainText(_imeInput.Text);
        ClearImeInput();
        if (CurrentEditableSelection() is not { } selection) return;
        var before = _editorText.Capture();
        var planned = MarkdownTableCommands.Plan(CurrentPresentation().Text.Tables,
            selection.Focus.Offset, command);
        if (planned is null)
        {
            _inputFeedback = "请将光标放在可编辑表格单元格；表头和最后一列不能删除";
            UpdateStatusIfReady();
            return;
        }
        ApplySourcePlan(planned, before, selection);
    }

    private bool TryTableTab(StyledDocumentSnapshot before, TextSelection selection, bool reverse)
    {
        var tables = CurrentPresentation().Text.Tables;
        var navigation = MarkdownTableCommands.Tab(tables, selection.Focus.Offset, reverse);
        if (navigation is null) return false;
        if (navigation.Edit is { } edit)
            ApplySourcePlan(edit, before, selection);
        else
        {
            _history.BreakCoalescing();
            var caret = new TextCaret(TextSurface.Body, before.Source.Version,
                navigation.Selection.Start, CaretAffinity.Downstream);
            _selection = new(caret, caret);
            _preferredCaretX = null;
            _inputFeedback = null;
            UpdateMarkdownReveal();
            ResetCaretBlink();
            InvalidateInteraction();
            RevealCaret(_renderer?.Document, _canvas);
            UpdateStatusIfReady();
            FocusImeInput();
        }
        return true;
    }

    private bool TryTableEnter(StyledDocumentSnapshot before, TextSelection selection)
    {
        if (!selection.IsEmpty) return false;
        var planned = MarkdownTableCommands.Plan(CurrentPresentation().Text.Tables,
            selection.Focus.Offset, MarkdownTableCommand.InsertRow);
        if (planned is null) return false;
        ApplySourcePlan(planned, before, selection);
        return true;
    }

    private string PrepareTableInsertion(StyledDocumentSnapshot before,
        TextSelection selection, string text)
    {
        if (SearchViewModel.IsSourceMode || !text.Contains('|') || text.Contains('\n') || text.Contains('\r')) return text;
        var projection = MarkdownRichTextProjection.Create(before.Source).Text;
        var hit = projection.Tables.CellAt(selection.Range.Start);
        if (hit is null || selection.Range.End > hit.Value.Cell.Source.End) return text;
        // A pipe inside `code|span` is already protected by its backtick fence.
        if (projection.SyntaxUnits.Any(unit => unit.Kind == MarkdownSyntaxKind.CodeSpan &&
            unit.Source.Start < selection.Range.Start && selection.Range.End < unit.Source.End))
            return text;
        return MarkdownTableCommands.EscapeCellPipes(before.Source, selection.Range.Start, text);
    }
}
