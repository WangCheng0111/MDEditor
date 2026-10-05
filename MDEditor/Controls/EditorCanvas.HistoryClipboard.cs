using System;
using System.Linq;
using System.Threading.Tasks;
using MDEditor.Core.Text;
using MDEditor.Core.Documents;
using MDEditor.Native.Text;
using MDEditor.Services;
using MDEditor.Typesetting.Layout;
using MDEditor.Typesetting.Markdown;
using Windows.ApplicationModel.DataTransfer;

namespace MDEditor.Controls;

public sealed partial class EditorCanvas
{
    private TextEditHistory _history;

    private void UndoOrRedo(bool redo)
    {
        var selection = redo ? _history.Redo() : _history.Undo();
        if (selection is not { } restored)
        {
            _inputFeedback = redo ? "没有可重做的编辑" : "没有可撤销的编辑";
            UpdateStatusIfReady();
            return;
        }
        ApplyCommittedSelection(restored, changed: true, immediate: true);
    }

    private void SelectAllSource()
    {
        var surface = _selection?.Focus.Surface ?? TextSurface.Body;
        var source = surface == TextSurface.Body ? _editorText.Capture().Source : MarkdownMathSample.Source;
        _history.BreakCoalescing();
        _selection = new(
            new(surface, source.Version, 0, CaretAffinity.Downstream),
            new(surface, source.Version, source.Length, CaretAffinity.Upstream));
        _inputFeedback = null;
        ResetCaretBlink(); InvalidateInteraction(); UpdateStatusIfReady();
    }

    private bool TrySelectedText(out string text)
    {
        text = "";
        if (_selection is not { IsEmpty: false } selection) return false;
        var source = selection.Anchor.Surface == TextSurface.Body
            ? _editorText.Capture().Source : MarkdownMathSample.Source;
        if (selection.Anchor.SourceVersion != source.Version || selection.Focus.SourceVersion != source.Version ||
            !source.FullRange.Contains(selection.Range)) return false;
        var range = selection.Range;
        if (selection.Anchor.Surface == TextSurface.Body)
        {
            var projection = CurrentPresentation().Text;
            if (range != source.FullRange)
                range = projection.ToSourceRange(projection.ToDisplayRange(range), true);
        }
        text = source.GetText(range);
        return text.Length > 0;
    }

    private bool CopySelection()
    {
        _history.BreakCoalescing();
        if (_selection is not { IsEmpty: false } selection || !TrySelectedText(out var text)) return false;
        try
        {
            var package = new DataPackage();
            var math = selection.Anchor.Surface == TextSurface.MarkdownMath
                ? MarkdownMathSample.Document.FindExactMath(selection.Range) : null;
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            _inputFeedback = math is not null ? "已复制公式源码" : $"已复制 {text.Length} 个 UTF-16 单元";
            UpdateStatusIfReady();
            return true;
        }
        catch (Exception error)
        {
            _inputFeedback = $"复制失败：{error.Message}";
            UpdateStatusIfReady();
            return false;
        }
    }

    private void CutSelection()
    {
        if (CurrentEditableSelection() is not { IsEmpty: false } selection)
        {
            if (_selection?.Anchor.Surface == TextSurface.MarkdownMath) ShowReadOnlyMathFeedback();
            return;
        }
        var version = _editorText.Capture().Source.Version;
        if (!CopySelection() || _disposed || _selection != selection ||
            _editorText.Capture().Source.Version != version) return;
        var before = _editorText.Capture();
        var projection = CurrentPresentation().Text;
        var range = selection.Range == before.Source.FullRange ? before.Source.FullRange :
            projection.ToSourceRange(projection.ToDisplayRange(selection.Range), true);
        var first = new TextCaret(TextSurface.Body, version, range.Start, CaretAffinity.Downstream);
        var last = first with { Offset = range.End, Affinity = CaretAffinity.Upstream };
        ApplyEdit(TextEditingOperations.Replace(_editorText, new(first, last), ""), before, selection,
            HistoryEditKind.Cut);
    }

    private async Task PasteAsync()
    {
        if (CurrentEditableSelection() is not { } selection)
        {
            ShowReadOnlyMathFeedback();
            return;
        }
        _history.BreakCoalescing();
        var before = _editorText.Capture();
        var pointerSelection = _selection;
        var identity = _documentIdentity;
        var directory = _documentDirectory;
        StoredImageAttachment? attachment = null;
        var attachmentCommitted = false;
        bool CanCommit() => !_disposed && IsEnabled && _interactionFocused && !_imeComposing &&
            identity == _documentIdentity && ReferenceEquals(before.Source, _editorText.Capture().Source) &&
            _selection == pointerSelection && directory == _documentDirectory;
        try
        {
            var content = Clipboard.GetContent();
            var png = await ClipboardImageReader.ReadPngAsync(content);
            if (!CanCommit()) return;
            string text;
            if (png is not null)
            {
                if (ImagePasteSuspended)
                {
                    _inputFeedback = "正在保存文档，请完成保存后再粘贴图片";
                    UpdateStatusIfReady();
                    return;
                }
                attachment = await ImageAttachments.StorePngAsync(png, directory);
                if (!CanCommit() || ImagePasteSuspended) return;
                text = attachment.Markdown;
            }
            else if (content.Contains(StandardDataFormats.Text)) text = await content.GetTextAsync();
            else
            {
                _inputFeedback = "剪贴板没有文本或可粘贴的单张图片";
                UpdateStatusIfReady();
                return;
            }
            if (!CanCommit()) return;
            if (text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            {
                _inputFeedback = "剪贴板文本包含不支持的控制字符";
                UpdateStatusIfReady();
                return;
            }
            // The current native paragraph adapter does not shape tab glyphs; match the Tab key's four spaces.
            text = text.Replace("\t", "    ");
            if (!SearchViewModel.IsSourceMode) text = MarkdownMathPaste.Prepare(before, selection.Range, text);
            text = PrepareTableInsertion(before, selection, text);
            var edit = TextEditingOperations.Replace(_editorText, selection, text);
            attachmentCommitted = attachment is not null && edit.Change.Changed;
            if (attachmentCommitted)
            {
                ImageAttachments.Commit(attachment!);
                // Capture the folded image before moving the caret to its trailing source edge.
                // Explicit clicks/navigation can still reveal its Markdown normally.
                _ = CurrentPresentation();
            }
            ApplyEdit(edit, before, selection, HistoryEditKind.Paste);
            if (attachmentCommitted)
            {
                _inputFeedback = "已粘贴图片（本地 PNG 附件）";
                UpdateStatusIfReady();
            }
        }
        catch (Exception error)
        {
            if (_disposed) return;
            _inputFeedback = $"粘贴失败：{error.Message}";
            UpdateStatusIfReady();
        }
        finally
        {
            // Undo/redo and recovery still need committed images; only abandoned insertions are removed.
            if (attachment is not null && !attachmentCommitted)
            {
                try { ImageAttachments.DiscardUncommitted(attachment); }
                catch (System.IO.IOException) { /* A locked unreferenced attachment is safe to retain. */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
