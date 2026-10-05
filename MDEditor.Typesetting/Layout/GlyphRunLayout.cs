using System.Collections.Immutable;
using MDEditor.Core.Text;

namespace MDEditor.Typesetting.Layout;

/// <summary>Inspection metadata only. FontIndex is snapshot-local; names never resolve a native face.</summary>
public sealed class FontFaceDescriptor
{
    public string Family { get; }
    public string Face { get; }
    public FontFaceDescriptor(string family, string face)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentException.ThrowIfNullOrWhiteSpace(face);
        Family = family; Face = face;
    }
}

public readonly record struct GlyphPlacement
{
    public int Index { get; }
    public double Advance { get; }
    public double AdvanceOffset { get; }
    public double AscenderOffset { get; }
    public GlyphPlacement(int index, double advance, double advanceOffset = 0, double ascenderOffset = 0)
    {
        if (index < 0 || index > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(index));
        LayoutValidation.Nonnegative(advance, nameof(advance));
        LayoutValidation.Finite(advanceOffset, nameof(advanceOffset));
        LayoutValidation.Finite(ascenderOffset, nameof(ascenderOffset));
        Index = index; Advance = advance; AdvanceOffset = advanceOffset; AscenderOffset = ascenderOffset;
    }
}

public sealed class GlyphClusterLayout
{
    public SourceRange Source { get; }
    public int GlyphStart { get; }
    public int GlyphCount { get; }
    public double Advance { get; }
    /// <summary>Generated display-only suffix, anchored to an empty source range. Never part of copied source.</summary>
    public string? GeneratedText { get; }
    public GlyphClusterLayout(SourceRange source, int glyphStart, int glyphCount, double advance, string? generatedText = null)
    {
        if (glyphStart < 0) throw new ArgumentOutOfRangeException(nameof(glyphStart));
        if (glyphCount <= 0 || glyphCount > int.MaxValue - glyphStart)
            throw new ArgumentOutOfRangeException(nameof(glyphCount));
        LayoutValidation.Nonnegative(advance, nameof(advance));
        if (generatedText is not null && (source.Length != 0 || generatedText is not "-" and not "\u2010"))
            throw new ArgumentException("A generated hyphen must have an empty source anchor.", nameof(generatedText));
        Source = source; GlyphStart = glyphStart; GlyphCount = glyphCount; Advance = advance;
        GeneratedText = generatedText;
    }
}

/// <summary>Final glyph metrics and absolute baseline origin in document DIP; no COM or mutable arrays.</summary>
public sealed class GlyphRunLayout
{
    public int FontIndex { get; }
    public SourceRange Source { get; }
    public LayoutPoint BaselineOrigin { get; }
    public double FontSize { get; }
    public uint BidiLevel { get; }
    public string Locale { get; }
    public ImmutableArray<GlyphPlacement> Glyphs { get; }
    public ImmutableArray<GlyphClusterLayout> Clusters { get; }
    public double Advance { get; }

    public GlyphRunLayout(int fontIndex, SourceRange source, LayoutPoint baselineOrigin, double fontSize,
        uint bidiLevel, string locale, IEnumerable<GlyphPlacement> glyphs, IEnumerable<GlyphClusterLayout> clusters)
    {
        if (fontIndex < 0) throw new ArgumentOutOfRangeException(nameof(fontIndex));
        LayoutValidation.Positive(fontSize, nameof(fontSize));
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ArgumentNullException.ThrowIfNull(glyphs);
        ArgumentNullException.ThrowIfNull(clusters);
        var copiedGlyphs = LayoutValidation.Freeze(glyphs);
        var copiedClusters = LayoutValidation.Freeze(clusters);
        if (copiedGlyphs.IsEmpty || copiedClusters.IsEmpty)
            throw new ArgumentException("An empty line has no runs; a run must have glyphs and clusters.");
        var usedGlyphs = new bool[copiedGlyphs.Length];
        var sourceEnd = source.Start;
        foreach (var cluster in copiedClusters)
        {
            ArgumentNullException.ThrowIfNull(cluster);
            if (!source.Contains(cluster.Source) || cluster.Source.Start < sourceEnd)
                throw new ArgumentException("Clusters must be source-ordered and contained in their run.", nameof(clusters));
            sourceEnd = cluster.Source.End;
            if (cluster.GlyphStart + cluster.GlyphCount > copiedGlyphs.Length)
                throw new ArgumentException("Cluster glyph range is outside the run.", nameof(clusters));
            double width = 0;
            for (var i = cluster.GlyphStart; i < cluster.GlyphStart + cluster.GlyphCount; i++)
            {
                if (usedGlyphs[i]) throw new ArgumentException("Glyph clusters overlap.", nameof(clusters));
                usedGlyphs[i] = true;
                width += copiedGlyphs[i].Advance;
            }
            if (Math.Abs(width - cluster.Advance) > 1e-7 * Math.Max(1, width))
                throw new ArgumentException("Cluster advance must match its glyph advances.", nameof(clusters));
        }
        if (usedGlyphs.Any(used => !used)) throw new ArgumentException("Every glyph must belong to a cluster.", nameof(clusters));
        var advance = copiedGlyphs.Sum(glyph => glyph.Advance);
        LayoutValidation.Nonnegative(advance, nameof(glyphs));
        FontIndex = fontIndex; Source = source; BaselineOrigin = baselineOrigin; FontSize = fontSize;
        BidiLevel = bidiLevel; Locale = locale; Glyphs = copiedGlyphs; Clusters = copiedClusters; Advance = advance;
    }
}
