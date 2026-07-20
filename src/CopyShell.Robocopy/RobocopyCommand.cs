namespace CopyShell.Robocopy;

public sealed record RobocopyCommand(
    IReadOnlyList<string> Arguments,
    string LogPath);
