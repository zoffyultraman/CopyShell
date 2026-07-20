using CopyShell.Core.Models;

namespace CopyShell.Robocopy;

public static class RobocopyExitCodeInterpreter
{
    private const int ExtraItems = 2;
    private const int MismatchedItems = 4;
    private const int FailedCopies = 8;
    private const int FatalError = 16;

    public static CopyExecutionOutcome GetOutcome(int exitCode)
    {
        if ((exitCode & (FailedCopies | FatalError)) != 0 || exitCode >= 16)
        {
            return CopyExecutionOutcome.Failed;
        }

        if ((exitCode & (ExtraItems | MismatchedItems)) != 0)
        {
            return CopyExecutionOutcome.CompletedWithDifferences;
        }

        return CopyExecutionOutcome.Completed;
    }

    public static string Describe(int exitCode) => exitCode switch
    {
        0 => "没有文件需要复制。",
        1 => "文件复制成功。",
        2 => "目标中存在额外项目。",
        3 => "复制成功，目标中存在额外项目。",
        4 => "检测到不匹配项目。",
        5 => "复制成功，并检测到不匹配项目。",
        6 => "检测到额外项目和不匹配项目。",
        7 => "复制成功，并检测到额外项目和不匹配项目。",
        8 => "至少有一个文件复制失败。",
        16 => "发生严重错误，未执行复制。",
        _ when exitCode < 8 => "操作完成，但存在非致命差异。",
        _ => "Robocopy 执行失败。"
    };
}
