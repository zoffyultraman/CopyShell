namespace CopyShell.Core.Models;

public sealed record CopyFailure(
    string Message,
    string? Path = null,
    int? NativeErrorCode = null);
