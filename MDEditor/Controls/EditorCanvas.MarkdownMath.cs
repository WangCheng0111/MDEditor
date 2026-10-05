using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Native.Text;
using MDEditor.Services;
using MDEditor.Typesetting.Layout;
using MDEditor.Typesetting.Markdown;
using MDEditor.Typesetting.Mathematics;
using MDEditor.Typesetting.Typography;

namespace MDEditor.Controls;

public sealed partial class EditorCanvas
{
    private readonly MathWorkerClient _mathClient = new();
    private CancellationTokenSource? _markdownMathCancellation;
    private bool _markdownMathLoading, _markdownMathReady;
    private string? _markdownMathError;
    private IReadOnlyDictionary<int, MathLayoutResult>? _markdownLayouts;
    private readonly Dictionary<(string Formula, MarkdownMathKind Kind), MathLayoutResult> _editableMathCache = new();
    private sealed record FormulaFailure(string Message, int? Position);
    private readonly Dictionary<(string Formula, MarkdownMathKind Kind), FormulaFailure> _invalidEditableMath = new();
    private CancellationTokenSource? _editableMathCancellation;
    private long _editableMathRequestedVersion = -1;

    private ReflowContent EditableContent() => ReflowContent.FromStyled(_editorText.Capture(),
        _editableMathCache, CurrentPresentation(), _imageResources, _typography, _showSampleMathPage);

    private void RequestEditableMathLayouts()
    {
        if (_disposed) return;
        var source = _editorText.Capture().Source;
        if (source.Version == _editableMathRequestedVersion) return;
        _editableMathRequestedVersion = source.Version;
        _editableMathCancellation?.Cancel();
        if (!source.Text.Contains('$') && !source.Text.Contains("\\(") &&
            !source.Text.Contains("\\[")) return;
        var missing = CurrentPresentation().Text.MathSpans
            .Select(node => (Formula: CurrentPresentation().Text.References.LayoutContent(node), Kind:
                node.Kind == MarkdownMathSyntaxKind.Display ? MarkdownMathKind.Display : MarkdownMathKind.Inline))
            .Distinct()
            .Where(formula => !_editableMathCache.ContainsKey(formula) && !_invalidEditableMath.ContainsKey(formula))
            .ToArray();
        if (missing.Length == 0) return;
        _ = PrepareEditableMathAsync(missing, source.Version);
    }

