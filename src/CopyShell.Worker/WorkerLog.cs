namespace CopyShell.Worker;

internal static class WorkerLog
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "CopyShell",
                "Logs",
                "Worker");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"{DateTimeOffset.UtcNow:yyyy-MM}.log");
            var line =
                $"{DateTimeOffset.UtcNow:O} " +
                $"PID={Environment.ProcessId} {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Logging must never keep the queue worker from running or exiting.
        }
    }
}
