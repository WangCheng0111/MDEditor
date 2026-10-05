namespace MDEditor.Core.Text;

/// <summary>A paragraph-style anchor in the UTF-16 source, independent of visual line wrapping.</summary>
public readonly record struct DocumentStyleMarker(int Offset, int StyleIndex);

public sealed class StyledDocumentSnapshot
{
    public SourceTextSnapshot Source { get; }
    public DocumentLineMap Lines { get; }
    public IReadOnlyList<DocumentStyleMarker> Styles { get; }

    internal StyledDocumentSnapshot(SourceTextSnapshot source, DocumentLineMap lines, DocumentStyleMarker[] styles)
    { Source = source; Lines = lines; Styles = Array.AsReadOnly(styles); }

    public int StyleAt(int offset)
    {
        if (offset < 0 || offset > Source.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var low = 0; var high = Styles.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (Styles[middle].Offset <= offset) low = middle + 1;
            else high = middle;
        }
        return Styles[low - 1].StyleIndex;
    }
}

/// <summary>Piece-table text plus durable paragraph-style anchors. One edit atomically publishes a new snapshot.</summary>
public sealed class StyledDocumentBuffer
{
    private readonly object _gate = new();
    private readonly DocumentTextBuffer _text;
    private List<DocumentStyleMarker> _styles;
    private StyledDocumentSnapshot? _snapshot;

    public StyledDocumentBuffer(string text, IEnumerable<DocumentStyleMarker> styles, long version = 0)
    {
        ArgumentNullException.ThrowIfNull(styles);
        _text = new(text, version);
        _styles = styles.ToList();
        if (_styles.Count == 0 || _styles[0].Offset != 0 || _styles.Any(style =>
            style.Offset < 0 || style.Offset > text.Length || style.StyleIndex < 0) ||
            _styles.Skip(1).Where((style, index) => style.Offset <= _styles[index].Offset).Any())
            throw new ArgumentException("Style anchors must start at zero and increase strictly.", nameof(styles));
    }

    public StyledDocumentSnapshot Capture()
    {
        lock (_gate)
            return _snapshot ??= new(_text.CaptureSnapshot(), _text.CaptureLineMap(), _styles.ToArray());
    }

    public DocumentEditResult Replace(SourceRange range, string replacement)
    {
        lock (_gate)
        {
            var result = _text.Replace(range, replacement);
            if (!result.Changed) return result;
            var change = result.Changes[0];
            var next = new List<DocumentStyleMarker>(_styles.Count);
            foreach (var marker in _styles)
            {
                if (marker.Offset == 0) { next.Add(marker); continue; }
                if (marker.Offset > change.OldRange.Start && marker.Offset < change.OldRange.End)
                    continue; // A deleted boundary merges into the preceding paragraph style.
                var offset = marker.Offset > change.OldRange.End ||
                    marker.Offset == change.OldRange.End && change.OldRange.Length > 0
                    ? checked(marker.Offset + result.NewLength - result.OldLength)
                    : marker.Offset;
                if (next[^1].Offset == offset) continue;
                next.Add(marker with { Offset = offset });
            }
            _styles = next;
            _snapshot = null;
            return result;
        }
    }

    /// <summary>Replays one verified edit and its exact style anchors while keeping source versions monotonic.</summary>
    public DocumentEditResult Restore(SourceRange range, string expectedText, string replacement,
        IReadOnlyList<DocumentStyleMarker> styles, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(expectedText);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(styles);
        lock (_gate)
        {
            var source = _text.CaptureSnapshot();
            if (source.Version != expectedVersion)
                throw new DocumentVersionConflictException(expectedVersion, source.Version);
            if (!source.FullRange.Contains(range) || source.GetText(range) != expectedText)
                throw new InvalidOperationException("History no longer matches the current text.");
            if (expectedText == replacement)
                throw new ArgumentException("History replay must change the text.", nameof(replacement));
            var length = checked(source.Length - range.Length + replacement.Length);
            var copy = styles.ToList();
            if (copy.Count == 0 || copy[0].Offset != 0 || copy.Any(style =>
                style.Offset < 0 || style.Offset > length || style.StyleIndex < 0) ||
                copy.Skip(1).Where((style, index) => style.Offset <= copy[index].Offset).Any())
                throw new ArgumentException("History style anchors are invalid.", nameof(styles));
            var result = _text.Replace(range, replacement);
            if (!result.Changed) throw new InvalidOperationException("History replay made no change.");
            _styles = copy;
            _snapshot = null;
            return result;
        }
    }

    /// <summary>Restores style anchors after a transient edit returned to the same text.</summary>
    public void RestoreStyles(IReadOnlyList<DocumentStyleMarker> styles, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(styles);
        lock (_gate)
        {
            var source = _text.CaptureSnapshot();
            if (source.Version != expectedVersion)
                throw new DocumentVersionConflictException(expectedVersion, source.Version);
            var copy = styles.ToList();
            if (copy.Count == 0 || copy[0].Offset != 0 || copy.Any(style =>
                style.Offset < 0 || style.Offset > source.Length || style.StyleIndex < 0) ||
                copy.Skip(1).Where((style, index) => style.Offset <= copy[index].Offset).Any())
                throw new ArgumentException("History style anchors are invalid.", nameof(styles));
            _styles = copy;
            _snapshot = null;
        }
    }
}
