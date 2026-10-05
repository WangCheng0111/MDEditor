using System;
using MDEditor.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MDEditor.Controls;

/// <summary>Presentation-only floating find/replace chrome; commands and document state live in the host.</summary>
public sealed partial class SearchWidget : UserControl
{
    public SearchWidget() => InitializeComponent();

    public TextBox FindInput => FindTextBox;
    public TextBox ReplaceInput => ReplaceTextBox;
    internal Border ShadowCaster => PanelChrome;

    public bool ContainsFocus
    {
        get
        {
            for (var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
                 element is not null; element = VisualTreeHelper.GetParent(element))
                if (ReferenceEquals(element, this)) return true;
            return false;
        }
    }

    public void RefreshPresentation(bool showReplace, bool emptyQuery, string status)
    {
        if (!showReplace && ReplaceTextBox.FocusState != FocusState.Unfocused)
            FindTextBox.Focus(FocusState.Programmatic);
        ReplacementRow.Visibility = showReplace ? Visibility.Visible : Visibility.Collapsed;
        ReplaceChevron.RenderTransform = new RotateTransform { Angle = showReplace ? 90 : 0 };
        AutomationProperties.SetName(ReplaceExpander, showReplace ? "收起替换" : "展开替换");
        // Keep the full message for tooltips/accessibility without growing the compact VS Code-style row.
        SearchStatus.Text = emptyQuery ? "" : status switch
        {
            "没有匹配项" => "无结果",
            "正在搜索…" => "搜索中…",
            _ => status.Replace(" / ", "/", StringComparison.Ordinal)
        };
        ToolTipService.SetToolTip(SearchStatus, status);
        AutomationProperties.SetName(SearchStatus, status);
    }

    private void Input_FocusChanged(object sender, RoutedEventArgs args)
    {
        VisualStateManager.GoToState(this, FindTextBox.FocusState != FocusState.Unfocused ? "FindFocused" : "FindUnfocused", false);
        VisualStateManager.GoToState(this, ReplaceTextBox.FocusState != FocusState.Unfocused ? "ReplaceFocused" : "ReplaceUnfocused", false);
    }

    private void ReplaceExpander_Click(object sender, RoutedEventArgs args)
    {
        if (DataContext is EditorSearchViewModel viewModel) viewModel.ShowReplace = !viewModel.ShowReplace;
    }
}
