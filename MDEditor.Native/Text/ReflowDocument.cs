using System.Diagnostics;
using System.Numerics;
using MDEditor.Core.Text;
using MDEditor.Core.Markdown;
using EditorBlockKind = MDEditor.Core.Markdown.MarkdownBlockKind;
using MDEditor.Typesetting.Hyphenation;
using MDEditor.Typesetting.Layout;
using MDEditor.Typesetting.LineBreaking;
using MDEditor.Typesetting.Markdown;
using MDEditor.Typesetting.Mathematics;
using MDEditor.Typesetting.Typography;
using MDEditor.Native.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;

namespace MDEditor.Native.Text;

public sealed record ReflowParagraph(string Label, SourceRange Source, string Family, float FontSize, string Locale,
    int StyleIndex = 0, IReadOnlyList<MarkdownStyleSpan>? InlineStyles = null,
    MarkdownBlockDisplay? Block = null, MarkdownCodeDisplay? Code = null,
    MarkdownTableDisplay? Table = null, int? EquationNumber = null,
    string? CjkFamily = null, string CodeFamily = TypographyPreset.CodeFamily,
    float LineAdvance = 1.3f, int HeadingLevel = 0, bool SourceLine = false,
    MarkdownThematicBreakDisplay? ThematicBreak = null);
public sealed record ReflowSection(ReflowParagraph Style, double LabelY, double BodyY,
    double Bottom, bool Feasible);

/// <summary>One immutable source/style epoch. A worker never reads the mutable edit buffer.</summary>
public sealed class ReflowContent
{
    public SourceTextSnapshot Source { get; }
    public MarkdownRichTextProjection? Presentation { get; }
    public IReadOnlyList<ReflowParagraph> Paragraphs { get; }
    public int MathAfterParagraphIndex { get; }
    public bool ShowSampleMathPage { get; }
    public IReadOnlyList<IReadOnlyList<MarkdownMathNode>> EditableMathByParagraph { get; }
    public IReadOnlyDictionary<int, MathLayoutResult> EditableMathLayouts { get; }
    public IReadOnlyList<IReadOnlyList<MarkdownImageAtom>> ImagesByParagraph { get; }
    public TypographyPreset Typography { get; }