    private async Task PrepareEditableMathAsync((string Formula, MarkdownMathKind Kind)[] formulas,
        long sourceVersion)
    {
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _editableMathCancellation = cancellation;
        try
        {
            var tasks = formulas.Select((formula, index) => LayoutEditableFormulaAsync(new MathLayoutRequest
            {
                RequestId = $"edit-{index}-{Guid.NewGuid():N}",
                Source = formula.Formula,
                Style = formula.Kind == MarkdownMathKind.Display ? MathLayoutStyle.Display : MathLayoutStyle.Text,
                EmSize = formula.Kind == MarkdownMathKind.Display ? TypographyPreset.DisplayMathEm :
                    TypographyPreset.InlineMathEm,
                FontFamily = TypographyPreset.MathFamily
            }, cancellation.Token)).ToArray();
            var results = await Task.WhenAll(tasks);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            for (var index = 0; index < results.Length; index++)
            {
                if (results[index].Layout is { } layout) _editableMathCache[formulas[index]] = layout;
                else if (results[index].Failure is { } failure)
                    _invalidEditableMath[formulas[index]] = failure;
            }
            if (_editorText.Capture().Source.Version == sourceVersion)
            {
                _renderer?.SetContent(EditableContent());
                RequestLayout(force: true, immediate: true);
                InvalidateInteraction();
                UpdateStatusIfReady();
            }
        }
        catch (OperationCanceledException) when (_disposed || cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_disposed) { _inputFeedback = $"正文公式排版失败：{error.Message}"; UpdateStatusIfReady(); }
        }
        finally
        {
            if (ReferenceEquals(_editableMathCancellation, cancellation)) _editableMathCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task<(MathLayoutResult? Layout, FormulaFailure? Failure)> LayoutEditableFormulaAsync(
        MathLayoutRequest request, CancellationToken token)
    {
        try { return ((await _mathClient.LayoutAsync(request, token)).Layout, null); }
        catch (MathWorkerRequestException error) when (error.Response.ErrorCode == "invalid-formula" ||
            error.Response.ErrorCode == "unsupported-glyph")
        {
            return (null, new(error.Response.ErrorMessage ?? error.Response.ErrorCode ?? "公式无效",
                error.Response.ErrorPosition));
        }
    }

    private IEnumerable<(SourceRange Source, SourceRange Fault, string Message)> FormulaDiagnostics()
    {
        if (_invalidEditableMath.Count == 0) yield break;
        var source = _editorText.Capture().Source;
        foreach (var math in CurrentPresentation().Text.MathSpans)
        {
            var key = (CurrentPresentation().Text.References.LayoutContent(math),
                math.Kind == MarkdownMathSyntaxKind.Display ?
                MarkdownMathKind.Display : MarkdownMathKind.Inline);
            if (!_invalidEditableMath.TryGetValue(key, out var failure)) continue;
            var position = CurrentPresentation().Text.References.SourceOffsetForLayoutError(math,
                failure.Position ?? 0);
            yield return (math.Source, new(position,
                position == math.Content.End ? 0 : 1), failure.Message);
        }
    }

    private string? FormulaDiagnosticStatus()
    {
        var diagnostics = FormulaDiagnostics().ToArray();
        if (diagnostics.Length == 0) return null;
        var offset = _selection?.Focus.Surface == TextSurface.Body ? _selection.Value.Focus.Offset : -1;
        var diagnostic = diagnostics.FirstOrDefault(item => item.Source.Start <= offset &&
            offset <= item.Source.End);
        if (diagnostic.Source.Length == 0) diagnostic = diagnostics[0];
        var source = _editorText.Capture().Source;
        var lines = DocumentLineMap.Create(source);
        var line = lines.FindLine(diagnostic.Fault.Start);
        var column = diagnostic.Fault.Start - lines.Lines[line].Content.Start + 1;
        return $" · 公式错误（第 {line + 1} 行，第 {column} 列）：{diagnostic.Message}；源码已保留";
    }

    private TextCaret? HitEditableMathSource(ReflowDocument document, LayoutPoint point)
    {
        var source = _editorText.Capture().Source;
        if (document.Presentation?.Text.Source.Version != source.Version) return null;
        foreach (var math in document.Presentation.Text.MathSpans)
        {
            var kind = math.Kind == MarkdownMathSyntaxKind.Display ? MarkdownMathKind.Display :
                MarkdownMathKind.Inline;
            if (!_editableMathCache.ContainsKey((document.Presentation.Text.References.LayoutContent(math), kind))) continue;
            foreach (var line in document.BodyInteraction.Lines)
            {
                if (point.Y < line.Bounds.Y || point.Y > line.Bounds.Bottom) continue;
                foreach (var span in line.Spans)
                {
                    if (span.Source != math.Source ||
                        point.X < Math.Min(span.StartX, span.EndX) ||
                        point.X > Math.Max(span.StartX, span.EndX)) continue;
                    var offset = point.X < (span.StartX + span.EndX) / 2 ?
                        math.Content.Start : math.Content.End;
                    return new(TextSurface.Body, source.Version, offset, CaretAffinity.Downstream);
                }
            }
        }
        return null;
    }

    private async Task InitializeMarkdownMathAsync()
    {
        if (_disposed || _markdownMathLoading) return;
        _markdownMathLoading = true;
        try
        {
            await PrepareMarkdownMathAsync();
        }
        finally
        {
            _markdownMathLoading = false;
            RequestEditableMathLayouts();
        }
    }

    private async Task PrepareMarkdownMathAsync()
    {
        if (_disposed || _markdownMathReady) return;
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = cancellation.Token;
        _markdownMathCancellation = cancellation;
        try
        {
            var nodes = MarkdownMathSample.Document.Math;
            var tasks = nodes.Select(node => _mathClient.LayoutAsync(new MathLayoutRequest
            {
                RequestId = $"body-{node.Source.Start}-{Guid.NewGuid():N}",
                Source = node.GetContent(MarkdownMathSample.Source),
                Style = node.Kind == MarkdownMathKind.Display ? MathLayoutStyle.Display : MathLayoutStyle.Text,
                EmSize = node.Kind == MarkdownMathKind.Display ? TypographyPreset.DisplayMathEm :
                    TypographyPreset.InlineMathEm,
                FontFamily = TypographyPreset.MathFamily
            }, token)).ToArray();
            var results = await Task.WhenAll(tasks);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            var layouts = new Dictionary<int, MathLayoutResult>();
            for (var index = 0; index < nodes.Length; index++)
                layouts.Add(nodes[index].Source.Start, results[index].Layout!);
            _markdownLayouts = layouts;
            foreach (var node in nodes)
                _editableMathCache.TryAdd((node.GetContent(MarkdownMathSample.Source), node.Kind),
                    layouts[node.Source.Start]);
            _renderer?.SetMarkdownMathLayouts(layouts);
            _renderer?.SetContent(EditableContent());
            _markdownMathReady = true; _markdownMathError = null;
            RequestLayout(force: true, immediate: true);
            UpdateStatusIfReady();
        }
        catch (OperationCanceledException) when (_disposed || cancellation.IsCancellationRequested)
        {
            if (!_disposed) _markdownMathError = "正文公式布局超时或取消";
        }
        catch (Exception error)
        {
            _markdownMathError = error.Message;
        }
        finally
        {
            if (ReferenceEquals(_markdownMathCancellation, cancellation)) _markdownMathCancellation = null;
            cancellation.Dispose();
            UpdateStatusIfReady();
        }
    }
}
