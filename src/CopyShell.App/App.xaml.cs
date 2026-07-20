using CopyShell.Core.Protocol;
using CopyShell.Core.Models;
using CopyShell.Core.Services;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace CopyShell.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        UnhandledException += OnUnhandledException;
        try
        {
            AppDiagnostics.Write(
                $"Application initialization started. " +
                $"OS={Environment.OSVersion}; BaseDirectory={AppContext.BaseDirectory}");
            InitializeComponent();
            AppDiagnostics.Write("Application resources initialized.");
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteException(
                "Application resource initialization failed",
                exception);
            if (!HasArgument(
                    Environment.GetCommandLineArgs(),
                    "--health-check"))
            {
                ShowFatalStartupError(exception);
            }
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppDiagnostics.Write("Application launch started.");
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

        try
        {
            AppDiagnostics.Write("Main window creation started.");
            _window = new MainWindow(
                request,
                recovery,
                queueStore,
                workerLauncher,
                startupMessage,
                startupMessageIsError);
            _window.Activate();
            AppDiagnostics.Write("Main window activated.");

            if (HasArgument(
                    Environment.GetCommandLineArgs(),
                    "--health-check"))
            {
                AppDiagnostics.Write("Application health check succeeded.");
                Environment.Exit(0);
            }
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteException(
                "Main window creation or activation failed",
                exception);
            if (HasArgument(
                    Environment.GetCommandLineArgs(),
                    "--health-check"))
            {
                Environment.Exit(1);
            }

            ShowFatalStartupError(exception);
            Environment.Exit(1);
        }
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

    private static bool HasArgument(
        IReadOnlyList<string> arguments,
        string expected) =>
        arguments.Any(
            argument => argument.Equals(
                expected,
                StringComparison.OrdinalIgnoreCase));

    private static void OnUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args) =>
        AppDiagnostics.WriteException(
            "Unhandled WinUI exception",
            args.Exception);

    private static void ShowFatalStartupError(Exception exception)
    {
        var message =
            "CopyShell could not create its window." +
            Environment.NewLine +
            Environment.NewLine +
            exception.Message +
            Environment.NewLine +
            Environment.NewLine +
            $"Diagnostic log: {AppDiagnostics.CurrentLogPath}";
        MessageBoxW(
            IntPtr.Zero,
            message,
            "CopyShell startup error",
            0x00000010);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(
        IntPtr window,
        string text,
        string caption,
        uint type);
}
