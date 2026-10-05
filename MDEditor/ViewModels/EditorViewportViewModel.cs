using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MDEditor.ViewModels;

/// <summary>Only editor-level state is observable. Glyph/line snapshots never become view models.</summary>
public partial class EditorViewportViewModel : ObservableObject
{
    [ObservableProperty]
    public partial int ZoomPercent { get; set; }
    public EditorViewportViewModel() => ZoomPercent = 100;
    partial void OnZoomPercentChanging(int value)
    {
        if (value is < 50 or > 300) throw new System.ArgumentOutOfRangeException(nameof(value));
    }
    [RelayCommand]
    private void ZoomIn() => ZoomPercent = System.Math.Min(300, ZoomPercent + 10);
    [RelayCommand]
    private void ZoomOut() => ZoomPercent = System.Math.Max(50, ZoomPercent - 10);
    [RelayCommand]
    private void ResetZoom() => ZoomPercent = 100;
}
