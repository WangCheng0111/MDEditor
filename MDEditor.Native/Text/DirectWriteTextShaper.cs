using System.Collections.ObjectModel;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Typography;

namespace MDEditor.Native.Text;

public sealed record GlyphMetrics(int Index, float Advance, float AdvanceOffset, float AscenderOffset);
public sealed record TextBreakCluster(int SourceStart, int SourceLength, CanvasClusterProperties Properties);
public sealed record GlyphRunMetrics(int SourceStart, int SourceLength, string FontFamily, string FontFace,
    float FontSize, uint BidiLevel, float BaselineX, float BaselineY,
    IReadOnlyList<GlyphMetrics> Glyphs, IReadOnlyList<int> ClusterMap, IReadOnlyList<GlyphCluster> Clusters);

/// <summary>Horizontal, unwrapped DirectWrite shaping adapter. No paragraph breaking or editor state.</summary>
public sealed class DirectWriteTextShaper
{
    public ShapedText Shape(ICanvasResourceCreator creator, string text, string family = "Cambria",
        float fontSize = 26, string locale = "zh-CN", bool standardLigatures = true,
        string? cjkFamily = null)
    {
        ArgumentNullException.ThrowIfNull(creator);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        if (!float.IsFinite(fontSize) || fontSize <= 0 || fontSize > 256)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (text.Any(char.IsControl))
            throw new ArgumentException("This adapter accepts a single horizontal line, without tabs or controls.", nameof(text));
        using var format = new CanvasTextFormat
        {
            FontFamily = family, FontSize = fontSize, LocaleName = locale,
            WordWrapping = CanvasWordWrapping.NoWrap,
            Direction = CanvasTextDirection.LeftToRightThenTopToBottom
        };
        using var typography = new CanvasTypography();
        typography.AddFeature(CanvasTypographyFeatureName.StandardLigatures, standardLigatures ? 1u : 0u);
        return Shape(creator, text, format, typography, cjkFamily is null ? null : layout =>
        {
            foreach (var span in ScriptFontPolicy.CjkOverrides(text))
                layout.SetFontFamily(span.Start, span.Length, cjkFamily);
        });
    }

    internal ShapedText Shape(ICanvasResourceCreator creator, string text, CanvasTextFormat format,
        CanvasTypography typography, Action<CanvasTextLayout>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(creator); ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(format); ArgumentNullException.ThrowIfNull(typography);
        if (text.Any(char.IsControl)) throw new ArgumentException("This adapter accepts a single horizontal line, without tabs or controls.", nameof(text));
        var layout = new CanvasTextLayout(creator, text, format, 1_000_000, 4096)
        {
            Options = CanvasDrawTextOptions.NoPixelSnap
        };
        try
        {
            if (text.Length > 0) layout.SetTypography(0, text.Length, typography);
            configure?.Invoke(layout);
            var runs = ItalicMarkerCorrection.Capture(creator, text, layout);
            return new ShapedText(text, layout, runs);
        }
        catch
        {
            layout.Dispose();
            throw;
        }
    }
}

/// <summary>Owns the source layout; captured font faces are borrowed and must not be closed independently.</summary>
public sealed class ShapedText : IDisposable
{
    private readonly CanvasTextLayout _layout;
    private readonly List<CapturedGlyphRun> _runs;
    private readonly Dictionary<SourceRange, (double Y, double Height)?> _verticalInkBounds = new();
    private bool _disposed;
    public string Text { get; }
    public IReadOnlyList<GlyphRunMetrics> Runs { get; }
    public double Advance { get; }
    public double Height { get; }
    public double Baseline { get; }
    public IReadOnlyList<TextBreakCluster> BreakClusters { get; }
    internal void VerifyAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
    internal IReadOnlyList<CapturedGlyphRun> CapturedRuns
    {
        get { VerifyAlive(); return _runs; }
    }

    internal ShapedText(string text, CanvasTextLayout layout, List<CapturedGlyphRun> runs)
    {
        Text = text;
        _layout = layout;
        _runs = runs;
        Runs = new ReadOnlyCollection<GlyphRunMetrics>(runs.Select(run => run.Metrics).ToList());
        Advance = Runs.Sum(run => run.Glyphs.Sum(glyph => (double)glyph.Advance));
        var cursor = 0;
        BreakClusters = Array.AsReadOnly(layout.ClusterMetrics.Select(cluster =>
        {
            var properties = cluster.Properties;
            // Keep the closing marker with its italic cluster. A line-start marker has no
            // preceding ink, so allowing this break would also change its measured advance.
            if (ItalicMarkerCorrection.IsBoundary(layout, text, cursor + cluster.CharacterCount))
                properties &= ~CanvasClusterProperties.CanWrapLineAfter;
            var result = new TextBreakCluster(cursor, cluster.CharacterCount, properties);
            cursor = checked(cursor + cluster.CharacterCount);
            return result;
        }).ToArray());
        if (cursor != text.Length) throw new InvalidOperationException("Incomplete DirectWrite cluster metrics.");
        Height = layout.LayoutBoundsIncludingTrailingWhitespace.Height;
        Baseline = runs.Count == 0 ? 0 : runs[0].Origin.Y;
    }

