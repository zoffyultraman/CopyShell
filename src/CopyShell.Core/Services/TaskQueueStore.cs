using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CopyShell.Core.Abstractions;
using CopyShell.Core.Models;

namespace CopyShell.Core.Services;

public sealed class TaskQueueStore
{
    private const int MaximumEntries = 1000;
    private const long MaximumDocumentBytes = 16 * 1024 * 1024;
    private const int MaximumErrorCharacters = 4000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _queueDirectory;
    private readonly string _documentPath;
    private readonly string _mutexName;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _processGate = new(1, 1);

    public TaskQueueStore(
        string? queueDirectory = null,
        TimeProvider? timeProvider = null)
    {
        _queueDirectory = Path.GetFullPath(
            queueDirectory ?? GetDefaultQueueDirectory());
        _documentPath = Path.Combine(_queueDirectory, "queue.json");
        _mutexName = CreateMutexName(_queueDirectory);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static string GetDefaultQueueDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CopyShell",
            "Queue");

    public Task<IReadOnlyList<QueueTaskEntry>> ListAsync(
        CancellationToken cancellationToken = default) =>
        WithLockAsync<IReadOnlyList<QueueTaskEntry>>(
            document => document.Items
                .OrderByDescending(item => item.Sequence)
                .ToArray(),
            writeResult: false,
            cancellationToken);

    public Task<QueueTaskEntry?> GetAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        WithLockAsync(
            document => document.Items.FirstOrDefault(item => item.TaskId == taskId),
            writeResult: false,
            cancellationToken);

    public Task<QueueTaskEntry> EnqueueAsync(
        CopyTask task,
        string planHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (string.IsNullOrWhiteSpace(planHash))
        {
            throw new ArgumentException("命令计划哈希不能为空。", nameof(planHash));
        }

        return WithMutationAsync(
            document =>
            {
                if (document.Items.Any(item => item.TaskId == task.TaskId))
                {
                    throw new InvalidOperationException($"任务已在队列中：{task.TaskId:D}");
                }

                var now = _timeProvider.GetUtcNow();
                var entry = new QueueTaskEntry
                {
                    TaskId = task.TaskId,
                    Task = task,
                    PlanHash = planHash,
                    Sequence = document.NextSequence,
                    State = QueueTaskState.Pending,
                    EnqueuedAtUtc = now,
                    UpdatedAtUtc = now
                };
                var items = document.Items
                    .Append(entry)
                    .ToList();
                TrimHistory(items);
                return (
                    document with
                    {
                        NextSequence = document.NextSequence + 1,
                        Items = items
                    },
                    entry);
            },
            cancellationToken);
    }

    public Task<QueueTaskEntry?> TryClaimNextAsync(
        ProcessIdentity owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return WithMutationAsync(
            document =>
            {
                var pending = document.Items
                    .Where(item => item.State == QueueTaskState.Pending)
                    .OrderBy(item => item.Sequence)
                    .FirstOrDefault();
                if (pending is null)
                {
                    return (document, (QueueTaskEntry?)null);
                }

                var now = _timeProvider.GetUtcNow();
                var claimed = pending with
                {
                    State = QueueTaskState.Running,
                    UpdatedAtUtc = now,
                    StartedAtUtc = now,
                    FinishedAtUtc = null,
                    Owner = owner,
                    AttemptCount = pending.AttemptCount + 1,
                    NativeExitCode = null,
                    CompletedSteps = 0,
                    BytesCompleted = null,
                    TotalBytes = null,
                    BytesPerSecond = null,
                    EstimatedRemainingSeconds = null,
                    CurrentMessage = null,
                    LogPaths = [],
                    FailureMessages = [],
                    Error = null
                };
                return (
                    Replace(document, claimed),
                    claimed);
            },
            cancellationToken);
    }

