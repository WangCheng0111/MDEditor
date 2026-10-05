using MDEditor.Typesetting.Layout;

namespace MDEditor.Typesetting.Export;

public readonly record struct PdfPageSlice(double Top, double Height);

/// <summary>Keeps lines/rows/formula/image atoms intact without changing existing line breaks.</summary>
public static class PdfPagePlanner
{
    public const double A4Width = 595.275590551;
    public const double A4Height = 841.88976378;
    public static IReadOnlyList<PdfPageSlice> Plan(double height, double capacity, IEnumerable<LayoutRect> protectedBands)
    {
        if (!double.IsFinite(height) || height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        PdfDocument.Positive(capacity);
        ArgumentNullException.ThrowIfNull(protectedBands);
        var bands = protectedBands.Where(b => b.Height > 0).OrderBy(b => b.Y).ToArray();
        var pages = new List<PdfPageSlice>(); var top = 0.0;
        while (top < height || pages.Count == 0)
        {
            var end = Math.Min(height, top + capacity);
            // A tall atom gets a taller page, not a clipped formula/image or an infinite loop.
            foreach (var band in bands)
                if (band.Y <= top + 0.01 && band.Bottom > end) end = Math.Min(height, band.Bottom);
            bool moved;
            do
            {
                moved = false;
                foreach (var band in bands)
                    if (band.Y > top + 0.01 && band.Y < end - 0.01 && band.Bottom > end + 0.01)
                    { end = band.Y; moved = true; break; }
            } while (moved);
            pages.Add(new(top, Math.Max(1, end - top)));
            if (end >= height) break;
            if (end <= top) throw new InvalidOperationException("Pagination made no progress.");
            top = end;
        }
        return pages;
    }
}