    public ReflowContent(SourceTextSnapshot source, IEnumerable<ReflowParagraph> paragraphs,
        int mathAfterParagraphIndex = 0,
        IReadOnlyDictionary<(string Formula, MarkdownMathKind Kind), MathLayoutResult>? editableMathBySource = null,
        MarkdownRichTextProjection? presentation = null,
        IReadOnlyDictionary<string, MarkdownImageResource>? imageResources = null,
        TypographyPreset? typography = null, bool showSampleMathPage = true)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Typography = typography ?? TypographyPreset.Balanced;
        if (presentation is not null && !ReferenceEquals(source, presentation.Text.Display))
            throw new ArgumentException("Presentation must own the display snapshot.", nameof(presentation));
        Presentation = presentation;
        ArgumentNullException.ThrowIfNull(paragraphs);
        var copy = paragraphs.ToArray();
        if (copy.Length == 0 || mathAfterParagraphIndex < 0 || mathAfterParagraphIndex >= copy.Length ||
            copy.Any(paragraph => !source.FullRange.Contains(paragraph.Source)))
            throw new ArgumentException("Reflow content needs source-mapped paragraphs.", nameof(paragraphs));
        Paragraphs = Array.AsReadOnly(copy);
        MathAfterParagraphIndex = mathAfterParagraphIndex;
        ShowSampleMathPage = showSampleMathPage;
        var math = presentation is null ? MarkdownMathDocument.Parse(source).Math.ToArray() :
            presentation.Text.MathSpans.Where(span => span != presentation.Text.RevealedMath)
                .Select(span => new MarkdownMathNode(
                presentation.Text.ToDisplayRange(span.Source),
                presentation.Text.ToDisplayRange(span.Content),
                span.Kind == MarkdownMathSyntaxKind.Display ? MarkdownMathKind.Display :
                    MarkdownMathKind.Inline)).ToArray();
        var layoutFormulaByStart = presentation?.Text.MathSpans.ToDictionary(
            span => presentation.Text.ToDisplayRange(span.Source).Start,
            span => presentation.Text.References.LayoutContent(span));
        var imagesInSourceOrder = presentation?.Text.IsSourceMode == true ? [] : presentation?.Text.References.Images
            .Select(image => (Image: image, Display: presentation.Text.ToDisplayRange(image.Source)))
            .OrderBy(entry => entry.Display.Start).ToArray() ?? [];
        var perParagraph = new IReadOnlyList<MarkdownMathNode>[copy.Length];
        var bound = new Dictionary<int, MathLayoutResult>();
        var images = new IReadOnlyList<MarkdownImageAtom>[copy.Length];
        var mathCursor = 0;
        var imageCursor = 0;
        for (var index = 0; index < copy.Length; index++)
        {
            var range = copy[index].Source;
            while (mathCursor < math.Length && math[mathCursor].Source.End <= range.Start) mathCursor++;
            var ready = new List<MarkdownMathNode>();
            for (var position = mathCursor; position < math.Length && math[position].Source.Start < range.End; position++)
            {
                var node = math[position];
                if (!range.Contains(node.Source)) continue;
                var formula = layoutFormulaByStart is not null &&
                    layoutFormulaByStart.TryGetValue(node.Source.Start, out var cleaned)
                    ? cleaned : node.GetContent(source);
                if (editableMathBySource?.TryGetValue((formula, node.Kind), out var layout) == true &&
                    layout.Source == formula)
                {
                    bound.Add(node.Source.Start, layout);
                    ready.Add(node);
                }
            }
            // Unready or invalid formulas stay visible as source text. A failed formula must
            // never prevent a different ready formula in the same paragraph from rendering.
            perParagraph[index] = ready.AsReadOnly();
            while (imageCursor < imagesInSourceOrder.Length &&
                imagesInSourceOrder[imageCursor].Display.End <= range.Start) imageCursor++;
            var paragraphImages = new List<MarkdownImageAtom>();
            for (var position = imageCursor; position < imagesInSourceOrder.Length &&
                imagesInSourceOrder[position].Display.Start < range.End; position++)
            {
                var (image, display) = imagesInSourceOrder[position];
                if (!range.Contains(display)) continue;
                paragraphImages.Add(new(display, image.Alternative,
                    imageResources is not null && imageResources.TryGetValue(image.Target, out var resource)
                        ? resource : MarkdownImageResource.Loading(image.Target)));
            }
            images[index] = paragraphImages;
        }
        EditableMathByParagraph = Array.AsReadOnly(perParagraph);
        EditableMathLayouts = bound;
        ImagesByParagraph = Array.AsReadOnly(images);
    }

    public bool HasVisualAtoms(int paragraphIndex) =>
        (EditableMathByParagraph[paragraphIndex].Count > 0 || ImagesByParagraph[paragraphIndex].Count > 0) &&
        EditableMathByParagraph[paragraphIndex].All(node => EditableMathLayouts.ContainsKey(node.Source.Start));

    /// <summary>One shared style contract for loaded files, layout and the TSF caret anchor.</summary>
    public static int FileStyleIndex => ReflowSample.Paragraphs.Count;

    public static ReflowParagraph StyleFor(int index) => index switch
    {
        var sample when sample >= 0 && sample < FileStyleIndex => ReflowSample.Paragraphs[sample],
        var file when file == FileStyleIndex => new("", new SourceRange(0, 0), "Segoe UI", 16, "zh-CN", file),
        _ => throw new ArgumentOutOfRangeException(nameof(index), "Unknown paragraph style.")
    };

    public static ReflowContent FromStyled(StyledDocumentSnapshot styled,
        IReadOnlyDictionary<(string Formula, MarkdownMathKind Kind), MathLayoutResult>? editableMathBySource = null,
        MarkdownRichTextProjection? presentation = null,
        IReadOnlyDictionary<string, MarkdownImageResource>? imageResources = null,
        TypographyPreset? preset = null, bool showSampleMathPage = true)
    {
        ArgumentNullException.ThrowIfNull(styled);
        preset ??= TypographyPreset.Balanced;
        if (presentation is not null && !ReferenceEquals(presentation.Text.Source, styled.Source))
            throw new ArgumentException("Presentation must refer to the current source snapshot.", nameof(presentation));
        var source = presentation?.Text.Display ?? styled.Source;
        var lines = presentation is null ? styled.Lines : DocumentLineMap.Create(source);
        if (presentation?.Text.IsSourceMode == true)
        {
            var literal = lines.Lines.Select(line => new ReflowParagraph("", line.Content,
                TypographyPreset.CodeFamily, 14, "en-US", styled.StyleAt(line.Content.Start),
                CjkFamily: preset.CjkFamily, LineAdvance: 1.5f, SourceLine: true));
            return new(source, literal, presentation: presentation, typography: preset, showSampleMathPage: false);
        }
        var equationAtStart = presentation?.Text.References.Equations.ToDictionary(
            equation => presentation.Text.ToDisplayOffset(equation.Formula.Start),
            equation => equation.Number);
        var paragraphs = lines.Lines.Select(line =>
        {
            var sourceOffset = presentation is null ? line.Content.Start :
                presentation.Text.ToSourceOffset(line.Content.Start, ProjectionBoundary.AfterHidden);
            var index = styled.StyleAt(sourceOffset);
            var style = StyleFor(index);
            var code = presentation?.CodeLineAt(line.Content);
            var level = presentation?.HeadingLevelAt(line.Content) ?? 0;
            var github = index == FileStyleIndex;
            var size = code is not null ? (github ? 13.6f : 16f) : level == 0 ? style.FontSize :
                github ? style.FontSize * preset.HeadingScale(level) :
                Math.Max(style.FontSize, style.FontSize * preset.HeadingScale(level));
            var inline = code is null ? presentation?.ParagraphStyles(line.Content).ToList() : null;
            if (level > 0 && line.Content.Length > 0)
            {
                inline ??= new();
                inline.Add(new(new(0, line.Content.Length), MarkdownVisualStyle.Strong));
            }
            return new ReflowParagraph(line.Content.Length == 0 ? "" : style.Label, line.Content,
                preset.TextFamily(style.Family, code is not null), size,
                code is null ? style.Locale : "en-US", index,
                inline, presentation?.BlockAt(line.Content), code,
                presentation?.TableLineAt(line.Content),
                equationAtStart is not null && equationAtStart.TryGetValue(line.Content.Start, out var number)
                    ? number : null,
                preset.CjkFamily,
                TypographyPreset.CodeFamily,
                github ? (code is not null ? 1.45f : level > 0 ? 1.25f : 1.5f) : preset.LineAdvance,
                level, ThematicBreak: presentation?.ThematicBreakAt(line.Content));
        }).ToArray();
        // A ready display formula may span several physical source lines. It is one native
        // math paragraph; until its layout arrives, each line stays editable as literal text.
        if (presentation is not null && editableMathBySource is not null)
        {
            var combined = new List<ReflowParagraph>(paragraphs.Length);
            for (var index = 0; index < paragraphs.Length; index++)
            {
                var first = paragraphs[index];
                var display = presentation.Text.MathSpans.FirstOrDefault(span =>
                    span.Kind == MarkdownMathSyntaxKind.Display &&
                    span.Source.Start == first.Source.Start && span.Source.End > first.Source.End);
                if (display is not null && display != presentation.Text.RevealedMath &&
                    editableMathBySource.ContainsKey(
                    (presentation.Text.References.LayoutContent(display), MarkdownMathKind.Display)))
                {
                    var mapped = presentation.Text.ToDisplayRange(display.Source);
                    var closing = index + 1;
                    while (closing < paragraphs.Length && paragraphs[closing].Source.End < mapped.End)
                        closing++;
                    if (closing < paragraphs.Length && paragraphs[closing].Source.End == mapped.End &&
                        mapped.Length == display.Source.Length)
                    {
                        combined.Add(first with { Source = mapped });
                        index = closing;
                        continue;
                    }
                }
                combined.Add(first);
            }
            paragraphs = combined.ToArray();
        }
        var mathAfter = Array.FindLastIndex(paragraphs, paragraph => paragraph.StyleIndex == 0);
        return new(source, paragraphs, Math.Max(0, mathAfter), editableMathBySource,
            presentation, imageResources, preset, showSampleMathPage);
    }
}

