namespace CopyShell.Core.Services;

public static class BackgroundWorkerProtocol
{
    public static string WorkerMutexName =>
        GetName("CopyShell.Worker");

    public static string CoordinationMutexName =>
        GetName("CopyShell.Worker.Coordinator");

    public static string WakeEventName =>
        GetName("CopyShell.Worker.Wake");

    private static string GetName(string value) =>
        OperatingSystem.IsWindows()
            ? $@"Local\{value}"
            : value;
}
