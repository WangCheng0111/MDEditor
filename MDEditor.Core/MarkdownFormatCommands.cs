using MDEditor.Core.Text;

namespace MDEditor.Core.Markdown;

public enum MarkdownFormatKind
{
    Paragraph, Heading1, Heading2, Heading3, Heading4, Heading5, Heading6,
    Strong, Emphasis, Strikethrough,
    CodeSpan, Link
}

/// <summary>One minimal source replacement and the selection to show after it.</summary>
public sealed record MarkdownSourceEdit(SourceRange Replace, string Text, SourceRange Selection);

/// <summary>Source-preserving formatting commands. Unrelated Markdown is never serialized again.</summary>
public static class MarkdownFormatCommands
{
    public static MarkdownSourceEdit? Plan(SourceTextSnapshot source, SourceRange selection,
        MarkdownFormatKind kind)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.FullRange.Contains(selection)) throw new ArgumentOutOfRangeException(nameof(selection));
        if (kind <= MarkdownFormatKind.Heading6)
            return Heading(source, selection, kind == MarkdownFormatKind.Paragraph ? 0 :
                (int)kind - (int)MarkdownFormatKind.Heading1 + 1);
        if (source.GetText(selection).IndexOfAny('\r', '\n') >= 0) return null;

        var syntaxKind = kind switch
        {
            MarkdownFormatKind.Strong => MarkdownSyntaxKind.Strong,
            MarkdownFormatKind.Emphasis => MarkdownSyntaxKind.Emphasis,
            MarkdownFormatKind.Strikethrough => MarkdownSyntaxKind.Strikethrough,
            MarkdownFormatKind.CodeSpan => MarkdownSyntaxKind.CodeSpan,
            MarkdownFormatKind.Link => MarkdownSyntaxKind.Link,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var projection = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(source));
        foreach (var unit in projection.SyntaxUnits)
        {
            if (unit.Kind != syntaxKind || unit.Delimiters.Length != 2) continue;
            var inner = new SourceRange(unit.Delimiters[0].End,
                unit.Delimiters[1].Start - unit.Delimiters[0].End);
            var caretInside = selection.Length == 0 &&
                inner.Start <= selection.Start && selection.Start <= inner.End;
            if (selection != inner && selection != unit.Source && !caretInside) continue;
            var content = source.GetText(inner);
            var afterSelection = caretInside
                ? new SourceRange(unit.Source.Start + selection.Start - inner.Start, 0)
                : new SourceRange(unit.Source.Start, content.Length);
            return new(unit.Source, content, afterSelection);
        }

        var selected = source.GetText(selection);
        string prefix, suffix;
        switch (kind)
        {
            case MarkdownFormatKind.Strong: prefix = suffix = "**"; break;
            case MarkdownFormatKind.Emphasis: prefix = suffix = "*"; break;
            case MarkdownFormatKind.Strikethrough: prefix = suffix = "~~"; break;
            case MarkdownFormatKind.CodeSpan:
                // A single code marker is enough for normal text; longer runs keep inner backticks literal.
                var run = 1;
                for (var index = 0; index < selected.Length;)
                {
                    if (selected[index] != '`') { index++; continue; }
                    var end = index;
                    while (end < selected.Length && selected[end] == '`') end++;
                    run = Math.Max(run, end - index + 1); index = end;
                }
                prefix = suffix = new string('`', run); break;
            case MarkdownFormatKind.Link:
                prefix = "["; suffix = "](https://example.com)";
                if (selected.Length == 0) selected = "link";
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var replacement = prefix + selected + suffix;
        return new(selection, replacement, new(selection.Start + prefix.Length,
            selected.Length == 0 ? 0 : selected.Length));
    }

    private static MarkdownSourceEdit? Heading(SourceTextSnapshot source, SourceRange selection, int level)
    {
        var lines = DocumentLineMap.Create(source);
        var line = lines.Lines[lines.FindLine(selection.Start)].Content;
        var text = source.GetText(line);
        var indentation = 0;
        while (indentation < Math.Min(3, text.Length) && text[indentation] == ' ') indentation++;
        var markerStart = indentation;
        var markerEnd = markerStart;
        while (markerEnd < text.Length && text[markerEnd] == '#' && markerEnd - markerStart < 6)
            markerEnd++;
        var oldLevel = markerEnd - markerStart;
        if (oldLevel > 0 && markerEnd < text.Length && text[markerEnd] == ' ')
            markerEnd++;
        else { oldLevel = 0; markerEnd = markerStart; }
        if (oldLevel == 0 && level == 0) return null;
        var marker = level == oldLevel ? "" : level == 0 ? "" : new string('#', level) + " ";
        var range = new SourceRange(line.Start + markerStart, markerEnd - markerStart);
        var shift = marker.Length - range.Length;
        var selectedStart = selection.Start >= range.End ? selection.Start + shift : range.Start + marker.Length;
        var selectedEnd = selection.End >= range.End ? selection.End + shift : range.Start + marker.Length;
        return new(range, marker, new(selectedStart, selectedEnd - selectedStart));
    }
}