/// <summary>Initial editable body content and its stable paragraph styles.</summary>
public static class ReflowSample
{
    private static readonly (string Label, string Text, string Font, float Size, string Locale)[] Items =
    [
        ("CHINESE / punctuation and mixed-script spacing",
            "“原生排版”不是把文字强行拉宽。中文标点（括号）、《书名》和“引号”需要遵守禁则；中西文如Windows2026、office与cafe\u0301应保留自然间距。省略号……与破折号——不能从中间断开；最后一行自然收尾。",
            "Arial", 18, "zh-CN"),
        ("ENGLISH / Knuth-Plass and discretionary hyphens",
            "High quality typography combines careful hyphenation with paragraph optimization. Representation and internationalization create opportunities for balanced composition. A dictionary proposes acceptable word divisions, while the complete paragraph determines whether a particular division improves the result. Original characters remain unchanged; generated hyphens belong only to the displayed line. Beautiful typography preserves natural letter shapes and keeps the final line comfortably short.",
            "Cambria", 17, "en-US"),
        ("LIGATURES / natural glyph shapes",
            "中文ffi汉字office中文", "Gabriola", 25, "zh-CN"),
        ("COMBINING MARKS / whole grapheme clusters",
            "中a\u0308\u0301文cafe\u0301中文", "Cambria", 20, "zh-CN"),
        ("BIDI / source order is not visual order",
            "中文 Latin العربية עברית 123 中文", "Segoe UI", 20, "zh-CN"),
        ("SCROLL / the document is not reduced to fit the window",
            "文档超过视口时使用纵向滚动，而不是把整页缩小。调整窗口宽度或编辑缩放会重新求解段落；屏幕DPI仅改变光栅密度，不应重复放大字体。源码、连字与组合字符始终保留。绿色边界表示逻辑行尾，非末行应与它对齐，末行保持自然长度。正文宽度无合法断行路径时会显示提示，不强行拉伸字形；扩大窗口或降低编辑缩放后自动恢复。",
            "Arial", 18, "zh-CN"),
        ("MARKDOWN / live heading", "# 原生 Markdown 实时编辑", "Arial", 18, "zh-CN"),
        ("MARKDOWN / live inline styles",
            "在同一正文中编辑 **加粗**、*强调*、~~删除线~~、`code` 和 [链接](https://example.com)。将光标移入格式可查看源码标记，移出后重新折叠。",
            "Arial", 18, "zh-CN"),
        ("STRUCTURE / quote, list and task editing",
            "> 原生引用与列表共用源码坐标。", "Arial", 18, "zh-CN"),
        ("STRUCTURE / unordered and nested",
            "- 无序项目，回车续项", "Arial", 18, "zh-CN"),
        ("STRUCTURE / unordered and nested",
            "  - 嵌套项目，可用 Tab / Shift+Tab 缩进", "Arial", 18, "zh-CN"),
        ("STRUCTURE / ordered and task",
            "1. 有序项目", "Arial", 18, "zh-CN"),
        ("STRUCTURE / ordered and task",
            "2. 回车自动续号", "Arial", 18, "zh-CN"),
        ("STRUCTURE / ordered and task",
            "- [ ] 待完成任务", "Arial", 18, "zh-CN"),
        ("STRUCTURE / ordered and task",
            "- [x] 已完成任务", "Arial", 18, "zh-CN"),
        ("MATH / four editable delimiters",
            "行内 $x_i^2+1$ 与 \\(\\frac{x+1}{y+1}\\) 都保留原始源码。", "Arial", 18, "zh-CN"),
        ("MATH / dollar display", "$$\\frac{a+1}{b+1}$$", "Arial", 18, "zh-CN"),
        // A blank block boundary is required before a GFM table header. Without
        // it Markdig continues the preceding math paragraph as ordinary text.
        ("MATH / bracket display", "\\[\\sqrt{x+1}\\]\n", "Arial", 18, "zh-CN"),
        ("TABLE / editable GFM cells", "| 名称 | 数值 | 说明 |", "Arial", 17, "zh-CN"),
        ("TABLE / delimiter", "| :--- | :---: | :---: |", "Arial", 17, "zh-CN"),
        ("TABLE / editable GFM cells", "| 中文 | 42 | a\\|b |", "Arial", 17, "zh-CN"),
        ("TABLE / editable GFM cells", "| `code` | 100 | **加粗** |\n", "Arial", 17, "zh-CN"),
        ("MEDIA / local image", "![本地示意图](Assets/Tiles/StoreDisplay-300.png)", "Arial", 18, "zh-CN"),
        ("MEDIA / missing resource", "![缺失图片](Assets/Tiles/missing-step27.png)", "Arial", 18, "zh-CN"),
        ("FOOTNOTE / live references", "脚注先引用[^detail]，再次引用[^detail]，然后查看下方定义。", "Arial", 18, "zh-CN"),
        ("FOOTNOTE / definition", "[^detail]: 修改此定义后两个上标仍应指向同一个脚注。", "Arial", 17, "zh-CN"),
        ("EQUATION / numbered display", "$$\\frac{m+1}{n+1}\\label{eq:ratio}$$", "Arial", 18, "zh-CN"),
        ("EQUATION / cross-reference", "公式\\eqref{eq:ratio}可被交叉引用；移走标签后显示未解析状态。", "Arial", 18, "zh-CN"),
        ("CODE / fenced C#", "```csharp", "Cascadia Code", 16, "en-US"),
        ("CODE / fenced C#", "// 中文注释：原生字形，不丢源码", "Cascadia Code", 16, "en-US"),
        ("CODE / fenced C#", "int answer = 42;", "Cascadia Code", 16, "en-US"),
        ("CODE / fenced C#", "Console.WriteLine(\"hello\");", "Cascadia Code", 16, "en-US"),
        ("CODE / fenced C#", "```", "Cascadia Code", 16, "en-US"),
        ("CODE / unclosed Python", "~~~python", "Cascadia Code", 16, "en-US"),
        ("CODE / unclosed Python", "print(\"你好\") # 未闭合围栏仍是代码", "Cascadia Code", 16, "en-US")
    ];
    public static SourceTextSnapshot Source { get; } = new(string.Join("\n\n", Items.Select(i => i.Text)), 100);
    public static IReadOnlyList<ReflowParagraph> Paragraphs { get; } = CreateParagraphs();
    public static ReflowContent Content { get; } = new(Source, Paragraphs);
    public static StyledDocumentBuffer CreateEditableBuffer()
    {
        var text = string.Join("\n", Items.Select(item => item.Text));
        var position = 0;
        var markers = new List<DocumentStyleMarker>();
        for (var index = 0; index < Items.Length; index++)
        {
            markers.Add(new(position, index));
            position += Items[index].Text.Length + 1;
        }
        return new(text, markers, Source.Version);
    }
    private static IReadOnlyList<ReflowParagraph> CreateParagraphs()
    {
        var paragraphs = new List<ReflowParagraph>(); var start = 0;
        for (var index = 0; index < Items.Length; index++)
        {
            var i = Items[index];
            paragraphs.Add(new(i.Label, new(start, i.Text.Length), i.Font, i.Size, i.Locale, index));
            start += i.Text.Length + 2;
        }
        return paragraphs.AsReadOnly();
    }
}

public sealed record ReflowGeometry(bool SourceCoverage, bool GeneratedAnchors, bool FiniteSpacing,
    double MaximumAdvanceError, double MaximumPaintSpanError, bool Passed);

