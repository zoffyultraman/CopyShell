using CopyShell.Core.Protocol;
using CopyShell.Core.Models;
using CopyShell.Core.Services;
using Microsoft.UI.Xaml;

namespace CopyShell.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ShellRequest? request = null;
        TaskJournalEntry? recovery = null;
        string? startupMessage = null;
        var startupMessageIsError = false;
        var journal = new TaskJournalStore();
        var queueStore = new TaskQueueStore();
        var processProbe = new PhysicalProcessProbe();
        var workerLauncher = new BackgroundWorkerLauncher();

        try
        {
            queueStore
                .MarkOrphanedRunsInterruptedAsync(processProbe)
                .GetAwaiter()
                .GetResult();
            var hasPendingTasks = queueStore
                .ListAsync()
                .GetAwaiter()
                .GetResult()
                .Any(entry => entry.State == QueueTaskState.Pending);
            if (hasPendingTasks)
            {
                workerLauncher.EnsureRunning();
            }

            var interruptedCount = journal
                .MarkOrphanedRunsInterruptedAsync()
                .GetAwaiter()
                .GetResult();
            var requestStore = new ShellRequestStore();
            requestStore.DeleteStaleRequests(TimeSpan.FromHours(24));
            var requestPath = FindRequestPath(Environment.GetCommandLineArgs());
            if (requestPath is not null)
            {
                request = requestStore
                    .ReadAndDeleteAsync(requestPath)
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                recovery = journal
                    .GetLatestInterruptedAsync()
                    .GetAwaiter()
                    .GetResult();
                if (recovery is not null)
                {
                    startupMessage = "检测到上次意外中断的任务。确认参数后可以重新执行。";
                }
                else
                {
                    startupMessage = "请在资源管理器中选择文件或文件夹，然后使用 CopyShell 右键菜单。";
                    startupMessageIsError = true;
                }
            }

            if (request is not null && interruptedCount > 0)
            {
                startupMessage =
                    $"检测到 {interruptedCount} 个意外中断的任务。稍后可直接启动 CopyShell 恢复最近一次任务。";
            }
        }
        catch (Exception exception)
        {
            startupMessage = exception.Message;
            startupMessageIsError = true;
        }

        _window = new MainWindow(
            request,
            recovery,
            queueStore,
            workerLauncher,
            startupMessage,
            startupMessageIsError);
        _window.Activate();
    }

    private static string? FindRequestPath(IReadOnlyList<string> arguments)
    {
        for (var index = 1; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals("--request", StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }
}