    public Task<QueueTaskEntry> RequestPauseAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            taskId,
            entry => entry.State switch
            {
                QueueTaskState.Pending or QueueTaskState.Interrupted =>
                    entry with
                    {
                        State = QueueTaskState.Paused,
                        UpdatedAtUtc = _timeProvider.GetUtcNow(),
                        Owner = null
                    },
                QueueTaskState.Running =>
                    entry with
                    {
                        State = QueueTaskState.PauseRequested,
                        UpdatedAtUtc = _timeProvider.GetUtcNow()
                    },
                _ => entry
            },
            cancellationToken);

    public Task<QueueTaskEntry> RequestCancelAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            taskId,
            entry => entry.State switch
            {
                QueueTaskState.Running or QueueTaskState.PauseRequested =>
                    entry with
                    {
                        State = QueueTaskState.CancelRequested,
                        UpdatedAtUtc = _timeProvider.GetUtcNow()
                    },
                QueueTaskState.Pending or
                QueueTaskState.Paused or
                QueueTaskState.Interrupted or
                QueueTaskState.Failed =>
                    entry with
                    {
                        State = QueueTaskState.Canceled,
                        UpdatedAtUtc = _timeProvider.GetUtcNow(),
                        FinishedAtUtc = _timeProvider.GetUtcNow(),
                        Owner = null
                    },
                _ => entry
            },
            cancellationToken);

    public Task<QueueTaskEntry> ResumeAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        RequeueAsync(
            taskId,
            [QueueTaskState.Paused, QueueTaskState.Interrupted],
            cancellationToken);

    public Task<QueueTaskEntry> RetryAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        RequeueAsync(
            taskId,
            [QueueTaskState.Failed, QueueTaskState.Canceled],
            cancellationToken);

    public Task<QueueTaskEntry> MarkPausedAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            taskId,
            entry => entry with
            {
                State = QueueTaskState.Paused,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                Owner = null,
                Error = null
            },
            cancellationToken);

    public Task<QueueTaskEntry> MarkCanceledAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            taskId,
            entry => entry with
            {
                State = QueueTaskState.Canceled,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                FinishedAtUtc = _timeProvider.GetUtcNow(),
                Owner = null,
                Error = null
            },
            cancellationToken);

    public Task<QueueTaskEntry> RecordProgressAsync(
        Guid taskId,
        CopyProgress progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return UpdateAsync(
            taskId,
            entry => entry.State is
                QueueTaskState.Running or
                QueueTaskState.PauseRequested or
                QueueTaskState.CancelRequested
                    ? entry with
                    {
                        UpdatedAtUtc = _timeProvider.GetUtcNow(),
                        CompletedSteps = Math.Max(
                            entry.CompletedSteps,
                            Math.Max(0, progress.StepIndex - 1)),
                        BytesCompleted = progress.BytesCompleted ?? entry.BytesCompleted,
                        TotalBytes = progress.TotalBytes ?? entry.TotalBytes,
                        BytesPerSecond = progress.BytesPerSecond ?? entry.BytesPerSecond,
                        EstimatedRemainingSeconds =
                            progress.EstimatedRemaining?.TotalSeconds
                            ?? entry.EstimatedRemainingSeconds,
                        CurrentMessage = Truncate(
                            progress.Message,
                            maximumCharacters: 1000)
                    }
                    : entry,
            cancellationToken);
    }

    public Task<QueueTaskEntry> RecordResultAsync(
        Guid taskId,
        CopyExecutionResult result,
        IReadOnlyList<string>? failureMessages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var state = result.Outcome switch
        {
            CopyExecutionOutcome.Completed => QueueTaskState.Completed,
            CopyExecutionOutcome.CompletedWithDifferences =>
                QueueTaskState.CompletedWithDifferences,
            CopyExecutionOutcome.Failed => QueueTaskState.Failed,
            CopyExecutionOutcome.Canceled => QueueTaskState.Canceled,
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };

        return UpdateAsync(
            taskId,
            entry => entry with
            {
                State = state,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                FinishedAtUtc = _timeProvider.GetUtcNow(),
                Owner = null,
                NativeExitCode = result.NativeExitCode,
                CompletedSteps = result.CompletedSteps,
                BytesCompleted = result.Outcome is
                    CopyExecutionOutcome.Completed or
                    CopyExecutionOutcome.CompletedWithDifferences
                        ? entry.TotalBytes ?? entry.BytesCompleted
                        : entry.BytesCompleted,
                BytesPerSecond = null,
                EstimatedRemainingSeconds = result.Outcome is
                    CopyExecutionOutcome.Completed or
                    CopyExecutionOutcome.CompletedWithDifferences
                        ? 0
                        : null,
                CurrentMessage = null,
                LogPaths = result.LogPaths,
                FailureMessages = failureMessages ?? [],
                Error = null
            },
            cancellationToken);
    }

    public Task<QueueTaskEntry> RecordFailureAsync(
        Guid taskId,
        Exception failure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return UpdateAsync(
            taskId,
            entry => entry with
            {
                State = QueueTaskState.Failed,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                FinishedAtUtc = _timeProvider.GetUtcNow(),
                Owner = null,
                Error = Truncate(failure.ToString())
            },
            cancellationToken);
    }

    public Task<int> MarkOrphanedRunsInterruptedAsync(
        IProcessProbe processProbe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processProbe);
        return WithMutationAsync(
            document =>
            {
                var count = 0;
                var now = _timeProvider.GetUtcNow();
                var items = document.Items
                    .Select(entry =>
                    {
                        if (entry.State is not (
                                QueueTaskState.Running or
                                QueueTaskState.PauseRequested or
                                QueueTaskState.CancelRequested) ||
                            (entry.Owner is not null &&
                             processProbe.IsAlive(entry.Owner)))
                        {
                            return entry;
                        }

                        count++;
                        return entry with
                        {
                            State = QueueTaskState.Interrupted,
                            UpdatedAtUtc = now,
                            Owner = null,
                            Error = "执行进程意外退出，可恢复任务。"
                        };
                    })
                    .ToArray();
                return (document with { Items = items }, count);
            },
            cancellationToken);
    }

    private Task<QueueTaskEntry> RequeueAsync(
        Guid taskId,
        QueueTaskState[] allowedStates,
        CancellationToken cancellationToken) =>
        WithMutationAsync(
            document =>
            {
                var entry = FindRequired(document, taskId);
                if (!allowedStates.Contains(entry.State))
                {
                    return (document, entry);
                }

                var queued = entry with
                {
                    State = QueueTaskState.Pending,
                    Sequence = document.NextSequence,
                    UpdatedAtUtc = _timeProvider.GetUtcNow(),
                    FinishedAtUtc = null,
                    Owner = null,
                    NativeExitCode = null,
                    CompletedSteps = 0,
                    BytesCompleted = null,
                    TotalBytes = null,
                    BytesPerSecond = null,
                    EstimatedRemainingSeconds = null,
                    CurrentMessage = null,
                    LogPaths = [],
                    FailureMessages = [],
                    Error = null
                };
                return (
                    Replace(
                        document with { NextSequence = document.NextSequence + 1 },
                        queued),
                    queued);
            },
            cancellationToken);

    private Task<QueueTaskEntry> UpdateAsync(
        Guid taskId,
        Func<QueueTaskEntry, QueueTaskEntry> update,
        CancellationToken cancellationToken) =>
        WithMutationAsync(
            document =>
            {
                var entry = update(FindRequired(document, taskId));
                return (Replace(document, entry), entry);
            },
            cancellationToken);

    private async Task<T> WithMutationAsync<T>(
        Func<TaskQueueDocument, (TaskQueueDocument Document, T Result)> mutation,
        CancellationToken cancellationToken)
    {
        var mutationResult = await WithLockAsync(
            document =>
            {
                var (updated, result) = mutation(document);
                return new MutationResult<T>(updated, result);
            },
            writeResult: true,
            cancellationToken).ConfigureAwait(false);
        return mutationResult.Result;
    }

    private async Task<T> WithLockAsync<T>(
        Func<TaskQueueDocument, T> action,
        bool writeResult,
        CancellationToken cancellationToken)
    {
        await _processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var mutex = new Mutex(initiallyOwned: false, _mutexName);
                    var acquired = false;
                    try
                    {
                        try
                        {
                            acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
                        }
                        catch (AbandonedMutexException)
                        {
                            acquired = true;
                        }

                        if (!acquired)
                        {
                            throw new TimeoutException("等待 CopyShell 任务队列锁超时。");
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        var document = ReadDocument();
                        var result = action(document);
                        if (writeResult && result is IQueueMutation mutation)
                        {
                            WriteDocument(mutation.Document, cancellationToken);
                        }

                        return result;
                    }
                    finally
                    {
                        if (acquired)
                        {
                            mutex.ReleaseMutex();
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private TaskQueueDocument ReadDocument()
    {
        if (!File.Exists(_documentPath))
        {
            return new TaskQueueDocument();
        }

        var information = new FileInfo(_documentPath);
        if (information.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException("任务队列文件超过 16 MiB 限制。");
        }

        using var stream = new FileStream(
            _documentPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 8192);
        var document = JsonSerializer.Deserialize<TaskQueueDocument>(
            stream,
            SerializerOptions);
        if (document is null ||
            document.Version != TaskQueueDocument.CurrentVersion ||
            document.NextSequence < 1 ||
            document.Items.Any(item =>
                item.TaskId == Guid.Empty ||
                item.Task is null ||
                item.Task.TaskId != item.TaskId))
        {
            throw new InvalidDataException("任务队列文件无效或版本不受支持。");
        }

        return document;
    }

    private void WriteDocument(
        TaskQueueDocument document,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_queueDirectory);
        var temporaryPath = Path.Combine(
            _queueDirectory,
            $"queue.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 8192,
                FileOptions.WriteThrough))
            {
                cancellationToken.ThrowIfCancellationRequested();
                JsonSerializer.Serialize(stream, document, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _documentPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static QueueTaskEntry FindRequired(
        TaskQueueDocument document,
        Guid taskId) =>
        document.Items.FirstOrDefault(item => item.TaskId == taskId)
        ?? throw new InvalidOperationException($"任务不在队列中：{taskId:D}");

    private static TaskQueueDocument Replace(
        TaskQueueDocument document,
        QueueTaskEntry replacement) =>
        document with
        {
            Items = document.Items
                .Select(item => item.TaskId == replacement.TaskId
                    ? replacement
                    : item)
                .ToArray()
        };

    private static void TrimHistory(List<QueueTaskEntry> items)
    {
        if (items.Count <= MaximumEntries)
        {
            return;
        }

        var removable = items
            .Where(item => item.State is
                QueueTaskState.Completed or
                QueueTaskState.CompletedWithDifferences or
                QueueTaskState.Canceled or
                QueueTaskState.Failed)
            .OrderBy(item => item.Sequence)
            .Take(items.Count - MaximumEntries)
            .Select(item => item.TaskId)
            .ToHashSet();
        items.RemoveAll(item => removable.Contains(item.TaskId));
    }

    private static string CreateMutexName(string queueDirectory)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(queueDirectory)))[..24];
        return OperatingSystem.IsWindows()
            ? $@"Local\CopyShell.TaskQueue.{hash}"
            : $"CopyShell.TaskQueue.{hash}";
    }

    private static string Truncate(
        string value,
        int maximumCharacters = MaximumErrorCharacters) =>
        value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters];

    private interface IQueueMutation
    {
        TaskQueueDocument Document { get; }
    }

    private sealed record MutationResult<T>(
        TaskQueueDocument Document,
        T Result) : IQueueMutation;
}