/// <summary>Build on one worker, then transfer to the UI. Neither workers nor this object touch XAML.</summary>
public sealed class ReflowDocument : IDisposable
{
    private readonly List<ParagraphLayout> _paragraphs = new();
    private readonly List<ReflowParagraph> _renderedParagraphStyles = new();
    private readonly List<ParagraphItemMap> _maps = new();
    private readonly List<EditableMathParagraphLayout> _editableMathParagraphs = new();
    private readonly List<EditableTableRowLayout> _editableTableRows = new();
    private readonly List<TextInteractionLine> _thematicBreakLines = new();
    private readonly List<ReflowSection> _sections = new();
    private readonly List<(LayoutRect Rect, MarkdownVisualStyle Style)> _decorations = new();
    private SourceRange[] _linkRanges = [];
    private SourceRange[] _mutedRanges = [];
    private ViewportIntervalIndex<ReflowSection>? _visibleSections;
    private ViewportIntervalIndex<(LayoutRect Rect, MarkdownVisualStyle Style)>? _visibleDecorations;
    private ViewportIntervalIndex<EditableTableRowLayout>? _visibleTables;
    private ViewportIntervalIndex<EditableMathParagraphLayout>? _visibleMath;
    private ViewportIntervalIndex<CodePanel>? _visibleCodePanels;
    private sealed record CodePanel(double Top, double Bottom, double X, string Language, bool HasLabel);
    private ReflowContent _content = null!;
    private NativeSnapshotBinding? _binding;
    private MarkdownMathPage? _mathPage;
    private bool _disposed;
    public LayoutSnapshot Snapshot => _binding!.Snapshot;
    public ITextInteractionMap BodyInteraction { get; private set; } = null!;
    public TextInteractionMap? MathInteraction => _mathPage?.Interaction;
    public IReadOnlyList<ReflowSection> Sections => _sections.AsReadOnly();
    public double Width { get; private set; }
    public long SourceVersion => _content.Source.Version;
    public MarkdownRichTextProjection? Presentation => _content.Presentation;
    public double Height { get; private set; }
    /// <summary>Conservative cache weight including managed geometry and native glyph ownership.</summary>
    public long EstimatedRetainedBytes { get; private set; }
    public double? MathHeadingY => _mathPage?.Top;
    public string? MathPageFingerprint => _mathPage?.Fingerprint;
    public int InfeasibleParagraphs => _sections.Count(s => !s.Feasible);
    public int InfeasibleMathBlocks => _mathPage?.InfeasibleBlocks ?? 0;
    public ReflowGeometry Geometry { get; private set; } = null!;

    /// <summary>
    /// Consume the entire table-row band. Outside the grid, the click has no caret;
    /// it must not fall through to the paragraph map's nearest-line extrapolation.
    /// </summary>
    public bool TryHitTableRow(LayoutPoint point, out TextCaret? caret)
    {
        caret = null;
        if (_content.Presentation is not { } presentation) return false;
        var rows = _visibleTables;
        if (rows is null) return false;
        var (start, candidateEnd) = rows.CandidateRange(point.Y, point.Y);
        for (var position = start; position < candidateEnd; position++)
        {
            if (!rows.Intersects(position, point.Y, point.Y)) continue;
            var layout = rows[position];
            var hit = layout.Hit(point);
            if (!hit.InRow) continue;
            if (hit.Column < 0) return true;
            var column = hit.Column;
            var rawCell = presentation.Text.Tables.Tables[layout.TableIndex]
                .Rows[layout.RowIndex].Cells[column].Source;
            var projected = layout.Interaction.HitTest(point);
            if (projected is null) return true;
            var source = presentation.Text.Source;
            var offset = presentation.Text.ToSourceOffset(projected.Value.Offset,
                projected.Value.Affinity == CaretAffinity.Upstream ?
                    ProjectionBoundary.BeforeHidden : ProjectionBoundary.AfterHidden);
            offset = MarkdownTableCaretPolicy.EditableOffset(source, rawCell, offset);
            var end = MarkdownTableCaretPolicy.EditableOffset(source, rawCell, int.MaxValue);
            caret = new(TextSurface.Body, source.Version, offset,
                offset == end ? CaretAffinity.Upstream : CaretAffinity.Downstream);
            return true;
        }
        return false;
    }

    public static ReflowDocument Create(CanvasDevice device, double width, CancellationToken cancellationToken = default)
    {
        using var engine = new ReflowEngine(device);
        return engine.Build(width, cancellationToken);
    }

