using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using System.Numerics;
using System.Text;

namespace MDEditor.Native.Export;

internal sealed class PdfTextDescriptions : IDisposable
{
    [ThreadStatic] internal static bool Enabled;
    private readonly bool _previous;
    internal PdfTextDescriptions() { _previous = Enabled; Enabled = true; }
    public void Dispose() => Enabled = _previous;

    internal static void Draw(CanvasDrawingSession session, Vector2 baseline, CanvasFontFace face,
        GlyphRunLayout run, CanvasGlyph[] glyphs, ICanvasBrush brush, SourceTextSnapshot source, int start = 0)
    {
        if (!Enabled)
        {
            session.DrawGlyphRun(baseline, face, (float)run.FontSize, glyphs, false, run.BidiLevel, brush, CanvasTextMeasuringMode.Natural);
            return;
        }
        var text = new StringBuilder(); var map = new List<int>();
        foreach (var cluster in run.Clusters.OrderBy(c => c.Source.Start))
        {
            if (cluster.GlyphStart < start || cluster.GlyphStart >= start + glyphs.Length) continue;
            var slice = cluster.GeneratedText ?? source.GetText(cluster.Source);
            text.Append(slice);
            map.AddRange(Enumerable.Repeat(cluster.GlyphStart - start, slice.Length));
        }
        session.DrawGlyphRun(baseline, face, (float)run.FontSize, glyphs, false, run.BidiLevel, brush,
            CanvasTextMeasuringMode.Natural, run.Locale, text.ToString(), map.ToArray(), 0);
    }
}
