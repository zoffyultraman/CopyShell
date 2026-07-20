using System.Diagnostics;
using CopyShell.Core.Abstractions;
using CopyShell.Core.Models;

namespace CopyShell.Core.Services;

public sealed class TaskQueueProcessor
{
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ControlPollInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ProgressPersistenceInterval = TimeSpan.FromSeconds(1);

    private readonly TaskQueueStore _store;
    private readonly CopyTaskPlanner _planner;
    private readonly ICopyEngine _engine;
    private readonly IProcessProbe _processProbe;

    public TaskQueueProcessor(
        TaskQueueStore store,
        CopyTaskPlanner planner,
        ICopyEngine engine,
        IProcessProbe processProbe)
    {
        _store = store;
        _planner = planner;
        _engine = engine;
        _processProbe = processProbe;
    }

    public event Action<QueueTaskEntry>? TaskChanged;

    public event Action<Guid, CopyProgress>? ProgressChanged;

    public event Action<Exception>? ProcessorError;

    public Task RunAsync(CancellationToken cancellationToken) =>
        Task.Factory.StartNew(
            () => RunWithOwnership(cancellationToken, idleTimeout: null),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    public Task RunUntilIdleAsync(
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        if (idleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idleTimeout),
                "空闲退出时间必须大于零。");
        }

        return Task.Factory.StartNew(
            () => RunWithOwnership(cancellationToken, idleTimeout),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void RunWithOwnership(
        CancellationToken cancellationToken,
        TimeSpan? idleTimeout)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var mutex = new Mutex(
                initiallyOwned: false,
                OperatingSystem.IsWindows()
                    ? @"Local\CopyShell.TaskQueue.Runner"
                    : "CopyShell.TaskQueue.Runner");
            var acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(TimeSpan.Zero);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    if (idleTimeout is not null)
                    {
                        return;
                    }

                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
                    continue;
                }

                RunOwnerLoopAsync(cancellationToken, idleTimeout)
                    .GetAwaiter()
                    .GetResult();
                if (idleTimeout is not null)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ProcessorError?.Invoke(exception);
                if (idleTimeout is not null)
                {
                    throw;
                }

                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
            }
            finally
            {
                if (acquired)
                {
                    mutex.ReleaseMutex();
                }
            }
        }
    }

    private async Task RunOwnerLoopAsync(
        CancellationToken cancellationToken,
        TimeSpan? idleTimeout)
    {
        await _store.MarkOrphanedRunsInterruptedAsync(
            _processProbe,
            cancellationToken).ConfigureAwait(false);

        Stopwatch? idleStopwatch = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var entry = await _store.TryClaimNextAsync(
                _processProbe.GetCurrent(),
                cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                if (idleTimeout is not null)
                {
                    idleStopwatch ??= Stopwatch.StartNew();
                    if (idleStopwatch.Elapsed >= idleTimeout.Value)
                    {
                        return;
                    }
                }

                await Task.Delay(IdlePollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            idleStopwatch = null;
            TaskChanged?.Invoke(entry);
            await ExecuteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteEntryAsync(
        QueueTaskEntry entry,
        CancellationToken applicationCancellationToken)
    {
        CopyPlan plan;
        try
        {
            plan = _planner.CreatePlan(entry.Task);
            if (!plan.PlanHash.Equals(entry.PlanHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "任务命令计划已发生变化。为确保安全，请重新创建任务。");
            }
        }
        catch (Exception exception)
        {
            var failed = await _store.RecordFailureAsync(
                entry.TaskId,
                exception,
                CancellationToken.None).ConfigureAwait(false);
            TaskChanged?.Invoke(failed);
            return;
        }

        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            applicationCancellationToken);
        using var monitorCancellation = new CancellationTokenSource();
        using var progressPersistenceCancellation = new CancellationTokenSource();
        var controlRequestValue = (int)QueueControlRequest.None;
        var latestProgress = new ProgressSnapshot();
        var monitorTask = MonitorControlAsync(
            entry.TaskId,
            request =>
            {
                Volatile.Write(ref controlRequestValue, (int)request);
                executionCancellation.Cancel();
            },
            monitorCancellation.Token);
        var progressPersistenceTask = PersistProgressAsync(
            entry.TaskId,
            latestProgress,
            progressPersistenceCancellation.Token);

        try
        {
            var progress = new InlineProgress<CopyProgress>(
                update =>
                {
                    latestProgress.Set(update);
                    ProgressChanged?.Invoke(entry.TaskId, update);
                });
            var result = await _engine.ExecuteAsync(
                plan,
                progress,
                executionCancellation.Token).ConfigureAwait(false);
            if (latestProgress.Get() is { } finalProgress)
            {
                await _store.RecordProgressAsync(
                    entry.TaskId,
                    finalProgress,
                    CancellationToken.None).ConfigureAwait(false);
            }

            var completed = await _store.RecordResultAsync(
                entry.TaskId,
                result,
                result.Failures.Select(failure => failure.Message).ToArray(),
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            TaskChanged?.Invoke(completed);
        }
        catch (OperationCanceledException)
        {
            if (applicationCancellationToken.IsCancellationRequested)
            {
                return;
            }

            var controlRequest = (QueueControlRequest)Volatile.Read(
                ref controlRequestValue);
            var updated = controlRequest switch
            {
                QueueControlRequest.Pause => await _store.MarkPausedAsync(
                    entry.TaskId,
                    CancellationToken.None).ConfigureAwait(false),
                QueueControlRequest.Cancel => await _store.MarkCanceledAsync(
                    entry.TaskId,
                    CancellationToken.None).ConfigureAwait(false),
                _ => await _store.RecordFailureAsync(
                    entry.TaskId,
                    new InvalidOperationException("任务在没有控制请求的情况下被取消。"),
                    CancellationToken.None).ConfigureAwait(false)
            };
            TaskChanged?.Invoke(updated);
        }
        catch (Exception exception)
        {
            var failed = await _store.RecordFailureAsync(
                entry.TaskId,
                exception,
                CancellationToken.None).ConfigureAwait(false);
            TaskChanged?.Invoke(failed);
        }
        finally
        {
            monitorCancellation.Cancel();
            progressPersistenceCancellation.Cancel();
            try
            {
                await Task.WhenAll(
                    monitorTask,
                    progressPersistenceTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task MonitorControlAsync(
        Guid taskId,
        Action<QueueControlRequest> onRequest,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(ControlPollInterval, cancellationToken).ConfigureAwait(false);
            var entry = await _store.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
            var request = entry?.State switch
            {
                QueueTaskState.PauseRequested => QueueControlRequest.Pause,
                QueueTaskState.CancelRequested => QueueControlRequest.Cancel,
                _ => QueueControlRequest.None
            };
            if (request != QueueControlRequest.None)
            {
                onRequest(request);
                return;
            }
        }
    }

    private async Task PersistProgressAsync(
        Guid taskId,
        ProgressSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        CopyProgress? lastPersisted = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(
                ProgressPersistenceInterval,
                cancellationToken).ConfigureAwait(false);
            var current = snapshot.Get();
            if (current is null || ReferenceEquals(current, lastPersisted))
            {
                continue;
            }

            await _store.RecordProgressAsync(
                taskId,
                current,
                cancellationToken).ConfigureAwait(false);
            lastPersisted = current;
        }
    }

    private sealed class ProgressSnapshot
    {
        private CopyProgress? _value;

        public CopyProgress? Get() => Volatile.Read(ref _value);

        public void Set(CopyProgress value) => Volatile.Write(ref _value, value);
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private enum QueueControlRequest
    {
        None,
        Pause,
        Cancel
    }
}
