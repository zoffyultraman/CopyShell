using System.Diagnostics;
using CopyShell.Core.Services;

namespace CopyShell.App;

public sealed class BackgroundWorkerLauncher
{
    private readonly string _workerPath;

    public BackgroundWorkerLauncher(string? workerPath = null)
    {
        _workerPath = Path.GetFullPath(
            workerPath ??
            Path.Combine(AppContext.BaseDirectory, "CopyShell.Worker.exe"));
    }

    public void EnsureRunning()
    {
        if (!File.Exists(_workerPath))
        {
            throw new FileNotFoundException(
                "找不到 CopyShell 后台任务进程，请重新安装应用。",
                _workerPath);
        }

        using var coordination = new Mutex(
            initiallyOwned: false,
            BackgroundWorkerProtocol.CoordinationMutexName);
        var ownsCoordination = false;
        try
        {
            try
            {
                ownsCoordination = coordination.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                ownsCoordination = true;
            }

            if (!ownsCoordination)
            {
                throw new TimeoutException("等待后台任务进程协调锁超时。");
            }

            try
            {
                using var wakeEvent = EventWaitHandle.OpenExisting(
                    BackgroundWorkerProtocol.WakeEventName);
                wakeEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _workerPath,
                WorkingDirectory = Path.GetDirectoryName(_workerPath)
                    ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "无法启动 CopyShell 后台任务进程。");
        }
        finally
        {
            if (ownsCoordination)
            {
                coordination.ReleaseMutex();
            }
        }
    }
}
