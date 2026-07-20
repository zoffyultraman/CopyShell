using System.Collections.ObjectModel;
using CopyShell.Core.Abstractions;
using CopyShell.Core.Models;
using CopyShell.Core.Protocol;
using CopyShell.Core.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace CopyShell.App;

public sealed partial class MainWindow : Window
{
    private const int MaximumLogCharacters = 250_000;

    private readonly bool _hasTaskContext;
    private readonly CopyOperation _operation;
    private readonly Guid _taskId;
    private readonly ObservableCollection<string> _sources = [];
    private readonly ObservableCollection<QueueTaskViewModel> _queueItems = [];
    private readonly CopyTaskPlanner _planner = new(new PhysicalFileSystemProbe());
    private readonly ISyncPreviewService _previewService = new FileSystemSyncPreviewService();
    private readonly TaskQueueStore _queueStore;
    private readonly BackgroundWorkerLauncher _workerLauncher;
    private readonly DispatcherQueueTimer _queueRefreshTimer;
    private CancellationTokenSource? _cancellation;
    private bool _isRunning;
    private bool _draftEnqueued;
    private bool _isRefreshingQueue;

    public MainWindow(
        ShellRequest? request,
        TaskJournalEntry? recovery,
        TaskQueueStore queueStore,
        BackgroundWorkerLauncher workerLauncher,
        string? startupMessage,
        bool startupMessageIsError)
    {
        AppDiagnostics.Write("MainWindow.InitializeComponent started.");
        InitializeComponent();
        AppDiagnostics.Write("MainWindow.InitializeComponent completed.");
        DestinationBox.TextChanged += OnDestinationChanged;
        QueueList.SelectionChanged += OnQueueSelectionChanged;
        _queueStore = queueStore;
        _workerLauncher = workerLauncher;
        _hasTaskContext = request is not null || recovery is not null;
        _operation = request?.Operation ?? recovery?.Task.Operation ?? CopyOperation.Copy;
        _taskId = recovery?.Task.TaskId ?? Guid.NewGuid();
        SourceList.ItemsSource = _sources;
        QueueList.ItemsSource = _queueItems;

        foreach (var source in request?.Sources ?? recovery?.Task.Sources ?? [])
        {
            _sources.Add(source);
        }

        ConfigureWindow();
        ConfigureOperation(_operation);
        if (recovery is not null)
        {
            LoadRecovery(recovery.Task);
        }

        if (!string.IsNullOrWhiteSpace(startupMessage))
        {
            StartupInfo.Title = startupMessageIsError ? "无法创建任务" : "任务恢复";
            StartupInfo.Severity = startupMessageIsError
                ? InfoBarSeverity.Error
                : InfoBarSeverity.Warning;
            StartupInfo.Message = startupMessage;
            StartupInfo.IsOpen = true;
        }

        _queueRefreshTimer = DispatcherQueue.CreateTimer();
        _queueRefreshTimer.Interval = TimeSpan.FromSeconds(1);
        _queueRefreshTimer.Tick += OnQueueRefreshTick;
        _queueRefreshTimer.Start();
        Closed += OnWindowClosed;

        UpdateStartButton();
        _ = RefreshQueueAsync();
    }

