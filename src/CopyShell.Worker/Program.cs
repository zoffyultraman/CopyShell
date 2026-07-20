using CopyShell.Core.Models;
using CopyShell.Core.Services;
using CopyShell.Robocopy;

namespace CopyShell.Worker;

internal static class Program
{
    private static readonly TimeSpan IdleExitTimeout = TimeSpan.FromSeconds(20);

    public static int Main(string[] arguments)
    {
        if (arguments.Any(argument =>
                argument.Equals(
                    "--health-check",
                    StringComparison.OrdinalIgnoreCase)))
        {
            var robocopyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "robocopy.exe");
            return File.Exists(robocopyPath) ? 0 : 2;
        }

        using var workerLock = new Mutex(
            initiallyOwned: false,
            BackgroundWorkerProtocol.WorkerMutexName);
        var ownsLock = false;
        EventWaitHandle? wakeEvent = null;
        try
        {
            try
            {
                ownsLock = workerLock.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                ownsLock = true;
            }

            if (!ownsLock)
            {
                return 0;
            }

            wakeEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                BackgroundWorkerProtocol.WakeEventName);
            using var shutdown = new CancellationTokenSource();
            void RequestShutdown(object? sender, EventArgs args) =>
                shutdown.Cancel();

            AppDomain.CurrentDomain.ProcessExit += RequestShutdown;
            try
            {
                var store = new TaskQueueStore();
                var processor = new TaskQueueProcessor(
                    store,
                    new CopyTaskPlanner(new PhysicalFileSystemProbe()),
                    new RobocopyEngine(new RobocopyCommandFactory()),
                    new PhysicalProcessProbe());
                processor.TaskChanged += entry =>
                    WorkerLog.Write(
                        $"任务 {entry.TaskId:D}：{entry.State}");
                processor.ProcessorError += exception =>
                    WorkerLog.Write($"队列处理错误：{exception}");

                WorkerLog.Write("后台任务进程已启动。");
                while (!shutdown.IsCancellationRequested)
                {
                    processor.RunUntilIdleAsync(
                        IdleExitTimeout,
                        shutdown.Token)
                        .GetAwaiter()
                        .GetResult();

                    using var coordination = new Mutex(
                        initiallyOwned: false,
                        BackgroundWorkerProtocol.CoordinationMutexName);
                    var ownsCoordination = false;
                    try
                    {
                        try
                        {
                            ownsCoordination = coordination.WaitOne(
                                TimeSpan.FromSeconds(5));
                        }
                        catch (AbandonedMutexException)
                        {
                            ownsCoordination = true;
                        }

                        if (!ownsCoordination)
                        {
                            continue;
                        }

                        var wasWoken = wakeEvent.WaitOne(TimeSpan.Zero);
                        var hasPendingTasks = store
                            .ListAsync(shutdown.Token)
                            .GetAwaiter()
                            .GetResult()
                            .Any(entry => entry.State == QueueTaskState.Pending);
                        if (wasWoken || hasPendingTasks)
                        {
                            continue;
                        }

                        wakeEvent.Dispose();
                        wakeEvent = null;
                        workerLock.ReleaseMutex();
                        ownsLock = false;
                        break;
                    }
                    finally
                    {
                        if (ownsCoordination)
                        {
                            coordination.ReleaseMutex();
                        }
                    }
                }

                WorkerLog.Write("队列已空闲，后台任务进程退出。");
                return 0;
            }
            finally
            {
                AppDomain.CurrentDomain.ProcessExit -= RequestShutdown;
            }
        }
        catch (OperationCanceledException)
        {
            WorkerLog.Write("后台任务进程正在关闭。");
            return 0;
        }
        catch (Exception exception)
        {
            WorkerLog.Write($"后台任务进程异常退出：{exception}");
            return 1;
        }
        finally
        {
            wakeEvent?.Dispose();
            if (ownsLock)
            {
                workerLock.ReleaseMutex();
            }
        }
    }
}
