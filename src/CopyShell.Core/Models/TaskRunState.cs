namespace CopyShell.Core.Models;

public enum TaskRunState
{
    Running,
    Completed,
    CompletedWithDifferences,
    Failed,
    Canceled,
    Interrupted
}