    internal static ReflowDocument CreateCore(CanvasDevice device, double width, ReflowContent content,
        IReadOnlyList<ParagraphLayoutSession?> sessions,
        IReadOnlyList<SharedResource<EditableMathParagraphSession>?> editableMathSessions,
        IReadOnlyList<SharedResource<EditableTableRowSession>?> tableSessions,
        SharedResource<MarkdownMathSession>? mathSession,
        CancellationToken cancellationToken, bool cacheOnly = false, long deadline = 0)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!double.IsFinite(width) || width <= 0 || width > 100_000) throw new ArgumentOutOfRangeException(nameof(width));
        var document = new ReflowDocument { Width = width, _content = content };
        try
        {
            var tableWidths = new Dictionary<int, double[]>();
            foreach (var resource in tableSessions)
            {
                if (resource is null) continue;
                using var lease = resource.Acquire();
                var row = lease.Value;
                if (!tableWidths.TryGetValue(row.TableIndex, out var columns))
                    tableWidths[row.TableIndex] = columns = new double[row.ColumnCount];
                for (var column = 0; column < columns.Length; column++)
                    columns[column] = Math.Max(columns[column], row.NaturalWidths[column]);
            }
            double y = 0;
            for (var index = 0; index < content.Paragraphs.Count; index++)
            {
                var style = content.Paragraphs[index];
                var github = style.StyleIndex == ReflowContent.FileStyleIndex;
                var foldedRule = style.ThematicBreak is { MarkerHidden: true };
                var bodyY = foldedRule ? y + MarkdownThematicBreakLayout.Margin :
                    style.SourceLine ? y : github && style.Code is null ? y + (style.HeadingLevel > 0 && index > 0 ? 8 :
                    style.Source.Length == 0 ? 4 : 0) :
                    style.Table is { } tableLine ? y + (tableLine.RowIndex == 0 ? 28 : 0) :
                    y + (style.Source.Length == 0 ? 4 : style.Code is null ? 28 :
                    style.Code.Value.Role == MarkdownCodeLineRole.OpeningFence ? 22 : 5);
                cancellationToken.ThrowIfCancellationRequested();
                double bottom;
                if (foldedRule)
                {
                    var rule = MarkdownThematicBreakLayout.Create(style.Source, width, bodyY,
                        style.FontSize, ParagraphIndent(style, width));
                    document._thematicBreakLines.Add(rule);
                    bottom = rule.Bounds.Bottom;
                    document._sections.Add(new(style, y, bodyY, bottom, true));
                }
                else if (style.Table is { RowIndex: -1 })
                {
                    bottom = y;
                    document._sections.Add(new(style, y, bodyY, bottom, true));
                }
                else if (tableSessions[index] is { } tableResource)
                {
                    var lease = tableResource.Acquire();
                    var columns = tableWidths[style.Table!.Value.TableIndex].ToArray();
                    var indent = ParagraphIndent(style, width);
                    var extra = Math.Max(0, width - indent - columns.Sum()) / columns.Length;
                    for (var column = 0; column < columns.Length; column++) columns[column] += extra;
                    var tableRow = new EditableTableRowLayout(lease, columns, indent, bodyY);
                    document._editableTableRows.Add(tableRow);
                    bottom = tableRow.Bottom;
                    document._sections.Add(new(style, y, bodyY, bottom, true));
                }
                else if (editableMathSessions[index] is { } mathSessionForParagraph)
                {
                    var lease = mathSessionForParagraph.Acquire();
                    var mathWidth = Math.Max(1, width - (style.EquationNumber is null ? 0 : 52));
                    var mathParagraph = EditableMathParagraphLayout.Create(lease, mathWidth, bodyY,
                        cancellationToken);
                    document._editableMathParagraphs.Add(mathParagraph);
                    bottom = mathParagraph.Feasible ? mathParagraph.Bottom : y + 92;
                    document._sections.Add(new(style, y, bodyY, bottom, mathParagraph.Feasible));
                }
                else
                {
                    var session = sessions[index] ?? throw new InvalidOperationException("Missing paragraph session.");
                    var indent = ParagraphIndent(style, width);
                    var layoutWidth = style.Code is null ? width - indent :
                        Math.Min(100_000, Math.Max(width - indent, session.NaturalAdvance + 8));
                    var paragraph = session.Layout(layoutWidth, new(indent, bodyY), cancellationToken: cancellationToken,
                        cacheOnly: cacheOnly, deadline: deadline);
                    bottom = paragraph?.Snapshot.Bounds.Bottom ?? y + 92;
                    document._sections.Add(new(style, y, bodyY, bottom, paragraph is not null));
                    if (paragraph is not null)
                    {
                        document._paragraphs.Add(paragraph);
                        document._renderedParagraphStyles.Add(style);
                        document._maps.Add(session.Map);
                    }
                }
                var continuedCode = style.Code is { } code &&
                    index + 1 < content.Paragraphs.Count &&
                    content.Paragraphs[index + 1].Code is { } next &&
                    next.BlockIndex == code.BlockIndex;
                var continuedTable = style.Table is { } table && index + 1 < content.Paragraphs.Count &&
                    content.Paragraphs[index + 1].Table is { } nextTable &&
                    nextTable.TableIndex == table.TableIndex;
                y = bottom + (foldedRule ? MarkdownThematicBreakLayout.Margin :
                    style.SourceLine ? style.FontSize * 0.5 : continuedTable ? 0 : continuedCode ? 2 : github ? 16 : 32);
                if (content.ShowSampleMathPage && index == content.MathAfterParagraphIndex && mathSession is not null)
                {
                    var lease = mathSession.Acquire();
                    document._mathPage = MarkdownMathPage.Create(lease, width, y, cancellationToken);
                    y = document._mathPage.Bottom + 24;
                }
            }
            if (cacheOnly && Stopwatch.GetTimestamp() > deadline) throw new CachedLayoutUnavailableException();
            cancellationToken.ThrowIfCancellationRequested();
            document.Height = y;
            var snapshotWidth = Math.Max(width, document._paragraphs.Count == 0 ? width :
                document._paragraphs.Max(paragraph => paragraph.Snapshot.Bounds.Right));
            if (document._editableTableRows.Count > 0)
                snapshotWidth = Math.Max(snapshotWidth, document._editableTableRows.Max(row => row.Right));
            document._binding = NativeSnapshotBinding.Compose(content.Source,
                new(0, 0, snapshotWidth, y), document._paragraphs);
            var textLines = TextInteractionMap.FromSnapshot(document.Snapshot).Lines;
            var mathLines = document._editableMathParagraphs.SelectMany(paragraph => paragraph.Interaction.Lines);
            var tableLines = document._editableTableRows.SelectMany(row => row.Interaction.Lines);
            var displayInteraction = new TextInteractionMap(content.Source, TextSurface.Body,
                textLines.Concat(mathLines).Concat(tableLines).Concat(document._thematicBreakLines)
                    .OrderBy(line => line.Bounds.Y));
            if (content.Presentation is { } rich)
                document.AddDecorations(rich, displayInteraction);
            document.BodyInteraction = content.Presentation is { } presentation
                ? new MarkdownProjectionInteractionMap(presentation.Text, displayInteraction)
                : displayInteraction;
            document.BuildViewportIndexes();
            document.Geometry = document.CheckGeometry();
            if (!document.Geometry.Passed) throw new InvalidOperationException("Production document geometry is invalid.");
            document.EstimatedRetainedBytes = document.EstimateRetainedBytes();
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private void AddDecorations(MarkdownRichTextProjection rich, TextInteractionMap interaction)
    {
        _linkRanges = MergeRanges(rich.Styles.Where(style =>
            (style.Style & MarkdownVisualStyle.Link) != 0).Select(style => style.Display));
        _mutedRanges = MergeRanges(_renderedParagraphStyles.Where(style =>
            style.Block is { QuoteDepth: > 0 } || style.HeadingLevel == 6)
            .Select(style => style.Source));
        var tableByLine = new Dictionary<SourceRange, EditableTableRowLayout>();
        foreach (var table in _editableTableRows)
            foreach (var line in table.Interaction.Lines)
                tableByLine.TryAdd(line.Source, table);
        var mathByLine = new Dictionary<SourceRange, EditableMathParagraphLayout>();
        foreach (var paragraph in _editableMathParagraphs)
            foreach (var line in paragraph.Interaction.Lines)
                mathByLine.TryAdd(line.Source, paragraph);
        foreach (var line in interaction.Lines)
        {
            var low = 0;
            var high = rich.Styles.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (rich.Styles[middle].Display.End <= line.Source.Start) low = middle + 1;
                else high = middle;
            }
            for (var styleIndex = low; styleIndex < rich.Styles.Length &&
                rich.Styles[styleIndex].Display.Start < line.Source.End; styleIndex++)
            {
                var style = rich.Styles[styleIndex];
                var spans = line.Spans.Where(span => span.Source.Start < style.Display.End &&
                    style.Display.Start < span.Source.End && span.Right > span.Left)
                    .OrderBy(span => span.Left).ToArray();
                if (spans.Length == 0) continue;
                var height = line.Bounds.Height;
                var y = line.Bounds.Y;
                if ((style.Style & MarkdownVisualStyle.Code) != 0)
                {
                    var codeBounds = tableByLine.TryGetValue(line.Source, out var table)
                        ? table.InlineCodeBounds(style.Display)
                        : mathByLine.TryGetValue(line.Source, out var mathParagraph)
                            ? mathParagraph.InlineTextBounds(line.Source, style.Display) : null;
                    if (codeBounds is { } textBounds)
                    {
                        // The paint path insets the stored decoration by 2 DIP vertically.
                        // Keep 2 DIP of visible padding around code, not the row or formula.
                        y = textBounds.Y - 4;
                        height = textBounds.Height + 8;
                    }
                }
                var left = spans[0].Left;
                var right = spans[0].Right;
                for (var index = 1; index < spans.Length; index++)
                {
                    if (spans[index].Left > right + 2)
                    {
                        _decorations.Add((new(left, y, right - left, height), style.Style));
                        left = spans[index].Left;
                    }
                    right = Math.Max(right, spans[index].Right);
                }
                _decorations.Add((new(left, y, right - left, height), style.Style));
            }
        }
    }

    private static SourceRange[] MergeRanges(IEnumerable<SourceRange> ranges)
    {
        var merged = new List<SourceRange>();
        foreach (var range in ranges.OrderBy(range => range.Start))
        {
            if (range.Length == 0) continue;
            if (merged.Count > 0 && merged[^1].End >= range.Start)
            {
                var previous = merged[^1];
                merged[^1] = new(previous.Start, Math.Max(previous.End, range.End) - previous.Start);
            }
            else merged.Add(range);
        }
        return merged.ToArray();
    }

    private static bool Overlaps(SourceRange[] ranges, SourceRange candidate)
    {
        var low = 0; var high = ranges.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (ranges[middle].End <= candidate.Start) low = middle + 1;
            else high = middle;
        }
        return low < ranges.Length && ranges[low].Start < candidate.End;
    }

    private void BuildViewportIndexes()
    {
        _visibleSections = new(_sections.Select(section => (section, section.LabelY, section.Bottom)));
        _visibleDecorations = new(_decorations.Select(decoration =>
            (decoration, decoration.Rect.Y, decoration.Rect.Bottom)));
        _visibleTables = new(_editableTableRows.Select(row => (row, row.Top, row.Bottom)));
        _visibleMath = new(_editableMathParagraphs.Select(paragraph =>
            (paragraph, paragraph.Top, paragraph.Bottom)));
        var panels = new List<(CodePanel Panel, double Top, double Bottom)>();
        for (var index = 0; index < _sections.Count;)
        {
            if (_sections[index].Style.Code is not { } code) { index++; continue; }
            var first = index;
            while (index + 1 < _sections.Count && _sections[index + 1].Style.Code is { } next &&
                next.BlockIndex == code.BlockIndex) index++;
            var last = index++;
            var top = _sections[first].LabelY;
            var bottom = _sections[last].Bottom + 8;
            panels.Add((new CodePanel(top, bottom, code.QuoteDepth * 20 + 3,
                code.Language, code.Role == MarkdownCodeLineRole.OpeningFence), top, bottom));
        }
        _visibleCodePanels = new(panels);
    }

    private long EstimateRetainedBytes()
    {
        long bytes = 65_536 + (long)_content.Source.Length * 2 +
            (long)_sections.Count * 256 + (long)_decorations.Count * 96;
        foreach (var block in Snapshot.Blocks)
        {
            bytes += 256;
            foreach (var line in block.Lines)
            {
                bytes += 192;
                foreach (var run in line.Runs)
                    bytes += 320 + (long)run.Glyphs.Length * 64 +
                        (long)run.Clusters.Length * 96;
            }
        }
        bytes += (long)_editableTableRows.Count * 4096 +
            (long)_editableMathParagraphs.Count * 8192 + (long)_thematicBreakLines.Count * 224;
        return Math.Max(1, bytes);
    }

    private ReflowGeometry CheckGeometry()
    {
        var coverage = true; var anchors = true; var finite = true; double advanceError = 0, spanError = 0;
        for (var p = 0; p < _paragraphs.Count; p++)
        {
            var paragraph = _paragraphs[p]; var cursor = _maps[p].Paragraph.Start;
            if (paragraph.Breaks.Lines.Length == 0)
            {
                // Empty/folded lines and all-space paragraphs keep a hit-test line.
                // It has no selected break and no glyphs to compare against a target width.
                coverage &= _content.Source.GetText(_maps[p].Paragraph).All(c => c == ' ') && paragraph.Lines.All(result =>
                    result.Line.Source.Length == 0 && result.Line.Runs.IsEmpty);
                continue;
            }
            for (var i = 0; i < paragraph.Lines.Count; i++)
            {
                var line = paragraph.Lines[i].Line; var chosen = paragraph.Breaks.Lines[i];
                coverage &= _content.Source.GetText(new(cursor, line.Source.Start - cursor)).All(c => c == ' ');
                cursor = line.Source.End;
                var indent = ParagraphIndent(_renderedParagraphStyles[p], Width);
                var literal = _renderedParagraphStyles[p].SourceLine || _renderedParagraphStyles[p].Code is not null;
                var expected = literal || chosen.IsParagraphEnd && chosen.AdjustmentRatio == 0
                    ? chosen.NaturalWidth : Width - indent;
                advanceError = Math.Max(advanceError, Math.Abs(line.Runs.Sum(r => r.Advance) - expected));
                // A whitespace-only selected line has no ink span. Keep its
                // finite zero-width origin without accepting lost visible text.
                if (line.Runs.IsEmpty)
                    coverage &= _content.Source.GetText(line.Source).All(c => c == ' ');
                var left = line.Runs.IsEmpty ? indent : double.PositiveInfinity;
                var right = line.Runs.IsEmpty ? indent : double.NegativeInfinity;
                foreach (var run in line.Runs)
                {
                    var origin = GlyphPaintCoordinates.Resolve(line, run, new(0, 0)).X;
                    left = Math.Min(left, origin - ((run.BidiLevel & 1) != 0 ? run.Advance : 0));
                    right = Math.Max(right, origin + ((run.BidiLevel & 1) == 0 ? run.Advance : 0));
                    foreach (var c in run.Clusters)
                        anchors &= c.GeneratedText is null || c.Source == new SourceRange(line.Source.End, 0);
                }
                spanError = Math.Max(spanError, Math.Max(Math.Abs(left - indent), Math.Abs(right - indent - expected)));
                finite &= chosen.AdjustmentRatio is >= -1 and <= 1;
            }
            coverage &= _content.Source.GetText(new(cursor, _maps[p].Paragraph.End - cursor)).All(c => c == ' ');
        }
        coverage &= _thematicBreakLines.All(rule => _content.Source.GetText(rule.Source) == "\uFFFC");
        return new(coverage, anchors, finite, advanceError, spanError,
            coverage && anchors && finite && advanceError < 0.001 && spanError < 0.001);
    }

    private static double ParagraphIndent(ReflowParagraph paragraph, double width) =>
        Math.Min(Math.Max(0, width - 1), paragraph.ThematicBreak is { } rule
            ? rule.QuoteDepth * 20 + rule.ListDepth *
                (paragraph.StyleIndex == ReflowContent.FileStyleIndex ? 32 : 24) : paragraph.Code is { } code
            ? 20 + code.QuoteDepth * 20 : paragraph.Block is { } block
            ? block.QuoteDepth * 20 + block.ListDepth *
                (paragraph.StyleIndex == ReflowContent.FileStyleIndex ? 32 : 24) : 0);

    public int? HitTaskCheckbox(LayoutPoint point)
    {
        if (_content.Presentation is null) return null;
        foreach (var section in _sections)
        {
            if (section.Style.Block is not { Kind: EditorBlockKind.TaskList, PrefixHidden: true })
                continue;
            var x = Math.Max(0, ParagraphIndent(section.Style, Width) - 23) + 2;
            var y = section.BodyY + 3;
            if (point.X >= x && point.X <= x + 18 && point.Y >= y && point.Y <= y + 22)
                return _content.Presentation.Text.ToSourceOffset(section.Style.Source.Start,
                    ProjectionBoundary.AfterHidden);
        }
        return null;
    }

    internal void DrawGlyphs(CanvasDrawingSession session, ICanvasBrush ink, double top, double bottom, bool composite)
    {
        if (composite)
        {
            _binding!.DrawVisible(session, ink, top, bottom);
            var math = _visibleMath!;
            var (mathStart, mathEnd) = math.CandidateRange(top, bottom);
            for (var index = mathStart; index < mathEnd; index++)
                if (math.Intersects(index, top, bottom)) math[index].Draw(session, ink, top, bottom);
            var tables = _visibleTables!;
            var (tableStart, tableEnd) = tables.CandidateRange(top, bottom);
            for (var index = tableStart; index < tableEnd; index++)
                if (tables.Intersects(index, top, bottom)) tables[index].Draw(session, ink, top, bottom, 1);
        }
        else
            foreach (var paragraph in _paragraphs)
                for (var i = 0; i < paragraph.Lines.Count; i++)
                    if (paragraph.Lines[i].Line.Bounds.Bottom >= top && paragraph.Lines[i].Line.Bounds.Y <= bottom)
                        paragraph.DrawLine(i, session, Vector2.Zero, ink);
    }

    public void Draw(CanvasDrawingSession session, DocumentViewport viewport, double scroll, ICanvasBrush ink, ICanvasBrush label, ICanvasBrush edge,
        ICanvasBrush muted, GithubMarkdownTheme theme, bool stableFirstLabel = false,
        IReadOnlyList<ICanvasBrush>? codeColors = null,
        MarkdownCodeHighlight? codeHighlight = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previous = session.Transform; var zoom = viewport.Zoom;
        try
        {
            using var clip = session.CreateLayer(1, new Rect(0, 0, viewport.Width, viewport.Height));
            session.Transform = Matrix3x2.CreateScale(zoom) *
                Matrix3x2.CreateTranslation((float)DocumentViewport.Padding, (float)(DocumentViewport.Padding - scroll * zoom)) * previous;
            var top = scroll - DocumentViewport.Padding / zoom;
            var bottom = scroll + viewport.Height / zoom;
            if (Export.PdfTextDescriptions.Enabled)
            {
                // PDF pages have fixed paper margins, not an overscanned scrolling viewport.
                top = scroll + 0.001;
                bottom = scroll + viewport.DocumentHeight - 0.001;
            }
            var tables = _visibleTables!;
            var (tableStart, tableEnd) = tables.CandidateRange(top, bottom);
            for (var index = tableStart; index < tableEnd; index++)
                if (tables.Intersects(index, top, bottom))
                    tables[index].DrawBackground(session, top, bottom, zoom, theme);
            var decorations = _visibleDecorations!;
            var (decorationStart, decorationEnd) = decorations.CandidateRange(top, bottom);
            for (var index = decorationStart; index < decorationEnd; index++)
            {
                if (!decorations.Intersects(index, top, bottom)) continue;
                var decoration = decorations[index];
                if ((decoration.Style & MarkdownVisualStyle.Code) != 0 &&
                    decoration.Rect.Bottom >= top && decoration.Rect.Y <= bottom)
                    session.FillRoundedRectangle(new Rect(decoration.Rect.X - 3, decoration.Rect.Y + 2,
                        decoration.Rect.Width + 6, Math.Max(1, decoration.Rect.Height - 4)),
                        6, 6, theme.NeutralBackground);
            }
            DrawCodePanels(session, top, bottom, theme);
            DrawBlockMarkers(session, top, bottom, zoom, theme);
            for (var index = tableStart; index < tableEnd; index++)
                if (tables.Intersects(index, top, bottom)) tables[index].DrawText(session, ink, top, bottom);
            if (codeColors is { Count: >= 22 } && _content.Presentation is { } rich)
                _binding!.DrawVisibleStyled(session, ink, top, bottom,
                    line => rich.IsCodeAtDisplayOffset(line.Start) || Overlaps(_linkRanges, line) ||
                        Overlaps(_mutedRanges, line),
                    source => rich.IsCodeAtDisplayOffset(source.Start)
                        ? MarkdownCodeHighlighter.KindAt(codeHighlight, rich.Text, source.Start) is { } kind
                            ? codeColors[(int)kind] : ink
                        : Overlaps(_linkRanges, source) ? label :
                            Overlaps(_mutedRanges, source) ? muted : null);
            else _binding!.DrawVisible(session, ink, top, bottom);
            var math = _visibleMath!;
            var (mathStart, mathEnd) = math.CandidateRange(top, bottom);
            for (var index = mathStart; index < mathEnd; index++)
                if (math.Intersects(index, top, bottom)) math[index].Draw(session, ink, top, bottom, theme, label, muted);
            using var equationFormat = new CanvasTextFormat { FontFamily = "Cambria", FontSize = 17,
                HorizontalAlignment = CanvasHorizontalAlignment.Right,
                VerticalAlignment = CanvasVerticalAlignment.Center };
            var sections = _visibleSections!;
            var (sectionStart, sectionEnd) = sections.CandidateRange(top, bottom);
            for (var index = sectionStart; index < sectionEnd; index++)
            {
                if (!sections.Intersects(index, top, bottom)) continue;
                var section = sections[index];
                if (section.Style.EquationNumber is { } number && section.Bottom >= top &&
                    section.LabelY <= bottom)
                    session.DrawText($"({number})", new Rect(Math.Max(0, Width - 50),
                        section.BodyY, 46, Math.Max(30, section.Bottom - section.BodyY)),
                        label, equationFormat);
            }
            for (var index = decorationStart; index < decorationEnd; index++)
            {
                if (!decorations.Intersects(index, top, bottom)) continue;
                var decoration = decorations[index];
                var rect = decoration.Rect;
                if (rect.Bottom < top || rect.Y > bottom) continue;
                if ((decoration.Style & MarkdownVisualStyle.Strikethrough) != 0)
                    session.DrawLine(new((float)rect.X, (float)(rect.Y + rect.Height * 0.53)),
                        new((float)rect.Right, (float)(rect.Y + rect.Height * 0.53)),
                        theme.Foreground, 1.1f / (float)zoom);
                // GitHub links are accent-colored without a permanent underline.
            }
            if (!stableFirstLabel) _mathPage?.Draw(session, ink, label, edge, top, bottom, zoom);
            using var format = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 11, WordWrapping = CanvasWordWrapping.Wrap };
            for (var index = sectionStart; index < sectionEnd; index++)
            {
                if (!sections.Intersects(index, top, bottom)) continue;
                var section = sections[index];
                if (section.Bottom < top || section.LabelY > bottom) continue;
                if (section.Style.ThematicBreak is { MarkerHidden: true })
                {
                    var indent = ParagraphIndent(section.Style, Width);
                    session.FillRectangle(new Rect(indent, section.BodyY, Width - indent,
                        section.Bottom - section.BodyY), theme.Border);
                }
                if (section.Style.Code is null && section.Style.Table is not { RowIndex: < 0 } &&
                    (section.Style.Table is null || section.Style.Table.Value.RowIndex == 0) &&
                    (!stableFirstLabel || !ReferenceEquals(section, _sections[0])))
                    session.DrawText(section.Style.Label, new Rect(0, section.LabelY, Width, 24), label, format);
                if (!section.Feasible)
                    session.DrawText("此宽度在有限间距规则下无合法断行。请扩大窗口或降低编辑缩放。\nNoFeasibleBreaks — source preserved.", new Rect(0, section.BodyY, Width, 64), label, format);
                if (section.Style.HeadingLevel is 1 or 2 && section.Style.StyleIndex == ReflowContent.FileStyleIndex)
                    session.DrawLine(new(0, (float)(section.Bottom + 4)),
                        new((float)Width, (float)(section.Bottom + 4)), theme.MutedBorder, 1f / zoom);
            }
        }
        finally { session.Transform = previous; }
    }

    private void DrawCodePanels(CanvasDrawingSession session, double top, double bottom,
        GithubMarkdownTheme theme)
    {
        using var languageFormat = new CanvasTextFormat
        { FontFamily = "Segoe UI", FontSize = 11, WordWrapping = CanvasWordWrapping.NoWrap };
        var panels = _visibleCodePanels!;
        var (start, end) = panels.CandidateRange(top, bottom);
        for (var index = start; index < end; index++)
        {
            if (!panels.Intersects(index, top, bottom)) continue;
            var panel = panels[index];
            session.FillRoundedRectangle(new Rect(panel.X, panel.Top, Math.Max(1, Width - panel.X),
                Math.Max(1, panel.Bottom - panel.Top)), 6, 6, theme.MutedBackground);
            if (panel.HasLabel)
                session.DrawText(string.IsNullOrWhiteSpace(panel.Language) ? "CODE" : panel.Language,
                    new Rect(panel.X + 12, panel.Top + 4, Math.Max(1, Width - panel.X - 20), 17),
                    theme.MutedForeground, languageFormat);
        }
    }

    private void DrawBlockMarkers(CanvasDrawingSession session, double top, double bottom,
        double zoom, GithubMarkdownTheme theme)
    {
        using var markerFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI", FontSize = 16,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };
        var markerInk = theme.Foreground;
        var quoteInk = theme.QuoteBorder;
        var sections = _visibleSections!;
        var (start, end) = sections.CandidateRange(top, bottom);
        for (var index = start; index < end; index++)
        {
            if (!sections.Intersects(index, top, bottom)) continue;
            var section = sections[index];
            if (section.Bottom < top || section.LabelY > bottom || section.Style.Block is not { } block)
                continue;
            for (var depth = 0; depth < block.QuoteDepth; depth++)
            {
                var x = depth * 20 +
                    (section.Style.StyleIndex == ReflowContent.FileStyleIndex ? 2 : 6);
                session.DrawLine(new(x, (float)section.BodyY),
                    new(x, (float)section.Bottom), quoteInk, 4f / (float)zoom);
            }
            if (!block.PrefixHidden || block.ListDepth == 0) continue;
            var indent = ParagraphIndent(section.Style, Width);
            var markerX = Math.Max(0, indent - 23);
            var markerY = section.BodyY;
            if (block.Kind == EditorBlockKind.TaskList)
            {
                var box = new Rect(markerX + 4, markerY + 5, 14, 14);
                session.DrawRoundedRectangle(box, 3, 3, markerInk, 1.5f / (float)zoom);
                if (block.Marker == "☑")
                {
                    session.DrawLine(new((float)box.X + 3, (float)box.Y + 7),
                        new((float)box.X + 6, (float)box.Y + 10), markerInk, 1.6f / (float)zoom);
                    session.DrawLine(new((float)box.X + 6, (float)box.Y + 10),
                        new((float)box.X + 12, (float)box.Y + 3), markerInk, 1.6f / (float)zoom);
                }
            }
            else
                session.DrawText(block.Kind == EditorBlockKind.BulletList ? "•" : block.Marker,
                    new Rect(markerX, markerY, 23, 26), markerInk, markerFormat);
        }
    }
    public void DrawFirstLabel(CanvasDrawingSession session, DocumentViewport viewport, double scroll,
        GithubMarkdownTheme theme)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sections.Count == 0) return;
        var section = _sections[0];
        var top = scroll - DocumentViewport.Padding / viewport.Zoom;
        var bottom = scroll + viewport.Height / viewport.Zoom;
        if (section.Bottom < top || section.LabelY > bottom) return;
        var previous = session.Transform;
        try
        {
            using var clip = session.CreateLayer(1, new Rect(0, 0, viewport.Width, viewport.Height));
            session.Transform = Matrix3x2.CreateScale(viewport.Zoom) *
                Matrix3x2.CreateTranslation((float)DocumentViewport.Padding,
                    (float)(DocumentViewport.Padding - scroll * viewport.Zoom)) * previous;
            using var format = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 11,
                WordWrapping = CanvasWordWrapping.Wrap };
            session.DrawText(section.Style.Label, new Rect(0, section.LabelY, Width, 24),
                theme.Accent, format);
        }
        finally { session.Transform = previous; }
    }
    public void DrawMathPage(CanvasDrawingSession session, DocumentViewport viewport, double scroll,
        ICanvasBrush ink, ICanvasBrush label, ICanvasBrush edge)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mathPage is null) return;
        var top = scroll - DocumentViewport.Padding / viewport.Zoom;
        var bottom = scroll + viewport.Height / viewport.Zoom;
        if (_mathPage.Bottom < top || _mathPage.Top > bottom) return;
        var previous = session.Transform;
        try
        {
            using var clip = session.CreateLayer(1, new Rect(0, 0, viewport.Width, viewport.Height));
            session.Transform = Matrix3x2.CreateScale(viewport.Zoom) *
                Matrix3x2.CreateTranslation((float)DocumentViewport.Padding,
                    (float)(DocumentViewport.Padding - scroll * viewport.Zoom)) * previous;
            _mathPage.Draw(session, ink, label, edge, top, bottom, viewport.Zoom);
        }
        finally { session.Transform = previous; }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _binding?.Dispose(); _mathPage?.Dispose(); foreach (var p in _paragraphs) p.Dispose();
        foreach (var paragraph in _editableMathParagraphs) paragraph.Dispose();
        foreach (var row in _editableTableRows) row.Dispose();
        _paragraphs.Clear(); _renderedParagraphStyles.Clear(); _maps.Clear();
        _editableMathParagraphs.Clear(); _editableTableRows.Clear(); _thematicBreakLines.Clear();
    }
}
