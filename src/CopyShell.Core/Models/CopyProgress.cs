namespace CopyShell.Core.Models;

public sealed record CopyProgress(
    int StepIndex,
    int StepCount,
    string Message,
    string? LogPath = null,
    long? BytesCompleted = null,
    long? TotalBytes = null,
    double? BytesPerSecond = null,
    TimeSpan? EstimatedRemaining = null);
