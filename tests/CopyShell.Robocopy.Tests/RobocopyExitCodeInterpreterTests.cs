using CopyShell.Core.Models;

namespace CopyShell.Robocopy.Tests;

[TestFixture]
public sealed class RobocopyExitCodeInterpreterTests
{
    [TestCase(0, CopyExecutionOutcome.Completed)]
    [TestCase(1, CopyExecutionOutcome.Completed)]
    [TestCase(2, CopyExecutionOutcome.CompletedWithDifferences)]
    [TestCase(7, CopyExecutionOutcome.CompletedWithDifferences)]
    [TestCase(8, CopyExecutionOutcome.Failed)]
    [TestCase(16, CopyExecutionOutcome.Failed)]
    public void GetOutcome_MapsRobocopyBitFlags(
        int exitCode,
        CopyExecutionOutcome expected)
    {
        Assert.That(
            RobocopyExitCodeInterpreter.GetOutcome(exitCode),
            Is.EqualTo(expected));
    }
}
