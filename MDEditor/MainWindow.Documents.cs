using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MDEditor.Core.Documents;
using MDEditor.Core.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MDEditor;

public sealed partial class MainWindow
{
    private readonly DocumentFileStore _fileStore = new();
    private readonly DocumentSessionState _documentSession = new();
    private readonly SemaphoreSlim _dialogSerial = new(1, 1);
    private DocumentRecoveryStore _recoveryStore = null!;
    private DispatcherQueueTimer _recoveryTimer = null!;
    private StyledDocumentSnapshot? _pendingRecovery;
    private Task _recoveryOperations = Task.CompletedTask;
    private bool _fileBusy, _closingPrompt, _closeApproved, _recoveryPaused, _startupChecked, _startupLoading;

    private void InitializeDocumentWorkflow()
    {
        _recoveryStore = new DocumentRecoveryStore(ApplicationData.Current.LocalFolder.Path);
        _documentSession.Load(EditorSurface.CaptureDocument().Source.Text, null, DocumentEncoding.Utf8, null);
        EditorSurface.IsEnabled = false;
        _recoveryTimer = DispatcherQueue.CreateTimer();
        _recoveryTimer.Interval = TimeSpan.FromMilliseconds(450);
        _recoveryTimer.IsRepeating = false;
        _recoveryTimer.Tick += RecoveryTimer_Tick;
        EditorSurface.DocumentChanged += EditorSurface_DocumentChanged;
        EditorSurface.NewRequested += (_, _) => _ = RunFileActionAsync(NewDocumentAsync);
        EditorSurface.OpenRequested += (_, _) => _ = RunFileActionAsync(OpenDocumentAsync);
        EditorSurface.SaveRequested += (_, _) => _ = RunFileActionAsync(() => SaveDocumentAsync(false));
        EditorSurface.SaveAsRequested += (_, _) => _ = RunFileActionAsync(() => SaveDocumentAsync(true));
        EditorSurface.ExportPdfRequested += (_, _) => _ = RunFileActionAsync(ExportPdfAsync);
        EditorSurface.Loaded += EditorSurface_DocumentLoaded;
        Activated += DocumentWindow_Activated;
        _appWindow.Closing += DocumentWindow_Closing;
        Closed += DocumentWindow_Closed;
        UpdateDocumentTitle();
    }

    private async void EditorSurface_DocumentLoaded(object sender, RoutedEventArgs args)
    {
        if (_startupChecked || _startupLoading) return;
        _startupLoading = true;
        try
        {
            var checkpoint = await _recoveryStore.ReadAsync();
            if (checkpoint is null) return;
            var name = checkpoint.FilePath is null ? "未命名文档" : Path.GetFileName(checkpoint.FilePath);
            var choice = await AskAsync("恢复未保存的编辑？",
                $"上次记录：{checkpoint.RecordedAt.LocalDateTime:g} · {name}\n恢复内容保存在应用本地目录，不会覆盖磁盘文件。",
                "恢复", "丢弃", null);
            if (choice == ContentDialogResult.Secondary)
            {
                await QueueRecoveryOperation(() => _recoveryStore.ClearAsync());
                return;
            }
            // Esc also restores; it must never silently discard the only recovery copy.
            EditorSurface.LoadDocument(checkpoint.Text, checkpoint.Styles, checkpoint.FilePath);
            _documentSession.Load(checkpoint.Text, checkpoint.FilePath, checkpoint.Encoding,
                checkpoint.SavedFingerprint, recovered: true);
            UpdateDocumentTitle();
        }
        catch (Exception error)
        {
            _recoveryPaused = true; // Preserve a corrupt/unreadable checkpoint instead of overwriting it.
            await ShowErrorAsync("恢复检查点无法读取", error);
        }
        finally
        {
            _startupChecked = true;
            _startupLoading = false;
            if (!_isClosed) EditorSurface.IsEnabled = true;
        }
    }

    private void EditorSurface_DocumentChanged(StyledDocumentSnapshot snapshot)
    {
        _documentSession.Edited(snapshot.Source.Text);
        UpdateDocumentTitle();
        if (_recoveryPaused) return;
        if (_documentSession.IsDirty) ScheduleRecovery(snapshot);
        else
        {
            _recoveryTimer.Stop(); _pendingRecovery = null;
            _ = ClearRecoveryWithFeedbackAsync();
        }
    }

    private void ScheduleRecovery(StyledDocumentSnapshot snapshot)
    {
        _pendingRecovery = snapshot;
        _recoveryTimer.Stop(); _recoveryTimer.Start();
    }

