namespace CopyShell.Core.Models;

public sealed record SyncPreviewItem(
    SyncPreviewChange Change,
    CopySourceKind ItemKind,
    string RelativePath,
    long Size);
