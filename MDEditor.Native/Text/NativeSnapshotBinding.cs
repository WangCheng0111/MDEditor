using System.Numerics;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;
using MDEditor.Typesetting.Hyphenation;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;

namespace MDEditor.Native.Text;

public sealed record ShapedLineInput(ShapedText Shape, SourceRange Source, LayoutPoint Origin, string Suffix = "");

/// <summary>
/// Separate native font binding. Borrows ShapedText owners; disposing an owner invalidates replay.
/// Snapshot geometry/glyphs are portable and remain readable after this binding is released.
/// </summary>
public sealed class NativeSnapshotBinding : IDisposable
{
    private readonly List<CanvasFontFace> _faces = new();
    private readonly List<ShapedText> _owners = new();
    private readonly Dictionary<GlyphRunLayout, CanvasGlyph[]> _glyphCache = new();
    private readonly Dictionary<GlyphRunLayout, GlyphClusterLayout[]> _clusterOrderCache = new();
    private readonly Dictionary<(GlyphRunLayout Run, int Start, int Count), CanvasGlyph[]> _coloredGlyphCache = new();
    private ViewportIntervalIndex<LineLayout>? _visibleLines;
    private bool _composedLifetime;
    private bool _disposed;
    public LayoutSnapshot Snapshot { get; private set; } = null!;

    public static NativeSnapshotBinding Create(SourceTextSnapshot source, LayoutRect bounds, IEnumerable<ShapedLineInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(inputs);
        var binding = new NativeSnapshotBinding();
        try
        {
            var fonts = new List<FontFaceDescriptor>();
            var blocks = new List<BlockLayout>();
            foreach (var input in inputs)
            {
                ArgumentNullException.ThrowIfNull(input);
                ArgumentNullException.ThrowIfNull(input.Shape);
                input.Shape.VerifyAlive();
                if (input.Suffix is not "" and not "-" and not "\u2010")
                    throw new ArgumentException("Invalid generated suffix.", nameof(inputs));
                var display = new DiscretionaryLine(input.Source, input.Suffix);
                if (!StringComparer.Ordinal.Equals(display.GetText(source), input.Shape.Text))
                    throw new ArgumentException("Source slice does not match the shaped text.", nameof(inputs));
                binding._owners.Add(input.Shape);
                var runs = new List<GlyphRunLayout>();
                foreach (var captured in input.Shape.CapturedRuns)
                {
                    var local = new SourceRange(checked((int)captured.SourceStart), captured.Map.Length);
                    // Win2D may expose the full associated text buffer in Text; it is not a run-length field.
                    // The run's UTF-16 extent comes from characterIndex and clusterMap.Length.
                    if (local.End > input.Shape.Text.Length)
                        throw new ArgumentException("Captured UTF-16 run is inconsistent.", nameof(inputs));
                    var sourceRange = new SourceRange(checked(input.Source.Start + Math.Min(local.Start, input.Source.Length)),
                        Math.Max(0, Math.Min(local.End, input.Source.Length) - Math.Min(local.Start, input.Source.Length)));
                    var fontIndex = fonts.Count;
                    fonts.Add(new(captured.Metrics.FontFamily, captured.Metrics.FontFace));
                    binding._faces.Add(captured.Face);
                    // Cluster source starts are already relative to the shape, NOT relative to the run.
                    var clusters = captured.Metrics.Clusters.Select(cluster => new GlyphClusterLayout(
                        display.MapDisplayRange(new(cluster.SourceStart, cluster.SourceLength)),
                        cluster.GlyphStart, cluster.GlyphCount, cluster.Advance,
                        cluster.SourceStart >= input.Source.Length && input.Suffix.Length > 0 ? input.Suffix : null));
                    runs.Add(new(fontIndex, sourceRange,
                        new LayoutPoint(input.Origin.X + captured.Origin.X, input.Origin.Y + captured.Origin.Y),
                        captured.FontSize, captured.BidiLevel, captured.Locale,
                        captured.Glyphs.Select(glyph => new GlyphPlacement(glyph.Index, glyph.Advance,
                            glyph.AdvanceOffset, glyph.AscenderOffset)), clusters));
                }
                var lineBounds = new LayoutRect(input.Origin.X, input.Origin.Y, input.Shape.Advance, input.Shape.Height);
                var line = new LineLayout(input.Source, lineBounds,
                    input.Origin.Y + input.Shape.Baseline, input.Shape.Advance, runs);
                blocks.Add(new(input.Source, lineBounds, new[] { line }));
            }
            binding.Snapshot = new(source, bounds, fonts, blocks);
            return binding;
        }
        catch { binding.Dispose(); throw; } // Shapes belong to the caller, not this binding.
    }

