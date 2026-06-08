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
    private readonly bool _interactiveOutput = !Console.IsOutputRedirected && Environment.UserInteractive;

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
        if (!_interactiveOutput)
            return;

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
        {
            Console.WriteLine(_interactiveOutput ? "\r" + PadForWindow(line) : line);
            return;
        }

        if (_interactiveOutput)
            Console.Write("\r" + PadForWindow(line));
    }

    private static string PadForWindow(string line)
    {
        var width = GetWindowWidth();
        if (width <= 1)
            return line;

        return line.PadRight(Math.Max(line.Length, width - 1));
    }

    private static int GetWindowWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            return 0;
        }
    }
}
