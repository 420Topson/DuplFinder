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

public sealed record ApplyStagePlanResult(
    bool DryRun,
    int Groups,
    int Planned,
    int Moved,
    int Skipped,
    int Failed,
    string? QuarantineSessionPath,
    string? ManifestPath);

public sealed record UndoQuarantineResult(
    bool DryRun,
    int Planned,
    int Restored,
    int Skipped,
    int Failed);
