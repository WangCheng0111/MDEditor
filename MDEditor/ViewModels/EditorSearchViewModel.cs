using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MDEditor.ViewModels;

public enum EditorSearchAction { Next, Previous, Replace, ReplaceAll, Close }

/// <summary>Transient search UI state; source text and geometry remain in the document/editor.</summary>
public partial class EditorSearchViewModel : ObservableObject
{
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial string Replacement { get; set; } = "";
    [ObservableProperty] public partial bool MatchCase { get; set; }
    [ObservableProperty] public partial bool WholeWord { get; set; }
    [ObservableProperty] public partial bool RegularExpression { get; set; }
    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial bool ShowReplace { get; set; }
    [ObservableProperty] public partial bool IsSourceMode { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "请输入搜索文字";

    public event Action<EditorSearchAction>? Requested;
    [RelayCommand] private void Next() => Requested?.Invoke(EditorSearchAction.Next);
    [RelayCommand] private void Previous() => Requested?.Invoke(EditorSearchAction.Previous);
    [RelayCommand] private void Replace() => Requested?.Invoke(EditorSearchAction.Replace);
    [RelayCommand] private void ReplaceAll() => Requested?.Invoke(EditorSearchAction.ReplaceAll);
    [RelayCommand] private void Close() => Requested?.Invoke(EditorSearchAction.Close);
    [RelayCommand] private void ToggleSource() => IsSourceMode = !IsSourceMode;
}
