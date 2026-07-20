using CopyShell.Core.Models;
using CopyShell.Core.Services;

namespace CopyShell.Core.Tests;

[TestFixture]
public sealed class FileSystemSyncPreviewServiceTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CopyShell.Preview.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task CreateAsync_ReportsAddsUpdatesAndDeletes()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "new.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(source, "updated.txt"), "new content");
        var newDirectory = Directory.CreateDirectory(Path.Combine(source, "new-folder"));
        await File.WriteAllTextAsync(Path.Combine(newDirectory.FullName, "child.txt"), "child");

        await File.WriteAllTextAsync(Path.Combine(target, "updated.txt"), "old");
        File.SetLastWriteTimeUtc(
            Path.Combine(target, "updated.txt"),
            DateTime.UtcNow.AddDays(-1));
        await File.WriteAllTextAsync(Path.Combine(target, "delete.txt"), "delete");
        var oldDirectory = Directory.CreateDirectory(Path.Combine(target, "old-folder"));
        await File.WriteAllTextAsync(Path.Combine(oldDirectory.FullName, "old.txt"), "old");

        var plan = new CopyTaskPlanner(new PhysicalFileSystemProbe()).CreatePlan(
            CopyTask.Create(CopyOperation.Sync, [source], target));

        var preview = await new FileSystemSyncPreviewService().CreateAsync(
            plan,
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.FilesToAdd, Is.EqualTo(2));
            Assert.That(preview.FilesToUpdate, Is.EqualTo(1));
            Assert.That(preview.FilesToDelete, Is.EqualTo(2));
            Assert.That(preview.DirectoriesToAdd, Is.EqualTo(1));
            Assert.That(preview.DirectoriesToDelete, Is.EqualTo(1));
            Assert.That(
                preview.Items.Any(
                    item => item.Change == SyncPreviewChange.Delete &&
                            item.RelativePath.EndsWith("delete.txt")),
                Is.True);
        }
    }
}
