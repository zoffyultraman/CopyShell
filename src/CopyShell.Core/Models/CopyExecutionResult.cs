namespace CopyShell.Core.Models;

public sealed record CopyExecutionResult(
    CopyExecutionOutcome Outcome,
    int NativeExitCode,
    int CompletedSteps,
    IReadOnlyList<string> LogPaths);
