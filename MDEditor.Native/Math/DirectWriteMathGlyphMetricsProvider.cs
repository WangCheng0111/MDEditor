using MDEditor.Native.Text;
using MDEditor.Typesetting.Mathematics;
using Microsoft.Graphics.Canvas;

namespace MDEditor.Native.Mathematics;

/// <summary>Resolves Unicode math symbols to the exact DirectWrite glyph IDs consumed by native drawing.</summary>
public sealed class DirectWriteMathGlyphMetricsProvider : IMathGlyphMetricsProvider, IDisposable
{
    private readonly CanvasDevice _device;
    private readonly DirectWriteTextShaper _shaper = new();
    private bool _disposed;

    public DirectWriteMathGlyphMetricsProvider() => _device = new CanvasDevice();

    public MathGlyphMetric Measure(string text, string fontFamily, double emSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFamily);
        if (!double.IsFinite(emSize) || emSize <= 0 || emSize > 2048) throw new ArgumentOutOfRangeException(nameof(emSize));
        using var shaped = _shaper.Shape(_device, text, fontFamily, (float)emSize, "en-US", standardLigatures: false);
        if (shaped.Runs.Count != 1 || shaped.Runs[0].Glyphs.Count != 1)
            throw new NotSupportedException($"Math atom '{text}' did not resolve to exactly one DirectWrite glyph.");
        var run = shaped.Runs[0]; var glyph = run.Glyphs[0];
        return new MathGlyphMetric
        {
            Text = text, FontFamily = run.FontFamily, FontFace = run.FontFace,
            GlyphIndex = glyph.Index, Advance = glyph.Advance,
            Ascent = shaped.Baseline, Descent = Math.Max(0, shaped.Height - shaped.Baseline),
            AdvanceOffset = glyph.AdvanceOffset, AscenderOffset = glyph.AscenderOffset
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _device.Dispose();
    }
}
