namespace CopyShell.Core.Models;

public sealed record CopyTask(
    Guid TaskId,
    CopyOperation Operation,
    IReadOnlyList<string> Sources,
    string Destination,
    CopyOptions Options)
{
    public static CopyTask Create(
        CopyOperation operation,
        IReadOnlyList<string> sources,
        string destination,
        CopyOptions? options = null) =>
        new(Guid.NewGuid(), operation, sources, destination, options ?? new CopyOptions());
}
