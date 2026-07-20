namespace CopyShell.Core.Models;

public sealed record CopyPlan(
    Guid TaskId,
    CopyOperation Operation,
    CopyOptions Options,
    IReadOnlyList<CopyStep> Steps,
    RiskLevel RiskLevel,
    IReadOnlyList<string> Warnings,
    string PlanHash);
