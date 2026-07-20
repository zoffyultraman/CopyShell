using CopyShell.Core.Abstractions;
using CopyShell.Core.Models;
using CopyShell.Core.Services;

namespace CopyShell.Core.Tests;

[TestFixture]
public sealed class TaskQueueStoreTests
{
    private string _root = null!;
    private MutableTimeProvider _time = null!;
    private TaskQueueStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            $"CopyShell.Queue.Tests.{Guid.NewGuid():N}");
        _time = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        _store = new TaskQueueStore(_root, _time);
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
    public async Task TryClaimNextAsync_ClaimsTasksInQueueOrder()
    {
        var first = CreateTask("first.txt");
        var second = CreateTask("second.txt");
        await _store.EnqueueAsync(first, "FIRST");
        await _store.EnqueueAsync(second, "SECOND");

        var claimed = await _store.TryClaimNextAsync(
            new ProcessIdentity(1234, _time.GetUtcNow()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(claimed, Is.Not.Null);
            Assert.That(claimed!.TaskId, Is.EqualTo(first.TaskId));
            Assert.That(claimed.State, Is.EqualTo(QueueTaskState.Running));
            Assert.That(claimed.AttemptCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task PauseAndResume_RequeuesTaskAtEnd()
    {
        var first = CreateTask("first.txt");
        var second = CreateTask("second.txt");
        var firstEntry = await _store.EnqueueAsync(first, "FIRST");
        var secondEntry = await _store.EnqueueAsync(second, "SECOND");

        var paused = await _store.RequestPauseAsync(first.TaskId);
        var resumed = await _store.ResumeAsync(first.TaskId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(paused.State, Is.EqualTo(QueueTaskState.Paused));
            Assert.That(resumed.State, Is.EqualTo(QueueTaskState.Pending));
            Assert.That(resumed.Sequence, Is.GreaterThan(secondEntry.Sequence));
            Assert.That(resumed.Sequence, Is.GreaterThan(firstEntry.Sequence));
        }
    }

    [Test]
    public async Task RunningTask_PersistsProgressAndFailureSummary()
    {
        var task = CreateTask("source.bin");
        await _store.EnqueueAsync(task, "PLAN");
        await _store.TryClaimNextAsync(
            new ProcessIdentity(1234, _time.GetUtcNow()));

        await _store.RecordProgressAsync(
            task.TaskId,
            new CopyProgress(
                1,
                1,
                "正在复制",
                BytesCompleted: 256,
                TotalBytes: 1024,
                BytesPerSecond: 128,
                EstimatedRemaining: TimeSpan.FromSeconds(6)));
        var failed = await _store.RecordResultAsync(
            task.TaskId,
            new CopyExecutionResult(
                CopyExecutionOutcome.Failed,
                8,
                0,
                ["copy.log"]),
            ["拒绝访问：source.bin"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed.State, Is.EqualTo(QueueTaskState.Failed));
            Assert.That(failed.BytesCompleted, Is.EqualTo(256));
            Assert.That(failed.TotalBytes, Is.EqualTo(1024));
            Assert.That(failed.NativeExitCode, Is.EqualTo(8));
            Assert.That(failed.FailureMessages, Has.Count.EqualTo(1));
            Assert.That(failed.LogPaths, Is.EqualTo(new[] { "copy.log" }));
        }
    }

    [Test]
    public async Task MarkOrphanedRunsInterruptedAsync_RecoversDeadRunner()
    {
        var task = CreateTask("source.txt");
        var owner = new ProcessIdentity(1234, _time.GetUtcNow());
        await _store.EnqueueAsync(task, "PLAN");
        await _store.TryClaimNextAsync(owner);

        var count = await _store.MarkOrphanedRunsInterruptedAsync(
            new FakeProcessProbe(isAlive: false));
        var recovered = await _store.GetAsync(task.TaskId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.EqualTo(1));
            Assert.That(recovered, Is.Not.Null);
            Assert.That(recovered!.State, Is.EqualTo(QueueTaskState.Interrupted));
            Assert.That(recovered.Owner, Is.Null);
        }
    }

    private CopyTask CreateTask(string fileName) =>
        CopyTask.Create(
            CopyOperation.Copy,
            [Path.Combine(_root, fileName)],
            Path.Combine(_root, "destination"));

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeProcessProbe(bool isAlive) : IProcessProbe
    {
        public ProcessIdentity GetCurrent() =>
            new(Environment.ProcessId, DateTimeOffset.UtcNow);

        public bool IsAlive(ProcessIdentity identity) => isAlive;
    }
}
