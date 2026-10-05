using System.Collections.Immutable;
using System.Globalization;
using MDEditor.Core.Text;
using MDEditor.Typesetting.LineBreaking;

namespace MDEditor.Typesetting.Typography;

using Layout;

/// <summary>Bounded Chinese spacing, measured ink-safe punctuation compression and ordered allocation.</summary>
public sealed class CjkGlyphSpacingPlan : IGlyphSpacingPlan
{
    private sealed record Slot(int Run, GlyphClusterLayout Cluster, CjkSpacingKind Kind,
        double BaseLeading, double BaseTrailing, double LeadingShrink, double TrailingShrink, double Stretch);
    private readonly LineLayout _line;
    private readonly ImmutableArray<Slot> _slots;
    private readonly SourceTextSnapshot _source;
    public double NaturalWidth { get; }
    public double Stretch { get; }
    public double Shrink { get; }
    public int OpportunityCount => _slots.Length;

    private CjkGlyphSpacingPlan(SourceTextSnapshot source, LineLayout line, List<Slot> slots)
    {
        _source = source; _line = line; _slots = LayoutValidation.Freeze(slots);
        NaturalWidth = line.Runs.Sum(r => r.Advance) + slots.Sum(s => s.BaseLeading + s.BaseTrailing);
        Stretch = slots.Sum(s => s.Stretch); Shrink = slots.Sum(s => s.LeadingShrink + s.TrailingShrink);
        LayoutValidation.Nonnegative(NaturalWidth, nameof(line));
        LayoutValidation.Nonnegative(Stretch, nameof(slots)); LayoutValidation.Nonnegative(Shrink, nameof(slots));
    }

