namespace CopyShell.Core.Abstractions;

public interface IFileSystemProbe
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    string GetFullPath(string path);
}
