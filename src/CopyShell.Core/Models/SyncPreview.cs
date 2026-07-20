namespace CopyShell.Core.Models;

public sealed record SyncPreview(
    int FilesToAdd,
    int FilesToUpdate,
    int FilesToDelete,
    int DirectoriesToAdd,
    int DirectoriesToDelete,
    long BytesToCopy,
    long BytesToDelete,
    IReadOnlyList<SyncPreviewItem> Items,
    bool IsTruncated)
{
    public int TotalChanges =>
        FilesToAdd +
        FilesToUpdate +
        FilesToDelete +
        DirectoriesToAdd +
        DirectoriesToDelete;
}
