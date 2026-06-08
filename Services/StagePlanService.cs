using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DuplicateFinder.Models;

namespace DuplicateFinder.Services;

public sealed class StagePlanService
{
    private const string StagePlanSchema = "duplfinder.stage-plan.v1";
    private const string ManifestSchema = "duplfinder.quarantine-manifest.v1";
    private const int BufferSize = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<ApplyStagePlanResult> ApplyStagePlanAsync(ApplyStagePlanOptions options, CancellationToken ct)
    {
        var planPath = Path.GetFullPath(options.PlanPath);
        var plan = await LoadStagePlanAsync(planPath, ct);
        var dryRun = options.DryRun || string.IsNullOrWhiteSpace(options.QuarantineRoot);

        string? quarantineRoot = null;
        string? sessionPath = null;
        string? manifestPath = null;
        var manifestEntries = new List<QuarantineManifestEntryDto>();

        if (!dryRun)
        {
            quarantineRoot = Path.GetFullPath(options.QuarantineRoot!);
            Directory.CreateDirectory(quarantineRoot);
            sessionPath = CreateUniqueSessionPath(quarantineRoot);
            Directory.CreateDirectory(sessionPath);
            manifestPath = Path.Combine(sessionPath, "duplfinder-quarantine-manifest.json");
        }

        var planned = 0;
        var moved = 0;
        var skipped = 0;
        var failed = 0;

        Console.WriteLine(dryRun ? "Mode: dry-run" : "Mode: quarantine");

        foreach (var group in plan.Groups)
        {
            ct.ThrowIfCancellationRequested();

            if (group.Size < 0 || string.IsNullOrWhiteSpace(group.Hash))
            {
                Console.WriteLine($"SKIP group {group.GroupNumber}: invalid size/hash in stage plan.");
                skipped++;
                continue;
            }

            var keepPath = GetFullPathSafe(group.KeepPath);
            if (keepPath is null)
            {
                Console.WriteLine($"SKIP group {group.GroupNumber}: invalid KEEP path.");
                skipped += group.StagePaths.Count;
                continue;
            }

            var keep = await TryValidateFileAsync(keepPath, group.Size, group.Hash, $"KEEP group {group.GroupNumber}", ct);
            if (!keep.IsValid)
            {
                Console.WriteLine($"SKIP group {group.GroupNumber}: KEEP file is missing, inaccessible, reparse, or no longer matches.");
                skipped += group.StagePaths.Count;
                continue;
            }

            var seenStagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawStagePath in group.StagePaths)
            {
                ct.ThrowIfCancellationRequested();

                var stagePath = GetFullPathSafe(rawStagePath);
                if (stagePath is null)
                {
                    Console.WriteLine($"SKIP group {group.GroupNumber}: invalid STAGE path.");
                    skipped++;
                    continue;
                }

                if (!seenStagePaths.Add(stagePath))
                {
                    Console.WriteLine($"SKIP group {group.GroupNumber}: duplicate STAGE path in plan: {stagePath}");
                    skipped++;
                    continue;
                }

                if (SamePath(stagePath, keepPath))
                {
                    Console.WriteLine($"SKIP group {group.GroupNumber}: STAGE path equals KEEP path: {stagePath}");
                    skipped++;
                    continue;
                }

                var stage = await TryValidateFileAsync(stagePath, group.Size, group.Hash, $"STAGE group {group.GroupNumber}", ct);
                if (!stage.IsValid)
                {
                    skipped++;
                    continue;
                }

                var destination = dryRun
                    ? BuildPreviewQuarantinePath(options.QuarantineRoot, stagePath)
                    : GetUniquePath(BuildQuarantinePath(sessionPath!, stagePath));

                planned++;

                if (dryRun)
                {
                    Console.WriteLine($"PLAN group {group.GroupNumber}: {stagePath} -> {destination}");
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(stagePath, destination);
                    moved++;
                    Console.WriteLine($"MOVED group {group.GroupNumber}: {stagePath} -> {destination}");
                    manifestEntries.Add(new QuarantineManifestEntryDto
                    {
                        OriginalPath = stagePath,
                        QuarantinePath = destination,
                        Size = stage.Fingerprint.Size,
                        Sha256 = stage.Fingerprint.Hash,
                        GroupNumber = group.GroupNumber,
                        GroupHash = group.Hash,
                        MovedUtc = DateTimeOffset.UtcNow.ToString("O"),
                        Status = "moved"
                    });
                }
                catch (Exception ex) when (IsSafeIoException(ex))
                {
                    failed++;
                    Console.WriteLine($"SKIP group {group.GroupNumber}: could not move {stagePath}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        if (!dryRun)
        {
            var manifest = new QuarantineManifestDto
            {
                Schema = ManifestSchema,
                CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
                SourceStagePlanPath = planPath,
                QuarantineRootPath = quarantineRoot!,
                QuarantineSessionPath = sessionPath!,
                ToolVersion = GetToolVersion(),
                Entries = manifestEntries
            };

            await WriteJsonNewFileAsync(manifestPath!, manifest, ct);
            Console.WriteLine($"Manifest: {manifestPath}");
        }

        return new ApplyStagePlanResult(
            DryRun: dryRun,
            Groups: plan.Groups.Count,
            Planned: planned,
            Moved: moved,
            Skipped: skipped,
            Failed: failed,
            QuarantineSessionPath: sessionPath,
            ManifestPath: manifestPath);
    }

    public async Task<UndoQuarantineResult> UndoQuarantineAsync(UndoQuarantineOptions options, CancellationToken ct)
    {
        var manifestPath = Path.GetFullPath(options.ManifestPath);
        var manifest = await LoadManifestAsync(manifestPath, ct);
        var dryRun = !options.Restore;
        var rootPath = Path.GetFullPath(manifest.QuarantineRootPath);
        var sessionPath = Path.GetFullPath(manifest.QuarantineSessionPath);

        if (!IsPathInside(sessionPath, rootPath))
            throw new InvalidDataException("Manifest quarantine session path is outside the quarantine root.");

        Console.WriteLine(dryRun ? "Mode: dry-run" : "Mode: restore");

        var planned = 0;
        var restored = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var entry in manifest.Entries.Where(e => string.Equals(e.Status, "moved", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();

            var quarantinePath = GetFullPathSafe(entry.QuarantinePath);
            var originalPath = GetFullPathSafe(entry.OriginalPath);
            if (quarantinePath is null || originalPath is null)
            {
                Console.WriteLine("SKIP restore: invalid original/quarantine path in manifest entry.");
                skipped++;
                continue;
            }

            if (!IsPathInside(quarantinePath, sessionPath) || !IsPathInside(quarantinePath, rootPath))
            {
                Console.WriteLine($"SKIP restore: quarantine path is outside manifest quarantine session/root: {quarantinePath}");
                skipped++;
                continue;
            }

            var quarantined = await TryValidateFileAsync(quarantinePath, entry.Size, entry.Sha256, "QUARANTINE restore", ct);
            if (!quarantined.IsValid)
            {
                skipped++;
                continue;
            }

            if (File.Exists(originalPath) || Directory.Exists(originalPath))
            {
                Console.WriteLine($"SKIP restore: original path already exists, refusing to overwrite: {originalPath}");
                skipped++;
                continue;
            }

            planned++;

            if (dryRun)
            {
                Console.WriteLine($"PLAN restore: {quarantinePath} -> {originalPath}");
                continue;
            }

            try
            {
                var parent = Path.GetDirectoryName(originalPath);
                if (!string.IsNullOrWhiteSpace(parent))
                    Directory.CreateDirectory(parent);

                File.Move(quarantinePath, originalPath);
                restored++;
                Console.WriteLine($"RESTORED: {quarantinePath} -> {originalPath}");
            }
            catch (Exception ex) when (IsSafeIoException(ex))
            {
                failed++;
                Console.WriteLine($"SKIP restore: could not move {quarantinePath}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return new UndoQuarantineResult(
            DryRun: dryRun,
            Planned: planned,
            Restored: restored,
            Skipped: skipped,
            Failed: failed);
    }

    private static async Task<StagePlanDto> LoadStagePlanAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var plan = await JsonSerializer.DeserializeAsync<StagePlanDto>(stream, JsonOptions, ct)
            ?? throw new InvalidDataException("Stage plan JSON is empty or invalid.");

        if (!string.Equals(plan.Schema, StagePlanSchema, StringComparison.Ordinal))
            throw new InvalidDataException($"Invalid stage plan schema. Expected {StagePlanSchema}.");

        return plan;
    }

    private static async Task<QuarantineManifestDto> LoadManifestAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var manifest = await JsonSerializer.DeserializeAsync<QuarantineManifestDto>(stream, JsonOptions, ct)
            ?? throw new InvalidDataException("Quarantine manifest JSON is empty or invalid.");

        if (!string.Equals(manifest.Schema, ManifestSchema, StringComparison.Ordinal))
            throw new InvalidDataException($"Invalid quarantine manifest schema. Expected {ManifestSchema}.");

        return manifest;
    }

    private static async Task WriteJsonNewFileAsync<T>(string path, T value, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, BufferSize, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct);
        await stream.WriteAsync("\n"u8.ToArray(), ct);
    }

    private static async Task<ValidationResult> TryValidateFileAsync(
        string path,
        long expectedSize,
        string expectedHash,
        string label,
        CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"SKIP {label}: file does not exist: {path}");
                return ValidationResult.Invalid;
            }

            if (IsReparsePoint(path))
            {
                Console.WriteLine($"SKIP {label}: refusing reparse point/symlink path: {path}");
                return ValidationResult.Invalid;
            }

            var fingerprint = await ComputeFingerprintAsync(path, ct);
            if (fingerprint.Size != expectedSize || !string.Equals(fingerprint.Hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"SKIP {label}: size/SHA-256 no longer matches stage plan: {path}");
                return ValidationResult.Invalid;
            }

            return new ValidationResult(true, fingerprint);
        }
        catch (Exception ex) when (IsSafeIoException(ex))
        {
            Console.WriteLine($"SKIP {label}: could not read {path}: {ex.GetType().Name}: {ex.Message}");
            return ValidationResult.Invalid;
        }
    }