    private async void RecoveryTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        try { await CheckpointNowAsync(); }
        catch (Exception error) { _recoveryPaused = true; await ShowErrorAsync("自动恢复记录失败", error); }
    }

    private async Task CheckpointNowAsync()
    {
        _recoveryTimer.Stop();
        if (_recoveryPaused || !_documentSession.IsDirty) return;
        var snapshot = _pendingRecovery ?? EditorSurface.CaptureDocument();
        _pendingRecovery = null;
        var checkpoint = new RecoveryCheckpoint(snapshot.Source.Text, snapshot.Styles.ToArray(),
            _documentSession.FilePath, _documentSession.Encoding,
            _documentSession.SavedFingerprint, DateTimeOffset.UtcNow);
        await QueueRecoveryOperation(() => Task.Run(() => _recoveryStore.WriteAsync(checkpoint)));
    }

    private async void DocumentWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated || !_documentSession.IsDirty ||
            _recoveryPaused || !_startupChecked) return;
        try { await CheckpointNowAsync(); }
        catch (Exception error) { _recoveryPaused = true; await ShowErrorAsync("自动恢复记录失败", error); }
    }

    private async Task ClearRecoveryWithFeedbackAsync()
    {
        try { await QueueRecoveryOperation(() => _recoveryStore.ClearAsync()); }
        catch (Exception error) { _recoveryPaused = true; await ShowErrorAsync("清理恢复记录失败", error); }
    }

    private Task QueueRecoveryOperation(Func<Task> operation)
    {
        var previous = _recoveryOperations;
        var next = RunAfterAsync(previous, operation);
        _recoveryOperations = next;
        return next;
    }

    private static async Task RunAfterAsync(Task previous, Func<Task> operation)
    {
        try { await previous; }
        catch { /* The originating UI operation already reported its own failure. */ }
        await operation();
    }

    private async Task RunFileActionAsync(Func<Task> action)
    {
        if (_fileBusy || _closingPrompt || !_startupChecked) return;
        _fileBusy = true;
        try { await action(); }
        catch (Exception error) { await ShowErrorAsync("文件操作失败", error); }
        finally { _fileBusy = false; }
    }

    private async Task NewDocumentAsync()
    {
        EditorSurface.CaptureDocumentForFile();
        if (!await ConfirmDiscardOrSaveAsync()) return;
        EditorSurface.LoadDocument("");
        _documentSession.Load("", null, DocumentEncoding.Utf8, null);
        _recoveryTimer.Stop(); _pendingRecovery = null;
        await QueueRecoveryOperation(() => _recoveryStore.ClearAsync());
        UpdateDocumentTitle();
    }

    private async Task OpenDocumentAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".md"); picker.FileTypeFilter.Add(".markdown");
        picker.FileTypeFilter.Add(".mdown"); picker.FileTypeFilter.Add(".mkd");
        picker.FileTypeFilter.Add(".txt");
        var chosen = await picker.PickSingleFileAsync();
        if (chosen is null) return;
        var path = PickedPath(chosen);
        EditorSurface.CaptureDocumentForFile();
        if (!await ConfirmDiscardOrSaveAsync()) return;
        var opened = await Task.Run(() => _fileStore.OpenAsync(path));
        EditorSurface.LoadDocument(opened.Text, filePath: path);
        _documentSession.Load(opened.Text, path, opened.Encoding, opened.Fingerprint);
        _recoveryTimer.Stop(); _pendingRecovery = null;
        await QueueRecoveryOperation(() => _recoveryStore.ClearAsync());
        UpdateDocumentTitle();
    }

    private async Task<bool> SaveDocumentAsync(bool saveAs)
    {
        EditorSurface.ImagePasteSuspended = true;
        try { return await SaveDocumentCoreAsync(saveAs); }
        finally { EditorSurface.ImagePasteSuspended = false; }
    }

    private async Task<bool> SaveDocumentCoreAsync(bool saveAs)
    {
        var path = _documentSession.FilePath;
        if (path is null || saveAs)
        {
            var picker = new FileSavePicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.SuggestedFileName = path is null ? "未命名" : Path.GetFileNameWithoutExtension(path);
            picker.FileTypeChoices.Add("Markdown", new List<string> { ".md", ".markdown" });
            var chosen = await picker.PickSaveFileAsync();
            if (chosen is null) return false;
            path = PickedPath(chosen);
        }
        var snapshot = EditorSurface.CaptureDocumentForFile();
        var epoch = _documentSession.Epoch;
        var expected = _documentSession.FilePath is not null &&
            string.Equals(Path.GetFullPath(path), Path.GetFullPath(_documentSession.FilePath),
                StringComparison.OrdinalIgnoreCase)
            ? _documentSession.SavedFingerprint : null;
        var format = _documentSession.Encoding;
        var imagePlan = await EditorSurface.PrepareImageAttachmentsForSaveAsync(snapshot, path);
        var savedText = imagePlan.Saved.Source.Text;
        var fingerprint = await Task.Run(() => _fileStore.SaveAsync(path, savedText,
            format, expected));
        if (epoch == _documentSession.Epoch)
        {
            EditorSurface.SetDocumentPath(path);
            EditorSurface.CommitSavedImageAttachments(imagePlan);
        }
        if (_documentSession.CompleteSave(epoch, path, _documentSession.Encoding,
            savedText, fingerprint, EditorSurface.CaptureDocument().Source.Text))
        {
            UpdateDocumentTitle();
            if (_documentSession.IsDirty) ScheduleRecovery(EditorSurface.CaptureDocument());
            else
            {
                _recoveryTimer.Stop(); _pendingRecovery = null;
                await QueueRecoveryOperation(() => _recoveryStore.ClearAsync());
            }
        }
        return !_documentSession.IsDirty;
    }

    private async Task<bool> ConfirmDiscardOrSaveAsync()
    {
        if (!_documentSession.IsDirty) return true;
        var choice = await AskAsync("未保存的修改", "先保存当前 Markdown 文档吗？",
            "保存", "不保存", "取消");
        if (choice == ContentDialogResult.Primary) return await SaveDocumentAsync(false);
        return choice == ContentDialogResult.Secondary;
    }

    private void DocumentWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeApproved) return;
        if (_fileBusy) { args.Cancel = true; return; }
        if (!_documentSession.IsDirty)
        {
            _recoveryTimer.Stop();
            if (!_recoveryOperations.IsCompleted)
            {
                args.Cancel = true;
                if (!_closingPrompt) _ = CloseAfterRecoveryAsync();
            }
            return;
        }
        args.Cancel = true;
        if (!_closingPrompt) _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        _closingPrompt = true;
        try
        {
            if (!await ConfirmDiscardOrSaveAsync()) return;
            _recoveryTimer.Stop(); _pendingRecovery = null;
            await QueueRecoveryOperation(() => _recoveryStore.ClearAsync());
            _closeApproved = true;
            Close();
        }
        catch (Exception error) { await ShowErrorAsync("关闭前保存失败", error); }
        finally { _closingPrompt = false; }
    }

    private async Task CloseAfterRecoveryAsync()
    {
        _closingPrompt = true;
        try
        {
            await _recoveryOperations;
            _closeApproved = true;
            Close();
        }
        catch (Exception error) { await ShowErrorAsync("恢复记录尚未写完", error); }
        finally { _closingPrompt = false; }
    }

    private void DocumentWindow_Closed(object sender, WindowEventArgs args)
    {
        _recoveryTimer.Stop(); _recoveryTimer.Tick -= RecoveryTimer_Tick;
        EditorSurface.DocumentChanged -= EditorSurface_DocumentChanged;
        EditorSurface.Loaded -= EditorSurface_DocumentLoaded;
        Activated -= DocumentWindow_Activated;
        _appWindow.Closing -= DocumentWindow_Closing;
        Closed -= DocumentWindow_Closed;
    }

    private void UpdateDocumentTitle()
    {
        var name = _documentSession.FilePath is null ? "未命名" : Path.GetFileName(_documentSession.FilePath);
        var title = $"{name}{(_documentSession.IsDirty ? " *" : "")}";
        TitleBarTextBlock.Text = title;
        _appWindow.Title = title;
    }

    private static string PickedPath(StorageFile file) => !string.IsNullOrWhiteSpace(file.Path)
        ? Path.GetFullPath(file.Path)
        : throw new NotSupportedException("此选择器返回的文件没有本地路径，暂无法用原生文件流打开。");

    private async Task<ContentDialogResult> AskAsync(string title, string message, string primary,
        string secondary, string? close)
    {
        await _dialogSerial.WaitAsync();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = EditorSurface.XamlRoot, Title = title, Content = message,
                RequestedTheme = EditorSurface.ResolvedTheme,
                PrimaryButtonText = primary, SecondaryButtonText = secondary,
                DefaultButton = ContentDialogButton.Primary
            };
            if (close is not null) dialog.CloseButtonText = close;
            return await dialog.ShowAsync();
        }
        finally { _dialogSerial.Release(); }
    }

    private async Task ShowErrorAsync(string title, Exception error)
    {
        if (_isClosed) return;
        await _dialogSerial.WaitAsync();
        try
        {
            if (_isClosed) return;
            var dialog = new ContentDialog
            {
                XamlRoot = EditorSurface.XamlRoot, Title = title, Content = error.Message,
                RequestedTheme = EditorSurface.ResolvedTheme,
                CloseButtonText = "确定"
            };
            await dialog.ShowAsync();
        }
        finally { _dialogSerial.Release(); }
    }
}