    internal static NativeSnapshotBinding Compose(SourceTextSnapshot source, LayoutRect bounds, IReadOnlyList<ParagraphLayout> paragraphs)
        => ComposeParts(source, bounds, paragraphs.Select(p => (p.Binding, p.Snapshot)));

    internal static NativeSnapshotBinding ComposeParts(SourceTextSnapshot source, LayoutRect bounds,
        IEnumerable<(NativeSnapshotBinding Binding, LayoutSnapshot Snapshot)> parts)
    {
        var binding = new NativeSnapshotBinding();
        try
        {
            var snapshots = new List<LayoutSnapshot>();
            foreach (var (part, snapshot) in parts)
            {
                ObjectDisposedException.ThrowIf(part._disposed, part);
                foreach (var owner in part._owners) owner.VerifyAlive();
                if (snapshot.Fonts.Length != part.Snapshot.Fonts.Length ||
                    snapshot.Fonts.Where((f, i) => !ReferenceEquals(f, part.Snapshot.Fonts[i])).Any())
                    throw new ArgumentException("Foreign font catalog.", nameof(parts));
                binding._faces.AddRange(part._faces);
                binding._owners.AddRange(part._owners);
                snapshots.Add(snapshot);
            }
            binding.Snapshot = LayoutSnapshotComposer.Compose(source, bounds, snapshots);
            // ReflowDocument owns every paragraph layout until the composed binding is disposed.
            binding._composedLifetime = true;
            binding._visibleLines = new(binding.Snapshot.Blocks.SelectMany(block => block.Lines)
                .Select(line => (line, line.Bounds.Y, line.Bounds.Bottom)));
            return binding;
        }
        catch { binding.Dispose(); throw; }
    }

    public void DrawVisible(CanvasDrawingSession session, ICanvasBrush brush, double top, double bottom)
    {
        Guard(Snapshot, session, brush);
        var index = VisibleLines();
        var (start, end) = index.CandidateRange(top, bottom);
        for (var position = start; position < end; position++)
            if (index.Intersects(position, top, bottom))
                DrawRuns(index[position], session, Vector2.Zero, brush);
    }

    /// <summary>Replays the same immutable glyph geometry with per-cluster ink, never re-shaping or changing advances.</summary>
    public void DrawVisibleStyled(CanvasDrawingSession session, ICanvasBrush brush,
        double top, double bottom, Func<SourceRange, bool> isStyledLine,
        Func<SourceRange, ICanvasBrush?> colorAt)
    {
        ArgumentNullException.ThrowIfNull(isStyledLine);
        ArgumentNullException.ThrowIfNull(colorAt);
        Guard(Snapshot, session, brush);
        var index = VisibleLines();
        var (start, end) = index.CandidateRange(top, bottom);
        for (var position = start; position < end; position++)
        {
            if (!index.Intersects(position, top, bottom)) continue;
            var line = index[position];
            if (isStyledLine(line.Source))
                DrawStyledRuns(line, session, brush, colorAt);
            else DrawRuns(line, session, Vector2.Zero, brush);
        }
    }

    public void Draw(LayoutSnapshot snapshot, CanvasDrawingSession session, Vector2 documentOffset, ICanvasBrush brush)
    {
        Guard(snapshot, session, brush);
        foreach (var block in snapshot.Blocks)
        foreach (var line in block.Lines) DrawRuns(line, session, documentOffset, brush);
    }

    public void DrawLine(LayoutSnapshot snapshot, int blockIndex, int lineIndex,
        CanvasDrawingSession session, Vector2 documentOffset, ICanvasBrush brush)
    {
        Guard(snapshot, session, brush);
        var line = snapshot.Blocks[blockIndex].Lines[lineIndex];
        DrawRuns(line, session, documentOffset, brush);
    }

