using System.Numerics;
using MDEditor.Typesetting.Typography;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI.Text;

namespace MDEditor.Native.Text;

internal static class ItalicMarkerCorrection
{
    internal static List<CapturedGlyphRun> Capture(ICanvasResourceCreator creator, string text,
        CanvasTextLayout layout)
    {
        // Ordinary text takes one shaping pass. Only a visible italic-to-marker boundary
        // needs outline measurement; synthetic CJK italics can extend past their advance.
        var boundaries = new List<int>();
        for (var index = 1; index < text.Length; index++)
            if (IsBoundary(layout, text, index)) boundaries.Add(index);
        var capture = new GlyphRunCapture();
        layout.DrawToTextRenderer(capture, Vector2.Zero);
        var changed = false;
        foreach (var index in boundaries)
        {
            var gap = Measure(creator, capture.Runs, index - 1, capture.Runs, index);
            if (gap <= 0) continue;
            layout.SetCharacterSpacing(index, 1,
                layout.GetLeadingCharacterSpacing(index) + (float)gap,
                layout.GetTrailingCharacterSpacing(index), layout.GetMinimumCharacterAdvance(index));
            changed = true;
        }
        if (!changed) return capture.Runs;
        // Capture again after setting real advances, so drawing, selection, hit testing,
        // paragraph measurement and the selected-line solver all consume identical metrics.
        capture = new GlyphRunCapture();
        layout.DrawToTextRenderer(capture, Vector2.Zero);
        return capture.Runs;
    }

    internal static bool IsBoundary(CanvasTextLayout layout, string text, int index) =>
        index > 0 && index < text.Length && text[index] is '*' or '_' &&
        !char.IsWhiteSpace(text[index - 1]) && ItalicMarkerSpacing.IsBoundary(text[index],
            layout.GetFontStyle(index - 1) != FontStyle.Normal,
            layout.GetFontStyle(index) != FontStyle.Normal);

    internal static double Measure(ICanvasResourceCreator creator,
        IReadOnlyList<CapturedGlyphRun> previous, int previousIndex,
        IReadOnlyList<CapturedGlyphRun> marker, int markerIndex,
        double markerOrigin = 0, double minimumExistingGap = 0)
    {
        var left = Find(previous, previousIndex);
        var right = Find(marker, markerIndex);
        if (left is not { } l || right is not { } r ||
            l.Cluster.SourceStart + l.Cluster.SourceLength != previousIndex + 1 ||
            r.Cluster.SourceStart != markerIndex) return 0;
        var leftInk = Bounds(creator, l.Run, l.Cluster);
        var rightInk = Bounds(creator, r.Run, r.Cluster);
        if (leftInk.Width <= 0 || rightInk.Width <= 0) return 0;
        return ItalicMarkerSpacing.AdditionalAdvance(leftInk.Right,
            markerOrigin + rightInk.Left, l.Run.FontSize, minimumExistingGap);
    }

    private static (CapturedGlyphRun Run, GlyphCluster Cluster)? Find(
        IReadOnlyList<CapturedGlyphRun> runs, int index)
    {
        foreach (var run in runs)
        {
            if ((run.BidiLevel & 1) != 0) continue;
            foreach (var cluster in run.Metrics.Clusters)
                if (cluster.SourceStart <= index && index < cluster.SourceStart + cluster.SourceLength)
                    return (run, cluster);
        }
        return null;
    }

    private static Windows.Foundation.Rect Bounds(ICanvasResourceCreator creator,
        CapturedGlyphRun run, GlyphCluster cluster)
    {
        var pen = 0f;
        for (var index = 0; index < cluster.GlyphStart; index++) pen += run.Glyphs[index].Advance;
        using var outline = CanvasGeometry.CreateGlyphRun(creator, run.Origin + new Vector2(pen, 0),
            run.Face, run.FontSize, run.Glyphs.AsSpan(cluster.GlyphStart, cluster.GlyphCount).ToArray(),
            false, run.BidiLevel, CanvasTextMeasuringMode.Natural, CanvasGlyphOrientation.Upright);
        return outline.ComputeBounds();
    }
}
