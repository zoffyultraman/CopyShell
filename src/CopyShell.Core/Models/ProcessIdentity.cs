namespace CopyShell.Core.Models;

public sealed record ProcessIdentity(
    int ProcessId,
    DateTimeOffset StartedAtUtc);
