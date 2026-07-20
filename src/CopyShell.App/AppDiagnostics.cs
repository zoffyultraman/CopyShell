namespace CopyShell.App;

internal static class AppDiagnostics
{
    private static readonly object Gate = new();

    public static string CurrentLogPath
    {
        get
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "CopyShell",
                "Logs",
                "App");
            return Path.Combine(
                directory,
                $"{DateTimeOffset.UtcNow:yyyy-MM}.log");
        }
    }

    public static void Write(string message)
    {
        try
        {
            var path = CurrentLogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
            // Diagnostics must never introduce another startup failure.
        }
    }

    public static void WriteException(string stage, Exception exception) =>
        Write($"{stage}: {exception}");
}
