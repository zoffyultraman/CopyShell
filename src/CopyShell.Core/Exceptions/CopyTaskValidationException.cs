namespace CopyShell.Core.Exceptions;

public sealed class CopyTaskValidationException : Exception
{
    public CopyTaskValidationException(string message)
        : base(message)
    {
    }
}
