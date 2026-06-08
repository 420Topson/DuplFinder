using Microsoft.Data.Sqlite;
using DuplicateFinder.Models;
using DuplicateFinder.Utils;
using System.Threading.Channels;

namespace DuplicateFinder.Services;

public sealed class DatabaseService
{
    public async Task InitializeAsync(string dbPath, CancellationToken ct)
    {
        await using var connection = OpenConnection(dbPath);
        await connection.OpenAsync(ct);
        await ApplyPragmasAsync(connection, ct);

        var sql = @"
CREATE TABLE IF NOT EXISTS files (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    path TEXT NOT NULL UNIQUE,
    file_name TEXT NOT NULL,
    extension TEXT NOT NULL,
    size INTEGER NOT NULL DEFAULT 0,
    last_write_time_utc TEXT NULL,
    hash TEXT NULL,
    hash_prefix INTEGER NULL,
    hash_algorithm TEXT NULL,
    last_scan_time_utc TEXT NOT NULL,
    status TEXT NOT NULL,
    error_message TEXT NULL,
    is_skipped INTEGER NOT NULL DEFAULT 0,
    skip_reason TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_files_size ON files(size);
CREATE INDEX IF NOT EXISTS idx_files_hash ON files(hash);
CREATE INDEX IF NOT EXISTS idx_files_size_hash ON files(size, hash);
CREATE INDEX IF NOT EXISTS idx_files_hash_prefix ON files(hash_prefix);
CREATE INDEX IF NOT EXISTS idx_files_status ON files(status);
CREATE INDEX IF NOT EXISTS idx_files_size_hash_ok ON files(size, hash) WHERE hash IS NOT NULL AND status = 'ok';
";
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public SqliteConnection OpenConnection(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        return new SqliteConnection(builder.ToString());
    }

    public async Task<FileHashResult?> TryGetCacheHitAsync(SqliteConnection connection, FileCandidate candidate, string algorithm, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT size, last_write_time_utc, hash, hash_prefix, hash_algorithm, status
FROM files
WHERE path = $path
LIMIT 1;";
        cmd.Parameters.AddWithValue("$path", candidate.Path);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var size = reader.GetInt64(0);
        var lastWriteIso = reader.IsDBNull(1) ? null : reader.GetString(1);
        var hash = reader.IsDBNull(2) ? null : reader.GetString(2);
        var hashPrefix = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
        var storedAlgorithm = reader.IsDBNull(4) ? null : reader.GetString(4);
        var status = reader.GetString(5);

        if (size == candidate.Size &&
            lastWriteIso == candidate.LastWriteIso &&
            !string.IsNullOrWhiteSpace(hash) &&
            string.Equals(storedAlgorithm, algorithm, StringComparison.OrdinalIgnoreCase) &&
            status == "ok")
        {
            return FileHashResult.FromCacheHit(candidate, hash, hashPrefix, algorithm);
        }

        return null;
    }

    public async Task WriteResultsAsync(
        ChannelReader<FileHashResult> input,
        ScanOptions options,
        ConsoleProgress progress,
        CancellationToken ct)
    {
        await using var connection = OpenConnection(options.DbPath);
        await connection.OpenAsync(ct);
        await ApplyPragmasAsync(connection, ct);

        var batch = new List<FileHashResult>(options.BatchSize);

        await foreach (var result in input.ReadAllAsync(ct))
        {
            batch.Add(result);
            if (batch.Count >= options.BatchSize)
            {
                await UpsertBatchAsync(connection, batch, ct);
                progress.Written(batch.Count);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await UpsertBatchAsync(connection, batch, ct);
            progress.Written(batch.Count);
        }
    }

    private async Task UpsertBatchAsync(SqliteConnection connection, List<FileHashResult> batch, CancellationToken ct)
    {
        _ = BatchBucketizer.BuildBuckets(batch);

        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = @"
INSERT INTO files
(path, file_name, extension, size, last_write_time_utc, hash, hash_prefix, hash_algorithm, last_scan_time_utc, status, error_message, is_skipped, skip_reason)
VALUES
($path, $file_name, $extension, $size, $last_write_time_utc, $hash, $hash_prefix, $hash_algorithm, $last_scan_time_utc, $status, $error_message, $is_skipped, $skip_reason)
ON CONFLICT(path) DO UPDATE SET
    file_name = excluded.file_name,
    extension = excluded.extension,
    size = excluded.size,
    last_write_time_utc = excluded.last_write_time_utc,
    hash = excluded.hash,
    hash_prefix = excluded.hash_prefix,
    hash_algorithm = excluded.hash_algorithm,
    last_scan_time_utc = excluded.last_scan_time_utc,
    status = excluded.status,
    error_message = excluded.error_message,
    is_skipped = excluded.is_skipped,
    skip_reason = excluded.skip_reason;";

        var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
        var pFileName = cmd.Parameters.Add("$file_name", SqliteType.Text);
        var pExtension = cmd.Parameters.Add("$extension", SqliteType.Text);
        var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
        var pLastWrite = cmd.Parameters.Add("$last_write_time_utc", SqliteType.Text);
        var pHash = cmd.Parameters.Add("$hash", SqliteType.Text);
        var pHashPrefix = cmd.Parameters.Add("$hash_prefix", SqliteType.Integer);
        var pHashAlgorithm = cmd.Parameters.Add("$hash_algorithm", SqliteType.Text);
        var pLastScan = cmd.Parameters.Add("$last_scan_time_utc", SqliteType.Text);
        var pStatus = cmd.Parameters.Add("$status", SqliteType.Text);
        var pError = cmd.Parameters.Add("$error_message", SqliteType.Text);
        var pSkipped = cmd.Parameters.Add("$is_skipped", SqliteType.Integer);
        var pSkipReason = cmd.Parameters.Add("$skip_reason", SqliteType.Text);

        await cmd.PrepareAsync(ct);

        foreach (var r in batch)
        {
            pPath.Value = r.Path;
            pFileName.Value = r.FileName;
            pExtension.Value = r.Extension;
            pSize.Value = r.Size;
            pLastWrite.Value = r.LastWriteTimeUtc?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value;
            pHash.Value = r.Hash ?? (object)DBNull.Value;
            pHashPrefix.Value = r.HashPrefix ?? (object)DBNull.Value;
            pHashAlgorithm.Value = r.HashAlgorithm;
            pLastScan.Value = r.LastScanTimeUtc.ToUniversalTime().ToString("O");
            pStatus.Value = r.Status;
            pError.Value = r.ErrorMessage ?? (object)DBNull.Value;
            pSkipped.Value = r.IsSkipped ? 1 : 0;
            pSkipReason.Value = r.SkipReason ?? (object)DBNull.Value;

            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<(long Total, long Hashed, long Errors, long Skipped, long DuplicateGroups, long PotentialSaving)> GetStatsAsync(string dbPath, CancellationToken ct)
    {
        await using var connection = OpenConnection(dbPath);
        await connection.OpenAsync(ct);

        var total = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM files;", ct);
        var hashed = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM files WHERE hash IS NOT NULL AND status = 'ok';", ct);
        var errors = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM files WHERE status = 'error';", ct);
        var skipped = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM files WHERE is_skipped = 1 OR status = 'skipped';", ct);
        var duplicateGroups = await ScalarLongAsync(connection, @"
SELECT COUNT(*) FROM (
    SELECT 1 FROM files
    WHERE hash IS NOT NULL AND status = 'ok'
    GROUP BY size, hash
    HAVING COUNT(*) > 1
);", ct);
        var potentialSaving = await ScalarLongAsync(connection, @"
SELECT COALESCE(SUM((copies - 1) * size), 0)
FROM (
    SELECT size, COUNT(*) AS copies
    FROM files
    WHERE hash IS NOT NULL AND status = 'ok'
    GROUP BY size, hash
    HAVING COUNT(*) > 1
);", ct);

        return (total, hashed, errors, skipped, duplicateGroups, potentialSaving);
    }

    public async Task<long> CleanMissingFilesAsync(string dbPath, int batchSize, CancellationToken ct)
    {
        await using var readConnection = OpenConnection(dbPath);
        await readConnection.OpenAsync(ct);
        await using var writeConnection = OpenConnection(dbPath);
        await writeConnection.OpenAsync(ct);

        var deleted = 0L;
        var toDelete = new List<string>(batchSize);

        await using var select = readConnection.CreateCommand();
        select.CommandText = "SELECT path FROM files WHERE status != 'skipped';";
        await using var reader = await select.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var path = reader.GetString(0);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                toDelete.Add(path);
                if (toDelete.Count >= batchSize)
                {
                    deleted += await DeletePathsAsync(writeConnection, toDelete, ct);
                    toDelete.Clear();
                }
            }
        }

        if (toDelete.Count > 0)
            deleted += await DeletePathsAsync(writeConnection, toDelete, ct);

        return deleted;
    }

    private static async Task<long> DeletePathsAsync(SqliteConnection connection, List<string> paths, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "DELETE FROM files WHERE path = $path;";
        var p = cmd.Parameters.Add("$path", SqliteType.Text);
        await cmd.PrepareAsync(ct);

        var deleted = 0L;
        foreach (var path in paths)
        {
            p.Value = path;
            deleted += await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return deleted;
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null || value == DBNull.Value ? 0 : Convert.ToInt64(value);
    }

    private static async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA busy_timeout=5000;
PRAGMA foreign_keys=ON;";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
