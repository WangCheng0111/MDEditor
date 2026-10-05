using System;
using System.Linq;
using System.Threading.Tasks;
using MDEditor.Native.Export;
using MDEditor.Native.Text;

namespace MDEditor.Controls;

public sealed partial class EditorCanvas
{
    public async Task<NativePdfCapture> CapturePdfAsync()
    {
        CaptureDocumentForFile();
        var identity = _documentIdentity;
        var source = _editorText.Capture().Source;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!_disposed && identity == _documentIdentity && source.Version == _editorText.Capture().Source.Version)
        {
            if (_error is not null) throw new InvalidOperationException($"无法导出：{_error}");
            if (!_pending && !_markdownMathLoading && _editableMathCancellation is null &&
                _codeHighlightCancellation is null &&
                !_imageResources.Values.Any(image => image.Status == MarkdownImageStatus.Loading) &&
                _renderer?.Document is { } document && document.SourceVersion == source.Version)
                return _renderer.CapturePdf();
            if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("正文或资源尚未就绪，请稍后重新导出。");
            await Task.Delay(25);
        }
        throw new InvalidOperationException("导出等待期间文档已改变，请重新导出。");
    }
    public void ReportPdfExport(string message) { _inputFeedback = message; UpdateStatusIfReady(); }
}
