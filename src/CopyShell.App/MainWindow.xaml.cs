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

    private readonly ShellRequest? _request;
    private readonly ObservableCollection<string> _sources = [];
    private readonly CopyTaskPlanner _planner = new(new PhysicalFileSystemProbe());
    private readonly ICopyEngine _engine = new RobocopyEngine(new RobocopyCommandFactory());
    private CancellationTokenSource? _cancellation;
    private bool _isRunning;

    public MainWindow(ShellRequest? request, string? startupError)
    {
        InitializeComponent();
        _request = request;
        SourceList.ItemsSource = _sources;

        foreach (var source in request?.Sources ?? [])
        {
            _sources.Add(source);
        }

        ConfigureWindow();
        ConfigureOperation(request?.Operation ?? CopyOperation.Copy);

        if (!string.IsNullOrWhiteSpace(startupError))
        {
            StartupInfo.Message = startupError;
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
        if (_isRunning || _request is null)
        {
            return;
        }

        CopyPlan plan;
        try
        {
            plan = _planner.CreatePlan(CopyTask.Create(
                _request.Operation,
                _request.Sources,
                DestinationBox.Text.Trim(),
                CreateOptions()));
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法开始任务", exception.Message);
            return;
        }

        if (plan.RiskLevel == RiskLevel.Destructive &&
            !await ConfirmDestructiveOperationAsync(plan))
        {
            return;
        }

        BeginRun();
        try
        {
            var progress = new Progress<CopyProgress>(OnProgress);
            var result = await _engine.ExecuteAsync(plan, progress, _cancellation!.Token);
            TaskProgress.IsIndeterminate = false;
            TaskProgress.Value = result.Outcome == CopyExecutionOutcome.Failed ? 0 : 100;
            StatusText.Text = RobocopyExitCodeInterpreter.Describe(result.NativeExitCode);

            await ShowMessageAsync(
                result.Outcome == CopyExecutionOutcome.Failed ? "任务失败" : "任务完成",
                $"{StatusText.Text}\n\nRobocopy 退出码：{result.NativeExitCode}");
        }
        catch (OperationCanceledException)
        {
            TaskProgress.IsIndeterminate = false;
            TaskProgress.Value = 0;
            StatusText.Text = "任务已取消。";
            AppendLog("[CopyShell] 任务已取消。");
        }
        catch (Exception exception)
        {
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
        ExcludeJunctions = ExcludeJunctionsCheckBox.IsChecked == true
    };

    private async Task<bool> ConfirmDestructiveOperationAsync(CopyPlan plan)
    {
        var destination = plan.Steps.Single().DestinationPath;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "确认镜像同步",
            Content =
                $"目标中源端不存在的文件和文件夹将被删除：\n\n{destination}\n\n请确认目标路径无误。",
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
        UpdateStartButton();
    }

    private void UpdateStartButton()
    {
        StartButton.IsEnabled =
            !_isRunning &&
            _request is not null &&
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
}
