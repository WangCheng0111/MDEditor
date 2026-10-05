using System.Collections.ObjectModel;

namespace MDEditor.Core.Text;

/// <summary>A committed replacement in old and new UTF-16 coordinates. Text is never normalized.</summary>
public sealed record DocumentTextChange(SourceRange OldRange, SourceRange NewRange,
    string OldText, string NewText);

/// <summary>One atomic document commit. An empty change list keeps the version unchanged.</summary>
public sealed record DocumentEditResult(long OldVersion, long NewVersion, int OldLength, int NewLength,
    IReadOnlyList<DocumentTextChange> Changes)
{
    public bool Changed => Changes.Count != 0;
}

public sealed class DocumentVersionConflictException : InvalidOperationException
{
    public long ExpectedVersion { get; }
    public long ActualVersion { get; }

    public DocumentVersionConflictException(long expectedVersion, long actualVersion)
        : base($"The edit was based on document version {expectedVersion}, but the current version is {actualVersion}.")
    {
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }
}

/// <summary>
/// UTF-16 piece table with immutable, lazily materialized snapshots. Edits use original coordinates
/// within a transaction; no edit mutates the visible document until the complete batch commits.
/// </summary>
public sealed class DocumentTextBuffer
{
    private readonly object _gate = new();
    private List<Piece> _pieces;
    private int _length;
    private long _version;
    private SourceTextSnapshot? _snapshot;
    private DocumentLineMap? _lineMap;

    public DocumentTextBuffer(string text = "", long version = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        ValidateUtf16(text);
        _pieces = text.Length == 0 ? [] : [new(text, 0, text.Length)];
        _length = text.Length;
        _version = version;
    }

    public DocumentTextBuffer(SourceTextSnapshot snapshot)
        : this((snapshot ?? throw new ArgumentNullException(nameof(snapshot))).Text, snapshot.Version) { }

    public int Length { get { lock (_gate) return _length; } }
    public long Version { get { lock (_gate) return _version; } }

    public SourceTextSnapshot CaptureSnapshot()
    {
        lock (_gate) return CaptureSnapshotCore();
    }

    public DocumentLineMap CaptureLineMap()
    {
        lock (_gate) return _lineMap ??= DocumentLineMap.Create(CaptureSnapshotCore());
    }

    public DocumentEditTransaction BeginTransaction()
    {
        lock (_gate) return new(this, _version, _length);
    }

    public DocumentEditResult Insert(int position, string text)
    {
        using var edit = BeginTransaction();
        edit.Insert(position, text);
        return edit.Commit();
    }

    public DocumentEditResult Delete(SourceRange range)
    {
        using var edit = BeginTransaction();
        edit.Delete(range);
        return edit.Commit();
    }

    public DocumentEditResult Replace(SourceRange range, string text)
    {
        using var edit = BeginTransaction();
        edit.Replace(range, text);
        return edit.Commit();
    }

    internal DocumentEditResult Commit(long baseVersion, IReadOnlyList<DocumentEditTransaction.Replacement> edits)
    {
        lock (_gate)
        {
            if (baseVersion != _version) throw new DocumentVersionConflictException(baseVersion, _version);
            var ordered = edits.OrderBy(edit => edit.Range.Start).ToArray();
            long expectedLength = _length;
            for (var index = 0; index < ordered.Length; index++)
            {
                var edit = ordered[index];
                if (edit.Range.End > _length) throw new ArgumentOutOfRangeException(nameof(edits));
                if (index > 0 && (ordered[index - 1].Range.End > edit.Range.Start ||
                    ordered[index - 1].Range.Start == edit.Range.Start))
                    throw new ArgumentException("Edits in one transaction must have distinct, nonoverlapping old ranges.", nameof(edits));
                ValidateBoundary(edit.Range.Start);
                ValidateBoundary(edit.Range.End);
                ValidateUtf16(edit.Text);
                expectedLength += (long)edit.Text.Length - edit.Range.Length;
            }
            if (expectedLength > int.MaxValue || expectedLength < 0)
                throw new ArgumentOutOfRangeException(nameof(edits), "The resulting UTF-16 document is too large.");

            var nextPieces = new List<Piece>(_pieces.Count + ordered.Length * 2);
            var changes = new List<DocumentTextChange>();
            var pieceIndex = 0;
            var withinPiece = 0;
            var oldOffset = 0;
            void AdvanceTo(int end, List<Piece> output)
            {
                while (oldOffset < end)
                {
                    var piece = _pieces[pieceIndex];
                    var count = Math.Min(end - oldOffset, piece.Length - withinPiece);
                    Append(output, new(piece.Text, piece.Start + withinPiece, count));
                    oldOffset += count;
                    withinPiece += count;
                    if (withinPiece == piece.Length) { pieceIndex++; withinPiece = 0; }
                }
            }

            long delta = 0;
            foreach (var edit in ordered)
            {
                AdvanceTo(edit.Range.Start, nextPieces);
                var oldPieces = new List<Piece>();
                AdvanceTo(edit.Range.End, oldPieces);
                var oldText = Flatten(oldPieces, edit.Range.Length);
                if (oldText == edit.Text)
                {
                    foreach (var piece in oldPieces) Append(nextPieces, piece);
                    continue;
                }
                var newStart = (long)edit.Range.Start + delta;
                if (newStart < 0 || newStart > int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(edits), "A resulting source range overflows.");
                changes.Add(new(edit.Range, new((int)newStart, edit.Text.Length), oldText, edit.Text));
                if (edit.Text.Length != 0) Append(nextPieces, new(edit.Text, 0, edit.Text.Length));
                delta += (long)edit.Text.Length - edit.Range.Length;
            }
            AdvanceTo(_length, nextPieces);
            if (changes.Count == 0)
                return new(_version, _version, _length, _length, Array.Empty<DocumentTextChange>());
            if (_version == long.MaxValue)
                throw new InvalidOperationException("Document version cannot advance beyond Int64.MaxValue.");
            var newLength = (long)_length + delta;
            if (newLength < 0 || newLength > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(edits), "The resulting UTF-16 document is too large.");
            var result = new DocumentEditResult(_version, _version + 1, _length, (int)newLength,
                new ReadOnlyCollection<DocumentTextChange>(changes));
            _pieces = nextPieces;
            _length = (int)newLength;
            _version++;
            _snapshot = null;
            _lineMap = null;
            return result;
        }
    }