    private static async Task<FileFingerprint> ComputeFingerprintAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return new FileFingerprint(stream.Length, Convert.ToHexString(hash));
    }

    private static bool IsReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        return (attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static string CreateUniqueSessionPath(string quarantineRoot)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var sessionName = $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{suffix}";
            var candidate = Path.Combine(quarantineRoot, sessionName);
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
                return candidate;
        }

        throw new IOException("Could not create a unique quarantine session folder name.");
    }

    private static string BuildPreviewQuarantinePath(string? quarantineRoot, string originalPath)
    {
        var root = string.IsNullOrWhiteSpace(quarantineRoot)
            ? "<quarantine-root>"
            : Path.GetFullPath(quarantineRoot);

        return Path.Combine(root, "session-...", BuildQuarantineRelativePath(originalPath));
    }

    private static string BuildQuarantinePath(string sessionPath, string originalPath)
    {
        return Path.Combine(sessionPath, BuildQuarantineRelativePath(originalPath));
    }

    private static string BuildQuarantineRelativePath(string originalPath)
    {
        var fullPath = Path.GetFullPath(originalPath);
        var root = Path.GetPathRoot(fullPath) ?? "";
        var rootSegment = SanitizeRootSegment(root);
        var remainder = string.IsNullOrEmpty(root) ? fullPath : fullPath[root.Length..];
        var parts = remainder
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizePathSegment)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();

        return parts.Length == 0
            ? rootSegment
            : Path.Combine(new[] { rootSegment }.Concat(parts).ToArray());
    }

    private static string SanitizeRootSegment(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return "relative";

        var trimmed = root.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length >= 2 && root[1] == ':')
            return "drive-" + SanitizePathSegment(root[0].ToString());

        if (root.StartsWith(@"\\", StringComparison.Ordinal))
            return "unc-" + SanitizePathSegment(trimmed.Replace('\\', '-'));

        return "root-" + SanitizePathSegment(trimmed.Replace('\\', '-').Replace('/', '-'));
    }

    private static string SanitizePathSegment(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = segment.Select(ch => invalid.Contains(ch) || ch is ':' or '\\' or '/' ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        sanitized = sanitized.TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or ".." ? "_" : sanitized;
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;

        var directory = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var index = 1; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{name}__{index}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }

        throw new IOException($"Could not create a unique quarantine destination for: {path}");
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string? GetFullPathSafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsPathInside(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeIoException(Exception ex) =>
        ex is UnauthorizedAccessException or IOException or PathTooLongException or FileNotFoundException or DirectoryNotFoundException or NotSupportedException or SecurityException;

    private static string GetToolVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    private sealed record FileFingerprint(long Size, string Hash);

    private sealed record ValidationResult(bool IsValid, FileFingerprint Fingerprint)
    {
        public static ValidationResult Invalid { get; } = new(false, new FileFingerprint(0, ""));
    }

    private sealed class StagePlanDto
    {
        [JsonPropertyName("schema")]
        public string Schema { get; set; } = "";

        [JsonPropertyName("created_utc")]
        public string CreatedUtc { get; set; } = "";

        [JsonPropertyName("source_db")]
        public string SourceDb { get; set; } = "";

        [JsonPropertyName("source_report")]
        public string SourceReport { get; set; } = "";

        [JsonPropertyName("generator")]
        public string Generator { get; set; } = "";

        [JsonPropertyName("groups")]
        public List<StagePlanGroupDto> Groups { get; set; } = [];
    }

    private sealed class StagePlanGroupDto
    {
        [JsonPropertyName("group_number")]
        public int GroupNumber { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("hash")]
        public string Hash { get; set; } = "";

        [JsonPropertyName("keep_path")]
        public string KeepPath { get; set; } = "";

        [JsonPropertyName("stage_paths")]
        public List<string> StagePaths { get; set; } = [];
    }

    private sealed class QuarantineManifestDto
    {
        [JsonPropertyName("schema")]
        public string Schema { get; set; } = "";

        [JsonPropertyName("created_utc")]
        public string CreatedUtc { get; set; } = "";

        [JsonPropertyName("source_stage_plan_path")]
        public string SourceStagePlanPath { get; set; } = "";

        [JsonPropertyName("quarantine_root_path")]
        public string QuarantineRootPath { get; set; } = "";

        [JsonPropertyName("quarantine_session_path")]
        public string QuarantineSessionPath { get; set; } = "";

        [JsonPropertyName("tool_version")]
        public string ToolVersion { get; set; } = "";

        [JsonPropertyName("entries")]
        public List<QuarantineManifestEntryDto> Entries { get; set; } = [];
    }

    private sealed class QuarantineManifestEntryDto
    {
        [JsonPropertyName("original_path")]
        public string OriginalPath { get; set; } = "";

        [JsonPropertyName("quarantine_path")]
        public string QuarantinePath { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = "";

        [JsonPropertyName("group_number")]
        public int GroupNumber { get; set; }

        [JsonPropertyName("group_hash")]
        public string GroupHash { get; set; } = "";

        [JsonPropertyName("moved_utc")]
        public string MovedUtc { get; set; } = "";

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";
    }
}
