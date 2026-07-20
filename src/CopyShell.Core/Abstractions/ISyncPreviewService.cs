using CopyShell.Core.Models;

namespace CopyShell.Core.Abstractions;

public interface ISyncPreviewService
{
    Task<SyncPreview> CreateAsync(
        CopyPlan plan,
        CancellationToken cancellationToken);
}