    private void Guard(LayoutSnapshot snapshot, CanvasDrawingSession session, ICanvasBrush brush)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(brush);
        // Resource slots are local to a live catalog; display names are not font identity.
        if (!ReferenceEquals(snapshot, Snapshot) && (snapshot.Fonts.Length != Snapshot.Fonts.Length ||
            snapshot.Fonts.Where((descriptor, index) => !ReferenceEquals(descriptor, Snapshot.Fonts[index])).Any()))
            throw new ArgumentException("Snapshot uses a foreign font catalog.", nameof(snapshot));
        if (!_composedLifetime)
            foreach (var owner in _owners) owner.VerifyAlive(); // Borrowed standalone binding.
    }

    private ViewportIntervalIndex<LineLayout> VisibleLines() => _visibleLines ??= new(
        Snapshot.Blocks.SelectMany(block => block.Lines)
            .Select(line => (line, line.Bounds.Y, line.Bounds.Bottom)));

    private void DrawRuns(LineLayout line, CanvasDrawingSession session, Vector2 offset, ICanvasBrush brush)
    {
        foreach (var run in line.Runs)
        {
            var glyphs = Glyphs(run);
            // Read geometry from the supplied snapshot; never read captured run Origin/Glyphs during replay.
            var point = GlyphPaintCoordinates.Resolve(line, run, new(offset.X, offset.Y));
            var baseline = new Vector2(Single(point.X), Single(point.Y));
            Export.PdfTextDescriptions.Draw(session, baseline, _faces[run.FontIndex], run, glyphs, brush, Snapshot.Source);
        }
    }

    private void DrawStyledRuns(LineLayout line, CanvasDrawingSession session,
        ICanvasBrush defaultBrush, Func<SourceRange, ICanvasBrush?> colorAt)
    {
        foreach (var run in line.Runs)
        {
            var glyphs = Glyphs(run);
            if (!_clusterOrderCache.TryGetValue(run, out var clusters))
            {
                clusters = run.Clusters.OrderBy(cluster => cluster.GlyphStart).ToArray();
                _clusterOrderCache.Add(run, clusters);
            }
            var origin = GlyphPaintCoordinates.Resolve(line, run, new(0, 0));
            var start = 0; var prefix = 0.0;
            var current = colorAt(clusters[0].Source) ?? defaultBrush;
            for (var index = 1; index <= clusters.Length; index++)
            {
                var next = index == clusters.Length ? null : colorAt(clusters[index].Source) ?? defaultBrush;
                if (index < clusters.Length && ReferenceEquals(current, next)) continue;
                var end = index == clusters.Length ? glyphs.Length : clusters[index].GlyphStart;
                if (end > start)
                {
                    var count = end - start;
                    CanvasGlyph[] slice;
                    if (start == 0 && count == glyphs.Length) slice = glyphs;
                    else if (!_coloredGlyphCache.TryGetValue((run, start, count), out slice!))
                    {
                        slice = new CanvasGlyph[count];
                        Array.Copy(glyphs, start, slice, 0, count);
                        _coloredGlyphCache.Add((run, start, count), slice);
                    }
                    var direction = (run.BidiLevel & 1) == 0 ? 1 : -1;
                    var baseline = new Vector2(Single(origin.X + direction * prefix), Single(origin.Y));
                    Export.PdfTextDescriptions.Draw(session, baseline, _faces[run.FontIndex], run, slice, current, Snapshot.Source, start);
                    for (var glyph = start; glyph < end; glyph++) prefix += run.Glyphs[glyph].Advance;
                }
                start = end;
                current = next!;
            }
        }
    }

    private CanvasGlyph[] Glyphs(GlyphRunLayout run)
    {
        if (_glyphCache.TryGetValue(run, out var cached)) return cached;
        var glyphs = run.Glyphs.Select(glyph => new CanvasGlyph
        {
            Index = glyph.Index, Advance = Single(glyph.Advance),
            AdvanceOffset = Single(glyph.AdvanceOffset), AscenderOffset = Single(glyph.AscenderOffset)
        }).ToArray();
        _glyphCache.Add(run, glyphs);
        return glyphs;
    }

    private static float Single(double value)
    {
        if (!double.IsFinite(value) || value < -float.MaxValue || value > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "Coordinate or glyph metric cannot be represented by Direct2D.");
        return (float)value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _glyphCache.Clear();
        _clusterOrderCache.Clear();
        _coloredGlyphCache.Clear();
        _faces.Clear(); // Borrowed font faces: do not independently close them.
        _owners.Clear();
    }
}
