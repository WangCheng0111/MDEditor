using System.Collections.Immutable;
using System.Globalization;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;
using MDEditor.Typesetting.Hyphenation;
using MDEditor.Typesetting.Typography;

namespace MDEditor.Typesetting.LineBreaking;

public readonly record struct MeasuredTextCluster
{
    public SourceRange Source { get; }
    public double Advance { get; }
    public bool CanBreakAfter { get; }
    public MeasuredTextCluster(SourceRange source, double advance, bool canBreakAfter)
    {
        if (source.Length == 0) throw new ArgumentException("Text clusters cannot be empty.", nameof(source));
        LayoutValidation.Nonnegative(advance, nameof(advance));
        Source = source; Advance = advance; CanBreakAfter = canBreakAfter;
    }
}

/// <summary>Source mapping for measured, glyph/grapheme-safe primitives. No character width estimation.</summary>
public sealed class ParagraphItemMap
{
    public SourceTextSnapshot Source { get; }
    public SourceRange Paragraph { get; }
    public CjkTypographyOptions Typography { get; }
    public HyphenationOptions Hyphenation { get; }
    public ImmutableHashSet<int> HyphenBreakItems { get; }
    public ImmutableArray<MeasuredTextCluster> Clusters { get; }
    public ImmutableArray<LineBreakItem> Items { get; }
    public ImmutableArray<SourceRange> ItemSources { get; }
    private ParagraphItemMap(SourceTextSnapshot source, SourceRange paragraph,
        ImmutableArray<MeasuredTextCluster> clusters, List<LineBreakItem> items, List<SourceRange> ranges,
        CjkTypographyOptions typography, HyphenationOptions hyphenation, ImmutableHashSet<int> hyphenBreakItems)
    {
        Source = source; Paragraph = paragraph; Clusters = clusters;
        Typography = typography;
        Hyphenation = hyphenation; HyphenBreakItems = hyphenBreakItems;
        Items = LayoutValidation.Freeze(items); ItemSources = LayoutValidation.Freeze(ranges);
    }

