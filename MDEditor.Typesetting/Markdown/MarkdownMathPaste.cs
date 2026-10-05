using MDEditor.Core.Text;

namespace MDEditor.Typesetting.Markdown;

/// <summary>Keeps a copied display formula on its own line when pasted inside a sentence.</summary>
public static class MarkdownMathPaste
{
    public static string Prepare(StyledDocumentSnapshot destination, SourceRange replacement, string text)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(text);
        if (!destination.Source.FullRange.Contains(replacement))
            throw new ArgumentOutOfRangeException(nameof(replacement));
        if (!text.Contains("$$", StringComparison.Ordinal) &&
            !text.Contains("\\[", StringComparison.Ordinal)) return text;

        var parsed = MarkdownMathDocument.Parse(new SourceTextSnapshot(text, 0));
        if (parsed.Math.Length != 1 || parsed.Math[0].Kind != MarkdownMathKind.Display)
            return text;
        var formula = parsed.Math[0].Source;
        if (!string.IsNullOrWhiteSpace(text[..formula.Start]) ||
            !string.IsNullOrWhiteSpace(text[formula.End..])) return text;

        var before = HasContentBefore(text, formula.Start) ||
            !HasLineBreakBefore(text, formula.Start) && HasContentBefore(destination.Source.Text, replacement.Start);
        var after = HasContentAfter(text, formula.End) ||
            !HasLineBreakAfter(text, formula.End) && HasContentAfter(destination.Source.Text, replacement.End);
        if (!before && !after) return text;

        var lineBreak = PreferredLineBreak(destination, replacement.Start);
        if (after) text = text.Insert(formula.End, lineBreak);
        if (before) text = text.Insert(formula.Start, lineBreak);
        return text;
    }

    private static bool HasContentBefore(string text, int offset)
    {
        for (var index = offset - 1; index >= 0; index--)
        {
            if (text[index] is '\r' or '\n') return false;
            if (!char.IsWhiteSpace(text[index])) return true;
        }
        return false;
    }

    private static bool HasContentAfter(string text, int offset)
    {
        for (var index = offset; index < text.Length; index++)
        {
            if (text[index] is '\r' or '\n') return false;
            if (!char.IsWhiteSpace(text[index])) return true;
        }
        return false;
    }

    private static bool HasLineBreakBefore(string text, int offset) =>
        text.AsSpan(0, offset).IndexOfAny('\r', '\n') >= 0;

    private static bool HasLineBreakAfter(string text, int offset) =>
        text.AsSpan(offset).IndexOfAny('\r', '\n') >= 0;

    private static string PreferredLineBreak(StyledDocumentSnapshot destination, int offset)
    {
        var lines = destination.Lines;
        var index = lines.FindLine(offset);
        var ending = lines.Lines[index].Ending;
        if (ending == DocumentLineEnding.None && index > 0)
            ending = lines.Lines[index - 1].Ending;
        return ending switch
        {
            DocumentLineEnding.CrLf => "\r\n",
            DocumentLineEnding.Cr => "\r",
            _ => "\n"
        };
    }
}
