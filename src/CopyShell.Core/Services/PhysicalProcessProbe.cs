using System.Diagnostics;
using CopyShell.Core.Abstractions;
using CopyShell.Core.Models;

namespace CopyShell.Core.Services;

public sealed class PhysicalProcessProbe : IProcessProbe
{
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    public ProcessIdentity GetCurrent()
    {
        using var process = Process.GetCurrentProcess();
        return new ProcessIdentity(
            process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
    }

    public bool IsAlive(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            var actualStart = new DateTimeOffset(
                process.StartTime.ToUniversalTime(),
                TimeSpan.Zero);
            return !process.HasExited &&
                   (actualStart - identity.StartedAtUtc).Duration() <= StartTimeTolerance;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
