using CopyShell.Core.Models;

namespace CopyShell.Robocopy;

internal static class RobocopyWorkloadEstimator
{
    public static RobocopyWorkload Estimate(
        CopyPlan plan,
        CancellationToken cancellationToken)
    {
        var stepBytes = plan.Steps
            .Select(step => EstimateStep(step, plan.Options, cancellationToken))
            .ToArray();
        return new RobocopyWorkload(stepBytes);
    }

    private static long EstimateStep(
        CopyStep step,
        CopyOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (step.SourceKind == CopySourceKind.File)
            {
                var source = new FileInfo(step.SourcePath);
                return ShouldCopy(
                    source,
                    step.DestinationPath,
                    options.ConflictStrategy,
                    step.Operation)
                    ? source.Length
                    : 0;
            }

            long total = 0;
            var pending = new Stack<string>();
            pending.Push(step.SourcePath);
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(path);
                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
                    if (isDirectory)
                    {
                        if (!isReparsePoint || !options.ExcludeJunctions)
                        {
                            pending.Push(path);
                        }
                        continue;
                    }

                    var relativePath = Path.GetRelativePath(step.SourcePath, path);
                    var destinationPath = Path.Combine(step.DestinationPath, relativePath);
                    var source = new FileInfo(path);
                    if (ShouldCopy(
                            source,
                            destinationPath,
                            options.ConflictStrategy,
                            step.Operation))
                    {
                        total = AddSaturating(total, source.Length);
                    }
                }
            }

            return total;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool ShouldCopy(
        FileInfo source,
        string destinationPath,
        ConflictStrategy strategy,
        CopyOperation operation)
    {
        var destination = new FileInfo(destinationPath);
        if (!destination.Exists)
        {
            return true;
        }

        if (operation == CopyOperation.Sync)
        {
            return source.Length != destination.Length ||
                   source.LastWriteTimeUtc != destination.LastWriteTimeUtc;
        }

        return strategy switch
        {
            ConflictStrategy.Overwrite =>
                source.Length != destination.Length ||
                source.LastWriteTimeUtc != destination.LastWriteTimeUtc,
            ConflictStrategy.SkipExisting => false,
            ConflictStrategy.NewerOnly =>
                source.LastWriteTimeUtc > destination.LastWriteTimeUtc,
            _ => true
        };
    }

    private static long AddSaturating(long left, long right) =>
        left > long.MaxValue - right
            ? long.MaxValue
            : left + right;
}
