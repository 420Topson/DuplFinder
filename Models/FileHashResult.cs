namespace DuplicateFinder.Models;

public sealed class FileHashResult
{
    public required string Path { get; init; }
    public string FileName { get; init; } = "";
    public string Extension { get; init; } = "";
    public long Size { get; init; }
    public DateTime? LastWriteTimeUtc { get; init; }
    public string? Hash { get; init; }
    public int? HashPrefix { get; init; }
    public string HashAlgorithm { get; init; } = "SHA-256";
    public DateTime LastScanTimeUtc { get; init; } = DateTime.UtcNow;
    public string Status { get; init; } = "ok";
    public string? ErrorMessage { get; init; }
    public bool IsSkipped { get; init; }
    public string? SkipReason { get; init; }
    public bool FromCache { get; init; }

    public static FileHashResult FromCandidateOk(FileCandidate candidate, string hash, string algorithm) => new()
    {
        Path = candidate.Path,
        FileName = candidate.FileName,
        Extension = candidate.Extension,
        Size = candidate.Size,
        LastWriteTimeUtc = candidate.LastWriteTimeUtc,
        Hash = hash,
        HashPrefix = HexPrefixToByte(hash),
        HashAlgorithm = algorithm,
        Status = "ok"
    };

    public static FileHashResult FromCacheHit(FileCandidate candidate, string hash, int? hashPrefix, string algorithm) => new()
    {
        Path = candidate.Path,
        FileName = candidate.FileName,
        Extension = candidate.Extension,
        Size = candidate.Size,
        LastWriteTimeUtc = candidate.LastWriteTimeUtc,
        Hash = hash,
        HashPrefix = hashPrefix ?? HexPrefixToByte(hash),
        HashAlgorithm = algorithm,
        Status = "ok",
        FromCache = true
    };

    public static FileHashResult FromError(FileCandidate candidate, string algorithm, Exception ex) => new()
    {
        Path = candidate.Path,
        FileName = candidate.FileName,
        Extension = candidate.Extension,
        Size = candidate.Size,
        LastWriteTimeUtc = candidate.LastWriteTimeUtc,
        HashAlgorithm = algorithm,
        Status = "error",
        ErrorMessage = ex.GetType().Name + ": " + ex.Message
    };

    public static FileHashResult FromSkipped(string path, string reason) => new()
    {
        Path = path,
        FileName = System.IO.Path.GetFileName(path),
        Extension = System.IO.Path.GetExtension(path),
        Size = 0,
        LastWriteTimeUtc = null,
        Status = "skipped",
        IsSkipped = true,
        SkipReason = reason
    };

    private static int? HexPrefixToByte(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2)
            return null;

        return Convert.ToInt32(hash[..2], 16);
    }
}