    public static CjkGlyphSpacingPlan Create(SourceTextSnapshot source, LineLayout line, double fontSize,
        CjkTypographyOptions options, IEnumerable<ClusterInkMargins>? inkMargins = null)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(line); ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled) throw new ArgumentException("Use the legacy spacing plan for a disabled profile.", nameof(options));
        LayoutValidation.Positive(fontSize, nameof(fontSize));
        var text = source.GetText(line.Source);
        var boundaries = StringInfo.ParseCombiningCharacters(text).Select(i => i + line.Source.Start).ToHashSet();
        boundaries.Add(line.Source.End);
        var entries = line.Runs.SelectMany((r, i) => r.Clusters.Select(c => (Run: i, Cluster: c, Text: c.GeneratedText ?? source.GetText(c.Source))))
            .OrderBy(e => e.Cluster.Source.Start).ToArray();
        var ink = (inkMargins ?? []).ToDictionary(m => m.Source);
        foreach (var margin in ink.Values)
        {
            var entry = entries.FirstOrDefault(e => e.Cluster.Source == margin.Source);
            if (!line.Source.Contains(margin.Source) || entry.Cluster is null ||
                margin.Leading + margin.Trailing > entry.Cluster.Advance + 1e-7 * Math.Max(1, entry.Cluster.Advance))
                throw new ArgumentException("Ink margins must refer to exact clusters in this line.", nameof(inkMargins));
        }
        var first = Array.FindIndex(entries, e => !TextSpacingPolicy.IsSpaces(e.Text));
        var last = Array.FindLastIndex(entries, e => !TextSpacingPolicy.IsSpaces(e.Text));
        var slots = new List<Slot>(); var cursor = line.Source.Start;
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i]; var cluster = e.Cluster;
            if (cluster.Source.Start != cursor ||
                cluster.Source.Length == 0 && (cluster.GeneratedText is null || cursor != line.Source.End))
                throw new ArgumentException("Glyph clusters must continuously cover the line.", nameof(line));
            cursor = cluster.Source.End;
            var safe = boundaries.Contains(cluster.Source.Start) && boundaries.Contains(cluster.Source.End);
            if (!safe) continue;
            if (i > first && i < last && TextSpacingPolicy.IsSpaces(e.Text) && cluster.Advance > 0)
                slots.Add(new(e.Run, cluster, CjkSpacingKind.WordSpace, 0, 0, 0, cluster.Advance / 3,
                    Math.Max(0, fontSize * 0.5 - cluster.Advance)));
            var ltr = (line.Runs[e.Run].BidiLevel & 1) == 0;
            if (ltr && options.CompressPunctuation && cluster.GlyphCount == 1 &&
                CjkTypographyRules.IsPunctuation(e.Text) && ink.TryGetValue(cluster.Source, out var margins))
            {
                var maximum = Math.Max(0, Math.Min(fontSize * 0.5, cluster.Advance - fontSize * 0.5));
                var leading = Math.Max(0, margins.Leading - fontSize * options.InkClearanceEm);
                var trailing = Math.Max(0, margins.Trailing - fontSize * options.InkClearanceEm);
                if (CjkTypographyRules.IsOpening(e.Text)) { leading = Math.Min(maximum, leading); trailing = 0; }
                else if (CjkTypographyRules.IsClosing(e.Text)) { leading = 0; trailing = Math.Min(maximum, trailing); }
                else
                {
                    var total = leading + trailing;
                    var scale = total == 0 ? 0 : Math.Min(1, maximum / total);
                    leading *= scale; trailing *= scale;
                }
                var baseLeading = 0d; var baseTrailing = 0d;
                if (i == first && CjkTypographyRules.IsOpening(e.Text) ||
                    i > first && CjkTypographyRules.IsPunctuation(entries[i - 1].Text))
                    baseLeading = -leading;
                if (i == last && !CjkTypographyRules.IsOpening(e.Text) ||
                    i < last && CjkTypographyRules.IsPunctuation(entries[i + 1].Text))
                    baseTrailing = -trailing;
                if (leading + trailing > 0)
                    slots.Add(new(e.Run, cluster, CjkSpacingKind.Punctuation, baseLeading, baseTrailing,
                        leading + baseLeading, trailing + baseTrailing, 0));
            }
            if (ltr && i + 1 < entries.Length)
            {
                var next = entries[i + 1];
                if (!boundaries.Contains(next.Cluster.Source.End) || (line.Runs[next.Run].BidiLevel & 1) != 0) continue;
                if (CjkTypographyRules.IsMixedBoundary(e.Text, next.Text))
                    slots.Add(new(e.Run, cluster, CjkSpacingKind.MixedScript, 0, fontSize * options.MixedNaturalEm, 0,
                        fontSize * (options.MixedNaturalEm - options.MixedMinimumEm),
                        fontSize * (options.MixedMaximumEm - options.MixedNaturalEm)));
                else if (CjkTypographyRules.IsInterCharacterBoundary(e.Text, next.Text))
                    slots.Add(new(e.Run, cluster, CjkSpacingKind.InterCharacter, 0, 0, 0, 0,
                        fontSize * options.InterCharacterMaximumEm));
            }
        }
        if (cursor != line.Source.End) throw new ArgumentException("Incomplete glyph source.", nameof(line));
        return new(source, line, slots);
    }

    public GlyphJustificationResult Apply(double ratio, double targetWidth, bool raggedRight)
    {
        LayoutValidation.Finite(ratio, nameof(ratio)); LayoutValidation.Positive(targetWidth, nameof(targetWidth));
        if (ratio < -1 || ratio > 1) throw new ArgumentOutOfRangeException(nameof(ratio), "Refined spacing uses bounded capacities with -1 <= ratio <= 1.");
        if (raggedRight && ratio != 0) throw new ArgumentException("Ragged lines must have zero adjustment.", nameof(ratio));
        var expected = NaturalWidth + ratio * (ratio < 0 ? Shrink : Stretch);
        var desired = raggedRight ? NaturalWidth : targetWidth;
        if (Math.Abs(expected - desired) > 1e-7 * Math.Max(1, desired)) throw new ArgumentException("Ratio and true line measurement disagree.", nameof(ratio));
        var leading = _slots.Select(s => s.BaseLeading).ToArray();
        var trailing = _slots.Select(s => s.BaseTrailing).ToArray();
        var remaining = Math.Abs(desired - NaturalWidth);
        int Priority(Slot slot) => ratio < 0 ? slot.Kind switch
        { CjkSpacingKind.Punctuation => 0, CjkSpacingKind.WordSpace => 1, _ => 2 } : slot.Kind switch
        { CjkSpacingKind.WordSpace => 0, CjkSpacingKind.MixedScript => 1, _ => 2 };
        foreach (var group in Enumerable.Range(0, _slots.Length).GroupBy(i => Priority(_slots[i])).OrderBy(g => g.Key))
        {
            var capacity = group.Sum(i => ratio < 0 ? _slots[i].LeadingShrink + _slots[i].TrailingShrink : _slots[i].Stretch);
            if (capacity == 0) continue;
            var used = Math.Min(remaining, capacity); var scale = used / capacity;
            foreach (var i in group)
            {
                if (ratio < 0) { leading[i] -= _slots[i].LeadingShrink * scale; trailing[i] -= _slots[i].TrailingShrink * scale; }
                else trailing[i] += _slots[i].Stretch * scale;
            }
            remaining -= used;
        }
        if (remaining > 1e-6) throw new InvalidOperationException("Finite spacing capacity exhausted.");
        var glyphs = _line.Runs.Select(r => r.Glyphs.ToArray()).ToArray();
        var combined = Enumerable.Range(0, _slots.Length).GroupBy(i => (_slots[i].Run, _slots[i].Cluster.GlyphStart)).ToArray();
        foreach (var group in combined)
        {
            var slot = _slots[group.First()]; var cluster = slot.Cluster;
            var total = group.Sum(i => leading[i] + trailing[i]); var offset = group.Sum(i => leading[i]);
            var space = TextSpacingPolicy.IsSpaces(_source.GetText(cluster.Source));
            AdjustCluster(glyphs[slot.Run], cluster, total, offset, space);
        }
        double Sum() => glyphs.Sum(r => r.Sum(g => g.Advance));
        var residual = desired - Sum();
        if (!raggedRight && Math.Abs(residual) > 0)
        {
            // Only correct float residue, not infeasibility. It may not visibly exceed any capacity.
            foreach (var group in combined.Reverse())
            {
                if (!group.Any(i => ratio < 0 ? _slots[i].TrailingShrink > 0 : _slots[i].Stretch > 0)) continue;
                var slot = _slots[group.First()]; var g = AdvanceCarrier(glyphs[slot.Run], slot.Cluster);
                var old = glyphs[slot.Run][g]; if (old.Advance + residual < 0) continue;
                AdjustCluster(glyphs[slot.Run], slot.Cluster, residual, 0, false);
                residual = desired - Sum(); if (Math.Abs(residual) < 1e-5) break;
            }
        }
        var actual = Sum();
        if (Math.Abs(actual - desired) > 0.01) throw new InvalidOperationException("Native glyph advances failed 0.01 DIP.");
        var deltas = glyphs.Select((r, i) => r.Sum(g => g.Advance) - _line.Runs[i].Advance).ToArray();
        var origins = _line.Runs.Select(r => r.BaselineOrigin).ToArray(); double preceding = 0;
        foreach (var i in Enumerable.Range(0, origins.Length).OrderBy(i =>
            _line.Runs[i].BaselineOrigin.X - ((_line.Runs[i].BidiLevel & 1) == 0 ? 0 : _line.Runs[i].Advance)).ThenBy(i => i))
        {
            var run = _line.Runs[i];
            origins[i] = new(run.BaselineOrigin.X + preceding + ((run.BidiLevel & 1) == 0 ? 0 : deltas[i]), run.BaselineOrigin.Y);
            preceding += deltas[i];
        }
        var runs = _line.Runs.Select((r, i) => new GlyphRunLayout(r.FontIndex, r.Source, origins[i], r.FontSize,
            r.BidiLevel, r.Locale, glyphs[i], r.Clusters.Select(c => new GlyphClusterLayout(c.Source, c.GlyphStart, c.GlyphCount,
                glyphs[i].Skip(c.GlyphStart).Take(c.GlyphCount).Sum(g => g.Advance), c.GeneratedText)))).ToArray();
        var spacings = combined.Select(group =>
        {
            var s = _slots[group.First()]; var c = s.Cluster;
            var delta = glyphs[s.Run].Skip(c.GlyphStart).Take(c.GlyphCount).Sum(g => g.Advance) - c.Advance;
            var offset = glyphs[s.Run][c.GlyphStart].AdvanceOffset - _line.Runs[s.Run].Glyphs[c.GlyphStart].AdvanceOffset;
            return new AppliedGlyphSpacing(c.Source, delta, offset, string.Join("+", group.Select(i => _slots[i].Kind).Distinct()));
        }).ToArray();
        var segmented = SegmentForPaint(runs, spacings, _line.Bounds.X);
        return new(new(_line.Source, new(_line.Bounds.X, _line.Bounds.Y, targetWidth, _line.Bounds.Height),
            _line.Baseline, actual, segmented), LayoutValidation.Freeze(spacings), Math.Abs(actual - desired));
    }

    private GlyphRunLayout[] SegmentForPaint(GlyphRunLayout[] runs, AppliedGlyphSpacing[] spacings, double documentX)
    {
        var bySource = spacings.ToDictionary(s => s.Source);
        var pieces = new List<(int Original, GlyphRunLayout Run)>();
        for (var r = 0; r < runs.Length; r++)
        {
            var run = runs[r];
            if ((run.BidiLevel & 1) != 0) { pieces.Add((r, run)); continue; } // Keep complex RTL runs intact.
            var groups = new List<List<GlyphClusterLayout>>();
            var previous = (double.NaN, double.NaN);
            foreach (var cluster in run.Clusters.OrderBy(c => c.GlyphStart))
            {
                bySource.TryGetValue(cluster.Source, out var spacing);
                var divisor = TextSpacingPolicy.IsSpaces(_source.GetText(cluster.Source)) ? cluster.Source.Length : 1;
                var attribute = (Native(spacing.LeadingDelta / divisor, false), Native((spacing.Delta - spacing.LeadingDelta) / divisor, false));
                if (groups.Count == 0 || attribute != previous) groups.Add(new());
                groups[^1].Add(cluster); previous = attribute;
            }
            double prefix = 0;
            foreach (var group in groups)
            {
                var start = group[0].GlyphStart; var count = group.Sum(c => c.GlyphCount);
                var source = new SourceRange(group[0].Source.Start, group[^1].Source.End - group[0].Source.Start);
                var glyphs = run.Glyphs.Skip(start).Take(count).ToArray();
                var clusters = group.Select(c => new GlyphClusterLayout(c.Source, c.GlyphStart - start, c.GlyphCount, c.Advance, c.GeneratedText));
                pieces.Add((r, new(run.FontIndex, source, new(run.BaselineOrigin.X + prefix, run.BaselineOrigin.Y),
                    run.FontSize, run.BidiLevel, run.Locale, glyphs, clusters)));
                prefix += glyphs.Sum(g => g.Advance);
            }
        }
        // Use a line-local native float pen, matching DirectWrite's styled-run origins.
        // Font slots remain the original binding's slots; no cluster is split or reshaped.
        var result = pieces.Select(p => p.Run).ToArray(); float cursor = 0;
        foreach (var i in Enumerable.Range(0, pieces.Count).OrderBy(i =>
            pieces[i].Run.BaselineOrigin.X - ((pieces[i].Run.BidiLevel & 1) == 0 ? 0 : pieces[i].Run.Advance)).ThenBy(i => i))
        {
            var run = result[i];
            // Accumulate each styled run locally, then add its width to the line pen.
            var next = GlyphPaintCoordinates.AdvancePen(cursor, run.Glyphs);
            var x = (run.BidiLevel & 1) == 0 ? cursor : next;
            result[i] = new(run.FontIndex, run.Source, new(documentX + x, run.BaselineOrigin.Y),
                run.FontSize, run.BidiLevel, run.Locale, run.Glyphs, run.Clusters);
            cursor = next;
        }
        return result;
    }

    private static int AdvanceCarrier(GlyphPlacement[] glyphs, GlyphClusterLayout cluster)
    {
        var end = cluster.GlyphStart + cluster.GlyphCount;
        for (var g = end - 1; g >= cluster.GlyphStart; g--)
            if (glyphs[g].Advance > 0) return g;
        return end - 1;
    }

    private static void AdjustCluster(GlyphPlacement[] glyphs, GlyphClusterLayout cluster,
        double delta, double leading, bool spaces)
    {
        // A trailing zero-advance mark must stay zero-advance. DirectWrite assigns
        // spacing to the last advancing glyph and compensates following mark offsets.
        // Advancing the mark instead changes its hinted raster at some DPI/scales.
        var carrier = AdvanceCarrier(glyphs, cluster); double precedingDelta = 0;
        for (var g = cluster.GlyphStart; g < cluster.GlyphStart + cluster.GlyphCount; g++)
        {
            var old = glyphs[g];
            var share = spaces ? delta * old.Advance / cluster.Advance : g == carrier ? delta : 0;
            var advance = Native(old.Advance + share, true);
            glyphs[g] = new(old.Index, advance,
                Native(old.AdvanceOffset + leading - (spaces ? 0 : precedingDelta), false), old.AscenderOffset);
            precedingDelta += advance - old.Advance;
        }
    }

    private static double Native(double value, bool nonnegative)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > float.MaxValue || nonnegative && value < 0)
            throw new InvalidOperationException("Invalid native metric.");
        return (double)(float)value;
    }
}
