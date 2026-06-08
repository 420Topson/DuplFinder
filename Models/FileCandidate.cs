namespace DuplicateFinder.Models;

public sealed record FileCandidate(
    string Path,
    string FileName,
    string Extension,
    long Size,
    DateTime LastWriteTimeUtc)
{
    public string LastWriteIso => LastWriteTimeUtc.ToUniversalTime().ToString("O");
}
