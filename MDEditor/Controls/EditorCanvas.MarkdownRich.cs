using System;
using System.Linq;
using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Native.Text;
using MDEditor.Typesetting.Layout;

namespace MDEditor.Controls;

public sealed partial class EditorCanvas
{
    private MarkdownRichTextProjection? _livePresentation;

    private MarkdownRichTextProjection CurrentPresentation()
    {
        var source = _editorText.Capture().Source;
        if (_livePresentation is { } current && ReferenceEquals(current.Text.Source, source) &&
            current.Text.IsSourceMode == SearchViewModel.IsSourceMode) return current;
        var reveal = _selection is { Anchor.Surface: TextSurface.Body } selection &&
            selection.Focus.SourceVersion == source.Version ? selection.Focus.Offset : (int?)null;
        if (_livePresentation is { } previous)
        {
            var update = MarkdownIncrementalParser.Update(previous.Syntax, source);
            _livePresentation = MarkdownRichTextProjection.FromSyntax(update.Syntax, reveal, SearchViewModel.IsSourceMode);
        }
        else _livePresentation = MarkdownRichTextProjection.FromSyntax(MarkdownSyntaxParser.Parse(source),
            reveal, SearchViewModel.IsSourceMode);
        RequestCodeHighlight(_livePresentation.Text.CodeBlocks);
        return _livePresentation;
    }

    /// <summary>Rebuild only when entering or leaving a visible Markdown construct changes projection.</summary>
    private void UpdateMarkdownReveal()
    {
        if (_disposed || _imeComposing || SearchViewModel.IsSourceMode) return;
        var source = _editorText.Capture().Source;
        var reveal = _selection is { Anchor.Surface: TextSurface.Body, Focus.Surface: TextSurface.Body } selection &&
            selection.Focus.SourceVersion == source.Version ? selection.Focus.Offset : (int?)null;
        var current = CurrentPresentation();
        if (current.Text.RevealedAtSourceOffset == reveal) return;
        var next = MarkdownRichTextProjection.FromSyntax(current.Syntax, reveal);
        if (current.Text.HiddenRanges.SequenceEqual(next.Text.HiddenRanges) &&
            current.Text.ActiveReplacements.SequenceEqual(next.Text.ActiveReplacements) &&
            current.Styles.SequenceEqual(next.Styles) && current.Headings.SequenceEqual(next.Headings) &&
            current.Text.RevealedMath?.Source == next.Text.RevealedMath?.Source)
        {
            _livePresentation = next;
            return;
        }
        _livePresentation = next;
        _renderer?.SetContent(EditableContent());
        RequestLayout(force: true, immediate: true);
    }

    private void ApplyMarkdownFormat(MarkdownFormatKind kind)
    {
        if (_imeComposing || CurrentEditableSelection() is not { } selection) return;
        var before = _editorText.Capture();
        var planned = MarkdownFormatCommands.Plan(before.Source, selection.Range, kind);
        if (planned is null)
        {
            _inputFeedback = "此处没有可切换的单段格式";
            UpdateStatusIfReady();
            return;
        }
        ApplySourcePlan(planned, before, selection);
    }

    private void ApplyMarkdownBlock(MarkdownBlockCommand command)
    {
        if (_imeComposing || CurrentEditableSelection() is not { } selection) return;
        var before = _editorText.Capture();
        var planned = MarkdownBlockCommands.Format(before.Source, selection.Range, command);
        if (planned is null)
        {
            _inputFeedback = "当前行没有可切换的任务标记";
            UpdateStatusIfReady();
            return;
        }
        ApplySourcePlan(planned, before, selection);
    }

    private void ApplySourcePlan(MarkdownSourceEdit planned, StyledDocumentSnapshot before,
        TextSelection selection)
    {
        _history.BreakCoalescing();
        var first = new TextCaret(TextSurface.Body, before.Source.Version,
            planned.Replace.Start, CaretAffinity.Downstream);
        var last = first with { Offset = planned.Replace.End, Affinity = CaretAffinity.Upstream };
        var edit = TextEditingOperations.Replace(_editorText, new(first, last), planned.Text);
        var changed = _editorText.Capture().Source;
        var anchor = new TextCaret(TextSurface.Body, changed.Version,
            planned.Selection.Start, CaretAffinity.Downstream);
        var focus = anchor with { Offset = planned.Selection.End, Affinity = CaretAffinity.Upstream };
        ApplyEdit(edit with { Selection = new(anchor, focus) }, before, selection, HistoryEditKind.Replace);
        FocusImeInput();
    }
}
