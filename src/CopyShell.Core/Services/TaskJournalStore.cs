using System.Text.Json;
using System.Text.Json.Serialization;
using CopyShell.Core.Abstractions;
using CopyShell.Core.Models;

namespace CopyShell.Core.Services;

public sealed class TaskJournalStore
{
    private const long MaximumEntryBytes = 1024 * 1024;
    private const int MaximumErrorCharacters = 4000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _journalDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly IProcessProbe _processProbe;

    public TaskJournalStore(
        string? journalDirectory = null,
        TimeProvider? timeProvider = null,
        IProcessProbe? processProbe = null)
    {
        _journalDirectory = Path.GetFullPath(
            journalDirectory ?? GetDefaultJournalDirectory());
        _timeProvider = timeProvider ?? TimeProvider.System;
        _processProbe = processProbe ?? new PhysicalProcessProbe();
    }

    public static string GetDefaultJournalDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CopyShell",
            "Tasks");

    public async Task BeginAsync(
        CopyTask task,
        string planHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (string.IsNullOrWhiteSpace(planHash))
        {
            throw new ArgumentException("命令计划哈希不能为空。", nameof(planHash));
        }

        var now = _timeProvider.GetUtcNow();
        var existing = await TryReadAsync(task.TaskId, cancellationToken).ConfigureAwait(false);
        var entry = new TaskJournalEntry
        {
            TaskId = task.TaskId,
            Task = task,
            PlanHash = planHash,
            State = TaskRunState.Running,
            CreatedAtUtc = existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now,
            Owner = _processProbe.GetCurrent()
        };
        await WriteAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordResultAsync(
        Guid taskId,
        CopyExecutionResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var state = result.Outcome switch
        {
            CopyExecutionOutcome.Completed => TaskRunState.Completed,
            CopyExecutionOutcome.CompletedWithDifferences => TaskRunState.CompletedWithDifferences,
            CopyExecutionOutcome.Failed => TaskRunState.Failed,
            CopyExecutionOutcome.Canceled => TaskRunState.Canceled,
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };

        await UpdateAsync(
            taskId,
            entry => entry with
            {
                State = state,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                Owner = null,
                NativeExitCode = result.NativeExitCode,
                CompletedSteps = result.CompletedSteps,
                Error = null
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task RecordFailureAsync(
        Guid taskId,
        string error,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            taskId,
            entry => entry with
            {
                State = TaskRunState.Failed,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                Owner = null,
                Error = Truncate(error)
            },
            cancellationToken);

    public Task RecordCanceledAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            taskId,
            entry => entry with
            {
                State = TaskRunState.Canceled,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                Owner = null,
                Error = null
            },
            cancellationToken);

    public async Task<int> MarkOrphanedRunsInterruptedAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_journalDirectory))
        {
            return 0;
        }

        var interrupted = 0;
        foreach (var path in Directory.EnumerateFiles(_journalDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = await TryReadPathAsync(path, cancellationToken).ConfigureAwait(false);
            if (entry is not { State: TaskRunState.Running } ||
                entry.Owner is not null && _processProbe.IsAlive(entry.Owner))
            {
                continue;
            }

            await WriteAsync(
                entry with
                {
                    State = TaskRunState.Interrupted,
                    UpdatedAtUtc = _timeProvider.GetUtcNow(),
                    Owner = null,
                    Error = "上次运行意外中断，可重新执行任务。"
                },
                cancellationToken).ConfigureAwait(false);
            interrupted++;
        }

        return interrupted;
    }

    public async Task<TaskJournalEntry?> GetLatestInterruptedAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_journalDirectory))
        {
            return null;
        }

        TaskJournalEntry? latest = null;
        foreach (var path in Directory.EnumerateFiles(_journalDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = await TryReadPathAsync(path, cancellationToken).ConfigureAwait(false);
            if (entry is { State: TaskRunState.Interrupted } &&
                (latest is null || entry.UpdatedAtUtc > latest.UpdatedAtUtc))
            {
                latest = entry;
            }
        }

        return latest;
    }

    private async Task UpdateAsync(
        Guid taskId,
        Func<TaskJournalEntry, TaskJournalEntry> update,
        CancellationToken cancellationToken)
    {
        var entry = await TryReadAsync(taskId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"找不到任务运行记录：{taskId:D}");
        await WriteAsync(update(entry), cancellationToken).ConfigureAwait(false);
    }

    private Task<TaskJournalEntry?> TryReadAsync(
        Guid taskId,
        CancellationToken cancellationToken) =>
        TryReadPathAsync(GetEntryPath(taskId), cancellationToken);

    private static async Task<TaskJournalEntry?> TryReadPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var information = new FileInfo(path);
            if (!information.Exists || information.Length > MaximumEntryBytes)
            {
                return null;
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);
            var entry = await JsonSerializer.DeserializeAsync<TaskJournalEntry>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            if (entry is null ||
                entry.Version != TaskJournalEntry.CurrentVersion ||
                entry.TaskId == Guid.Empty ||
                entry.Task is null ||
                entry.Task.TaskId != entry.TaskId)
            {
                return null;
            }

            return entry;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task WriteAsync(
        TaskJournalEntry entry,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_journalDirectory);
        var destination = GetEntryPath(entry.TaskId);
        var temporary = Path.Combine(
            _journalDirectory,
            $"{entry.TaskId:D}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    entry,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private string GetEntryPath(Guid taskId) =>
        Path.Combine(_journalDirectory, $"{taskId:D}.json");

    private static string Truncate(string error) =>
        string.IsNullOrEmpty(error) || error.Length <= MaximumErrorCharacters
            ? error
            : error[..MaximumErrorCharacters];
}
