using System.Collections.ObjectModel;

namespace MDEditor.Core.Text;

public enum DocumentLineEnding { None, Lf, CrLf, Cr }

/// <summary>One source line; Content excludes its exact, unchanged line-break bytes.</summary>
public readonly record struct DocumentLine(SourceRange Content, SourceRange Break, DocumentLineEnding Ending)
{
    public SourceRange FullRange => new(Content.Start, Break.End - Content.Start);
}

/// <summary>Immutable line index for a particular source version, including a final empty line.</summary>
public sealed class DocumentLineMap
{
    public SourceTextSnapshot Source { get; }
    public IReadOnlyList<DocumentLine> Lines { get; }
    public int Count => Lines.Count;

    private DocumentLineMap(SourceTextSnapshot source, DocumentLine[] lines)
    { Source = source; Lines = new ReadOnlyCollection<DocumentLine>(lines); }

    public static DocumentLineMap Create(SourceTextSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var lines = new List<DocumentLine>();
        var text = source.Text;
        var start = 0;
        for (var position = 0; position < text.Length; position++)
        {
            if (text[position] is not ('\r' or '\n')) continue;
            var ending = text[position] == '\r'
                ? position + 1 < text.Length && text[position + 1] == '\n'
                    ? DocumentLineEnding.CrLf : DocumentLineEnding.Cr
                : DocumentLineEnding.Lf;
            var breakLength = ending == DocumentLineEnding.CrLf ? 2 : 1;
            lines.Add(new(new(start, position - start), new(position, breakLength), ending));
            position += breakLength - 1;
            start = position + 1;
        }
        lines.Add(new(new(start, text.Length - start), new(text.Length, 0), DocumentLineEnding.None));
        return new(source, lines.ToArray());
    }

    /// <summary>Returns the line containing an offset; the next line owns the position after a break.</summary>
    public int FindLine(int offset)
    {
        if (offset < 0 || offset > Source.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var low = 0;
        var high = Lines.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (Lines[middle].Content.Start <= offset) low = middle + 1;
            else high = middle;
        }
        return low - 1;
    }
}
