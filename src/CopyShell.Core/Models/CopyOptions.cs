namespace CopyShell.Core.Models;

public sealed record CopyOptions
{
    public int RetryCount { get; init; } = 2;

    public int RetryWaitSeconds { get; init; } = 2;

    public int ThreadCount { get; init; } = 16;

    public bool Restartable { get; init; } = true;

    public bool ExcludeJunctions { get; init; } = true;
}