    private SourceTextSnapshot CaptureSnapshotCore() =>
        _snapshot ??= new SourceTextSnapshot(Flatten(_pieces, _length), _version);

    private void ValidateBoundary(int position)
    {
        if (position <= 0 || position >= _length) return;
        var previous = CharacterAt(position - 1);
        var next = CharacterAt(position);
        if (char.IsHighSurrogate(previous) && char.IsLowSurrogate(next) || previous == '\r' && next == '\n')
            throw new ArgumentException("An edit boundary cannot split a surrogate pair or CRLF sequence.");
    }

    private char CharacterAt(int offset)
    {
        foreach (var piece in _pieces)
        {
            if (offset < piece.Length) return piece.Text[piece.Start + offset];
            offset -= piece.Length;
        }
        throw new ArgumentOutOfRangeException(nameof(offset));
    }

    internal static void ValidateUtf16(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                    throw new ArgumentException("Text contains an unpaired high surrogate.", nameof(text));
                index++;
            }
            else if (char.IsLowSurrogate(text[index]))
                throw new ArgumentException("Text contains an unpaired low surrogate.", nameof(text));
        }
    }

    private static void Append(List<Piece> pieces, Piece next)
    {
        if (next.Length == 0) return;
        if (pieces.Count > 0)
        {
            var previous = pieces[^1];
            if (ReferenceEquals(previous.Text, next.Text) && previous.Start + previous.Length == next.Start)
            {
                pieces[^1] = previous with { Length = previous.Length + next.Length };
                return;
            }
        }
        pieces.Add(next);
    }

    private static string Flatten(IReadOnlyList<Piece> pieces, int length) =>
        string.Create(length, pieces, static (destination, source) =>
        {
            var offset = 0;
            foreach (var piece in source)
            {
                piece.Text.AsSpan(piece.Start, piece.Length).CopyTo(destination[offset..]);
                offset += piece.Length;
            }
            if (offset != destination.Length) throw new InvalidOperationException("Piece table length mismatch.");
        });

    private sealed record Piece(string Text, int Start, int Length);
}

/// <summary>Stages simultaneous replacements against one buffer version. Dispose without Commit to abort.</summary>
public sealed class DocumentEditTransaction : IDisposable
{
    private readonly DocumentTextBuffer _owner;
    private readonly List<Replacement> _replacements = [];
    private bool _completed;
    public long BaseVersion { get; }
    public int BaseLength { get; }

    internal DocumentEditTransaction(DocumentTextBuffer owner, long version, int length)
    { _owner = owner; BaseVersion = version; BaseLength = length; }

    public void Insert(int position, string text) => Replace(new(position, 0), text);
    public void Delete(SourceRange range) => Replace(range, "");

    public void Replace(SourceRange range, string text)
    {
        if (_completed) throw new InvalidOperationException("This edit transaction is closed.");
        if (range.End > BaseLength) throw new ArgumentOutOfRangeException(nameof(range));
        DocumentTextBuffer.ValidateUtf16(text);
        _replacements.Add(new(range, text));
    }

    public DocumentEditResult Commit()
    {
        if (_completed) throw new InvalidOperationException("This edit transaction is closed.");
        var result = _owner.Commit(BaseVersion, _replacements);
        _completed = true;
        return result;
    }

    public void Dispose() => _completed = true;

    internal readonly record struct Replacement(SourceRange Range, string Text);
}
