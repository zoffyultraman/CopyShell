namespace CopyShell.Core.Models;

public sealed record QueueTaskEntry
{
    public required Guid TaskId { get; init; }

    public required CopyTask Task { get; init; }

    public required string PlanHash { get; init; }

    public required long Sequence { get; init; }

    public required QueueTaskState State { get; init; }

    public required DateTimeOffset EnqueuedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? FinishedAtUtc { get; init; }

    public ProcessIdentity? Owner { get; init; }

    public int AttemptCount { get; init; }

    public int? NativeExitCode { get; init; }

    public int CompletedSteps { get; init; }

    public long? BytesCompleted { get; init; }

    public long? TotalBytes { get; init; }

    public double? BytesPerSecond { get; init; }

    public double? EstimatedRemainingSeconds { get; init; }

    public string? CurrentMessage { get; init; }

    public IReadOnlyList<string> LogPaths { get; init; } = [];

    public IReadOnlyList<string> FailureMessages { get; init; } = [];

    public string? Error { get; init; }
}
