using MDEditor.Core.Text;

namespace MDEditor.Core.Documents;

public static class DocumentNewlinePolicy
{
    /// <summary>Preserve the local line ending, then the nearest preceding ending; default to LF.</summary>
    public static string ForInsertion(DocumentLineMap lines, int offset)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var index = lines.FindLine(offset);
        for (var current = index; current >= 0; current--)
        {
            var ending = lines.Lines[current].Ending;
            if (ending != DocumentLineEnding.None) return EndingText(ending);
        }
        for (var current = index + 1; current < lines.Count; current++)
        {
            var ending = lines.Lines[current].Ending;
            if (ending != DocumentLineEnding.None) return EndingText(ending);
        }
        return "\n";
    }

    private static string EndingText(DocumentLineEnding ending) => ending switch
    {
        DocumentLineEnding.CrLf => "\r\n",
        DocumentLineEnding.Cr => "\r",
        _ => "\n"
    };
}
