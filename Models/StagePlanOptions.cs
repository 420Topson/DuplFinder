namespace DuplicateFinder.Models;

public sealed class ApplyStagePlanOptions
{
    public required string PlanPath { get; init; }
    public string? QuarantineRoot { get; init; }
    public bool DryRun { get; init; }
}

public sealed class UndoQuarantineOptions
{
    public required string ManifestPath { get; init; }
    public bool Restore { get; init; }
}

public sealed class PurgeQuarantineOptions
{
    public required string ManifestPath { get; init; }
    public bool ConfirmPurge { get; init; }
}

public sealed record ApplyStagePlanResult(
    bool DryRun,
    long Groups,
    long Planned,
    long Moved,
    long Skipped,
    long Failed,
    string? QuarantineSessionPath,
    string? ManifestPath);

public sealed record UndoQuarantineResult(
    bool DryRun,
    long ManifestEntries,
    long EligibleEntries,
    long Planned,
    long Restored,
    long Skipped,
    long Failed);

public sealed record PurgeQuarantineResult(
    bool DryRun,
    long ManifestEntries,
    long EligibleEntries,
    long Planned,
    long Purged,
    long Skipped,
    long Failed);
