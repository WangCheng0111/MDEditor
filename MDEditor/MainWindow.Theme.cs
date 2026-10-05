using CommunityToolkit.Mvvm.Input;
using MDEditor.Native.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MDEditor;

public sealed partial class MainWindow
{
    private GithubMarkdownTheme _captionTheme = GithubMarkdownTheme.Light;
    private RelayCommand? _toggleThemeCommand;
    public RelayCommand ToggleThemeCommand => _toggleThemeCommand ??= new(ToggleTheme);

    private void InitializeMarkdownTheme()
    {
        EditorSurface.ThemeChanged += EditorSurface_ThemeChanged;
        Closed += (_, _) => EditorSurface.ThemeChanged -= EditorSurface_ThemeChanged;
        UpdateThemeToggle();
    }

    private void ToggleTheme() => EditorSurface.SetThemeMode(EditorSurface.ResolvedTheme == ElementTheme.Dark
        ? Controls.EditorCanvas.ThemeMode.Light : Controls.EditorCanvas.ThemeMode.Dark);

    private void UpdateThemeToggle()
    {
        var isDark = EditorSurface.ResolvedTheme == ElementTheme.Dark;
        ThemeToggleIcon.Glyph = isDark ? "\uE706" : "\uE708";
        var action = isDark ? "切换为浅色主题" : "切换为深色主题";
        ToolTipService.SetToolTip(ThemeToggleButton, action);
        AutomationProperties.SetName(ThemeToggleButton, action);
        ThemeToggleButton.Foreground = new SolidColorBrush(BaseIconColor);
    }

    private void EditorSurface_ThemeChanged(GithubMarkdownTheme theme, ElementTheme resolved)
    {
        _captionTheme = theme;
        AppTitleBar.RequestedTheme = resolved;
        BackgroundOverlay.Background = new SolidColorBrush(theme.IsDark
            ? Color.FromArgb(128, 13, 17, 23)
            : Color.FromArgb(128, 255, 255, 255));
        TitleBarTextBlock.Foreground = new SolidColorBrush(BaseIconColor);
        UpdateThemeToggle();
        foreach (var button in _captionButtons)
            ApplyButtonVisual(button, button == _pressedButton ? StatePressed :
                button == _hoveredButton ? StatePointerOver : StateNormal, System.TimeSpan.Zero);
    }
}
