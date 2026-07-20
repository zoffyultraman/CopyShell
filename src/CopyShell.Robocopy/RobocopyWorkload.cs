namespace CopyShell.Robocopy;

internal sealed record RobocopyWorkload(
    IReadOnlyList<long> StepBytes)
{
    public long TotalBytes => StepBytes.Aggregate(
        0L,
        (total, value) => total > long.MaxValue - value
            ? long.MaxValue
            : total + value);
}
