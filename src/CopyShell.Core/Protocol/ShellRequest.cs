using System.Text.Json.Serialization;
using CopyShell.Core.Models;

namespace CopyShell.Core.Protocol;

public sealed record ShellRequest
{
    [JsonRequired]
    public int Version { get; init; }

    [JsonRequired]
    public Guid RequestId { get; init; }

    [JsonRequired]
    public DateTimeOffset CreatedAtUtc { get; init; }

    [JsonRequired]
    public DateTimeOffset ExpiresAtUtc { get; init; }

    [JsonRequired]
    public CopyOperation Operation { get; init; }

    [JsonRequired]
    public IReadOnlyList<string> Sources { get; init; } = [];

    public ShellRequestInvoker? Invoker { get; init; }
}

public sealed record ShellRequestInvoker
{
    public string Name { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;
}
