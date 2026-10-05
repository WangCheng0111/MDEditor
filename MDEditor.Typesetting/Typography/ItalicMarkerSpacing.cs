namespace MDEditor.Typesetting.Typography;

/// <summary>Optical clearance for a revealed upright marker after italic ink; never source whitespace.</summary>
public static class ItalicMarkerSpacing
{
    public static bool IsBoundary(char marker, bool previousItalic, bool markerItalic) =>
        marker is '*' or '_' && previousItalic && !markerItalic;

    public static double AdditionalAdvance(double previousInkRight, double markerInkLeft,
        double fontSize, double minimumExistingGap = 0)
    {
        if (!double.IsFinite(previousInkRight) || !double.IsFinite(markerInkLeft) ||
            !double.IsFinite(fontSize) || fontSize <= 0 ||
            !double.IsFinite(minimumExistingGap) || minimumExistingGap < 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        return Math.Max(0, previousInkRight + fontSize * 0.08 - markerInkLeft - minimumExistingGap);
    }
}
