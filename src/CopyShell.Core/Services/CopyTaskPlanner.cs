using System.Security.Cryptography;
using System.Text;
using CopyShell.Core.Abstractions;
using CopyShell.Core.Exceptions;
using CopyShell.Core.Models;

namespace CopyShell.Core.Services;

public sealed class CopyTaskPlanner
{
    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
    private readonly IFileSystemProbe _fileSystem;

    public CopyTaskPlanner(IFileSystemProbe fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public CopyPlan CreatePlan(CopyTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(task.Options);
        ValidateOptions(task.Options);

        if (task.Sources is null || task.Sources.Count == 0)
        {
            throw new CopyTaskValidationException("至少需要选择一个源文件或文件夹。");
        }

        if (string.IsNullOrWhiteSpace(task.Destination))
        {
            throw new CopyTaskValidationException("请选择目标文件夹。");
        }

        if (task.Operation == CopyOperation.Sync && task.Sources.Count != 1)
        {
            throw new CopyTaskValidationException("同步操作一次只能选择一个源文件夹。");
        }

        if (task.Operation == CopyOperation.Sync &&
            task.Options.ConflictStrategy != ConflictStrategy.Overwrite)
        {
            throw new CopyTaskValidationException("同步操作必须使用覆盖冲突策略。");
        }

        var destination = Normalize(task.Destination);
        if (_fileSystem.FileExists(destination))
        {
            throw new CopyTaskValidationException("目标路径必须是文件夹，不能是文件。");
        }

        var steps = new List<CopyStep>(task.Sources.Count);
        var effectiveDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawSource in task.Sources)
        {
            var source = Normalize(rawSource);
            var sourceKind = GetSourceKind(source);
            var effectiveDestination = GetEffectiveDestination(
                source,
                sourceKind,
                destination,
                task.Operation);

            ValidatePathRelationship(source, destination, effectiveDestination, sourceKind, task.Operation);

            if (!effectiveDestinations.Add(effectiveDestination))
            {
                throw new CopyTaskValidationException(
                    $"多个源项目将写入同一目标位置：{effectiveDestination}");
            }

            steps.Add(new CopyStep(source, sourceKind, effectiveDestination, task.Operation));
        }

        var warnings = new List<string>();
        var riskLevel = RiskLevel.Normal;
        if (task.Operation == CopyOperation.Sync)
        {
            ValidateSyncDestination(destination);
            riskLevel = RiskLevel.Destructive;
            warnings.Add("同步会删除目标中源端不存在的文件和文件夹。");
        }

        return new CopyPlan(
            task.TaskId,
            task.Operation,
            task.Options,
            steps,
            riskLevel,
            warnings,
            ComputePlanHash(task, steps));
    }

    private CopySourceKind GetSourceKind(string source)
    {
        if (_fileSystem.FileExists(source))
        {
            return CopySourceKind.File;
        }

        if (_fileSystem.DirectoryExists(source))
        {
            return CopySourceKind.Directory;
        }

        throw new CopyTaskValidationException($"源路径不存在：{source}");
    }

    private static string GetEffectiveDestination(
        string source,
        CopySourceKind sourceKind,
        string selectedDestination,
        CopyOperation operation)
    {
        if (operation == CopyOperation.Sync)
        {
            if (sourceKind != CopySourceKind.Directory)
            {
                throw new CopyTaskValidationException("同步操作的源必须是文件夹。");
            }

            return selectedDestination;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CopyTaskValidationException("不支持直接复制或移动驱动器根目录。");
        }

        return Path.Combine(selectedDestination, name);
    }

    private static void ValidatePathRelationship(
        string source,
        string selectedDestination,
        string effectiveDestination,
        CopySourceKind sourceKind,
        CopyOperation operation)
    {
        if (source.Equals(effectiveDestination, PathComparison))
        {
            throw new CopyTaskValidationException("源路径与有效目标路径不能相同。");
        }

        if (sourceKind != CopySourceKind.Directory)
        {
            return;
        }

        if (IsSameOrChild(selectedDestination, source))
        {
            throw new CopyTaskValidationException("目标文件夹不能位于源文件夹内部。");
        }

        if (operation == CopyOperation.Sync && IsSameOrChild(source, selectedDestination))
        {
            throw new CopyTaskValidationException("同步操作不允许源和目标存在目录重叠。");
        }
    }

    private static void ValidateSyncDestination(string destination)
    {
        var root = Path.GetPathRoot(destination);
        if (!string.IsNullOrEmpty(root) &&
            Path.TrimEndingDirectorySeparator(root).Equals(
                Path.TrimEndingDirectorySeparator(destination),
                PathComparison))
        {
            throw new CopyTaskValidationException("禁止将驱动器或共享根目录作为同步目标。");
        }

        var protectedTrees = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };

        if (protectedTrees
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.TrimEndingDirectorySeparator)
            .Any(path => IsSameOrChild(destination, path)))
        {
            throw new CopyTaskValidationException("禁止将系统或程序目录作为同步目标。");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile) &&
            destination.Equals(
                Path.TrimEndingDirectorySeparator(userProfile),
                PathComparison))
        {
            throw new CopyTaskValidationException("禁止将用户配置文件根目录作为同步目标。");
        }
    }

    private static void ValidateOptions(CopyOptions options)
    {
        if (options.ThreadCount is < 1 or > 128)
        {
            throw new CopyTaskValidationException("并行线程数必须在 1 到 128 之间。");
        }

        if (options.RetryCount is < 0 or > 100)
        {
            throw new CopyTaskValidationException("重试次数必须在 0 到 100 之间。");
        }

        if (options.RetryWaitSeconds is < 0 or > 3600)
        {
            throw new CopyTaskValidationException("重试等待时间必须在 0 到 3600 秒之间。");
        }
    }

    private string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new CopyTaskValidationException("路径不能为空。");
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(_fileSystem.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CopyTaskValidationException($"路径无效：{path}");
        }
    }

    private static bool IsSameOrChild(string candidate, string parent)
    {
        if (candidate.Equals(parent, PathComparison))
        {
            return true;
        }

        var parentWithSeparator = Path.EndsInDirectorySeparator(parent)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(parentWithSeparator, PathComparison);
    }

    private static string ComputePlanHash(CopyTask task, IReadOnlyList<CopyStep> steps)
    {
        var canonical = new StringBuilder()
            .Append(task.TaskId).Append('|')
            .Append(task.Operation).Append('|')
            .Append(task.Options.ConflictStrategy).Append('|')
            .Append(task.Options.RetryCount).Append('|')
            .Append(task.Options.RetryWaitSeconds).Append('|')
            .Append(task.Options.ThreadCount).Append('|')
            .Append(task.Options.Restartable).Append('|')
            .Append(task.Options.ExcludeJunctions);

        foreach (var step in steps)
        {
            canonical
                .Append('|').Append(step.SourcePath.ToUpperInvariant())
                .Append('|').Append(step.DestinationPath.ToUpperInvariant())
                .Append('|').Append(step.SourceKind);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
