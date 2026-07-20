namespace CopyShell.Core.Models;

public sealed record CopyStep(
    string SourcePath,
    CopySourceKind SourceKind,
    string DestinationPath,
    CopyOperation Operation);
