namespace CopyShell.Core.Models;

public enum QueueTaskState
{
    Pending,
    Running,
    PauseRequested,
    Paused,
    CancelRequested,
    Completed,
    CompletedWithDifferences,
    Failed,
    Canceled,
    Interrupted
}
