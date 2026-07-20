using System.Collections.ObjectModel;
using CopyShell.Core.Abstractions;
using CopyShell.Core.Models;
using CopyShell.Core.Protocol;
using CopyShell.Core.Services;
using CopyShell.Robocopy;
using Microsoft.UI;
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
    private readonly CopyTaskPlanner _planner = new(new PhysicalFileSystemProbe());
    private readonly ISyncPreviewService _previewService = new FileSystemSyncPreviewService();
    private readonly ICopyEngine _engine = new RobocopyEngine(new RobocopyCommandFactory());
    private readonly TaskJournalStore _journal;
    private CancellationTokenSource? _cancellation;
    private bool _isRunning;

    public MainWindow(
        ShellRequest? request,
        TaskJournalEntry? recovery,
        TaskJournalStore journal,
        string? startupMessage,
        bool startupMessageIsError)
    {
        InitializeComponent();
        _journal = journal;
        _hasTaskContext = request is not null || recovery is not null;
        _operation = request?.Operation ?? recovery?.Task.Operation ?? CopyOperation.Copy;
        _taskId = recovery?.Task.TaskId ?? Guid.NewGuid();
        SourceList.ItemsSource = _sources;

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

        UpdateStartButton();
    }

    private void ConfigureWindow()
    {
        Title = "CopyShell";
        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        AppWindow.GetFromWindowId(windowId).Resize(new SizeInt32(760, 760));
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
        var journalStarted = false;
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

            await _journal.BeginAsync(task, plan.PlanHash, _cancellation!.Token);
            journalStarted = true;
            var progress = new Progress<CopyProgress>(OnProgress);
            var result = await _engine.ExecuteAsync(plan, progress, _cancellation!.Token);
            await RecordResultSafelyAsync(result);
            journalStarted = false;
            TaskProgress.IsIndeterminate = false;
            TaskProgress.Value = result.Outcome == CopyExecutionOutcome.Failed ? 0 : 100;
            StatusText.Text = RobocopyExitCodeInterpreter.Describe(result.NativeExitCode);

            await ShowMessageAsync(
                result.Outcome == CopyExecutionOutcome.Failed ? "任务失败" : "任务完成",
                $"{StatusText.Text}\n\nRobocopy 退出码：{result.NativeExitCode}");
        }
        catch (OperationCanceledException)
        {
            if (journalStarted)
            {
                await RecordCanceledSafelyAsync();
            }

            TaskProgress.IsIndeterminate = false;
            TaskProgress.Value = 0;
            StatusText.Text = "任务已取消。";
            AppendLog("[CopyShell] 任务已取消。");
        }
        catch (Exception exception)
        {
            if (journalStarted)
            {
                await RecordFailureSafelyAsync(exception);
            }

            TaskProgress.IsIndeterminate = false;
            TaskProgress.Value = 0;
            StatusText.Text = "任务执行失败。";
            AppendLog($"[CopyShell] {exception}");
            await ShowMessageAsync("任务失败", exception.Message);
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
        StatusText.Text = "正在启动 Robocopy…";
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
            _hasTaskContext &&
            _sources.Count > 0 &&
            !string.IsNullOrWhiteSpace(DestinationBox.Text);
    }

    private void OnProgress(CopyProgress progress)
    {
        StatusText.Text = progress.Message;
        AppendLog(progress.Message);
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

    private async Task RecordResultSafelyAsync(CopyExecutionResult result)
    {
        try
        {
            await _journal.RecordResultAsync(
                _taskId,
                result,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            AppendLog($"[CopyShell] 无法保存任务结果：{exception.Message}");
        }
    }

    private async Task RecordCanceledSafelyAsync()
    {
        try
        {
            await _journal.RecordCanceledAsync(_taskId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            AppendLog($"[CopyShell] 无法保存取消状态：{exception.Message}");
        }
    }

    private async Task RecordFailureSafelyAsync(Exception failure)
    {
        try
        {
            await _journal.RecordFailureAsync(
                _taskId,
                failure.ToString(),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            AppendLog($"[CopyShell] 无法保存失败状态：{exception.Message}");
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
