using System.Numerics;
using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Native.Rendering;
using MDEditor.Typesetting.Layout;
using MDEditor.Typesetting.Typography;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;
using Windows.UI.Text;

namespace MDEditor.Native.Text;

/// <summary>Width-independent native glyph owners for one projected GFM table row.</summary>
internal sealed class EditableTableRowSession : IDisposable
{
    private readonly ShapedText?[] _cells;
    private readonly MarkdownTableDisplay _table;
    private readonly ReflowParagraph _style;
    private readonly SourceTextSnapshot _source;
    private bool _disposed;
    internal int TableIndex => _table.TableIndex;
    internal int RowIndex => _table.RowIndex;
    internal int ColumnCount => _table.Alignments.Length;
    internal double[] NaturalWidths { get; }
    internal double NaturalHeight { get; }

    internal EditableTableRowSession(CanvasDevice device, ReflowContent content, int paragraphIndex)
    {
        _source = content.Source;
        _style = content.Paragraphs[paragraphIndex];
        _table = _style.Table ?? throw new ArgumentException("Paragraph is not a table row.", nameof(content));
        var shaper = new DirectWriteTextShaper();
        _cells = new ShapedText?[_table.Cells.Length];
        NaturalWidths = new double[ColumnCount];
        var height = 20d;
        try
        {
            using var format = new CanvasTextFormat
            {
                FontFamily = _style.Family, FontSize = Math.Min(_style.FontSize, 18),
                LocaleName = _style.Locale, WordWrapping = CanvasWordWrapping.NoWrap,
                Direction = CanvasTextDirection.LeftToRightThenTopToBottom
            };
            using var typography = new CanvasTypography();
            typography.AddFeature(CanvasTypographyFeatureName.StandardLigatures, 1);
            for (var column = 0; column < _table.Cells.Length; column++)
            {
                var range = _table.Cells[column];
                var text = _source.GetText(range).Replace('\t', ' ');
                if (text.Length == 0)
                {
                    NaturalWidths[column] = 56;
                    continue;
                }
                var localStart = range.Start - _style.Source.Start;
                _cells[column] = shaper.Shape(device, text, format, typography, layout =>
                {
                    if (_table.RowIndex == 0)
                        layout.SetFontWeight(0, text.Length, new FontWeight { Weight = 600 });
                    foreach (var span in _style.InlineStyles ?? [])
                    {
                        var start = Math.Max(localStart, span.Display.Start);
                        var end = Math.Min(localStart + text.Length, span.Display.End);
                        if (start >= end) continue;
                        if ((span.Style & MarkdownVisualStyle.Strong) != 0)
                            layout.SetFontWeight(start - localStart, end - start,
                                new FontWeight { Weight = 600 });
                        if ((span.Style & MarkdownVisualStyle.Emphasis) != 0)
                            layout.SetFontStyle(start - localStart, end - start, FontStyle.Italic);
                        if ((span.Style & MarkdownVisualStyle.Code) != 0)
                        {
                            layout.SetFontFamily(start - localStart, end - start, _style.CodeFamily);
                            if (_style.StyleIndex == ReflowContent.FileStyleIndex)
                                layout.SetFontSize(start - localStart, end - start,
                                    Math.Min(_style.FontSize, 18) * 0.85f);
                        }
                    }
                    if (_style.CjkFamily is not null)
                        foreach (var span in ScriptFontPolicy.CjkOverrides(text))
                            layout.SetFontFamily(span.Start, span.Length, _style.CjkFamily);
                });
                NaturalWidths[column] = Math.Max(56, _cells[column]!.Advance + 24);
                height = Math.Max(height, _cells[column]!.Height);
            }
            for (var column = _table.Cells.Length; column < NaturalWidths.Length; column++)
                NaturalWidths[column] = 56;
            NaturalHeight = Math.Max(34, height + 14);
        }
        catch { Dispose(); throw; }
    }

    internal EditableTableRowLayout Layout(SharedResource<EditableTableRowSession>.Lease lease,
        double[] widths, double left, double top) => new(lease, widths, left, top);

    internal ShapedText? Shape(int column) => _cells[column];
    internal MarkdownTableDisplay Table => _table;
    internal ReflowParagraph Style => _style;
    internal SourceTextSnapshot Source => _source;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var cell in _cells) cell?.Dispose();
    }
}

internal sealed class EditableTableRowLayout : IDisposable
{
    private const double TextTopInset = 7;
    private readonly SharedResource<EditableTableRowSession>.Lease _lease;
    private readonly double[] _widths;
    private readonly double _left, _top;
    private bool _disposed;
    internal double Bottom { get; }
    internal double Top => _top;
    internal double Right => _left + _widths.Sum();
    internal int TableIndex => _lease.Value.TableIndex;
    internal int RowIndex => _lease.Value.RowIndex;
    internal TextInteractionMap Interaction { get; }

    internal EditableTableRowLayout(SharedResource<EditableTableRowSession>.Lease lease,
        double[] widths, double left, double top)
    {
        _lease = lease;
        try
        {
            _widths = widths;
            _left = left; _top = top;
            Bottom = top + lease.Value.NaturalHeight;
            Interaction = BuildInteraction();
        }
        catch { lease.Dispose(); throw; }
    }

