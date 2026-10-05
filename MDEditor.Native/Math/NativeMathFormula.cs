using System.Numerics;
using MDEditor.Native.Text;
using MDEditor.Typesetting.Mathematics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;

namespace MDEditor.Native.Mathematics;

/// <summary>
/// Owns the DirectWrite font faces needed to replay one portable math layout. Drawing consumes
/// the worker's exact glyph IDs and coordinates; formula text is never reshaped during paint.
/// </summary>
public sealed class NativeMathFormula : IDisposable
{
    private readonly Dictionary<FaceKey, CanvasFontFace> _faces = [];
    private readonly List<ShapedText> _owners = [];
    private readonly GlyphPaint[] _glyphs;
    private readonly RulePaint[] _rules;
    private readonly HashSet<int> _geometryGlyphs = [];
    private bool _disposed;

    public MathLayoutResult Layout { get; }

    private NativeMathFormula(ICanvasResourceCreator creator, MathLayoutResult layout)
    {
        ArgumentNullException.ThrowIfNull(creator);
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        var shaper = new DirectWriteTextShaper();
        try
        {
            foreach (var group in layout.Glyphs.GroupBy(glyph => new FaceKey(glyph.FontFamily, glyph.FontFace)))
            {
                var sample = group.First();
                var owner = shaper.Shape(creator, sample.Text, sample.FontFamily, Single(sample.FontSize),
                    "en-US", standardLigatures: false);
                if (owner.CapturedRuns.Count != 1 || owner.CapturedRuns[0].Glyphs.Length != 1)
                {
                    owner.Dispose();
                    throw new InvalidOperationException($"Could not bind math font face {sample.FontFamily} / {sample.FontFace}.");
                }
                var captured = owner.CapturedRuns[0];
                if (!captured.Metrics.FontFamily.Equals(sample.FontFamily, StringComparison.OrdinalIgnoreCase) ||
                    !captured.Metrics.FontFace.Equals(sample.FontFace, StringComparison.OrdinalIgnoreCase))
                {
                    owner.Dispose();
                    throw new InvalidOperationException($"Resolved math face {captured.Metrics.FontFamily} / " +
                        $"{captured.Metrics.FontFace}, expected {sample.FontFamily} / {sample.FontFace}.");
                }
                _owners.Add(owner);
                _faces.Add(group.Key, captured.Face);
            }
            _glyphs = layout.Glyphs.Select(glyph => new GlyphPaint(glyph,
                _faces[new FaceKey(glyph.FontFamily, glyph.FontFace)], ToCanvasGlyph(glyph))).ToArray();
            _rules = layout.Rules.Select(rule => CreateRulePaint(creator, layout, rule)).ToArray();
        }
        catch
        {
            foreach (var owner in _owners) owner.Dispose();
            _owners.Clear(); _faces.Clear();
            throw;
        }
    }

    public static NativeMathFormula Create(ICanvasResourceCreator creator, MathLayoutResult layout) =>
        new(creator, layout);

    public void Draw(CanvasDrawingSession session, Vector2 topLeft, ICanvasBrush ink)
    {
        Guard(session, ink);
        for (var i = 0; i < _glyphs.Length; i++)
        {
            if (_geometryGlyphs.Contains(i)) continue;
            var paint = _glyphs[i];
            var glyph = paint.Placement;
            var baseline = topLeft + new Vector2(Single(glyph.BaselineX), Single(glyph.BaselineY));
            if (Export.PdfTextDescriptions.Enabled)
                session.DrawGlyphRun(baseline, paint.Face, Single(glyph.FontSize), paint.Glyph, false, 0,
                    ink, CanvasTextMeasuringMode.Natural, "en-US", glyph.Text, new int[glyph.Text.Length], 0);
            else session.DrawGlyphRun(baseline, paint.Face, Single(glyph.FontSize), paint.Glyph, false, 0, ink, CanvasTextMeasuringMode.Natural);
        }
        DrawRules(session, topLeft, ink);
    }

    private void DrawRules(CanvasDrawingSession session, Vector2 topLeft, ICanvasBrush ink)
    {
        foreach (var paint in _rules)
        {
            if (paint.Geometry is not null) session.FillGeometry(paint.Geometry, topLeft, ink);
            else session.FillRectangle(new Windows.Foundation.Rect(topLeft.X + Single(paint.Rule.X),
                topLeft.Y + Single(paint.Rule.Y), Single(paint.Rule.Width), Single(paint.Rule.Height)), ink);
        }
    }

