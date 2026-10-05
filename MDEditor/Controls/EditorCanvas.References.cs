using System.Linq;

namespace MDEditor.Controls;

public sealed partial class EditorCanvas
{
    private string? ReferenceDiagnosticStatus()
    {
        var issues = CurrentPresentation().Text.References.Issues;
        if (issues.IsEmpty) return null;
        var caret = _selection?.Focus.Offset ?? -1;
        var selected = issues.FirstOrDefault(issue => issue.Source.Start <= caret &&
            caret <= issue.Source.End) ?? issues[0];
        return $" · {selected.Message}；源码已保留";
    }
}
