using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Collections.Concurrent;
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
        var workload = RobocopyWorkloadEstimator.Estimate(plan, cancellationToken);
        var commands = _commandFactory.CreateCommands(plan);
        var combinedExitCode = 0;
        var completedSteps = 0;
        long completedBytes = 0;
        var logPaths = new List<string>(commands.Count);
        var failures = new ConcurrentQueue<CopyFailure>();
        var stopwatch = Stopwatch.StartNew();

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
                command.LogPath,
                completedBytes,
                workload.TotalBytes,
                CalculateSpeed(completedBytes, stopwatch.Elapsed),
                CalculateRemaining(
                    completedBytes,
                    workload.TotalBytes,
                    stopwatch.Elapsed)));

            var exitCode = await ExecuteCommandAsync(
                command,
                index,
                commands.Count,
                completedBytes,
                workload.StepBytes[index],
                workload.TotalBytes,
                stopwatch,
                failures,
                progress,
                cancellationToken).ConfigureAwait(false);
            combinedExitCode |= exitCode;
            completedSteps++;

            if (RobocopyExitCodeInterpreter.GetOutcome(exitCode) == CopyExecutionOutcome.Failed)
            {
                if (failures.IsEmpty)
                {
                    failures.Enqueue(new CopyFailure(
                        $"Robocopy 执行失败，退出码：{exitCode}。"));
                }
                break;
            }

            completedBytes = Math.Min(
                workload.TotalBytes,
                AddSaturating(completedBytes, workload.StepBytes[index]));
            progress?.Report(new CopyProgress(
                index + 1,
                commands.Count,
                $"第 {index + 1}/{commands.Count} 项已完成。",
                command.LogPath,
                completedBytes,
                workload.TotalBytes,
                CalculateSpeed(completedBytes, stopwatch.Elapsed),
                CalculateRemaining(
                    completedBytes,
                    workload.TotalBytes,
                    stopwatch.Elapsed)));
        }

        return new CopyExecutionResult(
            RobocopyExitCodeInterpreter.GetOutcome(combinedExitCode),
            combinedExitCode,
            completedSteps,
            logPaths)
        {
            Failures = failures
                .DistinctBy(failure => failure.Message, StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToArray()
        };
    }

    private static async Task<int> ExecuteCommandAsync(
        RobocopyCommand command,
        int stepIndex,
        int stepCount,
        long completedBeforeStep,
        long stepBytes,
        long totalBytes,
        Stopwatch stopwatch,
        ConcurrentQueue<CopyFailure> failures,
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

        using var job = RobocopyProcessJob.Create();
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 robocopy.exe。");
        }

        try
        {
            job.Assign(process);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        RobocopyProcessMetrics.TryGetReadBytes(process, out var initialReadBytes);
        var outputTask = PumpOutputAsync(
            process.StandardOutput,
            stepIndex,
            stepCount,
            command.LogPath,
            progress,
            failures,
            isError: false);
        var errorTask = PumpOutputAsync(
            process.StandardError,
            stepIndex,
            stepCount,
            command.LogPath,
            progress,
            failures,
            isError: true);

        try
        {
            var waitTask = process.WaitForExitAsync(CancellationToken.None);
            while (!waitTask.IsCompleted)
            {
                var intervalTask = Task.Delay(
                    TimeSpan.FromMilliseconds(400),
                    cancellationToken);
                await Task.WhenAny(waitTask, intervalTask).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (RobocopyProcessMetrics.TryGetReadBytes(process, out var currentReadBytes))
                {
                    var stepProgress = currentReadBytes >= initialReadBytes
                        ? Math.Min(
                            stepBytes,
                            (long)Math.Min(
                                currentReadBytes - initialReadBytes,
                                (ulong)long.MaxValue))
                        : 0;
                    var completed = Math.Min(
                        totalBytes,
                        AddSaturating(completedBeforeStep, stepProgress));
                    progress?.Report(new CopyProgress(
                        stepIndex + 1,
                        stepCount,
                        $"正在处理第 {stepIndex + 1}/{stepCount} 项…",
                        command.LogPath,
                        completed,
                        totalBytes,
                        CalculateSpeed(completed, stopwatch.Elapsed),
                        CalculateRemaining(
                            completed,
                            totalBytes,
                            stopwatch.Elapsed)));
                }
            }

            await waitTask.ConfigureAwait(false);
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
        ConcurrentQueue<CopyFailure> failures,
        bool isError)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var failure = RobocopyFailureParser.TryParse(line, isError);
            if (failure is not null && failures.Count < 200)
            {
                failures.Enqueue(failure);
            }

            progress?.Report(new CopyProgress(
                stepIndex + 1,
                stepCount,
                isError ? $"[错误] {line}" : line,
                logPath));
        }
    }

    private static double? CalculateSpeed(long completedBytes, TimeSpan elapsed) =>
        completedBytes > 0 && elapsed.TotalSeconds > 0.25
            ? completedBytes / elapsed.TotalSeconds
            : null;

    private static TimeSpan? CalculateRemaining(
        long completedBytes,
        long totalBytes,
        TimeSpan elapsed)
    {
        var speed = CalculateSpeed(completedBytes, elapsed);
        if (speed is null or <= 0 || totalBytes <= completedBytes)
        {
            return totalBytes <= completedBytes && totalBytes > 0
                ? TimeSpan.Zero
                : null;
        }

        return TimeSpan.FromSeconds((totalBytes - completedBytes) / speed.Value);
    }

    private static long AddSaturating(long left, long right) =>
        left > long.MaxValue - right
            ? long.MaxValue
            : left + right;
}
