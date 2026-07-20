using CopyShell.Core.Abstractions;
using CopyShell.Core.Models;
using CopyShell.Core.Services;

namespace CopyShell.Core.Tests;

[TestFixture]
public sealed class TaskQueueProcessorTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            $"CopyShell.Processor.Tests.{Guid.NewGuid():N}");
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
    public async Task RunUntilIdleAsync_ExecutesPendingTaskAndExits()
    {
        var source = Path.Combine(_root, "source.txt");
        File.WriteAllText(source, "CopyShell worker");
        var destination = Directory.CreateDirectory(
            Path.Combine(_root, "destination")).FullName;
        var task = CopyTask.Create(
            CopyOperation.Copy,
            [source],
            destination);
        var planner = new CopyTaskPlanner(new PhysicalFileSystemProbe());
        var plan = planner.CreatePlan(task);
        var store = new TaskQueueStore(Path.Combine(_root, "queue"));
        await store.EnqueueAsync(task, plan.PlanHash);
        var engine = new RecordingEngine();
        var processor = new TaskQueueProcessor(
            store,
            planner,
            engine,
            new PhysicalProcessProbe());

        await processor.RunUntilIdleAsync(
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        var completed = await store.GetAsync(task.TaskId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(engine.ExecutionCount, Is.EqualTo(1));
            Assert.That(completed, Is.Not.Null);
            Assert.That(completed!.State, Is.EqualTo(QueueTaskState.Completed));
            Assert.That(completed.Owner, Is.Null);
        }
    }

    private sealed class RecordingEngine : ICopyEngine
    {
        public int ExecutionCount { get; private set; }

        public string Id => "test";

        public Task<CopyExecutionResult> ExecuteAsync(
            CopyPlan plan,
            IProgress<CopyProgress>? progress,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            progress?.Report(new CopyProgress(
                1,
                plan.Steps.Count,
                "完成",
                BytesCompleted: 16,
                TotalBytes: 16,
                BytesPerSecond: 16,
                EstimatedRemaining: TimeSpan.Zero));
            return Task.FromResult(
                new CopyExecutionResult(
                    CopyExecutionOutcome.Completed,
                    1,
                    plan.Steps.Count,
                    []));
        }
    }
}