    private void ConfigureWindow()
    {
        try
        {
            Title = "CopyShell";
            var windowHandle = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow is null)
            {
                AppDiagnostics.Write(
                    "AppWindow lookup returned null; default size will be used.");
                return;
            }

            appWindow.Resize(new SizeInt32(780, 900));
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteException(
                "Window sizing failed; default size will be used",
                exception);
        }
    }

    private void ConfigureOperation(CopyOperation operation)
    {
        (OperationTitle.Text, OperationDescription.Text, StartButton.Content) = operation switch
        {
            CopyOperation.Copy => (
                "高级复制到…",
                "保留数据、属性和时间戳，并支持失败重试。",
                "开始复制"),
            CopyOperation.Move => (
                "高级移动到…",
                "复制成功后删除源项目。",
                "开始移动"),
            CopyOperation.Sync => (
                "同步到…",
                "将单个源文件夹镜像到目标文件夹。",
                "开始同步"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        SyncWarning.IsOpen = operation == CopyOperation.Sync;
        ConflictStrategyBox.IsEnabled = operation != CopyOperation.Sync;
    }

    private async void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            DestinationBox.Text = folder.Path;
        }
    }

    private void OnDestinationChanged(object sender, TextChangedEventArgs e) =>
        UpdateStartButton();

    private async void OnStartClicked(object sender, RoutedEventArgs e)
    {
        if (_isRunning || !_hasTaskContext)
        {
            return;
        }

        var task = new CopyTask(
            _taskId,
            _operation,
            _sources.ToArray(),
            DestinationBox.Text.Trim(),
            CreateOptions());
        CopyPlan plan;
        try
        {
            plan = _planner.CreatePlan(task);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法开始任务", exception.Message);
            return;
        }

        BeginRun();
        try
        {
            if (plan.RiskLevel == RiskLevel.Destructive)
            {
                StatusText.Text = "正在分析同步变更…";
                AppendLog("[CopyShell] 正在生成同步预览。");
                var preview = await _previewService.CreateAsync(
                    plan,
                    _cancellation!.Token);
                if (!await ConfirmDestructiveOperationAsync(plan, preview))
                {
                    StatusText.Text = "已返回修改任务参数。";
                    return;
                }
            }

            var queued = await _queueStore.EnqueueAsync(
                task,
                plan.PlanHash,
                _cancellation!.Token);
            _draftEnqueued = true;
            string? workerError = null;
            try
            {
                _workerLauncher.EnsureRunning();
            }
            catch (Exception exception)
            {
                workerError = exception.Message;
            }

            TaskProgress.IsIndeterminate = false;
            TaskProgress.Value = 100;
            StatusText.Text = workerError is null
                ? "任务已加入队列，后台进程将按顺序执行。"
                : "任务已加入队列，但后台进程未能启动。";
            AppendLog($"[CopyShell] 任务已入队：{queued.TaskId:D}");
            await RefreshQueueAsync(queued.TaskId);
            if (workerError is not null)
            {
                await ShowMessageAsync(
                    "后台进程未启动",
                    $"{workerError}\n\n任务仍保留在队列中，重新启动 CopyShell 后可以继续。");
            }
        }
        catch (OperationCanceledException)
        {
            TaskProgress.IsIndeterminate = false;
            TaskProgress.Value = 0;
            StatusText.Text = "已取消任务准备。";
            AppendLog("[CopyShell] 已取消任务准备。");
        }
        catch (Exception exception)
        {
            TaskProgress.IsIndeterminate = false;
            TaskProgress.Value = 0;
            StatusText.Text = "任务入队失败。";
            AppendLog($"[CopyShell] {exception}");
            await ShowMessageAsync("无法加入队列", exception.Message);
        }
        finally
        {
            EndRun();
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在取消…";
        _cancellation?.Cancel();
    }

    private CopyOptions CreateOptions() => new()
    {
        ThreadCount = double.IsNaN(ThreadCountBox.Value)
            ? 16
            : (int)ThreadCountBox.Value,
        RetryCount = double.IsNaN(RetryCountBox.Value)
            ? 2
            : (int)RetryCountBox.Value,
        Restartable = RestartableCheckBox.IsChecked == true,
        ExcludeJunctions = ExcludeJunctionsCheckBox.IsChecked == true,
        ConflictStrategy = ConflictStrategyBox.SelectedIndex switch
        {
            1 => ConflictStrategy.SkipExisting,
            2 => ConflictStrategy.NewerOnly,
            _ => ConflictStrategy.Overwrite
        }
    };

    private async Task<bool> ConfirmDestructiveOperationAsync(
        CopyPlan plan,
        SyncPreview preview)
    {
        var destination = plan.Steps.Single().DestinationPath;
        var detailLines = preview.Items.Select(FormatPreviewItem).ToList();
        if (preview.IsTruncated)
        {
            detailLines.Add("…其余变更未在列表中显示");
        }

        var content = new StackPanel { Spacing = 10, MaxWidth = 620 };
        content.Children.Add(new TextBlock
        {
            Text =
                $"新增：{preview.FilesToAdd} 个文件、{preview.DirectoriesToAdd} 个文件夹\n" +
                $"更新：{preview.FilesToUpdate} 个文件\n" +
                $"删除：{preview.FilesToDelete} 个文件、{preview.DirectoriesToDelete} 个文件夹\n" +
                $"预计写入：{FormatBytes(preview.BytesToCopy)}；删除：{FormatBytes(preview.BytesToDelete)}",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = $"同步目标：\n{destination}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        if (detailLines.Count > 0)
        {
            content.Children.Add(new ScrollViewer
            {
                MaxHeight = 260,
                Content = new TextBlock
                {
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 12,
                    IsTextSelectionEnabled = true,
                    Text = string.Join(Environment.NewLine, detailLines),
                    TextWrapping = TextWrapping.NoWrap
                }
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "确认镜像同步",
            Content = content,
            PrimaryButtonText = "确认同步",
            CloseButtonText = "返回",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private void BeginRun()
    {
        _isRunning = true;
        _cancellation = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        DestinationBox.IsEnabled = false;
        ThreadCountBox.IsEnabled = false;
        RetryCountBox.IsEnabled = false;
        RestartableCheckBox.IsEnabled = false;
        ExcludeJunctionsCheckBox.IsEnabled = false;
        ConflictStrategyBox.IsEnabled = false;
        ProgressArea.Visibility = Visibility.Visible;
        LogExpander.Visibility = Visibility.Visible;
        LogBox.Text = string.Empty;
        TaskProgress.IsIndeterminate = true;
        TaskProgress.Value = 0;
        StatusText.Text = "正在准备任务…";
        ProgressDetailsText.Visibility = Visibility.Collapsed;
    }

    private void EndRun()
    {
        _cancellation?.Dispose();
        _cancellation = null;
        _isRunning = false;
        CancelButton.IsEnabled = false;
        DestinationBox.IsEnabled = true;
        ThreadCountBox.IsEnabled = true;
        RetryCountBox.IsEnabled = true;
        RestartableCheckBox.IsEnabled = true;
        ExcludeJunctionsCheckBox.IsEnabled = true;
        ConflictStrategyBox.IsEnabled = _operation != CopyOperation.Sync;
        UpdateStartButton();
    }

    private void UpdateStartButton()
    {
        StartButton.IsEnabled =
            !_isRunning &&
            !_draftEnqueued &&
            _hasTaskContext &&
            _sources.Count > 0 &&
            !string.IsNullOrWhiteSpace(DestinationBox.Text);
    }

    private void AppendLog(string line)
    {
        var updated = LogBox.Text + line + Environment.NewLine;
        if (updated.Length > MaximumLogCharacters)
        {
            updated = updated[^MaximumLogCharacters..];
        }

        LogBox.Text = updated;
    }

    private void LoadRecovery(CopyTask task)
    {
        DestinationBox.Text = task.Destination;
        ThreadCountBox.Value = task.Options.ThreadCount;
        RetryCountBox.Value = task.Options.RetryCount;
        RestartableCheckBox.IsChecked = task.Options.Restartable;
        ExcludeJunctionsCheckBox.IsChecked = task.Options.ExcludeJunctions;
        ConflictStrategyBox.SelectedIndex = task.Options.ConflictStrategy switch
        {
            ConflictStrategy.SkipExisting => 1,
            ConflictStrategy.NewerOnly => 2,
            _ => 0
        };
    }

    private async void OnRefreshQueueClicked(object sender, RoutedEventArgs e) =>
        await RefreshQueueAsync();

    private void OnQueueSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (QueueList.SelectedItem is not QueueTaskViewModel selected)
        {
            QueueDetailsBox.Text = string.Empty;
            UpdateQueueButtons(null);
            return;
        }

        QueueDetailsBox.Text = FormatQueueDetails(selected.Entry);
        UpdateQueueButtons(selected.Entry);
        var progress = GetProgress(selected.Entry);
        if (progress is not null)
        {
            DisplayProgress(progress, appendLog: false);
        }
    }

    private async void OnPauseQueueClicked(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is QueueTaskViewModel selected)
        {
            await ExecuteQueueActionAsync(
                selected.TaskId,
                token => _queueStore.RequestPauseAsync(selected.TaskId, token),
                "无法暂停任务",
                wakeWorker: false);
        }
    }

    private async void OnResumeQueueClicked(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is QueueTaskViewModel selected)
        {
            await ExecuteQueueActionAsync(
                selected.TaskId,
                token => _queueStore.ResumeAsync(selected.TaskId, token),
                "无法恢复任务",
                wakeWorker: true);
        }
    }

    private async void OnRetryQueueClicked(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is QueueTaskViewModel selected)
        {
            await ExecuteQueueActionAsync(
                selected.TaskId,
                token => _queueStore.RetryAsync(selected.TaskId, token),
                "无法重试任务",
                wakeWorker: true);
        }
    }

    private async void OnCancelQueueClicked(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is QueueTaskViewModel selected)
        {
            await ExecuteQueueActionAsync(
                selected.TaskId,
                token => _queueStore.RequestCancelAsync(selected.TaskId, token),
                "无法取消任务",
                wakeWorker: false);
        }
    }

    private async Task ExecuteQueueActionAsync(
        Guid taskId,
        Func<CancellationToken, Task<QueueTaskEntry>> action,
        string errorTitle,
        bool wakeWorker)
    {
        try
        {
            var updated = await action(CancellationToken.None);
            if (wakeWorker && updated.State == QueueTaskState.Pending)
            {
                try
                {
                    _workerLauncher.EnsureRunning();
                }
                catch (Exception exception)
                {
                    await ShowMessageAsync(
                        "后台进程未启动",
                        $"{exception.Message}\n\n任务已保留在队列中。");
                }
            }

            await RefreshQueueAsync(updated.TaskId);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(errorTitle, exception.Message);
            await RefreshQueueAsync(taskId);
        }
    }

    private async void OnQueueRefreshTick(
        DispatcherQueueTimer sender,
        object args) =>
        await RefreshQueueAsync();

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _queueRefreshTimer.Stop();
        _queueRefreshTimer.Tick -= OnQueueRefreshTick;
        DestinationBox.TextChanged -= OnDestinationChanged;
        QueueList.SelectionChanged -= OnQueueSelectionChanged;
        Closed -= OnWindowClosed;
    }

    private async Task RefreshQueueAsync(Guid? taskToSelect = null)
    {
        if (_isRefreshingQueue)
        {
            return;
        }

        _isRefreshingQueue = true;
        try
        {
            var selectedTaskId =
                taskToSelect ??
                (QueueList.SelectedItem as QueueTaskViewModel)?.TaskId;
            var entries = await _queueStore.ListAsync();
            _queueItems.Clear();
            foreach (var entry in entries.Take(200))
            {
                _queueItems.Add(new QueueTaskViewModel(
                    entry,
                    GetProgress(entry)));
            }

            QueueList.SelectedItem = selectedTaskId is null
                ? null
                : _queueItems.FirstOrDefault(
                    item => item.TaskId == selectedTaskId.Value);
            if (QueueList.SelectedItem is not QueueTaskViewModel selected)
            {
                QueueDetailsBox.Text = string.Empty;
                UpdateQueueButtons(null);
            }
            else
            {
                QueueDetailsBox.Text = FormatQueueDetails(selected.Entry);
                UpdateQueueButtons(selected.Entry);
                if (GetProgress(selected.Entry) is { } progress)
                {
                    DisplayProgress(progress, appendLog: false);
                }
            }
        }
        catch (Exception exception)
        {
            StartupInfo.Title = "无法读取任务队列";
            StartupInfo.Severity = InfoBarSeverity.Error;
            StartupInfo.Message = exception.Message;
            StartupInfo.IsOpen = true;
        }
        finally
        {
            _isRefreshingQueue = false;
        }
    }

    private CopyProgress? GetProgress(QueueTaskEntry entry)
    {
        if (entry.BytesCompleted is null &&
            entry.TotalBytes is null &&
            string.IsNullOrWhiteSpace(entry.CurrentMessage))
        {
            return null;
        }

        return new CopyProgress(
            Math.Min(entry.CompletedSteps + 1, Math.Max(1, entry.Task.Sources.Count)),
            Math.Max(1, entry.Task.Sources.Count),
            entry.CurrentMessage ?? StateText(entry.State),
            BytesCompleted: entry.BytesCompleted,
            TotalBytes: entry.TotalBytes,
            BytesPerSecond: entry.BytesPerSecond,
            EstimatedRemaining: entry.EstimatedRemainingSeconds is { } seconds
                ? TimeSpan.FromSeconds(Math.Max(0, seconds))
                : null);
    }

    private void DisplayProgress(
        CopyProgress progress,
        bool appendLog = true)
    {
        ProgressArea.Visibility = Visibility.Visible;
        LogExpander.Visibility = Visibility.Visible;
        StatusText.Text = progress.Message;

        if (progress.TotalBytes is > 0 && progress.BytesCompleted is { } completed)
        {
            TaskProgress.IsIndeterminate = false;
            TaskProgress.Value = Math.Clamp(
                completed * 100d / progress.TotalBytes.Value,
                0,
                100);
            ProgressDetailsText.Text =
                $"{FormatBytes(completed)} / {FormatBytes(progress.TotalBytes.Value)}" +
                FormatPerformance(progress);
            ProgressDetailsText.Visibility = Visibility.Visible;
        }
        else
        {
            TaskProgress.IsIndeterminate = true;
            ProgressDetailsText.Visibility = Visibility.Collapsed;
        }

        if (appendLog && !string.IsNullOrWhiteSpace(progress.Message))
        {
            AppendLog(progress.Message);
        }
    }

    private static string FormatPerformance(CopyProgress progress)
    {
        var parts = new List<string>();
        if (progress.BytesPerSecond is > 0)
        {
            parts.Add($"{FormatBytes((long)progress.BytesPerSecond.Value)}/s");
        }

        if (progress.EstimatedRemaining is { } remaining)
        {
            parts.Add($"剩余 {FormatDuration(remaining)}");
        }

        return parts.Count == 0
            ? string.Empty
            : $" · {string.Join(" · ", parts)}";
    }

    private void UpdateQueueButtons(QueueTaskEntry? entry)
    {
        PauseQueueButton.IsEnabled = entry?.State is
            QueueTaskState.Pending or
            QueueTaskState.Running or
            QueueTaskState.Interrupted;
        ResumeQueueButton.IsEnabled = entry?.State is
            QueueTaskState.Paused or
            QueueTaskState.Interrupted;
        RetryQueueButton.IsEnabled = entry?.State is
            QueueTaskState.Failed or
            QueueTaskState.Canceled;
        CancelQueueButton.IsEnabled = entry?.State is
            QueueTaskState.Pending or
            QueueTaskState.Running or
            QueueTaskState.PauseRequested or
            QueueTaskState.Paused or
            QueueTaskState.Interrupted or
            QueueTaskState.Failed;
    }

    private static string FormatQueueDetails(QueueTaskEntry entry)
    {
        var lines = new List<string>
        {
            $"任务：{entry.TaskId:D}",
            $"来源：{string.Join("；", entry.Task.Sources)}",
            $"目标：{entry.Task.Destination}",
            $"状态：{StateText(entry.State)}；尝试次数：{entry.AttemptCount}",
            $"创建：{entry.EnqueuedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
        };
        if (entry.NativeExitCode is { } exitCode)
        {
            lines.Add($"Robocopy 退出码：{exitCode}");
        }

        if (entry.FailureMessages.Count > 0)
        {
            lines.Add("失败摘要：");
            lines.AddRange(entry.FailureMessages.Take(20).Select(message => $"• {message}"));
        }

        if (!string.IsNullOrWhiteSpace(entry.Error))
        {
            lines.Add($"错误：{entry.Error}");
        }

        if (entry.LogPaths.Count > 0)
        {
            lines.Add("日志：");
            lines.AddRange(entry.LogPaths.Select(path => $"• {path}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string StateText(QueueTaskState state) => state switch
    {
        QueueTaskState.Pending => "等待中",
        QueueTaskState.Running => "执行中",
        QueueTaskState.PauseRequested => "正在暂停",
        QueueTaskState.Paused => "已暂停",
        QueueTaskState.CancelRequested => "正在取消",
        QueueTaskState.Completed => "已完成",
        QueueTaskState.CompletedWithDifferences => "完成（有差异）",
        QueueTaskState.Failed => "失败",
        QueueTaskState.Canceled => "已取消",
        QueueTaskState.Interrupted => "意外中断",
        _ => state.ToString()
    };

    private static string FormatDuration(TimeSpan duration)
    {
        duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}小时 {duration.Minutes}分钟";
        }

        return duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes}分钟 {duration.Seconds}秒"
            : $"{Math.Max(0, duration.Seconds)}秒";
    }

    private sealed class QueueTaskViewModel
    {
        public QueueTaskViewModel(
            QueueTaskEntry entry,
            CopyProgress? progress)
        {
            Entry = entry;
            Title = $"{OperationText(entry.Task.Operation)} · " +
                $"{entry.Task.Sources.Count} 个源项目";
            Route = $"{SourceSummary(entry.Task.Sources)} → {entry.Task.Destination}";
            StateText = MainWindow.StateText(entry.State);
            ProgressText = progress is { TotalBytes: > 0, BytesCompleted: { } completed }
                ? $"{FormatBytes(completed)} / {FormatBytes(progress.TotalBytes.Value)}" +
                  FormatPerformance(progress)
                : $"尝试 {entry.AttemptCount} 次 · " +
                  $"{entry.UpdatedAtUtc.ToLocalTime():MM-dd HH:mm:ss}";
        }

        public QueueTaskEntry Entry { get; }

        public Guid TaskId => Entry.TaskId;

        public string Title { get; }

        public string Route { get; }

        public string StateText { get; }

        public string ProgressText { get; }

        private static string OperationText(CopyOperation operation) => operation switch
        {
            CopyOperation.Copy => "复制",
            CopyOperation.Move => "移动",
            CopyOperation.Sync => "同步",
            _ => operation.ToString()
        };

        private static string SourceSummary(IReadOnlyList<string> sources)
        {
            var first = sources.Count > 0
                ? sources[0]
                : "未知来源";
            return sources.Count <= 1
                ? first
                : $"{first} 等 {sources.Count} 项";
        }
    }

    private static string FormatPreviewItem(SyncPreviewItem item)
    {
        var change = item.Change switch
        {
            SyncPreviewChange.Add => "+",
            SyncPreviewChange.Update => "~",
            SyncPreviewChange.Delete => "-",
            _ => "?"
        };
        var kind = item.ItemKind == CopySourceKind.Directory ? "[目录]" : "[文件]";
        return $"{change} {kind} {item.RelativePath}";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
