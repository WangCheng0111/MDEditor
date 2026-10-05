using System.Collections.Immutable;
using System.Globalization;
using MDEditor.Core.Text;
using MDEditor.Typesetting.LineBreaking;

namespace MDEditor.Typesetting.Layout;

public readonly record struct AppliedGlyphSpacing(SourceRange Source, double Delta, double LeadingDelta = 0, string Kind = "Legacy");

public interface IGlyphSpacingPlan
{
    double NaturalWidth { get; }
    double Stretch { get; }
    double Shrink { get; }
    int OpportunityCount { get; }
    GlyphJustificationResult Apply(double ratio, double targetWidth, bool raggedRight);
}

/// <summary>Measured opportunities, never estimated character widths or spacing inside a glyph cluster.</summary>
public sealed class GlyphSpacingPlan : IGlyphSpacingPlan
{
    private readonly LineLayout _line;
    private readonly ImmutableArray<Slot> _slots;
    private sealed record Slot(int Run, GlyphClusterLayout Cluster, double Stretch, double Shrink);
    public double NaturalWidth { get; }
    public double Stretch { get; }
    public double Shrink { get; }
    public int OpportunityCount => _slots.Length;

    private GlyphSpacingPlan(LineLayout line, List<Slot> slots)
    {
        _line = line; _slots = LayoutValidation.Freeze(slots);
        NaturalWidth = line.Runs.Sum(run => run.Advance);
        Stretch = slots.Sum(slot => slot.Stretch); Shrink = slots.Sum(slot => slot.Shrink);
    }

    public static GlyphSpacingPlan Create(SourceTextSnapshot source, LineLayout line, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(line);
        LayoutValidation.Positive(fontSize, nameof(fontSize));
        var text = source.GetText(line.Source);
        var boundaries = StringInfo.ParseCombiningCharacters(text).Select(i => line.Source.Start + i).ToHashSet();
        boundaries.Add(line.Source.End);
        var clusters = line.Runs.SelectMany((run, index) => run.Clusters.Select(cluster => (Run: index, Cluster: cluster)))
            .OrderBy(pair => pair.Cluster.Source.Start).ToArray();
        var slots = new List<Slot>();
        var firstContent = Array.FindIndex(clusters, pair => !TextSpacingPolicy.IsSpaces(source.GetText(pair.Cluster.Source)));
        var lastContent = Array.FindLastIndex(clusters, pair => !TextSpacingPolicy.IsSpaces(source.GetText(pair.Cluster.Source)));
        var cursor = line.Source.Start;
        for (var i = 0; i < clusters.Length; i++)
        {
            var (runIndex, cluster) = clusters[i];
            if (cluster.Source.Start != cursor ||
                cluster.Source.Length == 0 && (cluster.GeneratedText is null || cursor != line.Source.End))
                throw new ArgumentException("Glyph clusters must completely cover the line source.", nameof(line));
            cursor = cluster.Source.End;
            var safe = boundaries.Contains(cluster.Source.Start) && boundaries.Contains(cluster.Source.End);
            var slice = cluster.GeneratedText ?? source.GetText(cluster.Source);
            if (safe && i > firstContent && i < lastContent && TextSpacingPolicy.IsSpaces(slice) && cluster.Advance > 0)
                slots.Add(new(runIndex, cluster, cluster.Advance * 0.5, cluster.Advance / 3));
            else if (safe && i + 1 < clusters.Length)
            {
                var next = clusters[i + 1];
                if (boundaries.Contains(next.Cluster.Source.End) && TextSpacingPolicy.IsCjk(slice) &&
                    TextSpacingPolicy.IsCjk(source.GetText(next.Cluster.Source)) &&
                    (_Even(line.Runs[runIndex].BidiLevel) && _Even(line.Runs[next.Run].BidiLevel)))
                    slots.Add(new(runIndex, cluster, fontSize * 0.08, 0));
            }
        }
        if (cursor != line.Source.End) throw new ArgumentException("Incomplete glyph source coverage.", nameof(line));
        return new(line, slots);
    }

    private static bool _Even(uint level) => (level & 1) == 0;