    public static ParagraphItemMap Create(SourceTextSnapshot source, SourceRange paragraph,
        IEnumerable<MeasuredTextCluster> clusters, double fontSize, CjkTypographyOptions? typography = null,
        HyphenationOptions? hyphenation = null, double hyphenAdvance = 0, bool mergeSpaces = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clusters);
        LayoutValidation.Positive(fontSize, nameof(fontSize));
        typography ??= CjkTypographyOptions.Legacy;
        hyphenation ??= HyphenationOptions.None;
        LayoutValidation.Nonnegative(hyphenAdvance, nameof(hyphenAdvance));
        if (hyphenation.Enabled && hyphenAdvance == 0)
            throw new ArgumentOutOfRangeException(nameof(hyphenAdvance), "An enabled discretionary needs a real measured hyphen advance.");
        var text = source.GetText(paragraph);
        var boundaries = StringInfo.ParseCombiningCharacters(text).Select(index => paragraph.Start + index).ToHashSet();
        boundaries.Add(paragraph.End);
        var frozen = LayoutValidation.Freeze(clusters);
        var cursor = paragraph.Start;
        foreach (var cluster in frozen)
        {
            if (cluster.Source.Length == 0 || cluster.Source.Start != cursor || !paragraph.Contains(cluster.Source) ||
                !boundaries.Contains(cluster.Source.Start) || !boundaries.Contains(cluster.Source.End))
                throw new ArgumentException("Clusters must cover the paragraph in order without splitting graphemes.", nameof(clusters));
            cursor = cluster.Source.End;
        }
        if (cursor != paragraph.End) throw new ArgumentException("Incomplete cluster coverage.", nameof(clusters));
        var normalized = new List<MeasuredTextCluster>();
        foreach (var cluster in frozen)
        {
            if (mergeSpaces && normalized.Count > 0 && TextSpacingPolicy.IsSpaces(source.GetText(cluster.Source)) &&
                TextSpacingPolicy.IsSpaces(source.GetText(normalized[^1].Source)))
            {
                var previous = normalized[^1];
                normalized[^1] = new(new(previous.Source.Start, cluster.Source.End - previous.Source.Start),
                    previous.Advance + cluster.Advance, cluster.CanBreakAfter);
            }
            else normalized.Add(cluster);
        }
        frozen = LayoutValidation.Freeze(normalized);
        var hyphenPoints = EnglishWordBreaks.Find(source, paragraph, frozen.Select(c => c.Source.End), hyphenation);
        var hyphenItems = ImmutableHashSet.CreateBuilder<int>();
        var items = new List<LineBreakItem>();
        var ranges = new List<SourceRange>();
        void Add(LineBreakItem item, SourceRange range) { items.Add(item); ranges.Add(range); }
        for (var i = 0; i < frozen.Length; i++)
        {
            var current = frozen[i];
            var slice = source.GetText(current.Source);
            if (TextSpacingPolicy.IsSpaces(slice))
            {
                if (!current.CanBreakAfter || !CjkTypographyRules.MayBreak(source, current.Source.Start, typography))
                    Add(LineBreakItem.Penalty(0, 10000), new(current.Source.Start, 0));
                Add(LineBreakItem.Glue(current.Advance, current.Advance * 0.5, current.Advance / 3), current.Source);
                continue;
            }
            Add(LineBreakItem.Box(current.Advance), current.Source);
            if (i + 1 == frozen.Length || TextSpacingPolicy.IsSpaces(source.GetText(frozen[i + 1].Source))) continue;
            var next = frozen[i + 1];
            var nextText = source.GetText(next.Source);
            if (hyphenPoints.Contains(current.Source.End))
            {
                hyphenItems.Add(items.Count);
                Add(LineBreakItem.Penalty(hyphenAdvance, hyphenation.Penalty, true), new(current.Source.End, 0));
                continue;
            }
            var mixed = typography.Enabled && CjkTypographyRules.IsMixedBoundary(slice, nextText);
            var gap = typography.Enabled ? CjkTypographyRules.IsInterCharacterBoundary(slice, nextText) :
                TextSpacingPolicy.IsCjk(slice) && TextSpacingPolicy.IsCjk(nextText);
            var mayBreak = current.CanBreakAfter && CjkTypographyRules.MayBreak(source, current.Source.End, typography);
            if (mixed)
            {
                if (!mayBreak) Add(LineBreakItem.Penalty(0, 10000), new(current.Source.End, 0));
                Add(LineBreakItem.Glue(fontSize * typography.MixedNaturalEm,
                    fontSize * (typography.MixedMaximumEm - typography.MixedNaturalEm),
                    fontSize * (typography.MixedNaturalEm - typography.MixedMinimumEm)), new(current.Source.End, 0));
                continue;
            }
            if (gap)
            {
                if (!mayBreak) Add(LineBreakItem.Penalty(0, 10000), new(current.Source.End, 0));
                Add(LineBreakItem.Glue(0, fontSize * (typography.Enabled ? typography.InterCharacterMaximumEm : 0.08), 0),
                    new(current.Source.End, 0));
            }
            else if (mayBreak) Add(LineBreakItem.Penalty(0, 0), new(current.Source.End, 0));
        }
        return new(source, paragraph, frozen, items, ranges, typography, hyphenation, hyphenItems.ToImmutable());
    }

    public SourceRange GetSource(LineMeasureRequest request)
    {
        if (request.StartItemIndex < 0 || request.EndItemIndex < request.StartItemIndex ||
            request.EndItemIndex > Items.Length) throw new ArgumentOutOfRangeException(nameof(request));
        var start = Boundary(request.StartItemIndex);
        var end = request.StartItemIndex == request.EndItemIndex ? start : ItemSources[request.EndItemIndex - 1].End;
        return new(start, end - start);
    }
    public int Boundary(int itemIndex)
    {
        if (itemIndex < 0 || itemIndex > Items.Length) throw new ArgumentOutOfRangeException(nameof(itemIndex));
        return itemIndex == Items.Length ? Paragraph.End : ItemSources[itemIndex].Start;
    }
    public DiscretionaryLine GetLine(LineMeasureRequest request) =>
        new(GetSource(request), !request.IsParagraphEnd && HyphenBreakItems.Contains(request.BreakItemIndex) ? Hyphenation.Hyphen : "");
}

public static class TextSpacingPolicy
{
    public static bool IsSpaces(string text) => text.Length > 0 && text.All(character => character == ' ');
    // Script classification; profile-specific spacing and prohibitions are in Typography.
    public static bool IsCjk(string text)
    {
        if (text.Length == 0) return false;
        if (!System.Text.Rune.TryGetRuneAt(text, 0, out var rune)) return false;
        var value = rune.Value;
        return value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or
            >= 0x20000 and <= 0x3FFFF or >= 0x3040 and <= 0x309F or >= 0x30A0 and <= 0x30FF or
            >= 0xAC00 and <= 0xD7AF;
    }
}
