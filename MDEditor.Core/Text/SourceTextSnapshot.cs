namespace MDEditor.Core.Text;

/// <summary>Half-open UTF-16 source range. Not a glyph, scalar-value or grapheme range.</summary>
public readonly record struct SourceRange
{
    public int Start { get; }
    public int Length { get; }
    public int End => Start + Length;
    public SourceRange(int start, int length)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (length < 0 || length > int.MaxValue - start) throw new ArgumentOutOfRangeException(nameof(length));
        Start = start;
        Length = length;
    }
    public bool Contains(SourceRange other) => other.Start >= Start && other.End <= End;
}

/// <summary>Immutable source/version pair for layouts; not an editable buffer or transaction system.</summary>
public sealed class SourceTextSnapshot
{
    public string Text { get; }
    public long Version { get; }
    public int Length => Text.Length;
    public SourceRange FullRange => new(0, Length);
    public SourceTextSnapshot(string text, long version)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        Text = text;
        Version = version;
    }
    public string GetText(SourceRange range)
    {
        if (!FullRange.Contains(range)) throw new ArgumentOutOfRangeException(nameof(range));
        return Text.Substring(range.Start, range.Length);
    }
}
