using CopyShell.Core.Abstractions;

namespace CopyShell.Core.Services;

public sealed class PhysicalFileSystemProbe : IFileSystemProbe
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string GetFullPath(string path) => Path.GetFullPath(path);
}
