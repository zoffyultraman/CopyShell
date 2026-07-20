using System.Text.Json;
using System.Text.Json.Serialization;
using CopyShell.Core.Models;
using CopyShell.Core.Protocol;
using CopyShell.Core.Services;
using CopyShell.Robocopy;

namespace CopyShell.IntegrationTests;

[TestFixture]
[NonParallelizable]
public sealed class ShellRequestToRobocopyTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Robocopy 端到端测试仅在 Windows 上运行。");
        }

        _root = Path.Combine(Path.GetTempPath(), $"CopyShell.Integration.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Request_Plan_Engine_CopiesDirectory()
    {
        var requestDirectory = Directory.CreateDirectory(
            Path.Combine(_root, "requests")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(source, "payload.txt"),
            "CopyShell integration");

        var now = DateTimeOffset.UtcNow;
        var request = new ShellRequest
        {
            Version = ShellRequestStore.CurrentVersion,
            RequestId = Guid.NewGuid(),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(10),
            Operation = CopyOperation.Copy,
            Sources = [source]
        };
        var requestPath = Path.Combine(
            requestDirectory,
            $"{request.RequestId:D}.json");
        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(
                request,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Converters =
                    {
                        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
                    }
                }));

        var loaded = await new ShellRequestStore(requestDirectory)
            .ReadAndDeleteAsync(requestPath);
        var task = CopyTask.Create(
            loaded.Operation,
            loaded.Sources,
            destination);
        var plan = new CopyTaskPlanner(new PhysicalFileSystemProbe()).CreatePlan(task);
        var result = await new RobocopyEngine(
                new RobocopyCommandFactory(Path.Combine(_root, "logs")))
            .ExecuteAsync(plan, progress: null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                result.Outcome,
                Is.AnyOf(
                    CopyExecutionOutcome.Completed,
                    CopyExecutionOutcome.CompletedWithDifferences));
            Assert.That(
                File.ReadAllText(Path.Combine(destination, "source", "payload.txt")),
                Is.EqualTo("CopyShell integration"));
            Assert.That(File.Exists(requestPath), Is.False);
        }
    }
}
