namespace DuplicateFinder.Models;

public sealed class DuplicateGroup
{
    public int GroupNumber { get; init; }
    public long Size { get; init; }
    public required string Hash { get; init; }
    public int Copies { get; init; }
    public long PotentialSaving => Math.Max(0, Copies - 1) * Size;
    public List<string> Paths { get; init; } = new();
}
