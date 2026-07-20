namespace CopyShell.Core.Models;

public sealed record CopyProgress(
    int StepIndex,
    int StepCount,
    string Message,
    string? LogPath = null);
