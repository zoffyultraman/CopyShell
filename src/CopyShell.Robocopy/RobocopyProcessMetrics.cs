using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CopyShell.Robocopy;

internal static class RobocopyProcessMetrics
{
    public static bool TryGetReadBytes(Process process, out ulong readBytes)
    {
        readBytes = 0;
        try
        {
            if (!GetProcessIoCounters(process.SafeHandle, out var counters))
            {
                return false;
            }

            readBytes = counters.ReadTransferCount;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(
        Microsoft.Win32.SafeHandles.SafeProcessHandle process,
        out IoCounters counters);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
}
