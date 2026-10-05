using System;
using MDEditor.Native.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;

namespace MDEditor.Controls;

public sealed partial class EditorCanvas
{
    private const string ThemeSettingKey = "MarkdownThemeMode";
    public enum ThemeMode { System, Light, Dark }

    private GithubMarkdownTheme _githubTheme = GithubMarkdownTheme.Light;
    public ElementTheme ResolvedTheme => _root.ActualTheme;
    public event Action<GithubMarkdownTheme, ElementTheme>? ThemeChanged;

    private void InitializeTheme()
    {
        var saved = ApplicationData.Current.LocalSettings.Values[ThemeSettingKey] as string;
        var mode = Enum.TryParse<ThemeMode>(saved, out var parsed) &&
            Enum.IsDefined(parsed) ? parsed : ThemeMode.System;
        _root.RequestedTheme = ModeToElementTheme(mode);
        _root.ActualThemeChanged += Root_ActualThemeChanged;
    }

    /// <summary>Theme selection remains available without creating toolbar widgets.</summary>
    public void SetThemeMode(ThemeMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (_disposed) return;
        ApplicationData.Current.LocalSettings.Values[ThemeSettingKey] = mode.ToString();
        _root.RequestedTheme = ModeToElementTheme(mode);
        ApplyCurrentTheme();
    }

    private static ElementTheme ModeToElementTheme(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => ElementTheme.Light,
        ThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    private void Root_ActualThemeChanged(FrameworkElement sender, object args) => ApplyCurrentTheme();

    private void ApplyCurrentTheme()
    {
        if (_disposed) return;
        var resolved = _root.ActualTheme == ElementTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
        _githubTheme = resolved == ElementTheme.Dark ? GithubMarkdownTheme.Dark : GithubMarkdownTheme.Light;
        _root.Background = new SolidColorBrush(EditorSurfaceClearColor);
        _status.Foreground = new SolidColorBrush(_githubTheme.MutedForeground);
        if (_canvas is { } canvas) canvas.ClearColor = EditorSurfaceClearColor;
        _renderer?.SetTheme(_githubTheme);
        ThemeChanged?.Invoke(_githubTheme, resolved);
        if (_headingCanvas is { ReadyToDraw: true } heading) heading.Invalidate();
        if (_mathHeadingCanvas is { ReadyToDraw: true } mathHeading) mathHeading.Invalidate();
        Invalidate();
    }
}