    public GlyphJustificationResult Apply(double ratio, double targetWidth, bool raggedRight)
    {
        LayoutValidation.Finite(ratio, nameof(ratio));
        LayoutValidation.Positive(targetWidth, nameof(targetWidth));
        if (ratio < -1) throw new ArgumentOutOfRangeException(nameof(ratio));
        if (raggedRight && ratio != 0) throw new ArgumentException("Ragged lines must have zero adjustment.", nameof(ratio));
        var glyphs = _line.Runs.Select(run => run.Glyphs.ToArray()).ToArray();
        var desired = raggedRight ? NaturalWidth : targetWidth;
        var expected = NaturalWidth + ratio * (ratio >= 0 ? Stretch : Shrink);
        if (Math.Abs(expected - desired) > 1e-7 * Math.Max(1, desired))
            throw new ArgumentException("The selected ratio does not satisfy the measured line.", nameof(ratio));
        foreach (var slot in _slots)
        {
            var delta = ratio * (ratio >= 0 ? slot.Stretch : slot.Shrink);
            var cluster = slot.Cluster;
            // Distribute a space-cluster change proportionally; a zero-width CJK gap follows the entire cluster.
            if (slot.Shrink > 0)
            {
                for (var g = cluster.GlyphStart; g < cluster.GlyphStart + cluster.GlyphCount; g++)
                    Change(slot.Run, g, delta * glyphs[slot.Run][g].Advance / cluster.Advance);
            }
            else Change(slot.Run, cluster.GlyphStart + cluster.GlyphCount - 1, delta);
        }
        double Sum() => glyphs.Sum(run => run.Sum(glyph => glyph.Advance));
        // Correct only float-roundtrip residue, and only at an already legal opportunity.
        var residual = desired - Sum();
        if (!raggedRight && Math.Abs(residual) > 0 && _slots.Length > 0)
        {
            foreach (var slot in _slots.Reverse())
            {
                if (ratio < 0 && slot.Shrink == 0) continue;
                var index = slot.Cluster.GlyphStart + slot.Cluster.GlyphCount - 1;
                if (glyphs[slot.Run][index].Advance + residual < 0) continue;
                Change(slot.Run, index, residual);
                residual = desired - Sum();
                if (Math.Abs(residual) <= 1e-5) break;
            }
        }
        var advance = Sum();
        if (Math.Abs(advance - desired) > 0.01)
            throw new InvalidOperationException("Actual float glyph advances failed the 0.01 DIP alignment contract.");
        var runDeltas = glyphs.Select((run, index) => run.Sum(g => g.Advance) - _line.Runs[index].Advance).ToArray();
        var origins = _line.Runs.Select(run => run.BaselineOrigin).ToArray();
        var visual = Enumerable.Range(0, _line.Runs.Length).OrderBy(index =>
            _line.Runs[index].BaselineOrigin.X - (_Even(_line.Runs[index].BidiLevel) ? 0 : _line.Runs[index].Advance))
            .ThenBy(index => index).ToArray();
        double precedingDelta = 0;
        foreach (var index in visual)
        {
            var original = _line.Runs[index];
            origins[index] = new(original.BaselineOrigin.X + precedingDelta +
                (_Even(original.BidiLevel) ? 0 : runDeltas[index]), original.BaselineOrigin.Y);
            precedingDelta += runDeltas[index];
        }
        var runs = _line.Runs.Select((run, index) => new GlyphRunLayout(run.FontIndex, run.Source,
            origins[index], run.FontSize, run.BidiLevel, run.Locale, glyphs[index],
            run.Clusters.Select(cluster => new GlyphClusterLayout(cluster.Source, cluster.GlyphStart,
                cluster.GlyphCount, glyphs[index].Skip(cluster.GlyphStart).Take(cluster.GlyphCount).Sum(g => g.Advance),
                cluster.GeneratedText)))).ToArray();
        var spacing = _slots.Select(slot => new AppliedGlyphSpacing(slot.Cluster.Source,
            glyphs[slot.Run].Skip(slot.Cluster.GlyphStart).Take(slot.Cluster.GlyphCount).Sum(g => g.Advance) -
            slot.Cluster.Advance)).ToArray();
        var line = new LineLayout(_line.Source,
            new(_line.Bounds.X, _line.Bounds.Y, targetWidth, _line.Bounds.Height), _line.Baseline, advance, runs);
        return new(line, LayoutValidation.Freeze(spacing), Math.Abs(advance - desired));

        void Change(int runIndex, int glyphIndex, double delta)
        {
            var old = glyphs[runIndex][glyphIndex];
            var value = old.Advance + delta;
            if (!double.IsFinite(value) || value < 0 || value > float.MaxValue)
                throw new InvalidOperationException("Invalid adjusted native glyph advance.");
            glyphs[runIndex][glyphIndex] = new(old.Index, (double)(float)value, old.AdvanceOffset, old.AscenderOffset);
        }
    }
}

public sealed record GlyphJustificationResult(LineLayout Line, ImmutableArray<AppliedGlyphSpacing> Spacing, double WidthError);
