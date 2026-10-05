using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MDEditor.Core.Text;
using MDEditor.Native.Text;
using MDEditor.Typesetting.Layout;

namespace MDEditor.Controls;

public sealed partial class EditorCanvas
{
    public event Action<StyledDocumentSnapshot>? DocumentChanged;
    public event EventHandler? NewRequested;
    public event EventHandler? OpenRequested;
    public event EventHandler? SaveRequested;
    public event EventHandler? SaveAsRequested;
    public event EventHandler? ExportPdfRequested;

    public StyledDocumentSnapshot CaptureDocumentForFile()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EditorCanvas));
        if (_imeComposing) throw new InvalidOperationException("请先完成当前输入法候选，再执行文件操作。");
        MirrorPlainText(_imeInput.Text);
        ClearImeInput();
        return _editorText.Capture();
    }

    public StyledDocumentSnapshot CaptureDocument() => _editorText.Capture();

    public void LoadDocument(string text, IReadOnlyList<DocumentStyleMarker>? styles = null,
        string? filePath = null, bool showSampleMathPage = false)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EditorCanvas));
        ArgumentNullException.ThrowIfNull(text);
        if (styles is not null && styles.Any(marker => marker.StyleIndex > ReflowContent.FileStyleIndex))
            throw new InvalidDataException("恢复检查点含有未知段落样式。");
        var version = checked(_editorText.Capture().Source.Version + 1);
        var next = new StyledDocumentBuffer(text, styles ?? [new(0, ReflowContent.FileStyleIndex)], version);
        if (_imeComposing) CancelImeComposition();
        ClearImeInput();
        Revoke();
        _documentIdentity++;
        _editableMathCancellation?.Cancel();
        _editableMathRequestedVersion = -1;
        _imageGeneration++;
        ReleaseImageResources();
        _documentDirectory = filePath is null ? null : Path.GetDirectoryName(Path.GetFullPath(filePath));
        _editorText = next;
        _history = new TextEditHistory(next);
        _showSampleMathPage = showSampleMathPage;
        _livePresentation = null;
        _selection = null;
        _pendingImeClick = null;
        _preferredCaretX = null;
        _followCaretAfterCommit = false;
        _inputFeedback = null;
        _error = null;
        _scroll = 0;
        QueueSearch();
        _renderer?.ClearPresentedDocument();
        _renderer?.SetContent(EditableContent());
        RequestEditableMathLayouts();
        RequestImageResources();
        RequestLayout(force: true, immediate: true);
        Invalidate(); InvalidateInteraction(); UpdateStatusIfReady();
    }

    public void SetDocumentPath(string? filePath)
    {
        var directory = filePath is null ? null : Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (string.Equals(directory, _documentDirectory, StringComparison.OrdinalIgnoreCase)) return;
        _documentDirectory = directory;
        _imageGeneration++;
        ReleaseImageResources();
        RequestImageResources();
        _renderer?.SetContent(EditableContent());
        RequestLayout(force: true, immediate: true);
    }
}
