using CopyShell.Core.Models;

namespace CopyShell.Robocopy.Tests;

[TestFixture]
public sealed class RobocopyCommandFactoryTests
{
    [Test]
    public void CreateCommands_ForDirectoryCopy_UsesExpectedDefaults()
    {
        var plan = CreatePlan(
            CopyOperation.Copy,
            new CopyStep(
                @"C:\Source",
                CopySourceKind.Directory,
                @"D:\Target\Source",
                CopyOperation.Copy));
        var factory = new RobocopyCommandFactory(@"C:\Logs");

        var command = factory.CreateCommands(plan).Single();

        Assert.Multiple(() =>
        {
            Assert.That(command.Arguments.Take(2), Is.EqualTo(new[] { @"C:\Source", @"D:\Target\Source" }));
            Assert.That(command.Arguments, Does.Contain("/E"));
            Assert.That(command.Arguments, Does.Contain("/COPY:DAT"));
            Assert.That(command.Arguments, Does.Contain("/DCOPY:DAT"));
            Assert.That(command.Arguments, Does.Contain("/R:2"));
            Assert.That(command.Arguments, Does.Contain("/W:2"));
            Assert.That(command.Arguments, Does.Contain("/MT:16"));
            Assert.That(command.Arguments, Does.Contain("/Z"));
            Assert.That(command.Arguments, Does.Contain("/XJ"));
            Assert.That(command.Arguments.Any(argument => argument.StartsWith("/UNILOG:")), Is.True);
        });
    }

    [Test]
    public void CreateCommands_ForFileMove_UsesMovAndFileFilter()
    {
        var plan = CreatePlan(
            CopyOperation.Move,
            new CopyStep(
                @"C:\Source\demo.txt",
                CopySourceKind.File,
                @"D:\Target\demo.txt",
                CopyOperation.Move));
        var command = new RobocopyCommandFactory(@"C:\Logs")
            .CreateCommands(plan)
            .Single();

        Assert.Multiple(() =>
        {
            Assert.That(command.Arguments.Take(3), Is.EqualTo(new[] { @"C:\Source", @"D:\Target", "demo.txt" }));
            Assert.That(command.Arguments, Does.Contain("/MOV"));
            Assert.That(command.Arguments, Does.Not.Contain("/MOVE"));
        });
    }

    [Test]
    public void CreateCommands_ForSync_UsesMirror()
    {
        var plan = CreatePlan(
            CopyOperation.Sync,
            new CopyStep(
                @"C:\Source",
                CopySourceKind.Directory,
                @"D:\Target",
                CopyOperation.Sync));

        var command = new RobocopyCommandFactory(@"C:\Logs")
            .CreateCommands(plan)
            .Single();

        Assert.That(command.Arguments, Does.Contain("/MIR"));
    }

    private static CopyPlan CreatePlan(CopyOperation operation, CopyStep step) =>
        new(
            Guid.NewGuid(),
            operation,
            new CopyOptions(),
            [step],
            operation == CopyOperation.Sync ? RiskLevel.Destructive : RiskLevel.Normal,
            [],
            "HASH");
}
