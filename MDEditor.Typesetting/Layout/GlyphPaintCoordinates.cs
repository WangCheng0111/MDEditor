namespace MDEditor.Typesetting.Layout;

/// <summary>Round the shared line anchor once, then add each snapshot-local baseline.
/// This matches native DrawTextLayout translation, without per-fragment cancellation drift.</summary>
public static class GlyphPaintCoordinates
{
    /// <summary>DirectWrite uses a local Single accumulator for each styled run, then advances the line pen.</summary>
    public static float AdvancePen(float pen, IEnumerable<GlyphPlacement> glyphs)
    {
        ArgumentNullException.ThrowIfNull(glyphs);
        Single(pen);
        float width = 0;
        foreach (var glyph in glyphs) width = Single((double)width + Single(glyph.Advance));
        return Single((double)pen + width);
    }

    public static LayoutPoint Resolve(LineLayout line, GlyphRunLayout run, LayoutPoint offset)
    {
        ArgumentNullException.ThrowIfNull(line); ArgumentNullException.ThrowIfNull(run);
        var x = Single(line.Bounds.X + offset.X);
        var y = Single(line.Bounds.Y + offset.Y);
        return new(Single(x + (run.BaselineOrigin.X - line.Bounds.X)),
            Single(y + Single(run.BaselineOrigin.Y - line.Bounds.Y)));
    }
    private static float Single(double value)
    {
        if (!double.IsFinite(value) || value < -float.MaxValue || value > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "Coordinate cannot be represented by Direct2D.");
        return (float)value;
    }
}
