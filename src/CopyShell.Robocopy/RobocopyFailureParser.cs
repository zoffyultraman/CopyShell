using System.Text.RegularExpressions;
using CopyShell.Core.Models;

namespace CopyShell.Robocopy;

internal static partial class RobocopyFailureParser
{
    public static CopyFailure? TryParse(string line, bool isStandardError)
    {
        if (!isStandardError &&
            !line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("FAILED", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("错误", StringComparison.Ordinal) &&
            !line.Contains("失败", StringComparison.Ordinal) &&
            !line.Contains("拒绝访问", StringComparison.Ordinal))
        {
            return null;
        }

        int? errorCode = null;
        var codeMatch = ErrorCodeRegex().Match(line);
        if (codeMatch.Success &&
            int.TryParse(codeMatch.Groups["code"].Value, out var parsedCode))
        {
            errorCode = parsedCode;
        }

        var pathMatch = PathRegex().Match(line);
        return new CopyFailure(
            line.Trim(),
            pathMatch.Success ? pathMatch.Groups["path"].Value.Trim() : null,
            errorCode);
    }

    [GeneratedRegex(
        @"(?:ERROR|错误)\s+(?<code>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodeRegex();

    [GeneratedRegex(
        @"(?<path>(?:[A-Za-z]:\\|\\\\)[^\r\n]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PathRegex();
}
