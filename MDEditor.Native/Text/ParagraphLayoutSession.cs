using System.Globalization;
using System.Diagnostics;
using System.Numerics;
using MDEditor.Native.Rendering;
using MDEditor.Core.Text;
using MDEditor.Core.Markdown;
using MDEditor.Typesetting.Layout;
using MDEditor.Typesetting.Hyphenation;
using MDEditor.Typesetting.LineBreaking;
using MDEditor.Typesetting.Typography;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI.Text;

namespace MDEditor.Native.Text;

/// <summary>One paragraph/style/device session. Only pure candidate measurements are cached, not COM layouts.</summary>
public sealed class ParagraphLayoutSession : IDisposable, ILineMeasurementCache
{
    private readonly ICanvasResourceCreator _creator;
    private readonly DirectWriteTextShaper _shaper = new();
    private readonly string _family, _locale, _codeFamily;
    private readonly string? _cjkFamily;
    private readonly float _fontSize;
    private readonly float _lineAdvance, _codeFontScale;
    private readonly bool _ligatures;
    private readonly bool _literalLine;
    private readonly bool _wrapLiteral;
    private readonly MarkdownStyleSpan[] _inlineStyles;
    private readonly CanvasTextFormat _format;
    private readonly CanvasTypography _typography;
    private readonly ShapedText _paragraphShape;
    private readonly ShapedText? _hyphenShape;
    private readonly IReadOnlyList<ClusterInkMargins> _paragraphInk;
    private readonly ParagraphMetricCluster[] _paragraphMetrics;
    private readonly MeasuredTextCluster[] _clusters;
    private readonly double[] _paragraphAdvancePrefix;
    private int _leadingSpaceEnd, _trailingSpaceStart;
    private readonly Dictionary<DiscretionaryLine, LineMeasurement> _measurements = new();
    private readonly Dictionary<BreakCacheKey, LineBreakResult> _breaks = new();
    private readonly HashSet<DiscretionaryLine> _rejected = new();
    private readonly Dictionary<DiscretionaryLine, (SharedResource<PreparedLine> Resource, LinkedListNode<DiscretionaryLine> Node)> _prepared = new();
    private Dictionary<(string Text, int LocalStart), SharedResource<ShapedText>> _lineShapes = new();
    private readonly LinkedList<DiscretionaryLine> _lru = new();
    private readonly bool _reuseSelectedLines;
    private readonly double _stableLineHeight;
    private bool _cacheOnly;
    private long _deadline;
    private bool _disposed;
    public ParagraphItemMap Map { get; private set; }
    internal double StableLineHeight => _stableLineHeight;
    internal double NaturalAdvance => _paragraphShape.Advance;
    internal bool Matches(string text, string family, float fontSize, string locale,
        IReadOnlyList<MarkdownStyleSpan>? inlineStyles = null, bool code = false,
        string? cjkFamily = null, string codeFamily = "Cascadia Code", float lineAdvance = 1.3f,
        float codeFontScale = 1f, bool wrapLiteral = false) =>
        !_disposed && _paragraphShape.Text == text && _family == family && _fontSize == fontSize &&
        _locale == locale && _cjkFamily == cjkFamily && _codeFamily == codeFamily &&
        _lineAdvance == lineAdvance && _codeFontScale == codeFontScale &&
        _ligatures == !code && _literalLine == code && _wrapLiteral == wrapLiteral && Map.Typography.Enabled == !code &&
        _inlineStyles.SequenceEqual(inlineStyles ?? []);
    /// <summary>Keep immutable shaped line layouts across an edit in the same style/device epoch.</summary>
    internal bool TryTransferLineShapesTo(ParagraphLayoutSession replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (_disposed || replacement._disposed || !ReferenceEquals(_creator, replacement._creator) ||
            _family != replacement._family || _fontSize != replacement._fontSize ||
            _cjkFamily != replacement._cjkFamily || _codeFamily != replacement._codeFamily ||
            _lineAdvance != replacement._lineAdvance || _codeFontScale != replacement._codeFontScale ||
            _locale != replacement._locale || _ligatures != replacement._ligatures ||
            _literalLine != replacement._literalLine || _wrapLiteral != replacement._wrapLiteral ||
            Map.Typography.Enabled != replacement.Map.Typography.Enabled ||
            _reuseSelectedLines != replacement._reuseSelectedLines || replacement._lineShapes.Count != 0 ||
            !_inlineStyles.SequenceEqual(replacement._inlineStyles))
            return false;
        // Prepared bindings use old source offsets and are intentionally NOT transferred.
        // ShapedText owns its CanvasTextLayout; the cache reference can move while old frame leases remain alive.
        var empty = replacement._lineShapes;
        replacement._lineShapes = _lineShapes;
        _lineShapes = empty;
        return true;
    }
    /// <summary>Keep the expensive paragraph shape and measured break candidates when only its document offset changes.</summary>
    internal void Rebind(SourceTextSnapshot source, SourceRange paragraph)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (source.GetText(paragraph) != _paragraphShape.Text)
            throw new ArgumentException("A session can only rebind unchanged paragraph text.", nameof(paragraph));
        if (ReferenceEquals(Map.Source, source) && Map.Paragraph == paragraph) return;
        var delta = paragraph.Start - Map.Paragraph.Start;
        var shiftedClusters = _clusters.Select(cluster => new MeasuredTextCluster(
            new SourceRange(checked(cluster.Source.Start + delta), cluster.Source.Length),
            cluster.Advance, cluster.CanBreakAfter)).ToArray();
        var next = ParagraphItemMap.Create(source, paragraph, shiftedClusters, _fontSize,
            Map.Typography, Map.Hyphenation, _hyphenShape?.Advance ?? 0, mergeSpaces: !_wrapLiteral);
        var sameCandidates = Map.Items.SequenceEqual(next.Items) &&
            Map.HyphenBreakItems.SetEquals(next.HyphenBreakItems);
        foreach (var prepared in _prepared.Values) prepared.Resource.Dispose();
        _prepared.Clear(); _lru.Clear();
        if (sameCandidates)
        {
            var measurements = _measurements.Select(entry => (Key: new DiscretionaryLine(
                new SourceRange(checked(entry.Key.Source.Start + delta), entry.Key.Source.Length),
                entry.Key.Suffix), entry.Value)).ToArray();
            var rejected = _rejected.Select(entry => new DiscretionaryLine(
                new SourceRange(checked(entry.Source.Start + delta), entry.Source.Length), entry.Suffix)).ToArray();
            _measurements.Clear(); _rejected.Clear();
            foreach (var entry in measurements) _measurements.Add(entry.Key, entry.Value);
            foreach (var entry in rejected) _rejected.Add(entry);
        }
        else { _measurements.Clear(); _rejected.Clear(); _breaks.Clear(); }
        for (var i = 0; i < _clusters.Length; i++) _clusters[i] = shiftedClusters[i];
        for (var i = 0; i < _paragraphMetrics.Length; i++)
            _paragraphMetrics[i] = _paragraphMetrics[i] with
            {
                Source = new SourceRange(checked(_paragraphMetrics[i].Source.Start + delta),
                    _paragraphMetrics[i].Source.Length)
            };
        _leadingSpaceEnd = checked(_leadingSpaceEnd + delta);
        _trailingSpaceStart = checked(_trailingSpaceStart + delta);
        Map = next;
    }
    public ParagraphLayoutSession(ICanvasResourceCreator creator, SourceTextSnapshot source, SourceRange paragraph,
        string family = "Cambria", float fontSize = 22, string locale = "zh-CN", bool standardLigatures = true,
        CjkTypographyOptions? typography = null, HyphenationOptions? hyphenation = null,
        bool reuseSelectedLines = false, double? stableLineHeight = null,
        IReadOnlyList<MarkdownStyleSpan>? inlineStyles = null,
        string? cjkFamily = null, string codeFamily = "Cascadia Code", float lineAdvance = 1.3f,
        float codeFontScale = 1f, bool literalLine = false, bool wrapLiteral = false)
    {
        ArgumentNullException.ThrowIfNull(creator); ArgumentNullException.ThrowIfNull(source);
        var text = source.GetText(paragraph);
        if (text.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029' or '\u00AD'))
            throw new ArgumentException("The paragraph adapter rejects controls, line separators and discretionary hyphens.", nameof(paragraph));
        if (!float.IsFinite(lineAdvance) || lineAdvance < 1 || lineAdvance > 2)
            throw new ArgumentOutOfRangeException(nameof(lineAdvance));
        if (!float.IsFinite(codeFontScale) || codeFontScale is < 0.5f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(codeFontScale));
        _creator = creator; _family = family; _fontSize = fontSize; _locale = locale; _ligatures = standardLigatures;
        _literalLine = literalLine;
        _wrapLiteral = wrapLiteral;
        _cjkFamily = cjkFamily; _codeFamily = codeFamily;
        _lineAdvance = lineAdvance; _codeFontScale = codeFontScale;
        _inlineStyles = (inlineStyles ?? []).ToArray();
        if (_inlineStyles.Any(span => span.Display.Start < 0 || span.Display.End > text.Length))
            throw new ArgumentOutOfRangeException(nameof(inlineStyles));
        _reuseSelectedLines = reuseSelectedLines;
        _format = new CanvasTextFormat
        {
            FontFamily = family, FontSize = fontSize, LocaleName = locale,
            WordWrapping = CanvasWordWrapping.NoWrap,
            Direction = CanvasTextDirection.LeftToRightThenTopToBottom
        };
        _typography = new CanvasTypography();
        _typography.AddFeature(CanvasTypographyFeatureName.StandardLigatures, standardLigatures ? 1u : 0u);
        hyphenation ??= HyphenationOptions.None;
        _paragraphShape = Shape(text, 0);
        _stableLineHeight = stableLineHeight is > 0 and < 100_000
            ? stableLineHeight.Value : Math.Max(1, _paragraphShape.Height);
        _hyphenShape = hyphenation.Enabled ? Shape(hyphenation.Hyphen, -1) : null;
        try
        {
            _clusters = CaptureClusters(_paragraphShape, paragraph.Start).ToArray();
            Map = ParagraphItemMap.Create(source, paragraph, _clusters, fontSize,
                typography ?? CjkTypographyOptions.Refined, hyphenation, _hyphenShape?.Advance ?? 0,
                mergeSpaces: !wrapLiteral);
            _paragraphInk = MeasureInk(_paragraphShape, paragraph.Start, paragraph.Length);
            _paragraphMetrics = CaptureMetricClusters(_paragraphShape, source, paragraph, _paragraphInk);
            _paragraphAdvancePrefix = new double[_paragraphMetrics.Length + 1];
            for (var i = 0; i < _paragraphMetrics.Length; i++)
                _paragraphAdvancePrefix[i + 1] = _paragraphAdvancePrefix[i] + _paragraphMetrics[i].Advance;
            _leadingSpaceEnd = paragraph.Start;
            while (_leadingSpaceEnd < paragraph.End && source.Text[_leadingSpaceEnd] == ' ') _leadingSpaceEnd++;
            _trailingSpaceStart = paragraph.End;
            while (_trailingSpaceStart > paragraph.Start && source.Text[_trailingSpaceStart - 1] == ' ') _trailingSpaceStart--;
        }
        catch
        {
            _hyphenShape?.Dispose(); _paragraphShape.Dispose();
            _typography.Dispose(); _format.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<MeasuredTextCluster> CaptureClusters(ShapedText shape, int sourceStart)
    {
        var glyphClusters = shape.Runs.SelectMany(run => run.Clusters).OrderBy(c => c.SourceStart).ToArray();
        var graphemes = StringInfo.ParseCombiningCharacters(shape.Text).ToHashSet();
        graphemes.Add(shape.Text.Length);
        var result = new List<MeasuredTextCluster>();
        var start = 0;
        foreach (var raw in shape.BreakClusters)
        {
            var end = raw.SourceStart + raw.SourceLength;
            if (!graphemes.Contains(end) || glyphClusters.Any(c => c.SourceStart < end && end < c.SourceStart + c.SourceLength)) continue;
            var advance = glyphClusters.Where(c => c.SourceStart >= start && c.SourceStart + c.SourceLength <= end).Sum(c => c.Advance);
            var cluster = new MeasuredTextCluster(new(sourceStart + start, end - start), advance,
                (raw.Properties & CanvasClusterProperties.CanWrapLineAfter) != 0);
            // DirectWrite sometimes returns consecutive spaces individually; preserve their final wrap flag as one glue.
            if (result.Count > 0 && TextSpacingPolicy.IsSpaces(shape.Text[start..end]))
            {
                var previous = result[^1];
                if (TextSpacingPolicy.IsSpaces(shape.Text.Substring(previous.Source.Start - sourceStart, previous.Source.Length)))
                {
                    result[^1] = new(new(previous.Source.Start, cluster.Source.End - previous.Source.Start),
                        previous.Advance + cluster.Advance, cluster.CanBreakAfter);
                    start = end; continue;
                }
            }
            result.Add(cluster); start = end;
        }
        if (start != shape.Text.Length) throw new InvalidOperationException("Could not capture glyph/grapheme-safe paragraph boundaries.");
        return result;
    }

    private ShapedText Shape(string text, int localStart)
    {
        Action<CanvasTextLayout>? configure = localStart < 0 ? null : layout =>
        {
            foreach (var span in _inlineStyles)
            {
                var start = Math.Max(localStart, span.Display.Start);
                var end = Math.Min(localStart + text.Length, span.Display.End);
                if (start >= end) continue;
                var offset = start - localStart;
                var length = end - start;
                if ((span.Style & MarkdownVisualStyle.Strong) != 0)
                    layout.SetFontWeight(offset, length, new FontWeight { Weight = 600 });
                if ((span.Style & MarkdownVisualStyle.Emphasis) != 0)
                    layout.SetFontStyle(offset, length, FontStyle.Italic);
                if ((span.Style & MarkdownVisualStyle.Code) != 0)
                {
                    layout.SetFontFamily(offset, length, _codeFamily);
                    layout.SetFontSize(offset, length, _fontSize * _codeFontScale);
                }
            }
            if (_cjkFamily is not null)
                foreach (var span in ScriptFontPolicy.CjkOverrides(text))
                    layout.SetFontFamily(span.Start, span.Length, _cjkFamily);
        };
        return _shaper.Shape(_creator, text, _format, _typography, configure);
    }
    private LineMeasurement? Measure(LineMeasureRequest request)
    {
        var display = Map.GetLine(request); var range = display.Source;
        if (_cacheOnly && Stopwatch.GetTimestamp() > _deadline) throw new CachedLayoutUnavailableException();
        if (_measurements.TryGetValue(display, out var found)) return found;
        if (_rejected.Contains(display)) return null;
        if (_cacheOnly) throw new CachedLayoutUnavailableException();
        if (Map.Typography.Enabled && (_reuseSelectedLines ? !FastLineBoundariesValid(range) :
            !CjkTypographyRules.LineBoundariesValid(Map.Source, Map.Paragraph, range, Map.Typography)))
        { _rejected.Add(display); return null; }
        if (_reuseSelectedLines)
        {
            var fast = MeasureParagraphShapeLine(display);
            _measurements.Add(display, fast);
            return fast;
        }
        using var shape = Shape(display.GetText(Map.Source), range.Start - Map.Paragraph.Start);
        if (shape.Runs.SelectMany(r => r.Clusters).Any(c => c.SourceStart < range.Length && range.Length < c.SourceStart + c.SourceLength))
        { _rejected.Add(display); return null; } // A font may not fuse source text with the generated hyphen.
        using var binding = NativeSnapshotBinding.Create(Map.Source,
            new(0, 0, Math.Max(1, shape.Advance), Math.Max(1, shape.Height)),
            new[] { new ShapedLineInput(shape, range, new(0, 0), display.Suffix) });
        var plan = CreatePlan(shape, binding.Snapshot.Blocks[0].Lines[0]);
        var measured = new LineMeasurement(plan.NaturalWidth, plan.Stretch, plan.Shrink);
        _measurements.Add(display, measured);
        return measured;
    }

    private sealed record ParagraphMetricCluster(SourceRange Source, double Advance, uint BidiLevel,
        int GlyphCount, string Text, double InkLeading, double InkTrailing, bool GraphemeSafe);

    private static ParagraphMetricCluster[] CaptureMetricClusters(ShapedText shape, SourceTextSnapshot source,
        SourceRange paragraph, IReadOnlyList<ClusterInkMargins> inkMargins)
    {
        var boundaries = StringInfo.ParseCombiningCharacters(source.GetText(paragraph))
            .Select(index => paragraph.Start + index).ToHashSet();
        boundaries.Add(paragraph.End);
        var ink = inkMargins.ToDictionary(margin => margin.Source);
        var result = shape.CapturedRuns.SelectMany(run => run.Metrics.Clusters.Select(cluster =>
        {
            var range = new SourceRange(paragraph.Start + cluster.SourceStart, cluster.SourceLength);
            ink.TryGetValue(range, out var margins);
            return new ParagraphMetricCluster(range, cluster.Advance, run.BidiLevel, cluster.GlyphCount,
                source.GetText(range), margins.Leading, margins.Trailing,
                boundaries.Contains(range.Start) && boundaries.Contains(range.End));
        })).OrderBy(cluster => cluster.Source.Start).ToArray();
        var cursor = paragraph.Start;
        foreach (var cluster in result)
        {
            if (cluster.Source.Start != cursor)
                throw new InvalidOperationException("Paragraph shaping clusters are not continuous.");
            cursor = cluster.Source.End;
        }
        if (cursor != paragraph.End)
            throw new InvalidOperationException("Paragraph shaping clusters do not cover the source.");
        return result;
    }

    /// <summary>Exact scalar counterpart of CjkGlyphSpacingPlan.Create over one paragraph shaping pass.</summary>
    private LineMeasurement MeasureParagraphShapeLine(DiscretionaryLine display)
    {
        var start = MetricBoundary(display.Source.Start);
        var end = MetricBoundary(display.Source.End);
        var suffix = display.Suffix.Length > 0;
        var limit = end + (suffix ? 1 : 0);
        var first = -1; var last = -1;
        for (var i = start; i < limit; i++)
        {
            var text = i < end ? _paragraphMetrics[i].Text : display.Suffix;
            if (TextSpacingPolicy.IsSpaces(text)) continue;
            if (first < 0) first = i;
            last = i;
        }
        double natural = _paragraphAdvancePrefix[end] - _paragraphAdvancePrefix[start] +
            (_hyphenShape?.Advance ?? 0) * (suffix ? 1 : 0);
        double stretch = 0, shrink = 0;
        for (var i = start; i < end; i++)
        {
            var entry = _paragraphMetrics[i];
            if (!entry.GraphemeSafe) continue;
            if (i > first && i < last && TextSpacingPolicy.IsSpaces(entry.Text) && entry.Advance > 0)
            {
                shrink += entry.Advance / 3;
                stretch += Math.Max(0, _fontSize * 0.5 - entry.Advance);
            }
            var ltr = (entry.BidiLevel & 1) == 0;
            if (ltr && Map.Typography.CompressPunctuation && entry.GlyphCount == 1 &&
                CjkTypographyRules.IsPunctuation(entry.Text))
            {
                var maximum = Math.Max(0, Math.Min(_fontSize * 0.5, entry.Advance - _fontSize * 0.5));
                var leading = Math.Max(0, entry.InkLeading - _fontSize * Map.Typography.InkClearanceEm);
                var trailing = Math.Max(0, entry.InkTrailing - _fontSize * Map.Typography.InkClearanceEm);
                if (CjkTypographyRules.IsOpening(entry.Text)) { leading = Math.Min(maximum, leading); trailing = 0; }
                else if (CjkTypographyRules.IsClosing(entry.Text)) { leading = 0; trailing = Math.Min(maximum, trailing); }
                else
                {
                    var total = leading + trailing;
                    var scale = total == 0 ? 0 : Math.Min(1, maximum / total);
                    leading *= scale; trailing *= scale;
                }
                var previous = i > start ? _paragraphMetrics[i - 1].Text : "";
                var next = i + 1 < end ? _paragraphMetrics[i + 1].Text : suffix ? display.Suffix : "";
                var baseLeading = i == first && CjkTypographyRules.IsOpening(entry.Text) ||
                    i > first && CjkTypographyRules.IsPunctuation(previous) ? -leading : 0;
                var baseTrailing = i == last && !CjkTypographyRules.IsOpening(entry.Text) ||
                    i < last && CjkTypographyRules.IsPunctuation(next) ? -trailing : 0;
                natural += baseLeading + baseTrailing;
                shrink += leading + baseLeading + trailing + baseTrailing;
            }
            if (!ltr || i + 1 >= limit) continue;
            var nextText = i + 1 < end ? _paragraphMetrics[i + 1].Text : display.Suffix;
            var nextLtr = i + 1 >= end || (_paragraphMetrics[i + 1].BidiLevel & 1) == 0;
            if (!nextLtr) continue;
            if (CjkTypographyRules.IsMixedBoundary(entry.Text, nextText))
            {
                natural += _fontSize * Map.Typography.MixedNaturalEm;
                shrink += _fontSize * (Map.Typography.MixedNaturalEm - Map.Typography.MixedMinimumEm);
                stretch += _fontSize * (Map.Typography.MixedMaximumEm - Map.Typography.MixedNaturalEm);
            }
            else if (CjkTypographyRules.IsInterCharacterBoundary(entry.Text, nextText))
                stretch += _fontSize * Map.Typography.InterCharacterMaximumEm;
        }
        return new(natural, stretch, shrink);
    }

    private int MetricBoundary(int sourcePosition)
    {
        var low = 0; var high = _paragraphMetrics.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_paragraphMetrics[middle].Source.Start < sourcePosition) low = middle + 1;
            else high = middle;
        }
        if (low > 0 && _paragraphMetrics[low - 1].Source.End > sourcePosition)
            throw new InvalidOperationException("A candidate boundary split a shaped paragraph cluster.");
        return low;
    }

    private bool FastLineBoundariesValid(SourceRange line) =>
        (line.Start == Map.Paragraph.Start || line.Start <= _leadingSpaceEnd ||
            CjkTypographyRules.MayBreak(Map.Source, line.Start, Map.Typography)) &&
        (line.End == Map.Paragraph.End || line.End >= _trailingSpaceStart ||
            CjkTypographyRules.MayBreak(Map.Source, line.End, Map.Typography));
    bool ILineMeasurementCache.TryGet(LineMeasureRequest request, out LineMeasurement? measurement)
    {
        if (_cacheOnly && Stopwatch.GetTimestamp() > _deadline) throw new CachedLayoutUnavailableException();
        var display = Map.GetLine(request);
        if (_measurements.TryGetValue(display, out var found)) { measurement = found; return true; }
        measurement = null; return _rejected.Contains(display);
    }
    void ILineMeasurementCache.Measure(LineMeasureRequest request) => Measure(request);

    public LineBreakResult Break(double width, LineBreakOptions? options = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        options = ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (_literalLine)
        {
            if (_wrapLiteral) return LiteralLineBreaker.BreakWrapped(Map, width, Measure, cancellationToken);
            var end = Map.Items.Length;
            var request = new LineMeasureRequest(0, end, end, 0, true);
            var measurement = Measure(request) ??
                throw new InvalidOperationException("A literal code line must have a full-source measurement.");
            return LiteralLineBreaker.Break(Map, width, measurement);
        }
        var key = new BreakCacheKey(width, options.MaximumStretchRatio, options.LinePenalty,
            options.AdjacentFitnessDemerits, options.ConsecutiveFlaggedDemerits,
            options.FinalFlaggedDemerits, options.LastLineAlignment);
        if (_breaks.TryGetValue(key, out var cached)) return cached;
        var result = KnuthPlassLineBreaker.Break(Map.Items, width, options, cancellationToken, Measure);
        if (_breaks.Count >= 8) _breaks.Remove(_breaks.Keys.First());
        _breaks.Add(key, result);
        return result;
    }

    private readonly record struct BreakCacheKey(double Width, double Stretch, double LinePenalty,
        double AdjacentFitness, double ConsecutiveFlagged, double FinalFlagged,
        LastLineAlignment LastLine);

    public ParagraphLayout? Layout(double width, LayoutPoint origin, LineBreakOptions? options = null,
        CancellationToken cancellationToken = default, bool cacheOnly = false, long deadline = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(width) || width <= 0 || width > 100_000) throw new ArgumentOutOfRangeException(nameof(width));
        options = ValidateOptions(options);
        LineBreakResult breaks;
        _cacheOnly = cacheOnly; _deadline = deadline;
        try { breaks = Break(width, options, cancellationToken); }
        finally { _cacheOnly = false; }
        if (!breaks.IsSuccess) return null; // Never force a bad line to the margin or silently change font sizes.
        if (_reuseSelectedLines || _wrapLiteral && Map.Paragraph.Length == 0)
            return LayoutPrepared(width, origin, options, breaks, cancellationToken, cacheOnly, deadline);
        var shapes = new List<ShapedText>();
        NativeSnapshotBinding? binding = null;
        try
        {
            var displays = breaks.Lines.Select(line => Map.GetLine(new(line.StartItemIndex,
                line.EndItemIndex, line.BreakItemIndex, line.BreakWidth, line.IsParagraphEnd))).ToArray();
            if (displays.Length == 0) displays = [new(new(Map.Paragraph.End, 0), "")]; // Empty paragraph.
            var ranges = displays.Select(d => d.Source).ToArray();
            var inputs = new List<ShapedLineInput>();
            double y = origin.Y, maxNatural = width;
            foreach (var display in displays)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var shape = Shape(display.GetText(Map.Source), display.Source.Start - Map.Paragraph.Start);
                shapes.Add(shape); inputs.Add(new(shape, display.Source, new(origin.X, y), display.Suffix));
                maxNatural = Math.Max(maxNatural, shape.Advance);
                y += Math.Max(1, shape.Height) * _lineAdvance;
            }
            var height = inputs[^1].Origin.Y - origin.Y + Math.Max(1, shapes[^1].Height);
            binding = NativeSnapshotBinding.Create(Map.Source, new(origin.X, origin.Y, maxNatural, height), inputs);
            var applied = new List<GlyphJustificationResult>();
            for (var i = 0; i < ranges.Length; i++)
            {
                var plan = CreatePlan(shapes[i], binding.Snapshot.Blocks[i].Lines[0]);
                if (i < breaks.Lines.Length)
                {
                    var selected = breaks.Lines[i];
                    if (Math.Abs(plan.NaturalWidth - selected.NaturalWidth) > 1e-7 ||
                        Math.Abs(plan.Stretch - selected.Stretch) > 1e-7 || Math.Abs(plan.Shrink - selected.Shrink) > 1e-7)
                        throw new InvalidOperationException("Fresh selected-line shaping differs from the candidate measurement.");
                    var ragged = _literalLine || selected.IsParagraphEnd &&
                        options.LastLineAlignment == LastLineAlignment.RaggedRight &&
                        selected.AdjustmentRatio == 0 && selected.NaturalWidth <= width;
                    applied.Add(plan.Apply(selected.AdjustmentRatio, width, ragged));
                }
                else applied.Add(plan.Apply(0, width, true));
            }
            var bounds = new LayoutRect(origin.X, origin.Y, width, height);
            var snapshot = new LayoutSnapshot(Map.Source, bounds, binding.Snapshot.Fonts,
                new[] { new BlockLayout(Map.Paragraph, bounds, applied.Select(a => a.Line)) });
            return new(snapshot, breaks, applied, binding, shapes.Cast<IDisposable>().ToList(), _family, _fontSize, _locale, _ligatures);
        }
        catch
        {
            binding?.Dispose(); foreach (var shape in shapes) shape.Dispose(); throw;
        }
    }

    private sealed class PreparedLine(SharedResource<ShapedText>.Lease shapeLease,
        NativeSnapshotBinding binding, IGlyphSpacingPlan plan) : IDisposable
    {
        internal ShapedText Shape => shapeLease.Value;
        internal NativeSnapshotBinding Binding { get; } = binding;
        internal IGlyphSpacingPlan Plan { get; } = plan;
        public void Dispose() { Binding.Dispose(); shapeLease.Dispose(); }
    }
    private SharedResource<ShapedText>.Lease AcquireShape(string text, int localStart)
    {
        var key = (text, localStart);
        if (!_lineShapes.TryGetValue(key, out var resource))
        {
            resource = new SharedResource<ShapedText>(Shape(text, localStart));
            _lineShapes.Add(key, resource);
            if (_lineShapes.Count > 128)
            {
                var oldest = _lineShapes.First(entry => entry.Key != key);
                _lineShapes.Remove(oldest.Key); oldest.Value.Dispose();
            }
        }
        return resource.Acquire();
    }
    private SharedResource<PreparedLine>.Lease AcquireLine(DiscretionaryLine display, bool cacheOnly)
    {
        if (_prepared.TryGetValue(display, out var cached))
        {
            _lru.Remove(cached.Node); _lru.AddLast(cached.Node); return cached.Resource.Acquire();
        }
        if (cacheOnly) throw new CachedLayoutUnavailableException();
        var shapeLease = AcquireShape(display.GetText(Map.Source),
            display.Source.Start - Map.Paragraph.Start);
        var shape = shapeLease.Value; NativeSnapshotBinding? binding = null;
        try
        {
            binding = NativeSnapshotBinding.Create(Map.Source, new(0, 0, Math.Max(1, shape.Advance), Math.Max(1, shape.Height)),
                [new(shape, display.Source, new(0, 0), display.Suffix)]);
            var plan = CreatePlan(shape, binding.Snapshot.Blocks[0].Lines[0]);
            var resource = new SharedResource<PreparedLine>(new(shapeLease, binding, plan));
            _prepared.Add(display, (resource, _lru.AddLast(display)));
            if (_prepared.Count > 128)
            {
                var oldest = _lru.First!; _lru.RemoveFirst();
                var evicted = _prepared[oldest.Value]; _prepared.Remove(oldest.Value); evicted.Resource.Dispose();
            }
            return resource.Acquire();
        }
        catch { binding?.Dispose(); shapeLease.Dispose(); throw; }
    }
    private ParagraphLayout LayoutPrepared(double width, LayoutPoint origin, LineBreakOptions options,
        LineBreakResult breaks, CancellationToken token, bool cacheOnly, long deadline)
    {
        var leases = new List<IDisposable>(); var results = new List<GlyphJustificationResult>();
        var parts = new List<(NativeSnapshotBinding, LayoutSnapshot)>(); NativeSnapshotBinding? binding = null;
        try
        {
            if (breaks.Lines.Length == 0)
            {
                var emptyHeight = _wrapLiteral ? Math.Max(1, _stableLineHeight) :
                    Math.Max(_stableLineHeight, _fontSize * _lineAdvance);
                var emptyBounds = new LayoutRect(origin.X, origin.Y, width, emptyHeight);
                var emptyLine = new LineLayout(Map.Paragraph,
                    new LayoutRect(origin.X, origin.Y, 0, emptyHeight), origin.Y + _fontSize, 0, []);
                binding = NativeSnapshotBinding.ComposeParts(Map.Source, emptyBounds, parts);
                var emptySnapshot = new LayoutSnapshot(Map.Source, emptyBounds, [],
                    [new BlockLayout(Map.Paragraph, emptyBounds, [emptyLine])]);
                return new(emptySnapshot, breaks, results, binding, leases, _family, _fontSize, _locale, _ligatures);
            }
            double y = origin.Y; var fontBase = 0;
            foreach (var selected in breaks.Lines)
            {
                token.ThrowIfCancellationRequested();
                if (cacheOnly && Stopwatch.GetTimestamp() > deadline) throw new CachedLayoutUnavailableException();
                var display = Map.GetLine(new(selected.StartItemIndex, selected.EndItemIndex,
                    selected.BreakItemIndex, selected.BreakWidth, selected.IsParagraphEnd));
                var lease = AcquireLine(display, cacheOnly); leases.Add(lease); var prepared = lease.Value;
                var plan = prepared.Plan;
                if (Math.Abs(plan.NaturalWidth - selected.NaturalWidth) > 1e-7 ||
                    Math.Abs(plan.Stretch - selected.Stretch) > 1e-7 || Math.Abs(plan.Shrink - selected.Shrink) > 1e-7)
                    throw new InvalidOperationException($"Selected-line plan differs for {_family} {display.Source}: " +
                        $"natural {selected.NaturalWidth:R}->{plan.NaturalWidth:R}, " +
                        $"stretch {selected.Stretch:R}->{plan.Stretch:R}, shrink {selected.Shrink:R}->{plan.Shrink:R}.");
                var ragged = _literalLine || selected.IsParagraphEnd &&
                    options.LastLineAlignment == LastLineAlignment.RaggedRight &&
                    selected.AdjustmentRatio == 0 && selected.NaturalWidth <= width;
                var local = plan.Apply(selected.AdjustmentRatio, width, ragged);
                var lineHeight = Math.Max(_stableLineHeight, local.Line.Bounds.Height);
                LineLayout Move(LineLayout line, int fontOffset) => new(line.Source,
                    new(origin.X, y, line.Bounds.Width, lineHeight), y + line.Baseline, line.Advance,
                    line.Runs.Select(run => new GlyphRunLayout(fontOffset + run.FontIndex, run.Source,
                        new(origin.X + run.BaselineOrigin.X, y + run.BaselineOrigin.Y), run.FontSize,
                        run.BidiLevel, run.Locale, run.Glyphs, run.Clusters)));
                var moved = Move(local.Line, 0);
                parts.Add((prepared.Binding, new(Map.Source, moved.Bounds, prepared.Binding.Snapshot.Fonts,
                    [new(display.Source, moved.Bounds, [moved])])));
                results.Add(new(Move(local.Line, fontBase), local.Spacing, local.WidthError));
                fontBase += prepared.Binding.Snapshot.Fonts.Length;
                y += lineHeight * _lineAdvance;
            }
            var height = results.Count == 0 ? 1 : results[^1].Line.Bounds.Bottom - origin.Y;
            var bounds = new LayoutRect(origin.X, origin.Y, width, height);
            binding = NativeSnapshotBinding.ComposeParts(Map.Source, bounds, parts);
            var snapshot = new LayoutSnapshot(Map.Source, bounds, binding.Snapshot.Fonts,
                [new(Map.Paragraph, bounds, results.Select(r => r.Line))]);
            return new(snapshot, breaks, results, binding, leases, _family, _fontSize, _locale, _ligatures);
        }
        catch { binding?.Dispose(); foreach (var lease in leases) lease.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var cached in _prepared.Values) cached.Resource.Dispose();
        _prepared.Clear(); _lru.Clear(); _measurements.Clear(); _rejected.Clear();
        foreach (var shape in _lineShapes.Values) shape.Dispose();
        _lineShapes.Clear();
        _hyphenShape?.Dispose(); _paragraphShape.Dispose();
        _typography.Dispose(); _format.Dispose();
    }

    private LineBreakOptions ValidateOptions(LineBreakOptions? options)
    {
        options ??= new(maximumStretchRatio: Map.Typography.Enabled ? 1 : 3);
        if (Map.Typography.Enabled && options.MaximumStretchRatio > 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Refined typography has finite maximum spacing: MaximumStretchRatio must not exceed 1.");
        return options;
    }

    private IGlyphSpacingPlan CreatePlan(ShapedText shape, LineLayout line) => Map.Typography.Enabled
        ? CjkGlyphSpacingPlan.Create(Map.Source, line, _fontSize, Map.Typography, MeasureInk(shape, line.Source.Start, line.Source.Length))
        : GlyphSpacingPlan.Create(Map.Source, line, _fontSize);

    private static IReadOnlyList<ClusterInkMargins> MeasureInk(ShapedText shape, int sourceStart, int lineSourceLength)
    {
        var result = new List<ClusterInkMargins>();
        foreach (var run in shape.CapturedRuns)
        {
            if ((run.BidiLevel & 1) != 0) continue;
            var metrics = run.Face.GetGlyphMetrics(run.Glyphs.Select(g => g.Index).ToArray(), false);
            foreach (var c in run.Metrics.Clusters)
            {
                if (c.SourceStart >= lineSourceLength) continue; // Generated hyphens are fixed-width, never punctuation-compressed.
                double left = double.PositiveInfinity, right = double.NegativeInfinity, pen = 0;
                for (var i = c.GlyphStart; i < c.GlyphStart + c.GlyphCount; i++)
                {
                    var bounds = metrics[i].DrawBounds; // Win2D glyph metrics are in em, not design units.
                    var x = pen + run.Glyphs[i].AdvanceOffset + bounds.X * run.FontSize;
                    left = Math.Min(left, x); right = Math.Max(right, x + bounds.Width * run.FontSize);
                    pen += run.Glyphs[i].Advance;
                }
                result.Add(new(new(sourceStart + c.SourceStart, c.SourceLength),
                    Math.Clamp(left, 0, c.Advance), Math.Clamp(c.Advance - right, 0, c.Advance)));
            }
        }
        return result;
    }
}

