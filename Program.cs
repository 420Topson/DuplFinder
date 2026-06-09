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
                    Console.WriteLine($"Profile: {FormatProfile(options.Profile)}");
                    Console.WriteLine($"Threads: {options.Threads}, batch: {options.BatchSize}, channel: {options.ChannelCapacity}, buffer: {SizeParser.FormatBytes(options.BufferSize)}");
                    Console.WriteLine($"Low resource: {options.LowResource}, large file parallelism: {options.LargeFileParallelism}, large file threshold: {SizeParser.FormatBytes(options.LargeFileThresholdBytes)}");
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

                case "prestage-report":
                {
                    var options = ParsePrestageReportOptions(rest);
                    db.EnsureDatabaseExists(options.DbPath);
                    var report = new PrestageReportService();
                    var groups = await report.GenerateAsync(options, cts.Token);
                    Console.WriteLine($"HTML report written: {Path.GetFullPath(options.OutputPath)}");
                    Console.WriteLine($"Exact duplicate groups included: {groups}");
                    Console.WriteLine("This report does not move or delete files. It only exports a stage plan.");
                    return 0;
                }

                case "apply-stage-plan":
                {
                    var options = ParseApplyStagePlanOptions(rest);
                    var stagePlan = new StagePlanService();
                    var result = await stagePlan.ApplyStagePlanAsync(options, cts.Token);
                    Console.WriteLine($"Groups: {result.Groups}");
                    Console.WriteLine($"Planned: {result.Planned}");
                    Console.WriteLine($"Moved: {result.Moved}");
                    Console.WriteLine($"Skipped: {result.Skipped}");
                    Console.WriteLine($"Failed: {result.Failed}");
                    if (!string.IsNullOrWhiteSpace(result.QuarantineSessionPath))
                        Console.WriteLine($"Quarantine session: {result.QuarantineSessionPath}");
                    if (!string.IsNullOrWhiteSpace(result.ManifestPath))
                        Console.WriteLine($"Manifest: {result.ManifestPath}");
                    Console.WriteLine("No files were deleted. KEEP files were not modified.");
                    return 0;
                }

                case "undo-quarantine":
                {
                    var options = ParseUndoQuarantineOptions(rest);
                    var stagePlan = new StagePlanService();
                    var result = await stagePlan.UndoQuarantineAsync(options, cts.Token);
                    Console.WriteLine($"Manifest entries: {result.ManifestEntries}");
                    Console.WriteLine($"Eligible entries: {result.EligibleEntries}");
                    Console.WriteLine($"Planned: {result.Planned}");
                    Console.WriteLine($"Restored: {result.Restored}");
                    Console.WriteLine($"Skipped: {result.Skipped}");
                    Console.WriteLine($"Failed: {result.Failed}");
                    Console.WriteLine("No files were deleted. Existing original files were not overwritten.");
                    return 0;
                }

                case "purge-quarantine":
                {
                    var options = ParsePurgeQuarantineOptions(rest);
                    var stagePlan = new StagePlanService();
                    var result = await stagePlan.PurgeQuarantineAsync(options, cts.Token);
                    Console.WriteLine($"Manifest entries: {result.ManifestEntries}");
                    Console.WriteLine($"Eligible entries: {result.EligibleEntries}");
                    Console.WriteLine($"Planned: {result.Planned}");
                    Console.WriteLine($"Purged: {result.Purged}");
                    Console.WriteLine($"Skipped: {result.Skipped}");
                    Console.WriteLine($"Failed: {result.Failed}");
                    Console.WriteLine("Purge deletes only validated files inside the quarantine session. Original paths and KEEP files are not touched.");
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
        var requestedProfile = ParseProfile(GetOption(args, "--profile"));
        var profile = low ? ScanProfile.Hdd : requestedProfile;
        var defaults = GetProfileDefaults(profile);
        var threads = ParseThreads(GetOption(args, "--threads"), defaults.Threads);
        var batchSize = TryParseInt(GetOption(args, "--batch-size"), defaults.BatchSize);
        var channelCapacity = TryParseInt(GetOption(args, "--channel-capacity"), defaults.ChannelCapacity);
        var bufferSize = TryParseSizeToInt(GetOption(args, "--buffer-size"), defaults.BufferSize);
        var largeFileParallelism = TryParseInt(GetOption(args, "--large-file-parallelism"), defaults.LargeFileParallelism);
        var largeFileThresholdBytes = TryParseSizeToLong(GetOption(args, "--large-file-threshold"), defaults.LargeFileThresholdBytes);
        var includeExtensions = ParseExtensionList(GetOption(args, "--include-ext"));

        return new ScanOptions
        {
            RootPath = root,
            DbPath = GetOption(args, "--db") ?? "duplicates.db",
            Profile = profile,
            Threads = threads,
            LowResource = low,
            BatchSize = Math.Max(1, batchSize),
            ChannelCapacity = Math.Max(10, channelCapacity),
            BufferSize = Math.Max(64 * 1024, bufferSize),
            LargeFileParallelism = Math.Max(1, largeFileParallelism),
            LargeFileThresholdBytes = Math.Max(1, largeFileThresholdBytes),
            FollowReparsePoints = HasFlag(args, "--follow-reparse-points"),
            RecordSkipped = HasFlag(args, "--record-skipped"),
            IncludeExtensions = includeExtensions,
            IncludeNoExtension = HasFlag(args, "--include-no-extension")
        };
    }

    private static PrestageReportOptions ParsePrestageReportOptions(string[] args)
    {
        var outputPath = GetOption(args, "--out");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Opcja --out jest wymagana, np. prestage-report --db duplicates.db --out prestage-report.html.");

        return new PrestageReportOptions
        {
            DbPath = GetOption(args, "--db") ?? "duplicates.db",
            OutputPath = outputPath,
            Force = HasFlag(args, "--force")
        };
    }

    private static ApplyStagePlanOptions ParseApplyStagePlanOptions(string[] args)
    {
        var planPath = GetOption(args, "--plan");
        if (string.IsNullOrWhiteSpace(planPath))
            throw new ArgumentException("Opcja --plan jest wymagana, np. apply-stage-plan --plan stage-plan.json --dry-run.");

        var quarantineRoot = GetOption(args, "--quarantine");
        return new ApplyStagePlanOptions
        {
            PlanPath = planPath,
            QuarantineRoot = quarantineRoot,
            DryRun = HasFlag(args, "--dry-run") || string.IsNullOrWhiteSpace(quarantineRoot)
        };
    }

    private static UndoQuarantineOptions ParseUndoQuarantineOptions(string[] args)
    {
        var manifestPath = GetOption(args, "--manifest");
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new ArgumentException("Opcja --manifest jest wymagana, np. undo-quarantine --manifest duplfinder-quarantine-manifest.json --dry-run.");

        if (HasFlag(args, "--restore") && HasFlag(args, "--dry-run"))
            throw new ArgumentException("Użyj albo --restore, albo --dry-run. Domyślnie undo-quarantine działa jako dry-run.");

        return new UndoQuarantineOptions
        {
            ManifestPath = manifestPath,
            Restore = HasFlag(args, "--restore")
        };
    }

    private static PurgeQuarantineOptions ParsePurgeQuarantineOptions(string[] args)
    {
        var manifestPath = GetOption(args, "--manifest");
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new ArgumentException("Opcja --manifest jest wymagana, np. purge-quarantine --manifest duplfinder-quarantine-manifest.json --dry-run.");

        if (HasFlag(args, "--confirm-purge") && HasFlag(args, "--dry-run"))
            throw new ArgumentException("Użyj albo --confirm-purge, albo --dry-run. Domyślnie purge-quarantine działa jako dry-run.");

        return new PurgeQuarantineOptions
        {
            ManifestPath = manifestPath,
            ConfirmPurge = HasFlag(args, "--confirm-purge")
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

    private static ScanProfile ParseProfile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ScanProfile.SataSsd;

        return value.Trim().ToLowerInvariant() switch
        {
            "hdd" => ScanProfile.Hdd,
            "sata-ssd" => ScanProfile.SataSsd,
            "nvme" => ScanProfile.Nvme,
            _ => throw new ArgumentException("Unknown profile. Valid values: hdd, sata-ssd, nvme")
        };
    }

    private static ScanProfileDefaults GetProfileDefaults(ScanProfile profile)
    {
        var cpu = Environment.ProcessorCount;

        return profile switch
        {
            ScanProfile.Hdd => new ScanProfileDefaults(
                Threads: Math.Min(2, Math.Max(1, cpu - 1)),
                BatchSize: 500,
                ChannelCapacity: 1000,
                BufferSize: 512 * 1024,
                LargeFileParallelism: 1,
                LargeFileThresholdBytes: 512L * 1024 * 1024),
            ScanProfile.SataSsd => new ScanProfileDefaults(
                Threads: Math.Max(1, cpu - 1),
                BatchSize: 1000,
                ChannelCapacity: 5000,
                BufferSize: 1024 * 1024,
                LargeFileParallelism: Math.Max(1, Math.Min(2, cpu / 2)),
                LargeFileThresholdBytes: 512L * 1024 * 1024),
            ScanProfile.Nvme => new ScanProfileDefaults(
                Threads: Math.Max(1, cpu),
                BatchSize: 5000,
                ChannelCapacity: 20000,
                BufferSize: 4 * 1024 * 1024,
                LargeFileParallelism: Math.Max(2, Math.Min(4, cpu / 2)),
                LargeFileThresholdBytes: 512L * 1024 * 1024),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
        };
    }

    private static IReadOnlyCollection<string> ParseExtensionList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPart in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
                continue;

            if (part.Contains(Path.DirectorySeparatorChar) || part.Contains(Path.AltDirectorySeparatorChar))
                throw new ArgumentException($"Invalid extension in --include-ext: {part}");

            if (!part.StartsWith(".", StringComparison.Ordinal))
                part = "." + part;

            result.Add(part);
        }

        return result.ToArray();
    }

    private static string FormatProfile(ScanProfile profile) => profile switch
    {
        ScanProfile.Hdd => "hdd",
        ScanProfile.SataSsd => "sata-ssd",
        ScanProfile.Nvme => "nvme",
        _ => profile.ToString()
    };

    private static int ParseThreads(string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return fallback;

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

    private static long TryParseSizeToLong(string? value, long fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        return SizeParser.ParseBytes(value);
    }

    private sealed record ScanProfileDefaults(
        int Threads,
        int BatchSize,
        int ChannelCapacity,
        int BufferSize,
        int LargeFileParallelism,
        long LargeFileThresholdBytes);

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
  prestage-report [opcje]
  apply-stage-plan [opcje]
  undo-quarantine [opcje]
  purge-quarantine [opcje]

Scan:
  scan <path> --db duplicates.db --profile sata-ssd
  scan "D:" --db duplicates.db --profile hdd
  scan "D:" --db duplicates.db --profile sata-ssd
  scan "D:" --db duplicates.db --profile nvme
  scan "C:\Users\You\Pictures" --db pictures.db --profile nvme --threads 4
  scan "D:\Photos" --db photos.db --include-ext .jpg,.jpeg,.png --include-no-extension

Opcje scan:
  <path>                           Dowolny dysk lub katalog do skanowania
  --db <plik>                      Domyślnie duplicates.db
  --profile hdd|sata-ssd|nvme      Domyślnie sata-ssd
                                   hdd       HDD / USB / stare PC / konserwatywne I/O
                                   sata-ssd  domyślny profil zbalansowany
                                   nvme      NVMe / nowy CPU / więcej RAM / agresywne kolejki i bufory
  --threads auto|1|2|4|8           Domyślnie z profilu
  --low-resource                   Alias profilu hdd; jawne flagi nadal nadpisują defaulty
  --batch-size <n>                 Ile rekordów SQLite na transakcję
  --channel-capacity <n>           Limit kolejki Channel<T>, czyli backpressure
  --buffer-size <512KB|1MB|...>    Bufor odczytu pliku
  --large-file-parallelism <n>     Limit równoległego hashowania dużych plików
  --large-file-threshold <512MB>   Od jakiego rozmiaru stosować limit dużych plików
  --follow-reparse-points          Domyślnie wyłączone
  --record-skipped                 Zapisuje pominięte wpisy do DB, może zwiększyć bazę
  --include-ext .jpg,.png,.mp4     Skanuje tylko wybrane rozszerzenia z bezpiecznej listy użytkownika
  --include-no-extension           Dołącza pliki bez rozszerzenia; domyślnie są pomijane
  Safety: scan only records metadata/hashes. It does not move or delete files.

Duplicates:
  duplicates --db duplicates.db
  duplicates --db duplicates.db --min-size 1MB
  duplicates --db duplicates.db --min-size 1MB --export duplicates.csv
  Safety: read-only against an existing DB; reports exact size + SHA-256 groups.

Prestage report:
  prestage-report --db duplicates.db --out prestage-report.html
  prestage-report --db duplicates.db --out prestage-report.html --force
  prestage-report --db C:\TEST\duplicates.db --out C:\TEST\prestage-report.html

  Generates an interactive local dark themed HTML review report for exact duplicate groups.
  The report lets you choose keep/stage candidates and export stage-plan.json.
  It does not move or delete files.

Opcje prestage-report:
  --db <plik>                      Istniejąca baza po skanowaniu, domyślnie duplicates.db
  --out <html>                     Ścieżka raportu HTML, wymagana
  --force                          Nadpisuje istniejący raport HTML
  Safety: read-only against the DB. The HTML only exports stage-plan.json.

Apply stage plan:
  apply-stage-plan --plan stage-plan.json --dry-run
  apply-stage-plan --plan stage-plan.json --quarantine "D:\DuplFinder-Quarantine"

  Default is dry-run/report only. Quarantine mode moves only selected stage_paths
  from the exported stage-plan.json into a unique session folder and writes
  duplfinder-quarantine-manifest.json for rollback. KEEP files are never moved.
  No files are permanently deleted.

Opcje apply-stage-plan:
  --plan <json>                     stage-plan.json z raportu prestage, wymagany
  --dry-run                         Tylko pokazuje plan, niczego nie przenosi
  --quarantine <folder>             Folder kwarantanny; tworzy session-* i manifest
  Safety: defaults to dry-run. Validates untrusted plan paths, size, and SHA-256.
          Moves only selected stage_paths. KEEP files are never moved.

Undo quarantine:
  undo-quarantine --manifest "D:\DuplFinder-Quarantine\session-...\duplfinder-quarantine-manifest.json" --dry-run
  undo-quarantine --manifest "D:\DuplFinder-Quarantine\session-...\duplfinder-quarantine-manifest.json" --restore

  Default is dry-run/report only. --restore is required to move files back.
  Undo verifies manifest schema, quarantine path containment, size and SHA-256,
  and never overwrites an existing original path. No files are deleted.

Opcje undo-quarantine:
  --manifest <json>                 Manifest kwarantanny, wymagany
  --dry-run                         Tylko pokazuje plan, domyślnie
  --restore                         Przywraca pliki z manifestu bez nadpisywania
  Safety: defaults to dry-run. Validates untrusted manifest paths, containment,
          size, and SHA-256. No permanent deletion.

Purge quarantine:
  purge-quarantine --manifest "D:\DuplFinder-Quarantine\session-...\duplfinder-quarantine-manifest.json" --dry-run
  purge-quarantine --manifest "D:\DuplFinder-Quarantine\session-...\duplfinder-quarantine-manifest.json" --confirm-purge

  Default is dry-run/report only. --confirm-purge is required to delete anything.
  Purge deletes only validated files inside the quarantine session that are listed
  in the manifest with status moved. It never deletes original_path or keep_path.

Opcje purge-quarantine:
  --manifest <json>                 Manifest kwarantanny, wymagany
  --dry-run                         Tylko pokazuje plan, domyślnie
  --confirm-purge                   Usuwa zweryfikowane pliki z kwarantanny
  Safety: validates untrusted manifest schema, paths, containment, size, SHA-256,
          local paths only, and refuses UNC, ADS, symlink/junction/reparse paths.

Stats:
  stats --db duplicates.db
  Safety: read-only stats against an existing DB.

Clean DB:
  clean-db --db duplicates.db
  Safety: removes stale DB records only. It does not delete files from disk.
""");
    }
}
