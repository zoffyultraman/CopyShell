using CopyShell.Core.Models;

namespace CopyShell.Core.Abstractions;

public interface ICopyEngine
{
    string Id { get; }

    Task<CopyExecutionResult> ExecuteAsync(
        CopyPlan plan,
        IProgress<CopyProgress>? progress,
        CancellationToken cancellationToken);
}
