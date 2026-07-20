using CopyShell.Core.Exceptions;
using CopyShell.Core.Models;
using CopyShell.Core.Services;

namespace CopyShell.Core.Tests;

[TestFixture]
public sealed class CopyTaskPlannerTests
{
    private string _root = null!;
    private CopyTaskPlanner _planner = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CopyShell.Core.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _planner = new CopyTaskPlanner(new PhysicalFileSystemProbe());
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void CopyDirectory_PreservesTopLevelDirectoryName()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "源 文件夹")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(_root, "目标")).FullName;

        var plan = _planner.CreatePlan(CopyTask.Create(
            CopyOperation.Copy,
            [source],
            destination));

        Assert.That(plan.Steps, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Steps[0].SourceKind, Is.EqualTo(CopySourceKind.Directory));
            Assert.That(
                plan.Steps[0].DestinationPath,
                Is.EqualTo(Path.Combine(destination, "源 文件夹")));
            Assert.That(plan.RiskLevel, Is.EqualTo(RiskLevel.Normal));
        }
    }

    [Test]
    public void MoveFile_TargetsSelectedDirectoryWithSameFileName()
    {
        var source = Path.Combine(_root, "演示 文件.txt");
        File.WriteAllText(source, "CopyShell");
        var destination = Directory.CreateDirectory(Path.Combine(_root, "目标")).FullName;

        var plan = _planner.CreatePlan(CopyTask.Create(
            CopyOperation.Move,
            [source],
            destination));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Steps[0].SourceKind, Is.EqualTo(CopySourceKind.File));
            Assert.That(
                plan.Steps[0].DestinationPath,
                Is.EqualTo(Path.Combine(destination, "演示 文件.txt")));
        }
    }

    [Test]
    public void Sync_RejectsMoreThanOneSource()
    {
        var first = Directory.CreateDirectory(Path.Combine(_root, "A")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_root, "B")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(_root, "目标")).FullName;

        Assert.Throws<CopyTaskValidationException>(() =>
        {
            _planner.CreatePlan(CopyTask.Create(
                CopyOperation.Sync,
                [first, second],
                destination));
        });
    }

    [Test]
    public void Sync_RejectsNonOverwriteConflictStrategy()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "源")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(_root, "目标")).FullName;

        Assert.Throws<CopyTaskValidationException>(() =>
        {
            _planner.CreatePlan(CopyTask.Create(
                CopyOperation.Sync,
                [source],
                destination,
                new CopyOptions { ConflictStrategy = ConflictStrategy.SkipExisting }));
        });
    }

    [Test]
    public void Copy_RejectsDestinationInsideSource()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "源")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(source, "目标")).FullName;

        Assert.Throws<CopyTaskValidationException>(() =>
        {
            _planner.CreatePlan(CopyTask.Create(
                CopyOperation.Copy,
                [source],
                destination));
        });
    }

    [Test]
    public void Copy_RejectsDestinationThatIsAFile()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "源")).FullName;
        var destination = Path.Combine(_root, "目标.txt");
        File.WriteAllText(destination, "not a directory");

        Assert.Throws<CopyTaskValidationException>(() =>
        {
            _planner.CreatePlan(CopyTask.Create(
                CopyOperation.Copy,
                [source],
                destination));
        });
    }

    [Test]
    public void Sync_IsMarkedDestructive()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "源")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(_root, "目标")).FullName;

        var plan = _planner.CreatePlan(CopyTask.Create(
            CopyOperation.Sync,
            [source],
            destination));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.RiskLevel, Is.EqualTo(RiskLevel.Destructive));
            Assert.That(plan.Warnings, Is.Not.Empty);
        }
    }
}
