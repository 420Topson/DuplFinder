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
    private const long MaxJsonBytes = 64L * 1024 * 1024;
    private const long MaxStagePlanGroups = 100_000;
    private const long MaxStagePlanStagePaths = 1_000_000;
    private const long MaxManifestEntries = 1_000_000;

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
            quarantineRoot = NormalizeCommandPath(options.QuarantineRoot!, "quarantine root");
            Directory.CreateDirectory(quarantineRoot);
            EnsureNoReparsePointInPath(quarantineRoot, includeLeaf: true, "quarantine root");

            sessionPath = CreateUniqueSessionPath(quarantineRoot);
            Directory.CreateDirectory(sessionPath);
            EnsureNoReparsePointInPath(sessionPath, includeLeaf: true, "quarantine session");

            manifestPath = Path.Combine(sessionPath, "duplfinder-quarantine-manifest.json");
        }

        long planned = 0;
        long moved = 0;
        long skipped = 0;
        long failed = 0;

        Console.WriteLine(dryRun ? "Mode: dry-run" : "Mode: quarantine");

        foreach (var group in plan.Groups)
        {
            ct.ThrowIfCancellationRequested();

            if (!ValidateStagePlanGroup(group, out var groupSkipCount, out var groupReason))
            {
                Console.WriteLine($"SKIP group {group.GroupNumber}: {groupReason}");
                skipped = checked(skipped + groupSkipCount);
                continue;
            }

            if (!TryNormalizeDataPath(group.KeepPath, out var keepPath, out var keepPathReason))
            {
                Console.WriteLine($"SKIP group {group.GroupNumber}: invalid KEEP path: {keepPathReason}");
                skipped = checked(skipped + group.StagePaths.Count);
                continue;
            }

            var keep = await TryValidateFileAsync(keepPath, group.Size, group.Hash, $"KEEP group {group.GroupNumber}", ct);
            if (!keep.IsValid)
            {
                Console.WriteLine($"SKIP group {group.GroupNumber}: KEEP file is missing, inaccessible, reparse, or no longer matches.");
                skipped = checked(skipped + group.StagePaths.Count);
                continue;
            }

            var seenStagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawStagePath in group.StagePaths)
            {
                ct.ThrowIfCancellationRequested();

                if (!TryNormalizeDataPath(rawStagePath, out var stagePath, out var stagePathReason))
                {
                    Console.WriteLine($"SKIP group {group.GroupNumber}: invalid STAGE path: {stagePathReason}");
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
                    var destinationParent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrWhiteSpace(destinationParent))
                    {
                        Directory.CreateDirectory(destinationParent);
                        EnsureNoReparsePointInPath(destinationParent, includeLeaf: true, "quarantine destination parent");
                    }

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

        Console.WriteLine(dryRun ? "Mode: dry-run" : "Mode: restore");

        var manifestEntries = CountManifestEntries(manifest);
        long eligibleEntries = 0;
        long planned = 0;
        long restored = 0;
        long skipped = 0;
        long failed = 0;

        foreach (var entry in manifest.Entries.Where(e => string.Equals(e.Status, "moved", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            eligibleEntries++;

            var quarantine = await TryValidateManifestQuarantineFileAsync(entry, manifest, "restore", ct);
            if (!quarantine.IsValid)
            {
                skipped++;
                continue;
            }

            if (!TryNormalizeDataPath(entry.OriginalPath, out var originalPath, out var originalReason))
            {
                Console.WriteLine($"SKIP restore: invalid original path: {originalReason}");
                skipped++;
                continue;
            }

            if (SamePath(quarantine.QuarantinePath, originalPath))
            {
                Console.WriteLine($"SKIP restore: quarantine path equals original path: {quarantine.QuarantinePath}");
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
                Console.WriteLine($"PLAN restore: {quarantine.QuarantinePath} -> {originalPath}");
                continue;
            }

            try
            {
                var parent = Path.GetDirectoryName(originalPath);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    EnsureNoReparsePointInPath(parent, includeLeaf: true, "original destination parent");
                    Directory.CreateDirectory(parent);
                    EnsureNoReparsePointInPath(parent, includeLeaf: true, "original destination parent");
                }

                File.Move(quarantine.QuarantinePath, originalPath);
                restored++;
                Console.WriteLine($"RESTORED: {quarantine.QuarantinePath} -> {originalPath}");
            }
            catch (Exception ex) when (IsSafeIoException(ex))
            {
                failed++;
                Console.WriteLine($"SKIP restore: could not move {quarantine.QuarantinePath}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return new UndoQuarantineResult(
            DryRun: dryRun,
            ManifestEntries: manifestEntries,
            EligibleEntries: eligibleEntries,
            Planned: planned,
            Restored: restored,
            Skipped: skipped,
            Failed: failed);
    }

    public async Task<PurgeQuarantineResult> PurgeQuarantineAsync(PurgeQuarantineOptions options, CancellationToken ct)
    {
        var manifestPath = Path.GetFullPath(options.ManifestPath);
        var manifest = await LoadManifestAsync(manifestPath, ct);
        var dryRun = !options.ConfirmPurge;

        Console.WriteLine(dryRun ? "Mode: dry-run" : "Mode: purge");

        var manifestEntries = CountManifestEntries(manifest);
        long eligibleEntries = 0;
        long planned = 0;
        long purged = 0;
        long skipped = 0;
        long failed = 0;

        foreach (var entry in manifest.Entries.Where(e => string.Equals(e.Status, "moved", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            eligibleEntries++;

            var quarantine = await TryValidateManifestQuarantineFileAsync(entry, manifest, "purge", ct);
            if (!quarantine.IsValid)
            {
                skipped++;
                continue;
            }

            if (!TryNormalizeDataPath(entry.OriginalPath, out var originalPath, out var originalReason))
            {
                Console.WriteLine($"SKIP purge: invalid original path in manifest entry: {originalReason}");
                skipped++;
                continue;
            }

            if (SamePath(quarantine.QuarantinePath, originalPath))
            {
                Console.WriteLine($"SKIP purge: quarantine path equals original path, refusing deletion: {quarantine.QuarantinePath}");
                skipped++;
                continue;
            }

            planned++;

            if (dryRun)
            {
                Console.WriteLine($"PLAN purge: {quarantine.QuarantinePath}");
                continue;
            }

            try
            {
                EnsureNoReparsePointInPath(quarantine.QuarantinePath, includeLeaf: true, "purge target");
                if (Directory.Exists(quarantine.QuarantinePath))
                {
                    Console.WriteLine($"SKIP purge: target is a directory, not a file: {quarantine.QuarantinePath}");
                    skipped++;
                    continue;
                }

                if (!File.Exists(quarantine.QuarantinePath))
                {
                    Console.WriteLine($"SKIP purge: file does not exist: {quarantine.QuarantinePath}");
                    skipped++;
                    continue;
                }

                File.Delete(quarantine.QuarantinePath);
                purged++;
                Console.WriteLine($"PURGED: {quarantine.QuarantinePath}");
            }
            catch (Exception ex) when (IsSafeIoException(ex))
            {
                failed++;
                Console.WriteLine($"SKIP purge: could not delete {quarantine.QuarantinePath}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return new PurgeQuarantineResult(
            DryRun: dryRun,
            ManifestEntries: manifestEntries,
            EligibleEntries: eligibleEntries,
            Planned: planned,
            Purged: purged,
            Skipped: skipped,
            Failed: failed);
    }

    private static async Task<StagePlanDto> LoadStagePlanAsync(string path, CancellationToken ct)
    {
        var plan = await DeserializeJsonFileAsync<StagePlanDto>(path, "stage plan", ct);

        if (!string.Equals(plan.Schema, StagePlanSchema, StringComparison.Ordinal))
            throw new InvalidDataException($"Invalid stage plan schema. Expected {StagePlanSchema}.");

        if (plan.Groups is null)
            throw new InvalidDataException("Stage plan is missing required field: groups.");

        if (plan.Groups.Count > MaxStagePlanGroups)
            throw new InvalidDataException($"Stage plan has too many groups. Limit: {MaxStagePlanGroups}.");

        long stagePaths = 0;
        foreach (var group in plan.Groups)
        {
            if (group.StagePaths is null)
                throw new InvalidDataException($"Stage plan group {group.GroupNumber} is missing required field: stage_paths.");

            stagePaths = checked(stagePaths + group.StagePaths.Count);
            if (stagePaths > MaxStagePlanStagePaths)
                throw new InvalidDataException($"Stage plan has too many stage paths. Limit: {MaxStagePlanStagePaths}.");
        }

        return plan;
    }

    private static async Task<QuarantineManifestDto> LoadManifestAsync(string path, CancellationToken ct)
    {
        var manifest = await DeserializeJsonFileAsync<QuarantineManifestDto>(path, "quarantine manifest", ct);

        if (!string.Equals(manifest.Schema, ManifestSchema, StringComparison.Ordinal))
            throw new InvalidDataException($"Invalid quarantine manifest schema. Expected {ManifestSchema}.");

        if (!TryNormalizeDataPath(manifest.QuarantineRootPath, out var rootPath, out var rootReason))
            throw new InvalidDataException($"Invalid quarantine manifest root path: {rootReason}");

        if (!TryNormalizeDataPath(manifest.QuarantineSessionPath, out var sessionPath, out var sessionReason))
            throw new InvalidDataException($"Invalid quarantine manifest session path: {sessionReason}");

        if (!IsPathInside(sessionPath, rootPath))
            throw new InvalidDataException("Manifest quarantine session path is outside the quarantine root.");

        if (manifest.Entries is null)
            throw new InvalidDataException("Quarantine manifest is missing required field: entries.");

        if (manifest.Entries.Count > MaxManifestEntries)
            throw new InvalidDataException($"Quarantine manifest has too many entries. Limit: {MaxManifestEntries}.");

        var seenQuarantinePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenOriginalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries.Where(e => string.Equals(e.Status, "moved", StringComparison.OrdinalIgnoreCase)))
        {
            if (TryNormalizeDataPath(entry.QuarantinePath, out var quarantinePath, out _) &&
                !seenQuarantinePaths.Add(quarantinePath))
                throw new InvalidDataException($"Duplicate quarantine_path in manifest: {quarantinePath}");

            if (TryNormalizeDataPath(entry.OriginalPath, out var originalPath, out _) &&
                !seenOriginalPaths.Add(originalPath))
                throw new InvalidDataException($"Duplicate original_path in manifest: {originalPath}");
        }

        manifest.QuarantineRootPath = rootPath;
        manifest.QuarantineSessionPath = sessionPath;
        return manifest;
    }

    private static async Task<T> DeserializeJsonFileAsync<T>(string path, string label, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException($"{label} file not found: {info.FullName}", info.FullName);

        if (info.Length > MaxJsonBytes)
            throw new InvalidDataException($"{label} is too large. Limit: {MaxJsonBytes} bytes.");

        try
        {
            await using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct)
                ?? throw new InvalidDataException($"{label} JSON is empty or invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid JSON in {label}: {ex.Message}", ex);
        }
    }

    private static async Task WriteJsonNewFileAsync<T>(string path, T value, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, BufferSize, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct);
        await stream.WriteAsync("\n"u8.ToArray(), ct);
    }

    private static bool ValidateStagePlanGroup(StagePlanGroupDto group, out long skippedStagePaths, out string reason)
    {
        skippedStagePaths = group.StagePaths?.Count ?? 0;

        if (group.Size < 0)
        {
            reason = "negative size in stage plan.";
            return false;
        }

        if (!IsValidSha256(group.Hash))
        {
            reason = "invalid SHA-256 hash in stage plan.";
            return false;
        }

        if (group.StagePaths is null)
        {
            reason = "missing stage_paths.";
            skippedStagePaths = 0;
            return false;
        }

        reason = "";
        return true;
    }

    private static async Task<ManifestFileValidation> TryValidateManifestQuarantineFileAsync(
        QuarantineManifestEntryDto entry,
        QuarantineManifestDto manifest,
        string action,
        CancellationToken ct)
    {
        if (entry.Size < 0)
        {
            Console.WriteLine($"SKIP {action}: negative size in manifest entry.");
            return ManifestFileValidation.Invalid;
        }

        if (!IsValidSha256(entry.Sha256))
        {
            Console.WriteLine($"SKIP {action}: invalid SHA-256 in manifest entry.");
            return ManifestFileValidation.Invalid;
        }

        if (!IsValidSha256(entry.GroupHash))
        {
            Console.WriteLine($"SKIP {action}: invalid group_hash in manifest entry.");
            return ManifestFileValidation.Invalid;
        }

        if (!TryNormalizeDataPath(entry.QuarantinePath, out var quarantinePath, out var quarantineReason))
        {
            Console.WriteLine($"SKIP {action}: invalid quarantine path: {quarantineReason}");
            return ManifestFileValidation.Invalid;
        }

        if (!IsPathInside(quarantinePath, manifest.QuarantineSessionPath) || !IsPathInside(quarantinePath, manifest.QuarantineRootPath))
        {
            Console.WriteLine($"SKIP {action}: quarantine path is outside manifest quarantine session/root: {quarantinePath}");
            return ManifestFileValidation.Invalid;
        }

        var quarantined = await TryValidateFileAsync(quarantinePath, entry.Size, entry.Sha256, $"QUARANTINE {action}", ct);
        if (!quarantined.IsValid)
            return ManifestFileValidation.Invalid;

        return new ManifestFileValidation(true, quarantinePath, quarantined.Fingerprint);
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
            if (Directory.Exists(path))
            {
                Console.WriteLine($"SKIP {label}: path is a directory, not a file: {path}");
                return ValidationResult.Invalid;
            }

            if (!File.Exists(path))
            {
                Console.WriteLine($"SKIP {label}: file does not exist: {path}");
                return ValidationResult.Invalid;
            }

            if (ContainsReparsePointInPath(path, includeLeaf: true))
            {
                Console.WriteLine($"SKIP {label}: refusing reparse point/symlink/junction path: {path}");
                return ValidationResult.Invalid;
            }

            var fingerprint = await ComputeFingerprintAsync(path, ct);
            if (fingerprint.Size != expectedSize || !string.Equals(fingerprint.Hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"SKIP {label}: size/SHA-256 no longer matches manifest or stage plan: {path}");
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

    private static string NormalizeCommandPath(string path, string label)
    {
        var fullPath = Path.GetFullPath(path);
        if (IsUncPath(fullPath))
            throw new InvalidDataException($"{label} must be a local path. UNC/network paths are not supported for quarantine actions.");

        if (HasAlternateDataStreamSyntax(fullPath))
            throw new InvalidDataException($"{label} contains ':' outside the drive root, which is not allowed.");

        return fullPath;
    }

    private static bool TryNormalizeDataPath(string? path, out string normalized, out string reason)
    {
        normalized = "";

        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "path is empty.";
            return false;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            reason = "path is not fully qualified.";
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            reason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }

        if (IsUncPath(normalized))
        {
            reason = "UNC/network paths are not supported for destructive actions.";
            return false;
        }

        if (HasAlternateDataStreamSyntax(normalized))
        {
            reason = "Alternate Data Stream style paths are not allowed.";
            return false;
        }

        reason = "";
        return true;
    }

    private static bool IsValidSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            return false;

        foreach (var ch in value)
        {
            var isHex = ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex)
                return false;
        }

        return true;
    }

    private static bool IsUncPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal);

    private static bool HasAlternateDataStreamSyntax(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        var remainder = path.Length >= root.Length ? path[root.Length..] : path;
        return remainder.Contains(':', StringComparison.Ordinal);
    }

    private static void EnsureNoReparsePointInPath(string path, bool includeLeaf, string label)
    {
        if (ContainsReparsePointInPath(path, includeLeaf))
            throw new IOException($"{label} contains a reparse point, symlink, or junction.");
    }

    private static bool ContainsReparsePointInPath(string path, bool includeLeaf)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                return false;

            var parts = fullPath[root.Length..]
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            var current = root;
            for (var i = 0; i < parts.Length; i++)
            {
                if (!includeLeaf && i == parts.Length - 1)
                    break;

                current = Path.Combine(current, parts[i]);
                if (!File.Exists(current) && !Directory.Exists(current))
                    continue;

                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return true;
            }

            return false;
        }
        catch (Exception ex) when (IsSafeIoException(ex))
        {
            return true;
        }
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

    private static bool IsPathInside(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static long CountManifestEntries(QuarantineManifestDto manifest) => manifest.Entries.LongCount();

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

    private sealed record ManifestFileValidation(bool IsValid, string QuarantinePath, FileFingerprint Fingerprint)
    {
        public static ManifestFileValidation Invalid { get; } = new(false, "", new FileFingerprint(0, ""));
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