    /// <summary>Actual glyph ink in layout coordinates, excluding font leading and line spacing.</summary>
    internal (double Y, double Height)? VerticalInkBounds(SourceRange range)
    {
        VerifyAlive();
        if (!new SourceRange(0, Text.Length).Contains(range))
            throw new ArgumentOutOfRangeException(nameof(range));
        if (_verticalInkBounds.TryGetValue(range, out var cached)) return cached;
        var top = double.PositiveInfinity;
        var bottom = double.NegativeInfinity;
        foreach (var run in _runs)
        {
            var clusters = run.Metrics.Clusters.Where(cluster => cluster.SourceStart < range.End &&
                range.Start < cluster.SourceStart + cluster.SourceLength).ToArray();
            if (clusters.Length == 0) continue;
            var metrics = run.Face.GetGlyphMetrics(run.Glyphs.Select(glyph => glyph.Index).ToArray(), false);
            foreach (var cluster in clusters)
                for (var index = cluster.GlyphStart; index < cluster.GlyphStart + cluster.GlyphCount; index++)
                {
                    var bounds = metrics[index].DrawBounds;
                    if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                    // DrawBounds.Y includes default font leading; convert design metrics directly
                    // from the captured glyph baseline instead of adding that line-top offset.
                    var y = run.Origin.Y - run.Glyphs[index].AscenderOffset +
                        (metrics[index].TopSideBearing - metrics[index].VerticalOrigin) * run.FontSize;
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y + bounds.Height * run.FontSize);
                }
        }
        (double Y, double Height)? result = double.IsFinite(top) ? (top, bottom - top) : null;
        _verticalInkBounds.Add(range, result);
        return result;
    }

    /// <summary>Replays the very same glyph IDs, offsets, advances and resolved font faces; does not reshape.</summary>
    public void Draw(CanvasDrawingSession session, Vector2 topLeft, ICanvasBrush brush)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(brush);
        foreach (var run in _runs)
        {
            if (Export.PdfTextDescriptions.Enabled)
                session.DrawGlyphRun(topLeft + run.Origin, run.Face, run.FontSize, run.Glyphs,
                    false, run.BidiLevel, brush, CanvasTextMeasuringMode.Natural, run.Locale,
                    Text.Substring(checked((int)run.SourceStart), run.Map.Length), run.Map, 0);
            else
            session.DrawGlyphRun(topLeft + run.Origin, run.Face, run.FontSize, run.Glyphs,
                false, run.BidiLevel, brush, CanvasTextMeasuringMode.Natural, run.Locale,
                run.Text, run.Map, run.SourceStart);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _verticalInkBounds.Clear();
        _runs.Clear();
        _layout.Dispose();
    }
}

internal sealed record CapturedGlyphRun(Vector2 Origin, CanvasFontFace Face, float FontSize,
    CanvasGlyph[] Glyphs, uint BidiLevel, string Locale, string Text, int[] Map, uint SourceStart,
    GlyphRunMetrics Metrics);

internal sealed partial class GlyphRunCapture : ICanvasTextRenderer
{
    private readonly float _dpi;
    private readonly Matrix3x2 _transform;
    public GlyphRunCapture(float dpi = 96, Matrix3x2? transform = null) { _dpi = dpi; _transform = transform ?? Matrix3x2.Identity; }
    public List<CapturedGlyphRun> Runs { get; } = new();
    public float Dpi => _dpi;
    public bool PixelSnappingDisabled => true;
    public Matrix3x2 Transform => _transform;

    public void DrawGlyphRun(Vector2 point, CanvasFontFace fontFace, float fontSize, CanvasGlyph[] glyphs,
        bool isSideways, uint bidiLevel, object brush, CanvasTextMeasuringMode measuringMode,
        string localeName, string textString, int[] clusterMapIndices, uint characterIndex,
        CanvasGlyphOrientation glyphOrientation)
    {
        if (isSideways || glyphOrientation != CanvasGlyphOrientation.Upright ||
            measuringMode != CanvasTextMeasuringMode.Natural)
            throw new NotSupportedException("Only horizontal natural-metrics runs are supported.");
        var copiedGlyphs = (CanvasGlyph[])glyphs.Clone();
        var copiedMap = (int[])clusterMapIndices.Clone();
        var clusters = GlyphClusterDecoder.Decode(checked((int)characterIndex), copiedMap,
            copiedGlyphs.Select(glyph => glyph.Advance).ToArray());
        var metrics = new GlyphRunMetrics(checked((int)characterIndex), copiedMap.Length,
            Name(fontFace.FamilyNames), Name(fontFace.FaceNames), fontSize, bidiLevel, point.X, point.Y,
            Array.AsReadOnly(copiedGlyphs.Select(glyph => new GlyphMetrics(glyph.Index, glyph.Advance,
                glyph.AdvanceOffset, glyph.AscenderOffset)).ToArray()),
            Array.AsReadOnly((int[])copiedMap.Clone()), clusters);
        Runs.Add(new(point, fontFace, fontSize, copiedGlyphs, bidiLevel, localeName,
            textString, copiedMap, characterIndex, metrics));
    }

    private static string Name(IReadOnlyDictionary<string, string> names) =>
        names.TryGetValue("en-us", out var english) ? english :
        names.TryGetValue("zh-cn", out var chinese) ? chinese :
        names.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).FirstOrDefault() ?? "(unnamed)";

    public void DrawUnderline(Vector2 point, float width, float thickness, float offset, float runHeight,
        CanvasTextDirection direction, object brush, CanvasTextMeasuringMode mode, string locale,
        CanvasGlyphOrientation orientation) => throw new NotSupportedException("No decorations in shaping capture.");
    public void DrawStrikethrough(Vector2 point, float width, float thickness, float offset,
        CanvasTextDirection direction, object brush, CanvasTextMeasuringMode mode, string locale,
        CanvasGlyphOrientation orientation) => throw new NotSupportedException("No decorations in shaping capture.");
    public void DrawInlineObject(Vector2 point, ICanvasTextInlineObject inlineObject, bool isSideways,
        bool isRightToLeft, object brush, CanvasGlyphOrientation orientation) =>
        throw new NotSupportedException("No inline objects in shaping capture.");
}
