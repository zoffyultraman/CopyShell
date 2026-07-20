using System.Text.Json;
using System.Text.Json.Serialization;
using CopyShell.Core.Models;
using CopyShell.Core.Protocol;

namespace CopyShell.Protocol.Tests;

[TestFixture]
public sealed class ShellRequestStoreTests
{
    private string _root = null!;
    private DateTimeOffset _now;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CopyShell.Protocol.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _now = new DateTimeOffset(2026, 7, 20, 8, 30, 0, TimeSpan.Zero);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task ReadAndDeleteAsync_ConsumesValidRequest()
    {
        var source = Path.GetFullPath(Path.Combine(_root, "source.txt"));
        var request = new ShellRequest
        {
            Version = 1,
            RequestId = Guid.NewGuid(),
            CreatedAtUtc = _now,
            ExpiresAtUtc = _now.AddMinutes(10),
            Operation = CopyOperation.Copy,
            Sources = [source],
            Invoker = new ShellRequestInvoker { Name = "test", Version = "1.0" }
        };
        var path = await WriteRequestAsync(request);
        var store = new ShellRequestStore(_root, new FixedTimeProvider(_now));

        var loaded = await store.ReadAndDeleteAsync(path);

        Assert.Multiple(() =>
        {
            Assert.That(loaded.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(loaded.Operation, Is.EqualTo(CopyOperation.Copy));
            Assert.That(File.Exists(path), Is.False);
            Assert.That(
                File.Exists(Path.Combine(_root, $"{request.RequestId:D}.processing")),
                Is.False);
        });
    }

    [Test]
    public void ReadAndDeleteAsync_RejectsExpiredRequest()
    {
        var request = new ShellRequest
        {
            Version = 1,
            RequestId = Guid.NewGuid(),
            CreatedAtUtc = _now.AddMinutes(-20),
            ExpiresAtUtc = _now.AddMinutes(-10),
            Operation = CopyOperation.Copy,
            Sources = [Path.GetFullPath(Path.Combine(_root, "source.txt"))]
        };
        var path = WriteRequestAsync(request).GetAwaiter().GetResult();
        var store = new ShellRequestStore(_root, new FixedTimeProvider(_now));

        Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.ReadAndDeleteAsync(path));
    }

    [Test]
    public void ReadAndDeleteAsync_RejectsNullSources()
    {
        var request = new ShellRequest
        {
            Version = 1,
            RequestId = Guid.NewGuid(),
            CreatedAtUtc = _now,
            ExpiresAtUtc = _now.AddMinutes(10),
            Operation = CopyOperation.Copy,
            Sources = null!
        };
        var path = WriteRequestAsync(request).GetAwaiter().GetResult();
        var store = new ShellRequestStore(_root, new FixedTimeProvider(_now));

        Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.ReadAndDeleteAsync(path));
    }

    private async Task<string> WriteRequestAsync(ShellRequest request)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var path = Path.Combine(_root, $"{request.RequestId:D}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request, options));
        return path;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
