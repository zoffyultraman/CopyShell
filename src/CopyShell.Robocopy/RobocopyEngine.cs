using System.Diagnostics;
using System.Globalization;
using System.Text;
using CopyShell.Core.Abstractions;
using CopyShell.Core.Models;

namespace CopyShell.Robocopy;

public sealed class RobocopyEngine : ICopyEngine
{
    private readonly RobocopyCommandFactory _commandFactory;

    public RobocopyEngine(RobocopyCommandFactory commandFactory)
    {
        _commandFactory = commandFactory;
    }

    public string Id => "robocopy";

    public async Task<CopyExecutionResult> ExecuteAsync(
        CopyPlan plan,
        IProgress<CopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        var commands = _commandFactory.CreateCommands(plan);
        var combinedExitCode = 0;
        var completedSteps = 0;
        var logPaths = new List<string>(commands.Count);

        for (var index = 0; index < commands.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = commands[index];
            logPaths.Add(command.LogPath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(command.LogPath)
                ?? throw new InvalidOperationException("无法创建 Robocopy 日志目录。"));

            progress?.Report(new CopyProgress(
                index + 1,
                commands.Count,
                $"正在处理第 {index + 1}/{commands.Count} 项…",
                command.LogPath));

            var exitCode = await ExecuteCommandAsync(
                command,
                index,
                commands.Count,
                progress,
                cancellationToken).ConfigureAwait(false);
            combinedExitCode |= exitCode;
            completedSteps++;

            if (RobocopyExitCodeInterpreter.GetOutcome(exitCode) == CopyExecutionOutcome.Failed)
            {
                break;
            }
        }

        return new CopyExecutionResult(
            RobocopyExitCodeInterpreter.GetOutcome(combinedExitCode),
            combinedExitCode,
            completedSteps,
            logPaths);
    }

    private static async Task<int> ExecuteCommandAsync(
        RobocopyCommand command,
        int stepIndex,
        int stepCount,
        IProgress<CopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var outputEncoding = Encoding.GetEncoding(
            CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "robocopy.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 robocopy.exe。");
        }

        var outputTask = PumpOutputAsync(
            process.StandardOutput,
            stepIndex,
            stepCount,
            command.LogPath,
            progress,
            isError: false);
        var errorTask = PumpOutputAsync(
            process.StandardError,
            stepIndex,
            stepCount,
            command.LogPath,
            progress,
            isError: true);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task PumpOutputAsync(
        StreamReader reader,
        int stepIndex,
        int stepCount,
        string logPath,
        IProgress<CopyProgress>? progress,
        bool isError)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            progress?.Report(new CopyProgress(
                stepIndex + 1,
                stepCount,
                isError ? $"[错误] {line}" : line,
                logPath));
        }
    }
}
