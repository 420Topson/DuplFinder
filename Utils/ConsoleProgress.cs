using DuplicateFinder.Models;

namespace DuplicateFinder.Utils;

public sealed class ConsoleProgress
{
    private long _candidates;
    private long _skipped;
    private long _cacheHits;
    private long _hashed;
    private long _errors;
    private long _bytesHashed;
    private long _written;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    public void Candidate() => Interlocked.Increment(ref _candidates);
    public void Skipped() => Interlocked.Increment(ref _skipped);
    public void CacheHit() => Interlocked.Increment(ref _cacheHits);
    public void Hashed(long bytes)
    {
        Interlocked.Increment(ref _hashed);
        Interlocked.Add(ref _bytesHashed, bytes);
    }
    public void Error() => Interlocked.Increment(ref _errors);
    public void Written(long count) => Interlocked.Add(ref _written, count);

    public async Task RenderUntilCancelledAsync(ScanOptions options, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Render(options, final: false);
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Render(ScanOptions options, bool final)
    {
        var elapsed = Math.Max(0.001, (DateTime.UtcNow - _startedUtc).TotalSeconds);
        var mbps = Interlocked.Read(ref _bytesHashed) / 1024d / 1024d / elapsed;

        var line =
            $"Candidates: {Interlocked.Read(ref _candidates),8} | " +
            $"Skipped: {Interlocked.Read(ref _skipped),8} | " +
            $"Cache: {Interlocked.Read(ref _cacheHits),8} | " +
            $"Hashed: {Interlocked.Read(ref _hashed),8} | " +
            $"Errors: {Interlocked.Read(ref _errors),6} | " +
            $"Written: {Interlocked.Read(ref _written),8} | " +
            $"MB/s: {mbps,7:0.0} | " +
            $"Workers: {options.Threads}";

        if (final)
            Console.WriteLine(line);
        else
            Console.Write("\r" + line.PadRight(Console.WindowWidth > 0 ? Console.WindowWidth - 1 : line.Length));
    }
}
