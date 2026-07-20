namespace CopyShell.Core.Models;

public sealed record TaskJournalEntry
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public required Guid TaskId { get; init; }

    public required CopyTask Task { get; init; }

    public required string PlanHash { get; init; }

    public required TaskRunState State { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public ProcessIdentity? Owner { get; init; }

    public int? NativeExitCode { get; init; }

    public int CompletedSteps { get; init; }

    public string? Error { get; init; }
}
