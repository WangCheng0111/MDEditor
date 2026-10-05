using Windows.UI;

namespace MDEditor.Native.Rendering;

/// <summary>
/// Native equivalents of the color variables in the supplied github-markdown.css,
/// github-markdown-light.css and github-markdown-dark.css. Layout never depends on
/// this palette, so switching a color scheme cannot invalidate a line break.
/// </summary>
public sealed class GithubMarkdownTheme
{
    public static GithubMarkdownTheme Light { get; } = new(false);
    public static GithubMarkdownTheme Dark { get; } = new(true);

    public bool IsDark { get; }
    public Color Foreground { get; }
    public Color MutedForeground { get; }
    public Color Accent { get; }
    public Color Success { get; }
    public Color Background { get; }
    public Color MutedBackground { get; }
    public Color CanvasBackground { get; }
    public Color NeutralBackground { get; }
    public Color Border { get; }
    public Color MutedBorder { get; }
    public Color Danger { get; }
    public Color QuoteBorder { get; }
    public Color Selection { get; }
    public Color Caret { get; }
    public IReadOnlyList<Color> CodeColors { get; }

    private GithubMarkdownTheme(bool dark)
    {
        IsDark = dark;
        Foreground = Rgb(dark ? 0xf0f6fc : 0x1f2328);
        MutedForeground = Rgb(dark ? 0x9198a1 : 0x59636e);
        Accent = Rgb(dark ? 0x4493f8 : 0x0969da);
        Success = Rgb(dark ? 0x3fb950 : 0x1a7f37);
        Background = Rgb(dark ? 0x0d1117 : 0xffffff);
        MutedBackground = Rgb(dark ? 0x151b23 : 0xf6f8fa);
        CanvasBackground = MutedBackground;
        NeutralBackground = dark ? Color.FromArgb(0x33, 0x65, 0x6c, 0x76) :
            Color.FromArgb(0x1f, 0x81, 0x8b, 0x98);
        Border = Rgb(dark ? 0x3d444d : 0xd1d9e0);
        MutedBorder = dark ? Color.FromArgb(0xb3, 0x3d, 0x44, 0x4d) :
            Color.FromArgb(0xb3, 0xd1, 0xd9, 0xe0);
        Danger = Rgb(dark ? 0xf85149 : 0xd1242f);
        QuoteBorder = Border;
        Selection = dark ? Color.FromArgb(0x66, 0x44, 0x93, 0xf8) :
            Color.FromArgb(0x50, 0x09, 0x69, 0xda);
        Caret = Accent;
        // starry-night 3.11 light/dark CSS, indexed by MarkdownCodeTokenKind.
        // Invalid/diff tokens use readable foregrounds even without HTML backgrounds.
        CodeColors = dark
            ? [Rgb(0x9198a1), Rgb(0x79c0ff), Rgb(0xd2a8ff), Foreground,
                Rgb(0x7ee787), Rgb(0xff7b72), Rgb(0xa5d6ff), Rgb(0xffa657),
                Rgb(0xf85149), Rgb(0xf85149), Rgb(0xf85149), Rgb(0x7ee787),
                Rgb(0xf2cc60), Rgb(0x1f6feb), Rgb(0xff7b72), Rgb(0x7ee787),
                Rgb(0xffdfb6), Foreground, Rgb(0xd2a8ff), Rgb(0x9198a1),
                Rgb(0x9198a1), Rgb(0xa5d6ff)]
            : [Rgb(0x59636e), Rgb(0x0550ae), Rgb(0x6639ba), Foreground,
                Rgb(0x0550ae), Rgb(0xcf222e), Rgb(0x0a3069), Rgb(0x953800),
                Rgb(0x82071e), Rgb(0x82071e), Rgb(0xcf222e), Rgb(0x116329),
                Rgb(0x3b2300), Rgb(0x0550ae), Rgb(0x82071e), Rgb(0x116329),
                Rgb(0x953800), Foreground, Rgb(0x8250df), Rgb(0x59636e),
                Rgb(0x818b98), Rgb(0x0a3069)];
    }

    private static Color Rgb(int rgb) => Color.FromArgb(255, (byte)(rgb >> 16),
        (byte)(rgb >> 8), (byte)rgb);
}
