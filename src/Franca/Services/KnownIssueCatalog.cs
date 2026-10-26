using Franca.Models;

namespace Franca.Services;

public enum KnownIssueRepairKind
{
    None,
    RestartSteamClean,
}

public sealed record KnownIssueDefinition(
    string Id,
    string TitleKey,
    string SummaryKey,
    string ReasonKey,
    string RepairKey,
    KnownIssueRepairKind RepairKind,
    bool RequiresConfirmation);

public sealed record KnownIssueDetection(
    KnownIssueDefinition Issue,
    GameInfo Game,
    int ProcessId);

public sealed record KnownIssueRepairOutcome(bool Success, string MessageKey);

public static class KnownIssueCatalog
{
    public const string DoorstopSteamEnvironmentId = "unity-doorstop-steam-environment";

    public static readonly KnownIssueDefinition[] All =
    {
        new(
            DoorstopSteamEnvironmentId,
            "issue.doorstop.title",
            "issue.doorstop.summary",
            "issue.doorstop.reason",
            "issue.doorstop.repair",
            KnownIssueRepairKind.RestartSteamClean,
            RequiresConfirmation: true),
    };

    public static KnownIssueDefinition? Find(string id) =>
        All.FirstOrDefault(issue => issue.Id == id);
}
