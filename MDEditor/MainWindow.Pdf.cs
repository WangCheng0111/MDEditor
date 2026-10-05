using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MDEditor;

public sealed partial class MainWindow
{
    private async Task ExportPdfAsync()
    {
        EditorSurface.CaptureDocumentForFile();
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedFileName = _documentSession.FilePath is null ? "未命名" : Path.GetFileNameWithoutExtension(_documentSession.FilePath);
        picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
        var chosen = await picker.PickSaveFileAsync();
        if (chosen is null) return;
        var path = PickedPath(chosen);
        if (!string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("导出目标必须是 .pdf 文件。");
        try
        {
            EditorSurface.ReportPdfExport("正在导出矢量 PDF…");
            using var capture = await EditorSurface.CapturePdfAsync();
            // Recording finished on the UI thread. COM references are held by the command lists;
            // native replay/compression and disk I/O never read the mutable editor or change its session.
            await Task.Run(async () =>
            {
                var bytes = capture.ToBytes();
                var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
                var temporary = Path.Combine(directory, ".mdeditor-pdf-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await stream.WriteAsync(bytes);
                        await stream.FlushAsync();
                    }
                    File.Move(temporary, path, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            });
            EditorSurface.ReportPdfExport("矢量 PDF 已导出");
        }
        catch
        {
            EditorSurface.ReportPdfExport("PDF 导出失败；正文未改变");
            throw;
        }
    }
}
