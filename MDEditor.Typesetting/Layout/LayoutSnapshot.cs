using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MDEditor.Core.Text;

namespace MDEditor.Typesetting.Layout;

public sealed class LineLayout
{
    public SourceRange Source { get; }
    public LayoutRect Bounds { get; }
    public double Baseline { get; }
    public double Advance { get; }
    public ImmutableArray<GlyphRunLayout> Runs { get; }
    public LineLayout(SourceRange source, LayoutRect bounds, double baseline, double advance, IEnumerable<GlyphRunLayout> runs)
    {
        LayoutValidation.Finite(baseline, nameof(baseline));
        LayoutValidation.Nonnegative(advance, nameof(advance));
        if (baseline < bounds.Y || baseline > bounds.Bottom) throw new ArgumentOutOfRangeException(nameof(baseline));
        ArgumentNullException.ThrowIfNull(runs);
        var copy = LayoutValidation.Freeze(runs);
        foreach (var run in copy)
        {
            ArgumentNullException.ThrowIfNull(run);
            if (!source.Contains(run.Source)) throw new ArgumentException("Run source is outside the line.", nameof(runs));
        }
        var generated = copy.SelectMany(r => r.Clusters).Where(c => c.GeneratedText is not null).ToArray();
        if (generated.Length > 1 || generated.Any(c => c.Source != new SourceRange(source.End, 0)))
            throw new ArgumentException("A line may have one generated hyphen, anchored exactly at its source end.", nameof(runs));
        Source = source; Bounds = bounds; Baseline = baseline; Advance = advance; Runs = copy;
    }
}

public sealed class BlockLayout
{
    public SourceRange Source { get; }
    public LayoutRect Bounds { get; }
    public ImmutableArray<LineLayout> Lines { get; }
    public BlockLayout(SourceRange source, LayoutRect bounds, IEnumerable<LineLayout> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var copy = LayoutValidation.Freeze(lines);
        foreach (var line in copy)
        {
            ArgumentNullException.ThrowIfNull(line);
            if (!source.Contains(line.Source) || !bounds.Contains(line.Bounds))
                throw new ArgumentException("Line must be contained in its block.", nameof(lines));
        }
        Source = source; Bounds = bounds; Lines = copy;
    }
}

/// <summary>Deeply immutable, portable final layout. No native handles, timestamps or resource ownership.</summary>
public sealed class LayoutSnapshot
{
    public SourceTextSnapshot Source { get; }
    public LayoutRect Bounds { get; }
    public ImmutableArray<FontFaceDescriptor> Fonts { get; }
    public ImmutableArray<BlockLayout> Blocks { get; }

    public LayoutSnapshot(SourceTextSnapshot source, LayoutRect bounds,
        IEnumerable<FontFaceDescriptor> fonts, IEnumerable<BlockLayout> blocks)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(fonts);
        ArgumentNullException.ThrowIfNull(blocks);
        var copiedFonts = LayoutValidation.Freeze(fonts);
        var copiedBlocks = LayoutValidation.Freeze(blocks);
        foreach (var font in copiedFonts) ArgumentNullException.ThrowIfNull(font);
        foreach (var block in copiedBlocks)
        {
            ArgumentNullException.ThrowIfNull(block);
            if (!source.FullRange.Contains(block.Source) || !bounds.Contains(block.Bounds))
                throw new ArgumentException("Block must be contained in source and snapshot bounds.", nameof(blocks));
            foreach (var line in block.Lines)
            foreach (var run in line.Runs)
                if (run.FontIndex >= copiedFonts.Length)
                    throw new ArgumentException("Glyph run font reference is unresolved.", nameof(fonts));
        }
        Source = source; Bounds = bounds; Fonts = copiedFonts; Blocks = copiedBlocks;
    }
}

public sealed record LayoutSummary(long SourceVersion, int SourceLength, int FontCount,
    int BlockCount, int LineCount, int RunCount, int GlyphCount, int ClusterCount);

/// <summary>Independent inspection of the production snapshot; serialization is diagnostics, not a persistence ABI.</summary>
public static class LayoutSnapshotInspector
{
    public static LayoutSummary Inspect(LayoutSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var lines = snapshot.Blocks.SelectMany(block => block.Lines).ToArray();
        var runs = lines.SelectMany(line => line.Runs).ToArray();
        return new(snapshot.Source.Version, snapshot.Source.Length, snapshot.Fonts.Length,
            snapshot.Blocks.Length, lines.Length, runs.Length,
            runs.Sum(run => run.Glyphs.Length), runs.Sum(run => run.Clusters.Length));
    }
    public static string ToJson(LayoutSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
    }
    public static string Fingerprint(LayoutSnapshot snapshot)
    {
        // Stable for identical metadata on this contract/runtime; not a global font ID or cross-version file format.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson(snapshot))));
    }
}
