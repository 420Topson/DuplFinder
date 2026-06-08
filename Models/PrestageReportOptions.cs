namespace DuplicateFinder.Models;

public sealed class PrestageReportOptions
{
    public string DbPath { get; init; } = "duplicates.db";
    public required string OutputPath { get; init; }
    public bool Force { get; init; }
}
