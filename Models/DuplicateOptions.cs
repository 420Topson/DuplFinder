namespace DuplicateFinder.Models;

public sealed class DuplicateOptions
{
    public string DbPath { get; init; } = "duplicates.db";
    public long MinSizeBytes { get; init; }
    public string? ExportCsvPath { get; init; }
}
