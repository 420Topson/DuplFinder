using DuplicateFinder.Models;
using DuplicateFinder.Services;
using DuplicateFinder.Utils;

namespace DuplicateFinder;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("\nPrzerywanie po Ctrl+C...");
        };

        try
        {
            var db = new DatabaseService();
            var command = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();

            switch (command)
            {
                case "scan":
                {
                    var options = ParseScanOptions(rest);
                    Console.WriteLine($"DB: {options.DbPath}");
                    Console.WriteLine($"Root: {options.RootPath}");
                    Console.WriteLine($"Threads: {options.Threads}, batch: {options.BatchSize}, channel: {options.ChannelCapacity}, buffer: {SizeParser.FormatBytes(options.BufferSize)}");
                    Console.WriteLine($"Low resource: {options.LowResource}, large file parallelism: {options.LargeFileParallelism}");
                    Console.WriteLine("Kasowanie plików jest wyłączone. Program tylko raportuje duplikaty.");

                    var scan = new ScanService(db);
                    await scan.RunScanAsync(options, cts.Token);
                    return 0;
                }

                case "duplicates":
                {
                    var options = ParseDuplicateOptions(rest);
                    db.EnsureDatabaseExists(options.DbPath);
                    await db.InitializeAsync(options.DbPath, cts.Token);
                    var duplicateService = new DuplicateService(db);
                    await duplicateService.PrintDuplicatesAsync(options, cts.Token);
                    return 0;
                }

                case "stats":
                {
                    var dbPath = GetOption(rest, "--db") ?? "duplicates.db";
                    db.EnsureDatabaseExists(dbPath);
                    await db.InitializeAsync(dbPath, cts.Token);
                    var stats = await db.GetStatsAsync(dbPath, cts.Token);
                    Console.WriteLine($"Files in DB: {stats.Total}");
                    Console.WriteLine($"Hashed OK: {stats.Hashed}");
                    Console.WriteLine($"Errors: {stats.Errors}");
                    Console.WriteLine($"Skipped: {stats.Skipped}");
                    Console.WriteLine($"Duplicate groups: {stats.DuplicateGroups}");
                    Console.WriteLine($"Potential saving: {SizeParser.FormatBytes(stats.PotentialSaving)}");
                    return 0;
                }

                case "clean-db":
                {
                    var dbPath = GetOption(rest, "--db") ?? "duplicates.db";
                    var batchSize = TryParseInt(GetOption(rest, "--batch-size"), 1000);
                    db.EnsureDatabaseExists(dbPath);
                    await db.InitializeAsync(dbPath, cts.Token);
                    var deleted = await db.CleanMissingFilesAsync(dbPath, batchSize, cts.Token);
                    Console.WriteLine($"Usunięto z bazy wpisy nieistniejących plików: {deleted}");
                    return 0;
                }

                default:
                    Console.WriteLine($"Nieznana komenda: {args[0]}");
                    PrintHelp();
                    return 2;
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Przerwano.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static ScanOptions ParseScanOptions(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Podaj katalog lub dysk, np. scan \"D:\\\".");

        var root = args[0];
        var low = HasFlag(args, "--low-resource");
        var threads = ParseThreads(GetOption(args, "--threads"), low);
        var batchSize = TryParseInt(GetOption(args, "--batch-size"), low ? 500 : 1000);
        var channelCapacity = TryParseInt(GetOption(args, "--channel-capacity"), low ? 1000 : 5000);
        var bufferSize = TryParseSizeToInt(GetOption(args, "--buffer-size"), low ? 512 * 1024 : 1024 * 1024);
        var largeFileParallelism = TryParseInt(GetOption(args, "--large-file-parallelism"), low ? 1 : Math.Max(1, Math.Min(2, Environment.ProcessorCount / 2)));

        return new ScanOptions
        {
            RootPath = root,
            DbPath = GetOption(args, "--db") ?? "duplicates.db",
            Threads = threads,
            LowResource = low,
            BatchSize = Math.Max(1, batchSize),
            ChannelCapacity = Math.Max(10, channelCapacity),
            BufferSize = Math.Max(64 * 1024, bufferSize),
            LargeFileParallelism = Math.Max(1, largeFileParallelism),
            FollowReparsePoints = HasFlag(args, "--follow-reparse-points"),
            RecordSkipped = HasFlag(args, "--record-skipped")
        };
    }

    private static DuplicateOptions ParseDuplicateOptions(string[] args)
    {
        return new DuplicateOptions
        {
            DbPath = GetOption(args, "--db") ?? "duplicates.db",
            MinSizeBytes = SizeParser.ParseBytes(GetOption(args, "--min-size") ?? "0"),
            ExportCsvPath = GetOption(args, "--export")
        };
    }

    private static int ParseThreads(string? value, bool lowResource)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return lowResource ? Math.Min(2, Math.Max(1, Environment.ProcessorCount - 1)) : Math.Max(1, Environment.ProcessorCount - 1);

        if (!int.TryParse(value, out var parsed) || parsed < 1)
            throw new ArgumentException("--threads musi być: auto, 1, 2, 4, 8 itd.");

        return parsed;
    }

    private static int TryParseInt(string? value, int fallback)
    {
        if (int.TryParse(value, out var parsed))
            return parsed;
        return fallback;
    }

    private static int TryParseSizeToInt(string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var bytes = SizeParser.ParseBytes(value);
        if (bytes > int.MaxValue)
            throw new ArgumentException("Wartość jest zbyt duża dla bufora.");
        return (int)bytes;
    }

    private static bool HasFlag(string[] args, string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Opcja {name} wymaga wartości.");

            return args[i + 1];
        }

        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
DuplicateFinder, .NET 8, SQLite, SHA-256

Komendy:
  scan <path> [opcje]
  duplicates [opcje]
  stats [opcje]
  clean-db [opcje]

Scan:
  scan "D:\" --db duplicates.db --threads auto
  scan "D:\" --db duplicates.db --threads 8 --batch-size 5000
  scan "D:\" --low-resource --threads 2 --batch-size 500 --channel-capacity 1000 --large-file-parallelism 1
  scan "D:\" --low-resource --threads 1 --batch-size 250 --buffer-size 512KB

Opcje scan:
  --db <plik>                      Domyślnie duplicates.db
  --threads auto|1|2|4|8           Domyślnie max(1, CPU-1)
  --low-resource                   Mniejsze kolejki, batch i domyślnie max 2 workery
  --batch-size <n>                 Ile rekordów SQLite na transakcję
  --channel-capacity <n>           Limit kolejki Channel<T>, czyli backpressure
  --buffer-size <512KB|1MB|...>    Bufor odczytu pliku
  --large-file-parallelism <n>     Limit równoległego hashowania dużych plików
  --follow-reparse-points          Domyślnie wyłączone
  --record-skipped                 Zapisuje pominięte wpisy do DB, może zwiększyć bazę

Duplicates:
  duplicates --db duplicates.db
  duplicates --db duplicates.db --min-size 1MB
  duplicates --db duplicates.db --min-size 1MB --export duplicates.csv

Stats:
  stats --db duplicates.db

Clean DB:
  clean-db --db duplicates.db
""");
    }
}
