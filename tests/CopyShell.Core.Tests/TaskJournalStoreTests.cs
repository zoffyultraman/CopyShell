using System.Text.Json;
using CopyShell.Core.Abstractions;
using CopyShell.Core.Models;
using CopyShell.Core.Services;

namespace CopyShell.Core.Tests;

[TestFixture]
public sealed class TaskJournalStoreTests
{
    private string _root = null!;
    private DateTimeOffset _now;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CopyShell.Journal.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _now = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task MarkOrphanedRunsInterruptedAsync_RecoversDeadOwner()
    {
        var task = new CopyTask(
            Guid.NewGuid(),
            CopyOperation.Copy,
            [Path.Combine(_root, "source.txt")],
            Path.Combine(_root, "target"),
            new CopyOptions());
        var owner = new ProcessIdentity(1234, _now.AddMinutes(-1));
        var runningStore = new TaskJournalStore(
            _root,
            new FixedTimeProvider(_now),
            new FakeProcessProbe(owner, isAlive: true));
        await runningStore.BeginAsync(task, "PLAN");

        var recoveryStore = new TaskJournalStore(
            _root,
            new FixedTimeProvider(_now.AddMinutes(1)),
            new FakeProcessProbe(
                new ProcessIdentity(5678, _now),
                isAlive: false));

        var count = await recoveryStore.MarkOrphanedRunsInterruptedAsync();
        var recovered = await recoveryStore.GetLatestInterruptedAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.EqualTo(1));
            Assert.That(recovered, Is.Not.Null);
            Assert.That(recovered!.TaskId, Is.EqualTo(task.TaskId));
            Assert.That(recovered.State, Is.EqualTo(TaskRunState.Interrupted));
        }
    }

    [Test]
    public async Task RecordResultAsync_AtomicallyStoresCompletion()
    {
        var task = new CopyTask(
            Guid.NewGuid(),
            CopyOperation.Copy,
            [Path.Combine(_root, "source.txt")],
            Path.Combine(_root, "target"),
            new CopyOptions());
        var store = new TaskJournalStore(
            _root,
            new FixedTimeProvider(_now),
            new FakeProcessProbe(
                new ProcessIdentity(1234, _now),
                isAlive: true));
        await store.BeginAsync(task, "PLAN");

        await store.RecordResultAsync(
            task.TaskId,
            new CopyExecutionResult(
                CopyExecutionOutcome.Completed,
                1,
                1,
                []));

        var path = Path.Combine(_root, $"{task.TaskId:D}.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.That(
            document.RootElement.GetProperty("state").GetString(),
            Is.EqualTo("completed"));
        Assert.That(
            Directory.EnumerateFiles(_root, "*.tmp"),
            Is.Empty);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeProcessProbe(
        ProcessIdentity current,
        bool isAlive) : IProcessProbe
    {
        public ProcessIdentity GetCurrent() => current;

        public bool IsAlive(ProcessIdentity identity) => isAlive;
    }
}
