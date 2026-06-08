namespace DuplicateFinder.Models;

public sealed class ScanOptions
{
    public required string RootPath { get; init; }
    public string DbPath { get; init; } = "duplicates.db";
    public string HashAlgorithm { get; init; } = "SHA-256";
    public int Threads { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);
    public bool LowResource { get; init; }
    public int BatchSize { get; init; } = 1000;
    public int ChannelCapacity { get; init; } = 5000;
    public int BufferSize { get; init; } = 1024 * 1024;
    public int LargeFileParallelism { get; init; } = Math.Max(1, Math.Min(2, Environment.ProcessorCount / 2));
    public long LargeFileThresholdBytes { get; init; } = 512L * 1024 * 1024;
    public bool FollowReparsePoints { get; init; }
    public bool RecordSkipped { get; init; }
    public bool AllFiles { get; init; }
    public HashSet<string> IncludeExtensions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExcludeExtensions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
