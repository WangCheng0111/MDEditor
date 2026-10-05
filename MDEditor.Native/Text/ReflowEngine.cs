using MDEditor.Typesetting.Hyphenation;
using System.Diagnostics;
using MDEditor.Native.Rendering;
using MDEditor.Typesetting.Mathematics;
using MDEditor.Typesetting.Typography;
using Microsoft.Graphics.Canvas;

namespace MDEditor.Native.Text;

/// <summary>One device/source/style epoch. Serial worker access; caches only width-independent pure line measurements.</summary>
public sealed class ReflowEngine : IDisposable
{
    private readonly CanvasDevice _device;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<ParagraphLayoutSession?> _sessions = new();
    private readonly List<SharedResource<EditableMathParagraphSession>?> _editableMathSessions = new();
    private readonly List<SharedResource<EditableTableRowSession>?> _tableSessions = new();
    private readonly Dictionary<(int StyleIndex, string Family, string? CjkFamily,
        string CodeFamily, float FontSize, string Locale, float LineAdvance), double> _stableLineHeights = new();
    private ReflowContent _content = ReflowSample.Content;
    private ReflowContent? _boundContent;
    private SharedResource<MarkdownMathSession>? _mathSession;
    private IReadOnlyDictionary<int, MathLayoutResult>? _mathLayouts;
    private IReadOnlyDictionary<int, MathLayoutResult>? _boundMathLayouts;
    private TypographyPreset? _boundMathTypography;
    private volatile bool _disposed;
    public ReflowEngine(CanvasDevice device) { ArgumentNullException.ThrowIfNull(device); _device = device; }
    public void SetContent(ReflowContent content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(content);
        Volatile.Write(ref _content, content);
    }
    public void SetMarkdownMathLayouts(IReadOnlyDictionary<int, MathLayoutResult> layouts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(layouts);
        Volatile.Write(ref _mathLayouts, layouts);
    }
    /// <summary>Bounded, no-shaping path. Serial lock is never waited for on the UI thread.</summary>
    public bool TryBuildCached(double width, out ReflowDocument? document)
    {
        document = null;
        if (_disposed || !_serial.Wait(0)) return false;
        try
        {
            var content = Volatile.Read(ref _content);
            if (!ReferenceEquals(_boundContent, content) || _sessions.Count != content.Paragraphs.Count ||
                _editableMathSessions.Count != content.Paragraphs.Count ||
                _tableSessions.Count != content.Paragraphs.Count) return false;
            if (!ReferenceEquals(_boundMathLayouts, Volatile.Read(ref _mathLayouts)) ||
                _boundMathTypography != content.Typography) return false;
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 6 / 1000;
            document = ReflowDocument.CreateCore(_device, width, content, _sessions,
                _editableMathSessions, _tableSessions, _mathSession,
                _lifetime.Token, true, deadline);
            return true;
        }
        catch (CachedLayoutUnavailableException) { return false; }
        finally { _serial.Release(); }
    }
    public Task<ReflowDocument> BuildAsync(double width, CancellationToken token) =>
        Task.Run(() => Build(width, token), token);
    internal ReflowDocument Build(double width, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        _serial.Wait(linked.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var content = Volatile.Read(ref _content);
            if (!ReferenceEquals(_boundContent, content))
            {
                UpdateParagraphSessions(content, linked.Token);
                _boundContent = content;
            }
            var layouts = Volatile.Read(ref _mathLayouts);
            if (!ReferenceEquals(layouts, _boundMathLayouts) ||
                _boundMathTypography != content.Typography)
            {
                _mathSession?.Dispose(); _mathSession = null;
                _boundMathLayouts = layouts; _boundMathTypography = content.Typography;
                if (layouts is not null)
                    _mathSession = new SharedResource<MarkdownMathSession>(
                        MarkdownMathSession.Create(_device, layouts, content.Typography));
            }
            return ReflowDocument.CreateCore(_device, width, content, _sessions,
                _editableMathSessions, _tableSessions, _mathSession, linked.Token);
        }
        finally { if (_disposed) ClearSessions(); _serial.Release(); }
    }
    private void UpdateParagraphSessions(ReflowContent content, CancellationToken token)
    {
        if (_sessions.Count != _editableMathSessions.Count ||
            _sessions.Count != _tableSessions.Count) ClearParagraphSessions();
        if (_sessions.Count != 0 && _sessions.Count != content.Paragraphs.Count)
            AlignUnchangedParagraphSessions(content);
        for (var index = 0; index < content.Paragraphs.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            if (content.Paragraphs[index].ThematicBreak is { MarkerHidden: true })
            {
                if (index < _sessions.Count)
                {
                    _sessions[index]?.Dispose(); _sessions[index] = null;
                    _editableMathSessions[index]?.Dispose(); _editableMathSessions[index] = null;
                    _tableSessions[index]?.Dispose(); _tableSessions[index] = null;
                }
                else
                {
                    _sessions.Add(null); _editableMathSessions.Add(null); _tableSessions.Add(null);
                }
                continue;
            }
            if (content.Paragraphs[index].Table is { } table)
            {
                var tableReplacement = table.RowIndex < 0 ? null :
                    new SharedResource<EditableTableRowSession>(
                        new EditableTableRowSession(_device, content, index));
                if (index < _sessions.Count)
                {
                    _sessions[index]?.Dispose(); _sessions[index] = null;
                    _editableMathSessions[index]?.Dispose(); _editableMathSessions[index] = null;
                    _tableSessions[index]?.Dispose(); _tableSessions[index] = tableReplacement;
                }
                else
                {
                    _sessions.Add(null); _editableMathSessions.Add(null);
                    _tableSessions.Add(tableReplacement);
                }
                continue;
            }
            if (index < _tableSessions.Count)
            {
                _tableSessions[index]?.Dispose(); _tableSessions[index] = null;
            }
            else _tableSessions.Add(null);
            if (content.HasVisualAtoms(index))
            {
                var math = new SharedResource<EditableMathParagraphSession>(
                    new EditableMathParagraphSession(_device, content, index));
                if (index < _sessions.Count)
                {
                    _sessions[index]?.Dispose(); _sessions[index] = null;
                    _editableMathSessions[index]?.Dispose(); _editableMathSessions[index] = math;
                }
                else { _sessions.Add(null); _editableMathSessions.Add(math); }
                continue;
            }
            if (index < _editableMathSessions.Count)
            {
                _editableMathSessions[index]?.Dispose(); _editableMathSessions[index] = null;
            }
            else _editableMathSessions.Add(null);
            var style = content.Paragraphs[index];
            var text = content.Source.GetText(style.Source);
            if (index < _sessions.Count && _sessions[index] is { } existing &&
                existing.Matches(text, style.Family, style.FontSize, style.Locale,
                    style.InlineStyles, style.Code is not null || style.SourceLine,
                    style.CjkFamily, style.CodeFamily, style.LineAdvance,
                    style.StyleIndex == ReflowContent.FileStyleIndex ? 0.85f : 1f, style.SourceLine))
            {
                existing.Rebind(content.Source, style.Source);
                continue;
            }
            var key = (style.StyleIndex, style.Family, style.CjkFamily,
                style.CodeFamily, style.FontSize, style.Locale, style.LineAdvance);
            _stableLineHeights.TryGetValue(key, out var stableLineHeight);
            var replacement = new ParagraphLayoutSession(_device, content.Source, style.Source,
                style.Family, style.FontSize, style.Locale,
                standardLigatures: style.Code is null && !style.SourceLine,
                typography: style.Code is null && !style.SourceLine ? null : CjkTypographyOptions.Legacy,
                hyphenation: style.Code is null && !style.SourceLine ? HyphenationOptions.EnglishUs : HyphenationOptions.None,
                reuseSelectedLines: style.Code is null && !style.SourceLine,
                stableLineHeight: stableLineHeight > 0 ? stableLineHeight : null,
                inlineStyles: style.InlineStyles,
                cjkFamily: style.CjkFamily, codeFamily: style.CodeFamily,
                lineAdvance: style.LineAdvance,
                codeFontScale: style.StyleIndex == ReflowContent.FileStyleIndex ? 0.85f : 1f,
                literalLine: style.Code is not null || style.SourceLine, wrapLiteral: style.SourceLine);
            _stableLineHeights.TryAdd(key, replacement.StableLineHeight);
            if (index < _sessions.Count)
            {
                var previous = _sessions[index];
                previous?.TryTransferLineShapesTo(replacement);
                _sessions[index] = replacement; previous?.Dispose();
            }
            else _sessions.Add(replacement);
        }
    }
    /// <summary>
    /// A newline edit shifts indices. Preserve unchanged prefix/suffix text sessions (including
    /// their shaped glyphs and break measurements); Rebind moves their source coordinates below.
    /// Visual-atom/table sessions are recreated because their document-wide bindings may change.
    /// </summary>
    private void AlignUnchangedParagraphSessions(ReflowContent content)
    {
        var previous = _boundContent;
        if (previous is null) { ClearParagraphSessions(); return; }
        var old = _sessions.ToArray();
        var retained = new bool[old.Length];
        var aligned = new ParagraphLayoutSession?[content.Paragraphs.Count];
        bool Matches(int oldIndex, int newIndex)
        {
            var session = old[oldIndex];
            var style = content.Paragraphs[newIndex];
            var prior = previous.Paragraphs[oldIndex];
            if (prior.StyleIndex != style.StyleIndex || prior.Family != style.Family ||
                prior.FontSize != style.FontSize || prior.Locale != style.Locale ||
                prior.CjkFamily != style.CjkFamily || prior.CodeFamily != style.CodeFamily ||
                prior.LineAdvance != style.LineAdvance || prior.SourceLine != style.SourceLine ||
                !previous.Source.GetText(prior.Source).Equals(
                    content.Source.GetText(style.Source), StringComparison.Ordinal)) return false;
            return session is null || style.Table is null && !content.HasVisualAtoms(newIndex) &&
                session.Matches(content.Source.GetText(style.Source), style.Family, style.FontSize,
                    style.Locale, style.InlineStyles, style.Code is not null || style.SourceLine,
                    style.CjkFamily, style.CodeFamily, style.LineAdvance,
                    style.StyleIndex == ReflowContent.FileStyleIndex ? 0.85f : 1f, style.SourceLine);
        }
        var prefix = 0;
        while (prefix < old.Length && prefix < aligned.Length && Matches(prefix, prefix))
        { aligned[prefix] = old[prefix]; retained[prefix] = old[prefix] is not null; prefix++; }
        var oldTail = old.Length - 1;
        var newTail = aligned.Length - 1;
        while (oldTail >= prefix && newTail >= prefix && Matches(oldTail, newTail))
        {
            aligned[newTail] = old[oldTail];
            retained[oldTail] = old[oldTail] is not null;
            oldTail--; newTail--;
        }
        for (var index = 0; index < old.Length; index++)
            if (!retained[index]) old[index]?.Dispose();
        foreach (var resource in _editableMathSessions) resource?.Dispose();
        foreach (var resource in _tableSessions) resource?.Dispose();
        _sessions.Clear(); _sessions.AddRange(aligned);
        _editableMathSessions.Clear();
        _tableSessions.Clear();
        for (var index = 0; index < aligned.Length; index++)
        { _editableMathSessions.Add(null); _tableSessions.Add(null); }
    }
    private void ClearSessions()
    {
        ClearParagraphSessions();
        _mathSession?.Dispose(); _mathSession = null; _boundMathLayouts = null; _boundMathTypography = null;
        _stableLineHeights.Clear();
    }
    private void ClearParagraphSessions()
    {
        foreach (var session in _sessions) session?.Dispose();
        foreach (var session in _editableMathSessions) session?.Dispose();
        foreach (var session in _tableSessions) session?.Dispose();
        _sessions.Clear(); _editableMathSessions.Clear(); _tableSessions.Clear(); _boundContent = null;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _lifetime.Cancel();
        // Never wait for a worker on the UI thread. The active worker clears the cache in finally.
        if (_serial.Wait(0)) { try { ClearSessions(); } finally { _serial.Release(); } }
        // Keep the cancellation source and semaphore alive until outstanding linked waiters return.
        // They own no native font faces; transferred documents own their own bindings independently.
    }
}