    private TextInteractionMap BuildInteraction()
    {
        var session = _lease.Value;
        var spans = new List<TextInteractionSpan>();
        var x = _left;
        for (var column = 0; column < session.Table.Cells.Length; column++)
        {
            var cell = session.Table.Cells[column];
            var shape = session.Shape(column);
            var textX = TextX(session, column, x, _widths[column]);
            var textEnd = textX + (shape?.Advance ?? 0);
            // The whole cell, including alignment padding and empty cells, must be a
            // caret target. Zero-length spans add hit stops without selecting padding.
            spans.Add(new(new(cell.Start, 0), x + 2, textX));
            if (shape is null)
                spans.Add(new(cell, x + 12, x + _widths[column] - 12));
            else
                foreach (var run in shape.Runs)
                {
                    var advances = new double[run.Glyphs.Count + 1];
                    for (var index = 0; index < run.Glyphs.Count; index++)
                        advances[index + 1] = advances[index] + run.Glyphs[index].Advance;
                    var rtl = (run.BidiLevel & 1) != 0;
                    foreach (var cluster in run.Clusters)
                    {
                        var range = new SourceRange(cell.Start + cluster.SourceStart,
                            cluster.SourceLength);
                        var first = textX + run.BaselineX +
                            (rtl ? -advances[^1] + advances[cluster.GlyphStart] :
                                advances[cluster.GlyphStart]);
                        var last = textX + run.BaselineX +
                            (rtl ? -advances[^1] + advances[cluster.GlyphStart + cluster.GlyphCount] :
                                advances[cluster.GlyphStart + cluster.GlyphCount]);
                        if (rtl) (first, last) = (last, first);
                        TextInteractionMap.AddGraphemes(spans, session.Source, range, first, last);
                    }
                }
            spans.Add(new(new(cell.End, 0), textEnd, x + _widths[column] - 2));
            x += _widths[column];
        }
        var line = new TextInteractionLine(session.Style.Source,
            new(_left, _top, _widths.Sum(), Bottom - _top), spans);
        return new(session.Source, TextSurface.Body, [line]);
    }

    private static double TextX(EditableTableRowSession session, int column, double left, double width)
    {
        var advance = session.Shape(column)?.Advance ?? 0;
        var available = Math.Max(0, width - 24 - advance);
        return left + 12 + (session.Table.Alignments[column] switch
        {
            MarkdownTableAlignment.Center => available / 2,
            MarkdownTableAlignment.Right => available,
            _ => 0
        });
    }

    internal MarkdownTablePointerHit Hit(LayoutPoint point) =>
        MarkdownTableCaretPolicy.HitTest(point.X, point.Y, _left, _top, Bottom, _widths);

    /// <summary>Code decoration follows glyph ink, not the taller row or another cell's font.</summary>
    internal (double Y, double Height)? InlineCodeBounds(SourceRange source)
    {
        var session = _lease.Value;
        var top = double.PositiveInfinity;
        var bottom = double.NegativeInfinity;
        for (var column = 0; column < session.Table.Cells.Length; column++)
        {
            var cell = session.Table.Cells[column];
            var start = Math.Max(cell.Start, source.Start);
            var end = Math.Min(cell.End, source.End);
            if (start >= end || session.Shape(column) is not { } shape) continue;
            var ink = shape.VerticalInkBounds(new(start - cell.Start, end - start)) ?? (Y: 0d, Height: shape.Height);
            var y = _top + TextTopInset + ink.Y;
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y + ink.Height);
        }
        return double.IsFinite(top) ? (top, bottom - top) : null;
    }

    internal void Draw(CanvasDrawingSession drawing, Microsoft.Graphics.Canvas.Brushes.ICanvasBrush ink,
        double top, double bottom, double zoom, GithubMarkdownTheme? theme = null)
    {
        DrawBackground(drawing, top, bottom, zoom, theme ?? GithubMarkdownTheme.Light);
        DrawText(drawing, ink, top, bottom);
    }

    internal void DrawBackground(CanvasDrawingSession drawing, double top, double bottom,
        double zoom, GithubMarkdownTheme theme)
    {
        if (Bottom < top || _top > bottom) return;
        var session = _lease.Value;
        var fill = session.RowIndex % 2 == 1 ? theme.MutedBackground : theme.Background;
        drawing.FillRectangle(new Windows.Foundation.Rect(_left, _top, _widths.Sum(),
            Bottom - _top), fill);
        var border = theme.Border;
        var x = _left;
        drawing.DrawLine(new((float)_left, (float)_top), new((float)Right, (float)_top),
            border, 1f / (float)zoom);
        drawing.DrawLine(new((float)_left, (float)Bottom), new((float)Right, (float)Bottom),
            border, 1f / (float)zoom);
        drawing.DrawLine(new((float)_left, (float)_top), new((float)_left, (float)Bottom),
            border, 1f / (float)zoom);
        for (var column = 0; column < _widths.Length; column++)
        {
            x += _widths[column];
            drawing.DrawLine(new((float)x, (float)_top), new((float)x, (float)Bottom),
                border, 1f / (float)zoom);
        }
    }

    internal void DrawText(CanvasDrawingSession drawing,
        Microsoft.Graphics.Canvas.Brushes.ICanvasBrush ink, double top, double bottom)
    {
        if (Bottom < top || _top > bottom) return;
        var session = _lease.Value;
        var x = _left;
        for (var column = 0; column < _widths.Length; column++)
        {
            if (session.Shape(column) is { } shape)
                shape.Draw(drawing, new Vector2((float)TextX(session, column, x, _widths[column]),
                    (float)(_top + TextTopInset)), ink);
            x += _widths[column];
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lease.Dispose();
    }
}