    private RulePaint CreateRulePaint(ICanvasResourceCreator creator, MathLayoutResult layout, MathRulePlacement rule)
    {
        var top = layout.Glyphs.Select((glyph, index) => (glyph, index))
            .Where(candidate => candidate.glyph.Text == "√" && candidate.glyph.SourceStart == rule.SourceStart)
            .OrderBy(candidate => candidate.glyph.BaselineY).FirstOrDefault();
        if (top.glyph is null) return new(rule, null);
        var face = _faces[new FaceKey(top.glyph.FontFamily, top.glyph.FontFace)];
        using var glyph = CanvasGeometry.CreateGlyphRun(creator,
            new(Single(top.glyph.BaselineX), Single(top.glyph.BaselineY)), face,
            Single(top.glyph.FontSize), ToCanvasGlyph(top.glyph), false, 0,
            CanvasTextMeasuringMode.Natural, CanvasGlyphOrientation.Upright);
        if (top.glyph.Role == MathGlyphRole.VerticalVariant)
        {
            // The ready-made glyph already transitions from the diagonal shoulder into a
            // RadicalRuleThickness overbar. Union the continuation with that stable segment;
            // sampling or cutting inside the shoulder either exaggerates its overlap or
            // severs the natural join.
            using var continuation = CanvasGeometry.CreateRectangle(creator, new Windows.Foundation.Rect(
                Single(rule.X), Single(rule.Y), Single(rule.Width), Single(rule.Height)));
            var variant = glyph.CombineWith(continuation, Matrix3x2.Identity, CanvasGeometryCombine.Union);
            _geometryGlyphs.Add(top.index);
            return new(rule, variant);
        }
        var connectorEnd = rule.X + rule.Height * 0.5;
        var sampleLeft = connectorEnd - rule.Height * 1.8;
        var sampleWidth = Math.Max(rule.Height * 0.1, 1d / 64);
        using var clip = CanvasGeometry.CreateRectangle(creator, new Windows.Foundation.Rect(
            Single(sampleLeft), Single(rule.Y - rule.Height * 2), Single(sampleWidth), Single(rule.Height * 5)));
        using var sample = glyph.CombineWith(clip, Matrix3x2.Identity, CanvasGeometryCombine.Intersect);
        var band = sample.ComputeBounds();
        if (band.IsEmpty || band.Height <= 0 || !float.IsFinite((float)band.Y) || !float.IsFinite((float)band.Height))
            throw new InvalidOperationException("Could not measure the radical connector outline.");
        var extensionX = sampleLeft + sampleWidth;
        var ruleEnd = rule.X + rule.Width;
        if (ruleEnd <= extensionX)
            throw new InvalidOperationException("The radical continuation has no positive width.");
        using var extension = CanvasGeometry.CreateRectangle(creator, new Windows.Foundation.Rect(
            Single(extensionX), band.Y, Single(ruleEnd - extensionX), band.Height));
        using var cut = CanvasGeometry.CreateRectangle(creator, new Windows.Foundation.Rect(
            Single(extensionX), band.Y - Single(rule.Height * 2), Single(ruleEnd - extensionX),
            band.Height + Single(rule.Height * 4)));
        using var trimmed = glyph.CombineWith(cut, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        var combined = trimmed.CombineWith(extension, Matrix3x2.Identity, CanvasGeometryCombine.Union);
        _geometryGlyphs.Add(top.index);
        return new(rule, combined);
    }

    private void Guard(CanvasDrawingSession session, ICanvasBrush ink)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(ink);
        foreach (var owner in _owners) owner.VerifyAlive();
    }

    private static CanvasGlyph[] ToCanvasGlyph(MathGlyphPlacement glyph) =>
    [
        new CanvasGlyph
        {
            Index = glyph.GlyphIndex, Advance = Single(glyph.Advance),
            AdvanceOffset = Single(glyph.AdvanceOffset), AscenderOffset = Single(glyph.AscenderOffset)
        }
    ];

    internal static float Single(double value)
    {
        if (!double.IsFinite(value) || value < -float.MaxValue || value > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "Math geometry cannot be represented by Direct2D.");
        return (float)value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var rule in _rules) rule.Geometry?.Dispose();
        foreach (var owner in _owners) owner.Dispose();
        _owners.Clear(); _faces.Clear();
    }

    private sealed record GlyphPaint(MathGlyphPlacement Placement, CanvasFontFace Face, CanvasGlyph[] Glyph);
    private sealed record RulePaint(MathRulePlacement Rule, CanvasGeometry? Geometry);
    private readonly record struct FaceKey(string Family, string Face);
}
