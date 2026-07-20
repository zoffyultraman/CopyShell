using CopyShell.Core.Models;

namespace CopyShell.Core.Abstractions;

public interface IProcessProbe
{
    ProcessIdentity GetCurrent();

    bool IsAlive(ProcessIdentity identity);
}
