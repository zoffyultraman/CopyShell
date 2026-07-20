using CopyShell.Core.Abstractions;
using CopyShell.Core.Models;

namespace CopyShell.Core.Services;

public sealed class FileSystemSyncPreviewService : ISyncPreviewService
{
    private const int MaximumPreviewItems = 500;
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public Task<SyncPreview> CreateAsync(
        CopyPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Operation != CopyOperation.Sync ||
            plan.Steps is not [{ SourceKind: CopySourceKind.Directory }])
        {
            throw new ArgumentException("同步预览只接受单个文件夹同步计划。", nameof(plan));
        }

        return Task.Run(
            () => CreateCore(plan, cancellationToken),
            cancellationToken);
    }

    private static SyncPreview CreateCore(
        CopyPlan plan,
        CancellationToken cancellationToken)
    {
        var step = plan.Steps[0];
        var sourceItems = EnumerateTree(
            step.SourcePath,
            plan.Options.ExcludeJunctions,
            cancellationToken);
        var targetItems = Directory.Exists(step.DestinationPath)
            ? EnumerateTree(
                step.DestinationPath,
                plan.Options.ExcludeJunctions,
                cancellationToken)
            : new Dictionary<string, TreeItem>(PathComparer);

        var additions = new List<SyncPreviewItem>();
        var deletions = new List<SyncPreviewItem>();
        var filesToAdd = 0;
        var filesToUpdate = 0;
        var filesToDelete = 0;
        var directoriesToAdd = 0;
        var directoriesToDelete = 0;
        long bytesToCopy = 0;
        long bytesToDelete = 0;

        foreach (var (relativePath, source) in sourceItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!targetItems.TryGetValue(relativePath, out var target))
            {
                AddSourceChange(source, SyncPreviewChange.Add);
                continue;
            }

            if (source.Kind != target.Kind)
            {
                AddTargetDeletion(target);
                AddSourceChange(source, SyncPreviewChange.Add);
                continue;
            }

            if (source.Kind == CopySourceKind.File &&
                (source.Size != target.Size ||
                 source.LastWriteTimeUtc != target.LastWriteTimeUtc))
            {
                filesToUpdate++;
                bytesToCopy += source.Size;
                AddDetail(
                    additions,
                    new SyncPreviewItem(
                        SyncPreviewChange.Update,
                        CopySourceKind.File,
                        relativePath,
                        source.Size));
            }
        }

        foreach (var (relativePath, target) in targetItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sourceItems.ContainsKey(relativePath))
            {
                AddTargetDeletion(target);
            }
        }

        var totalChanges =
            filesToAdd +
            filesToUpdate +
            filesToDelete +
            directoriesToAdd +
            directoriesToDelete;
        var details = deletions
            .Concat(additions)
            .Take(MaximumPreviewItems)
            .ToArray();

        return new SyncPreview(
            filesToAdd,
            filesToUpdate,
            filesToDelete,
            directoriesToAdd,
            directoriesToDelete,
            bytesToCopy,
            bytesToDelete,
            details,
            totalChanges > details.Length);

        void AddSourceChange(TreeItem item, SyncPreviewChange change)
        {
            if (item.Kind == CopySourceKind.File)
            {
                filesToAdd++;
                bytesToCopy += item.Size;
            }
            else
            {
                directoriesToAdd++;
            }

            AddDetail(
                additions,
                new SyncPreviewItem(change, item.Kind, item.RelativePath, item.Size));
        }

        void AddTargetDeletion(TreeItem item)
        {
            if (item.Kind == CopySourceKind.File)
            {
                filesToDelete++;
                bytesToDelete += item.Size;
            }
            else
            {
                directoriesToDelete++;
            }

            AddDetail(
                deletions,
                new SyncPreviewItem(
                    SyncPreviewChange.Delete,
                    item.Kind,
                    item.RelativePath,
                    item.Size));
        }
    }

    private static Dictionary<string, TreeItem> EnumerateTree(
        string root,
        bool excludeJunctions,
        CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, TreeItem>(PathComparer);
        var pending = new Stack<string>();
        pending.Push(root);

        try
        {
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(path);
                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
                    if (isDirectory && isReparsePoint && excludeJunctions)
                    {
                        continue;
                    }

                    var relativePath = Path.GetRelativePath(root, path);
                    if (isDirectory)
                    {
                        items.Add(
                            relativePath,
                            new TreeItem(
                                relativePath,
                                CopySourceKind.Directory,
                                0,
                                DateTime.MinValue));
                        pending.Push(path);
                    }
                    else
                    {
                        var information = new FileInfo(path);
                        items.Add(
                            relativePath,
                            new TreeItem(
                                relativePath,
                                CopySourceKind.File,
                                information.Length,
                                information.LastWriteTimeUtc));
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"无法读取同步预览目录：{root}",
                exception);
        }

        return items;
    }

    private static void AddDetail(
        ICollection<SyncPreviewItem> items,
        SyncPreviewItem item)
    {
        if (items.Count < MaximumPreviewItems)
        {
            items.Add(item);
        }
    }

    private sealed record TreeItem(
        string RelativePath,
        CopySourceKind Kind,
        long Size,
        DateTime LastWriteTimeUtc);
}
