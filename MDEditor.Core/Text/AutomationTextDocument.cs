using System.Globalization;
using System.Text;

namespace MDEditor.Core.Text;

public enum AutomationTextUnit { Character, Word, Line, Paragraph, Document }

/// <summary>Platform-independent UIA text navigation. All endpoints are original UTF-16 offsets.</summary>
public sealed class AutomationTextDocument
{
    private readonly Dictionary<AutomationTextUnit, int[]> _boundaries = new();
    private readonly int[]? _visualLines;
    public SourceTextSnapshot Source { get; }

    public AutomationTextDocument(SourceTextSnapshot source, IEnumerable<int>? visualLineStarts = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        if (visualLineStarts is not null)
        {
            _visualLines = visualLineStarts.Append(0).Append(source.Length).Distinct().Order().ToArray();
            if (_visualLines.Any(offset => offset < 0 || offset > source.Length))
                throw new ArgumentOutOfRangeException(nameof(visualLineStarts));
        }
    }

    public int Snap(int offset, bool forward = false)
    {
        if (offset < 0 || offset > Source.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var boundaries = Boundaries(AutomationTextUnit.Character);
        var index = Array.BinarySearch(boundaries, offset);
        return index >= 0 ? offset : boundaries[forward ? ~index : ~index - 1];
    }

    public SourceRange SafeRange(SourceRange range)
    {
        Validate(range);
        var start = Snap(range.Start);
        return range.Length == 0 ? new(start, 0) : new(start, Snap(range.End, true) - start);
    }

    public string GetText(SourceRange range, int maxLength = -1)
    {
        Validate(range);
        if (maxLength < -1) throw new ArgumentOutOfRangeException(nameof(maxLength));
        var end = maxLength == -1 ? range.End : range.Start + Math.Min(range.Length, maxLength);
        // A maximum UTF-16 length must not cut a surrogate pair, combining cluster or CRLF.
        if (end < range.End) end = Math.Max(range.Start, Snap(end));
        return Source.GetText(new(range.Start, end - range.Start));
    }

    public SourceRange Enclosing(SourceRange range, AutomationTextUnit unit)
    {
        Validate(range);
        var boundaries = Boundaries(unit);
        if (boundaries.Length < 2) return new(0, 0);
        var index = UnitIndex(boundaries, range.Start);
        return new(boundaries[index], boundaries[index + 1] - boundaries[index]);
    }

    public (int Offset, int Moved) MoveEndpoint(int offset, AutomationTextUnit unit, int count)
    {
        if (offset < 0 || offset > Source.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count == 0) return (offset, 0);
        var boundaries = Boundaries(unit);
        var index = Array.BinarySearch(boundaries, offset);
        if (index < 0) index = count > 0 ? ~index - 1 : ~index;
        var target = (int)Math.Clamp((long)index + count, 0, boundaries.Length - 1);
        return (boundaries[target], target - index);
    }

    public (SourceRange Range, int Moved) Move(SourceRange range, AutomationTextUnit unit, int count)
    {
        Validate(range);
        if (count == 0) return (range, 0);
        if (range.Length == 0)
        {
            var point = MoveEndpoint(range.Start, unit, count);
            return (new(point.Offset, 0), point.Moved);
        }
        var boundaries = Boundaries(unit);
        if (boundaries.Length < 2) return (new(0, 0), 0);
        var index = UnitIndex(boundaries, range.Start);
        var target = (int)Math.Clamp((long)index + count, 0, boundaries.Length - 2);
        return (new(boundaries[target], boundaries[target + 1] - boundaries[target]), target - index);
    }

    public SourceRange? FindText(SourceRange range, string text, bool backward = false, bool ignoreCase = false)
    {
        Validate(range);
        ArgumentException.ThrowIfNullOrEmpty(text);
        var span = Source.Text.AsSpan(range.Start, range.Length);
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var index = backward ? span.LastIndexOf(text.AsSpan(), comparison) : span.IndexOf(text.AsSpan(), comparison);
        return index < 0 ? null : new(range.Start + index, text.Length);
    }

    /// <summary>Rebase retained UIA ranges after a replacement, without retaining edit/layout histories.</summary>
    public SourceRange Rebase(SourceRange range, SourceTextSnapshot next)
    {
        Validate(range);
        ArgumentNullException.ThrowIfNull(next);
        if (ReferenceEquals(Source, next)) return range;
        var old = Source.Text; var fresh = next.Text;
        var prefix = 0;
        while (prefix < old.Length && prefix < fresh.Length && old[prefix] == fresh[prefix]) prefix++;
        var oldEnd = old.Length; var newEnd = fresh.Length;
        while (oldEnd > prefix && newEnd > prefix && old[oldEnd - 1] == fresh[newEnd - 1])
        { oldEnd--; newEnd--; }
        int Map(int offset, bool trailing)
        {
            if (offset < prefix) return offset;
            if (offset > oldEnd) return offset + newEnd - oldEnd;
            if (oldEnd == prefix) return trailing ? newEnd : prefix;
            if (offset == oldEnd) return newEnd;
            if (offset == prefix) return prefix;
            return trailing ? newEnd : prefix;
        }
        var updated = new AutomationTextDocument(next);
        if (range.Length == 0) return new(updated.Snap(Map(range.Start, true), true), 0);
        var start = updated.Snap(Map(range.Start, true));
        var end = updated.Snap(Map(range.End, false), true);
        return new(start, Math.Max(0, end - start));
    }

    private void Validate(SourceRange range)
    {
        if (!Source.FullRange.Contains(range)) throw new ArgumentOutOfRangeException(nameof(range));
    }

    private static int UnitIndex(int[] boundaries, int offset)
    {
        var index = Array.BinarySearch(boundaries, offset);
        return Math.Min(index >= 0 ? index : ~index - 1, boundaries.Length - 2);
    }

    private int[] Boundaries(AutomationTextUnit unit)
    {
        if (_boundaries.TryGetValue(unit, out var cached)) return cached;
        int[] result = unit switch
        {
            AutomationTextUnit.Character => StringInfo.ParseCombiningCharacters(Source.Text)
                .Where(offset => offset == 0 || Source.Text[offset - 1] != '\r' || Source.Text[offset] != '\n')
                .Append(0).Append(Source.Length).Distinct().Order().ToArray(),
            AutomationTextUnit.Word => WordBoundaries(),
            AutomationTextUnit.Line => _visualLines ?? ParagraphBoundaries(),
            AutomationTextUnit.Paragraph => ParagraphBoundaries(),
            AutomationTextUnit.Document => Source.Length == 0 ? [0] : [0, Source.Length],
            _ => throw new ArgumentOutOfRangeException(nameof(unit))
        };
        _boundaries.Add(unit, result);
        return result;
    }

    private int[] ParagraphBoundaries() => DocumentLineMap.Create(Source).Lines.Select(line => line.Content.Start)
        .Append(Source.Length).Distinct().Order().ToArray();

    private int[] WordBoundaries()
    {
        var starts = new List<int> { 0 };
        var previous = -1;
        foreach (var offset in Boundaries(AutomationTextUnit.Character))
        {
            if (offset == Source.Length) break;
            // The editor preserves arbitrary UTF-16, including an unpaired surrogate
            // pasted from another program. Accessibility queries must not throw for it.
            if (!Rune.TryGetRuneAt(Source.Text, offset, out var rune)) rune = Rune.ReplacementChar;
            var kind = Rune.IsWhiteSpace(rune) ? -1 : IsHan(rune.Value) ? 2 :
                Rune.IsLetterOrDigit(rune) || rune.Value == '_' ? 0 : 1;
            if (kind >= 0 && offset > 0 && (previous != kind || kind != 0)) starts.Add(offset);
            previous = kind;
        }
        starts.Add(Source.Length);
        return starts.Distinct().Order().ToArray();
    }

    private static bool IsHan(int value) => value is >= 0x3400 and <= 0x9FFF or >= 0x20000 and <= 0x323AF;
}
