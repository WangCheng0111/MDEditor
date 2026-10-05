using MDEditor.Core.Text;

namespace MDEditor.Typesetting.Layout;

/// <summary>Keep the same logical top-of-viewport text when an edit shifts source offsets.</summary>
public static class ViewportAnchorMap
{
    public static int Map(ITextInteractionMap previous, ITextInteractionMap next, int oldOffset)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        return Map(previous.Source, next.Source, oldOffset);
    }

    public static int Map(SourceTextSnapshot previous, SourceTextSnapshot next, int oldOffset)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        if (oldOffset < 0 || oldOffset > previous.Length)
            throw new ArgumentOutOfRangeException(nameof(oldOffset));
        var old = previous.Text;
        var fresh = next.Text;
        var prefix = 0;
        while (prefix < old.Length && prefix < fresh.Length && old[prefix] == fresh[prefix]) prefix++;
        var oldEnd = old.Length;
        var newEnd = fresh.Length;
        while (oldEnd > prefix && newEnd > prefix && old[oldEnd - 1] == fresh[newEnd - 1])
        { oldEnd--; newEnd--; }
        if (oldOffset <= prefix) return oldOffset;
        if (oldOffset >= oldEnd) return checked(oldOffset + newEnd - oldEnd);
        return newEnd;
    }
}
