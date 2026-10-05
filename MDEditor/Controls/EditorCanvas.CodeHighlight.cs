using System;
using System.Threading;
using System.Threading.Tasks;
using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Services;

namespace MDEditor.Controls;

public sealed partial class EditorCanvas
{
    private readonly StarryNightClient _codeHighlighter = new();
    private CancellationTokenSource? _codeHighlightCancellation;
    private SourceTextSnapshot? _codeHighlightRequestedSource;
    private MarkdownCodeHighlight? _codeHighlight;

    private void RequestCodeHighlight(MarkdownCodeBlockStructure structure)
    {
        if (_disposed || ReferenceEquals(_codeHighlightRequestedSource, structure.Source)) return;
        _codeHighlightRequestedSource = structure.Source;
        _codeHighlightCancellation?.Cancel();
        _codeHighlight = null;
        _renderer?.SetCodeHighlight(null);
        if (structure.Blocks.Length == 0) return;
        var cancellation = new CancellationTokenSource();
        _codeHighlightCancellation = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                // Coalesce rapid edits off the UI thread; never delay the visible text/caret.
                await Task.Delay(30, cancellation.Token).ConfigureAwait(false);
                var inputs = MarkdownCodeHighlighter.CreateInputs(structure);
                var result = await _codeHighlighter.HighlightAsync(inputs, cancellation.Token).ConfigureAwait(false);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed || cancellation.IsCancellationRequested ||
                        !ReferenceEquals(_editorText.Capture().Source, result.Source)) return;
                    _codeHighlight = result;
                    _renderer?.SetCodeHighlight(result);
                    // Paint-only update. No re-shaping, reflow, history edit or caret change.
                    if (_canvas is { ReadyToDraw: true } canvas) canvas.Invalidate();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is System.IO.IOException or
                System.Text.Json.JsonException or ArgumentException or InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed || cancellation.IsCancellationRequested ||
                        !ReferenceEquals(_editorText.Capture().Source, structure.Source)) return;
                    _inputFeedback = "代码高亮暂不可用；源码与编辑保留";
                    UpdateStatusIfReady();
                });
            }
            finally
            {
                if (!DispatcherQueue.TryEnqueue(() =>
                {
                    if (ReferenceEquals(_codeHighlightCancellation, cancellation))
                        _codeHighlightCancellation = null;
                    cancellation.Dispose();
                })) cancellation.Dispose();
            }
        });
    }
}
