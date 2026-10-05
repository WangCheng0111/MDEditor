using MDEditor.Core.Text;

namespace MDEditor.Typesetting.Layout;

/// <summary>Concatenate local font catalogs without deduplicating by name. Geometry remains document-local.</summary>
public static class LayoutSnapshotComposer
{
    public static LayoutSnapshot Compose(SourceTextSnapshot source, LayoutRect bounds, IEnumerable<LayoutSnapshot> parts)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(parts);
        var fonts = new List<FontFaceDescriptor>();
        var blocks = new List<BlockLayout>();
        foreach (var part in parts)
        {
            ArgumentNullException.ThrowIfNull(part);
            if (!ReferenceEquals(part.Source, source)) throw new ArgumentException("All parts must share the same immutable source.", nameof(parts));
            var firstFont = fonts.Count;
            fonts.AddRange(part.Fonts);
            foreach (var block in part.Blocks)
                blocks.Add(new(block.Source, block.Bounds, block.Lines.Select(line =>
                    new LineLayout(line.Source, line.Bounds, line.Baseline, line.Advance, line.Runs.Select(run =>
                        new GlyphRunLayout(checked(firstFont + run.FontIndex), run.Source, run.BaselineOrigin,
                            run.FontSize, run.BidiLevel, run.Locale, run.Glyphs, run.Clusters))))));
        }
        return new(source, bounds, fonts, blocks);
    }
}
