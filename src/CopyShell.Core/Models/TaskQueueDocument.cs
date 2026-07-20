namespace CopyShell.Core.Models;

public sealed record TaskQueueDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public long NextSequence { get; init; } = 1;

    public IReadOnlyList<QueueTaskEntry> Items { get; init; } = [];
}
