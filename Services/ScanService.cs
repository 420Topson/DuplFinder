using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using DuplicateFinder.Models;
using DuplicateFinder.Utils;

namespace DuplicateFinder.Services;

public sealed class ScanService
{
    private readonly FileEnumeratorService _enumerator = new();
    private readonly HashingService _hashing = new();
    private readonly DatabaseService _database;

    public ScanService(DatabaseService database)
    {
        _database = database;
    }

    public async Task RunScanAsync(ScanOptions options, CancellationToken ct)
    {
        await _database.InitializeAsync(options.DbPath, ct);

        var boundedOptions = new BoundedChannelOptions(options.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = false
        };

        var candidates = Channel.CreateBounded<FileCandidate>(boundedOptions);
        var toHash = Channel.CreateBounded<FileCandidate>(boundedOptions);
        var results = Channel.CreateBounded<FileHashResult>(boundedOptions);

        var progress = new ConsoleProgress();
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progressTask = progress.RenderUntilCancelledAsync(options, progressCts.Token);

        var enumeratorTask = _enumerator.EnumerateAsync(options, candidates.Writer, results.Writer, progress, ct);
        var cacheTask = FilterCacheAsync(candidates.Reader, toHash.Writer, results.Writer, options, progress, ct);
        var largeFileLimiter = new SemaphoreSlim(Math.Max(1, options.LargeFileParallelism));

        var hashTasks = Enumerable.Range(1, options.Threads)
            .Select(id => _hashing.RunWorkerAsync(id, toHash.Reader, results.Writer, options, progress, largeFileLimiter, ct))
            .ToArray();

        var writerTask = _database.WriteResultsAsync(results.Reader, options, progress, ct);

        try
        {
            await enumeratorTask;
            await cacheTask;
            await Task.WhenAll(hashTasks);
        }
        finally
        {
            results.Writer.TryComplete();
        }

        await writerTask;

        progressCts.Cancel();
        try { await progressTask; } catch (OperationCanceledException) { }
        Console.WriteLine();
        progress.Render(options, final: true);
    }

    private async Task FilterCacheAsync(
        ChannelReader<FileCandidate> candidates,
        ChannelWriter<FileCandidate> toHash,
        ChannelWriter<FileHashResult> results,
        ScanOptions options,
        ConsoleProgress progress,
        CancellationToken ct)
    {
        await using var connection = _database.OpenConnection(options.DbPath);
        await connection.OpenAsync(ct);

        try
        {
            await foreach (var candidate in candidates.ReadAllAsync(ct))
            {
                FileHashResult? cached = null;
                try
                {
                    cached = await _database.TryGetCacheHitAsync(connection, candidate, options.HashAlgorithm, ct);
                }
                catch (SqliteException ex)
                {
                    progress.Error();
                    await results.WriteAsync(FileHashResult.FromError(candidate, options.HashAlgorithm, ex), ct);
                    continue;
                }

                if (cached is not null)
                {
                    progress.CacheHit();
                    await results.WriteAsync(cached, ct);
                }
                else
                {
                    await toHash.WriteAsync(candidate, ct);
                }
            }
        }
        finally
        {
            toHash.TryComplete();
        }
    }
}
