using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MDEditor.Core.Text;
using MDEditor.Core.Markdown;
using MDEditor.Native.Text;
using MDEditor.Typesetting.Layout;
using MDEditor.ViewModels;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace MDEditor.Controls;

public sealed partial class EditorCanvas
{
    public EditorSearchViewModel SearchViewModel { get; } = new();
    private readonly SearchWidget _searchPanel = new()
    {
        Visibility = Visibility.Collapsed, Width = 420,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 6, 24, 0)
    };
    private TextBox _findInput => _searchPanel.FindInput;
    private TextBox _replaceInput => _searchPanel.ReplaceInput;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _replaceCancellation;
    private DocumentSearchResult? _searchResult;
    private int _activeSearchIndex = -1;
    private bool _updatingSourceMode;
    private bool _searchInputComposing;

    private void InitializeSearch()
    {
        // Keep the shortcuts without WinUI's automatic editor-wide accelerator tooltip.
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        _searchPanel.DataContext = SearchViewModel;
        foreach (var input in new[] { _findInput, _replaceInput })
        {
            input.PreviewKeyDown += SearchInput_PreviewKeyDown;
            input.TextCompositionStarted += (_, _) => _searchInputComposing = true;
            input.TextCompositionEnded += (_, _) => _searchInputComposing = false;
        }
        var shadow = new ThemeShadow(); shadow.Receivers.Add(_surface);
        _searchPanel.ShadowCaster.Shadow = shadow;
        _searchPanel.ShadowCaster.Translation = new(0, 0, 16);
        Grid.SetRow(_searchPanel, 0); _root.Children.Add(_searchPanel);
        _root.SizeChanged += SearchHost_SizeChanged;
        RefreshSearchPresentation();
        SearchViewModel.PropertyChanged += SearchChanged;
        SearchViewModel.Requested += SearchAction;
        AddAccelerator(VirtualKey.F, _ => OpenSearch(false));
        AddAccelerator(VirtualKey.H, _ => OpenSearch(true));
        AddAccelerator((VirtualKey)191, SearchViewModel.ToggleSourceCommand.Execute);
        AddAccelerator(VirtualKey.F3, SearchViewModel.NextCommand.Execute, VirtualKeyModifiers.None);
        AddAccelerator(VirtualKey.F3, SearchViewModel.PreviousCommand.Execute, VirtualKeyModifiers.Shift);
    }

    private void SearchHost_SizeChanged(object sender, SizeChangedEventArgs args) =>
        _searchPanel.Width = Math.Min(420, Math.Max(0, args.NewSize.Width - 40));

    private void RefreshSearchPresentation() => _searchPanel.RefreshPresentation(SearchViewModel.ShowReplace,
        SearchViewModel.Query.Length == 0, SearchViewModel.Status);

    private bool SearchInputHasFocus() => _findInput.FocusState != FocusState.Unfocused ||
        _replaceInput.FocusState != FocusState.Unfocused;

    private bool TrySearchKey(KeyRoutedEventArgs args)
    {
        if (args.Handled) return true;
        if (_imeComposing || _searchInputComposing) return false;
        var control = ModifierDown(VirtualKey.Control);
        if (control && args.Key == VirtualKey.F) OpenSearch(false);
        else if (control && args.Key == VirtualKey.H) OpenSearch(true);
        else if (control && args.Key == (VirtualKey)191) SearchViewModel.ToggleSourceCommand.Execute(null);
        else if (args.Key == VirtualKey.F3) NavigateSearch(ModifierDown(VirtualKey.Shift));
        else if (args.Key == VirtualKey.Escape && SearchViewModel.IsOpen) CloseSearch();
        else if (args.Key == VirtualKey.Enter && SearchInputHasFocus())
        {
            if (_replaceInput.FocusState != FocusState.Unfocused) ReplaceSearch(false);
            else NavigateSearch(ModifierDown(VirtualKey.Shift));
        }
        else return false;
        args.Handled = true; return true;
    }

    private void SearchInput_PreviewKeyDown(object sender, KeyRoutedEventArgs args) => TrySearchKey(args);

    private void OpenSearch(bool replace)
    {
        if (_disposed || _imeComposing) return;
        MirrorPlainText(_imeInput.Text); ClearImeInput();
        _history.BreakCoalescing();
        SearchViewModel.Query = DocumentSearch.QueryFromSelection(_editorText.Capture().Source,
            CurrentEditableSelection()?.Range, SearchViewModel.Query, _searchPanel.ContainsFocus);
        SearchViewModel.ShowReplace = replace;
        SearchViewModel.IsOpen = true;
        QueueSearch(selectMatch: true);
        _findInput.Focus(FocusState.Programmatic); _findInput.SelectAll();
    }

    private void CloseSearch()
    {
        SearchViewModel.IsOpen = false;
        FocusImeInput();
    }

    private void SearchChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed) return;
        switch (args.PropertyName)
        {
            case nameof(EditorSearchViewModel.IsOpen):
                _searchPanel.Visibility = SearchViewModel.IsOpen ? Visibility.Visible : Visibility.Collapsed;
                if (!SearchViewModel.IsOpen) { _searchCancellation?.Cancel(); _replaceCancellation?.Cancel(); _searchResult = null; }
                InvalidateInteraction(); break;
            case nameof(EditorSearchViewModel.ShowReplace):
            case nameof(EditorSearchViewModel.Status):
                RefreshSearchPresentation();
                break;
            case nameof(EditorSearchViewModel.Query):
            case nameof(EditorSearchViewModel.MatchCase):
            case nameof(EditorSearchViewModel.WholeWord):
            case nameof(EditorSearchViewModel.RegularExpression): QueueSearch(selectMatch: true); break;
            case nameof(EditorSearchViewModel.IsSourceMode): ChangeSourceMode(); break;
        }
    }

    private void SearchAction(EditorSearchAction action)
    {
        if (_disposed) return;
        switch (action)
        {
            case EditorSearchAction.Next: NavigateSearch(false); break;
            case EditorSearchAction.Previous: NavigateSearch(true); break;
            case EditorSearchAction.Replace: ReplaceSearch(false); break;
            case EditorSearchAction.ReplaceAll: ReplaceSearch(true); break;
            case EditorSearchAction.Close: CloseSearch(); break;
        }
    }

    private void QueueSearch(bool selectMatch = false)
    {
        _searchCancellation?.Cancel();
        _replaceCancellation?.Cancel();
        _searchResult = null; _activeSearchIndex = -1;
        InvalidateInteraction();
        if (_disposed || !SearchViewModel.IsOpen) return;
        var source = _editorText.Capture().Source;
        var query = SearchViewModel.Query;
        var options = new DocumentSearchOptions(SearchViewModel.MatchCase, SearchViewModel.WholeWord, SearchViewModel.RegularExpression);
        SearchViewModel.Status = query.Length == 0 ? "请输入搜索文字" : "正在搜索…";
        if (query.Length == 0) { InvalidateInteraction(); return; }
        _ = SearchAsync(source, query, options, selectMatch);
    }

    private async Task SearchAsync(SourceTextSnapshot source, string query, DocumentSearchOptions options, bool selectMatch)
    {
        var cancellation = new CancellationTokenSource(); _searchCancellation = cancellation;
        try
        {
            await Task.Delay(80, cancellation.Token);
            var result = await Task.Run(() => DocumentSearch.Find(source, query, options, cancellation.Token), cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || !SearchViewModel.IsOpen ||
                !ReferenceEquals(_editorText.Capture().Source, source)) return;
            _searchResult = result;
            _activeSearchIndex = result.FindIndex(CurrentEditableSelection()?.Range.Start ?? 0);
            if (selectMatch) ShowSearchMatch();
            else
            {
                UpdateSearchCount();
                InvalidateInteraction();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed && !cancellation.IsCancellationRequested) SearchViewModel.Status = $"搜索失败：{error.Message}"; }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation)) _searchCancellation = null;
            cancellation.Dispose();
        }
    }

    private void NavigateSearch(bool reverse)
    {
        if (_imeComposing || _searchInputComposing) return;
        if (!SearchInputHasFocus()) { MirrorPlainText(_imeInput.Text); ClearImeInput(); }
        if (!SearchViewModel.IsOpen) { OpenSearch(false); return; }
        if (_searchResult is not { Matches.IsEmpty: false } result ||
            !ReferenceEquals(result.Source, _editorText.Capture().Source)) return;
        _history.BreakCoalescing();
        var selection = CurrentEditableSelection();
        if (_activeSearchIndex >= 0 && selection?.Range == result.Matches[_activeSearchIndex])
            _activeSearchIndex = (_activeSearchIndex + (reverse ? result.Matches.Length - 1 : 1)) % result.Matches.Length;
        else _activeSearchIndex = result.FindIndex(selection?.Range.Start ?? 0, reverse);
        ShowSearchMatch();
    }

    private void ShowSearchMatch()
    {
        if (_searchResult is not { } result) return;
        if (result.Error is not null) { SearchViewModel.Status = result.Error; return; }
        if (_activeSearchIndex < 0) { SearchViewModel.Status = "没有匹配项"; InvalidateInteraction(); return; }
        var range = result.Matches[_activeSearchIndex];
        var anchor = new TextCaret(TextSurface.Body, result.Source.Version, range.Start, CaretAffinity.Downstream);
        _selection = new(anchor, anchor with { Offset = range.End, Affinity = CaretAffinity.Upstream });
        _preferredCaretX = null;
        // A search result may be entirely inside invisible syntax. Reveal at its interior,
        // not the folded object's outer edge, so the exact source match remains inspectable.
        if (!SearchViewModel.IsSourceMode)
        {
            var current = CurrentPresentation();
            var reveal = range.Length > 0 ? range.Start + (range.Length > 1 ? 1 : 0) : range.Start;
            var formula = current.Text.MathSpans.FirstOrDefault(math =>
                math.Source.Start <= range.Start && range.Start < math.Source.End);
            if (formula is not null)
                reveal = Math.Clamp(reveal, formula.Source.Start + 1, formula.Source.End - 1);
            _livePresentation = MarkdownRichTextProjection.FromSyntax(current.Syntax, reveal);
            if (_livePresentation.Text.ToDisplayRange(range).Length != range.Length ||
                current.Text.References.Images.Any(image => range.Start < image.Source.End && image.Source.Start < range.End))
                SearchViewModel.IsSourceMode = true;
            _renderer?.SetContent(EditableContent());
            RequestLayout(force: true, immediate: true);
        }
        RevealCaret(_renderer?.Document, _canvas);
        _followCaretAfterCommit = true;
        UpdateSearchCount();
        ResetCaretBlink(); InvalidateInteraction(); UpdateStatusIfReady();
    }

    private void ReplaceSearch(bool all)
    {
        _ = ReplaceSearchAsync(all);
    }

    private void UpdateSearchCount()
    {
        if (_searchResult is not { } result) return;
        SearchViewModel.Status = result.Error ?? (result.Matches.IsEmpty ? "没有匹配项" :
            $"{_activeSearchIndex + 1} / {result.Matches.Length}{(result.Truncated ? "+（仅显示前 100000 项）" : "")}");
    }

    private async Task ReplaceSearchAsync(bool all)
    {
        if (_imeComposing || _searchResult is not { } result || _activeSearchIndex < 0) return;
        var before = _editorText.Capture();
        if (!ReferenceEquals(before.Source, result.Source)) { QueueSearch(); return; }
        if (CurrentEditableSelection() is not { } selection) return;
        if (!all && selection.Range != result.Matches[_activeSearchIndex]) { ShowSearchMatch(); return; }
        var matchIndex = all ? (int?)null : _activeSearchIndex;
        _replaceCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); _replaceCancellation = cancellation;
        try
        {
            var replacement = SearchViewModel.Replacement;
            if (replacement.Any(character => char.IsControl(character) && character is not ('\r' or '\n')) ||
                replacement.Contains('\u2028') || replacement.Contains('\u2029') || replacement.Contains('\u00AD'))
                throw new InvalidOperationException("替换文字包含不支持的控制字符");
            var plan = await Task.Run(() => DocumentSearch.PlanReplace(before, result, replacement,
                matchIndex, cancellation.Token), cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested ||
                !ReferenceEquals(before.Source, _editorText.Capture().Source) || CurrentEditableSelection() != selection) return;
            if (plan is null) { NavigateSearch(false); return; }
            _history.BreakCoalescing();
            var change = _editorText.Restore(plan.Range, before.Source.GetText(plan.Range), plan.Text,
                plan.Styles, before.Source.Version);
            var caret = new TextCaret(TextSurface.Body, change.NewVersion, plan.CaretOffset, CaretAffinity.Downstream);
            ApplyEdit(new(change, new(caret, caret)), before, selection, HistoryEditKind.Replace);
            SearchViewModel.Status = $"已替换 {plan.Count} 项，正在重新搜索…";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed && !cancellation.IsCancellationRequested) SearchViewModel.Status = $"替换失败：{error.Message}"; }
        finally
        {
            if (ReferenceEquals(_replaceCancellation, cancellation)) _replaceCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ChangeSourceMode()
    {
        if (_disposed || _updatingSourceMode) return;
        if (_imeComposing)
        {
            _updatingSourceMode = true;
            SearchViewModel.IsSourceMode = !SearchViewModel.IsSourceMode;
            _updatingSourceMode = false;
            _inputFeedback = "请先完成输入法候选再切换源码模式"; UpdateStatusIfReady(); return;
        }
        MirrorPlainText(_imeInput.Text); ClearImeInput();
        _history.BreakCoalescing();
        Revoke();
        _editableMathRequestedVersion = -1;
        _selection = CurrentEditableSelection();
        _renderer?.SetContent(EditableContent());
        if (!SearchViewModel.IsSourceMode) RequestEditableMathLayouts();
        RequestLayout(force: true, immediate: true);
        Invalidate(); UpdateStatusIfReady();
        if (!SearchInputHasFocus()) FocusImeInput();
    }

    private void DrawSearchMatches(CanvasDrawingSession session, ReflowDocument document,
        double top, double bottom)
    {
        if (!SearchViewModel.IsOpen || _searchResult is not { } result ||
            !ReferenceEquals(result.Source, document.BodyInteraction.Source)) return;
        var map = document.BodyInteraction;
        var visible = map.Lines.Where(line => line.Bounds.Bottom >= top && line.Bounds.Y <= bottom).ToArray();
        if (visible.Length == 0) return;
        var start = visible.Min(line => line.Source.Start); var end = visible.Max(line => line.Source.End);
        var low = 0; var high = result.Matches.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (result.Matches[middle].End < start) low = middle + 1; else high = middle;
        }
        // Bound overlay work in pathological documents; navigation and replacement still use all results.
        for (var index = low; index < result.Matches.Length && index < low + 512 && result.Matches[index].Start <= end; index++)
        {
            var range = result.Matches[index];
            var anchor = new TextCaret(TextSurface.Body, result.Source.Version, range.Start, CaretAffinity.Downstream);
            var selection = new TextSelection(anchor, anchor with { Offset = range.End, Affinity = CaretAffinity.Upstream });
            foreach (var rectangle in map.SelectionRects(selection))
                if (rectangle.Bottom >= top && rectangle.Y <= bottom)
                    session.FillRectangle(new Rect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height),
                        Color.FromArgb(85, 230, 165, 35));
        }
    }

    private void DisposeSearch()
    {
        _searchCancellation?.Cancel();
        _replaceCancellation?.Cancel();
        SearchViewModel.PropertyChanged -= SearchChanged;
        SearchViewModel.Requested -= SearchAction;
        _root.SizeChanged -= SearchHost_SizeChanged;
    }
}
