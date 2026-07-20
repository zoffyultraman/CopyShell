using CopyShell.Core.Models;

namespace CopyShell.Robocopy;

public sealed class RobocopyCommandFactory
{
    private readonly string _logRoot;

    public RobocopyCommandFactory(string? logRoot = null)
    {
        _logRoot = logRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CopyShell",
            "Logs");
    }

    public IReadOnlyList<RobocopyCommand> CreateCommands(CopyPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Steps
            .Select((step, index) => CreateCommand(plan, step, index))
            .ToArray();
    }

    private RobocopyCommand CreateCommand(CopyPlan plan, CopyStep step, int index)
    {
        var logDirectory = Path.Combine(
            _logRoot,
            DateTimeOffset.UtcNow.ToString("yyyy-MM"),
            plan.TaskId.ToString("D"));
        var logPath = Path.Combine(logDirectory, $"{index + 1:D3}-{Guid.NewGuid():N}.log");

        string sourceDirectory;
        string destinationDirectory;
        string? fileFilter = null;

        if (step.SourceKind == CopySourceKind.File)
        {
            sourceDirectory = Path.GetDirectoryName(step.SourcePath)
                ?? throw new InvalidOperationException($"无法确定源文件目录：{step.SourcePath}");
            destinationDirectory = Path.GetDirectoryName(step.DestinationPath)
                ?? throw new InvalidOperationException($"无法确定目标文件目录：{step.DestinationPath}");
            fileFilter = Path.GetFileName(step.SourcePath);
        }
        else
        {
            sourceDirectory = step.SourcePath;
            destinationDirectory = step.DestinationPath;
        }

        var arguments = new List<string>
        {
            sourceDirectory,
            destinationDirectory
        };

        if (fileFilter is not null)
        {
            arguments.Add(fileFilter);
        }

        switch (step.Operation)
        {
            case CopyOperation.Copy when step.SourceKind == CopySourceKind.Directory:
                arguments.Add("/E");
                break;
            case CopyOperation.Move when step.SourceKind == CopySourceKind.File:
                arguments.Add("/MOV");
                break;
            case CopyOperation.Move:
                arguments.Add("/E");
                arguments.Add("/MOVE");
                break;
            case CopyOperation.Sync:
                arguments.Add("/MIR");
                break;
        }

        AddCommonArguments(arguments, plan.Options, logPath);
        return new RobocopyCommand(arguments, logPath);
    }

    private static void AddCommonArguments(
        ICollection<string> arguments,
        CopyOptions options,
        string logPath)
    {
        arguments.Add("/COPY:DAT");
        arguments.Add("/DCOPY:DAT");
        arguments.Add($"/R:{options.RetryCount}");
        arguments.Add($"/W:{options.RetryWaitSeconds}");
        arguments.Add($"/MT:{options.ThreadCount}");
        arguments.Add("/BYTES");
        arguments.Add("/FP");
        arguments.Add("/NP");

        if (options.Restartable)
        {
            arguments.Add("/Z");
        }

        if (options.ExcludeJunctions)
        {
            arguments.Add("/XJ");
        }

        arguments.Add($"/UNILOG:{logPath}");
        arguments.Add("/TEE");
    }
}
