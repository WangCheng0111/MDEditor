using MDEditor.Core.Text;

namespace MDEditor.Typesetting.Layout;

/// <summary>github-markdown-css hr: .25em height, 1.5rem vertical margins, no border.</summary>
public static class MarkdownThematicBreakLayout
{
    public const double Margin = 24; // 1.5rem at the CSS root's 16px, independent of text presets.

    public static TextInteractionLine Create(SourceRange display, double width, double top,
        double fontSize, double indent = 0)
    {
        if (display.Length != 1) throw new ArgumentException("A folded rule is one display object.", nameof(display));
        if (!double.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(top) || top < 0) throw new ArgumentOutOfRangeException(nameof(top));
        if (!double.IsFinite(fontSize) || fontSize <= 0) throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (!double.IsFinite(indent) || indent < 0 || indent >= width) throw new ArgumentOutOfRangeException(nameof(indent));
        return new(display, new(indent, top, width - indent, fontSize * 0.25),
            [new TextInteractionSpan(display, indent, width)], caretHeight: fontSize * 1.5);
    }
}