public sealed class ParagraphLayout : IDisposable
{
    private readonly NativeSnapshotBinding _binding;
    internal NativeSnapshotBinding Binding => _binding;
    private readonly List<IDisposable> _owners;
    private bool _disposed;
    public LayoutSnapshot Snapshot { get; }
    public LineBreakResult Breaks { get; }
    public IReadOnlyList<GlyphJustificationResult> Lines { get; }
    internal string Family { get; }
    internal float FontSize { get; }
    internal string Locale { get; }
    internal bool Ligatures { get; }
    internal ParagraphLayout(LayoutSnapshot snapshot, LineBreakResult breaks, List<GlyphJustificationResult> lines,
        NativeSnapshotBinding binding, List<IDisposable> owners, string family, float fontSize, string locale, bool ligatures)
    {
        Snapshot = snapshot; Breaks = breaks; Lines = Array.AsReadOnly(lines.ToArray()); _binding = binding; _owners = owners;
        Family = family; FontSize = fontSize; Locale = locale; Ligatures = ligatures;
    }
    public void Draw(CanvasDrawingSession session, Vector2 offset, ICanvasBrush brush)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _binding.Draw(Snapshot, session, offset, brush);
    }
    internal void DrawLine(int index, CanvasDrawingSession session, Vector2 offset, ICanvasBrush brush)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _binding.DrawLine(Snapshot, 0, index, session, offset, brush);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _binding.Dispose(); foreach (var owner in _owners) owner.Dispose(); _owners.Clear();
    }
}

internal sealed class CachedLayoutUnavailableException : Exception;
