using System.Security.Cryptography;
using System.Threading.Channels;
using DuplicateFinder.Models;
using DuplicateFinder.Utils;

namespace DuplicateFinder.Services;

public sealed class HashingService
{
    public async Task RunWorkerAsync(
        int workerId,
        ChannelReader<FileCandidate> input,
        ChannelWriter<FileHashResult> output,
        ScanOptions options,
        ConsoleProgress progress,
        SemaphoreSlim largeFileLimiter,
        CancellationToken ct)
    {
        await foreach (var candidate in input.ReadAllAsync(ct))
        {
            var largePermitTaken = false;

            try
            {
                if (candidate.Size >= options.LargeFileThresholdBytes)
                {
                    await largeFileLimiter.WaitAsync(ct);
                    largePermitTaken = true;
                }

                var hash = await ComputeSha256Async(candidate.Path, options.BufferSize, ct);
                progress.Hashed(candidate.Size);
                await output.WriteAsync(FileHashResult.FromCandidateOk(candidate, hash, options.HashAlgorithm), ct);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException or FileNotFoundException or DirectoryNotFoundException or CryptographicException)
            {
                progress.Error();
                await output.WriteAsync(FileHashResult.FromError(candidate, options.HashAlgorithm, ex), ct);
            }
            finally
            {
                if (largePermitTaken)
                    largeFileLimiter.Release();
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path, int bufferSize, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash);
    }
}
