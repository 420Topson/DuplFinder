using Microsoft.Data.Sqlite;
using DuplicateFinder.Models;
using DuplicateFinder.Utils;

namespace DuplicateFinder.Services;

public sealed class DuplicateService
{
    private readonly DatabaseService _database;

    public DuplicateService(DatabaseService database)
    {
        _database = database;
    }

    public async Task<List<DuplicateGroup>> GetDuplicateGroupsAsync(DuplicateOptions options, CancellationToken ct)
    {
        var groups = new List<DuplicateGroup>();
        await using var connection = _database.OpenConnection(options.DbPath);
        await connection.OpenAsync(ct);

        await using var groupCmd = connection.CreateCommand();
        groupCmd.CommandText = @"
SELECT size, hash, COUNT(*) AS copies
FROM files
WHERE hash IS NOT NULL AND status = 'ok' AND size >= $min_size
GROUP BY size, hash
HAVING COUNT(*) > 1
ORDER BY size DESC, copies DESC;";
        groupCmd.Parameters.AddWithValue("$min_size", options.MinSizeBytes);

        await using var groupReader = await groupCmd.ExecuteReaderAsync(ct);
        var temp = new List<(long Size, string Hash, int Copies)>();
        while (await groupReader.ReadAsync(ct))
        {
            temp.Add((groupReader.GetInt64(0), groupReader.GetString(1), groupReader.GetInt32(2)));
        }

        var groupNumber = 1;
        foreach (var g in temp)
        {
            var paths = await GetPathsForGroupAsync(connection, g.Size, g.Hash, ct);
            groups.Add(new DuplicateGroup
            {
                GroupNumber = groupNumber++,
                Size = g.Size,
                Hash = g.Hash,
                Copies = g.Copies,
                Paths = paths
            });
        }

        return groups;
    }

    public async Task PrintDuplicatesAsync(DuplicateOptions options, CancellationToken ct)
    {
        var groups = await GetDuplicateGroupsAsync(options, ct);

        if (groups.Count == 0)
        {
            Console.WriteLine("Nie znaleziono grup duplikatów dla podanych filtrów.");
            return;
        }

        foreach (var group in groups)
        {
            Console.WriteLine();
            Console.WriteLine($"Group {group.GroupNumber}");
            Console.WriteLine($"Size: {SizeParser.FormatBytes(group.Size)}");
            Console.WriteLine($"SHA-256: {group.Hash}");
            Console.WriteLine($"Copies: {group.Copies}");
            Console.WriteLine($"Potential saving: {SizeParser.FormatBytes(group.PotentialSaving)}");
            Console.WriteLine("Files:");

            for (var i = 0; i < group.Paths.Count; i++)
            {
                var marker = i == 0 ? "[keep?]" : "[dup ]";
                Console.WriteLine($"{marker} {group.Paths[i]}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Razem grup: {groups.Count}");
        Console.WriteLine($"Potencjalna oszczędność: {SizeParser.FormatBytes(groups.Sum(g => g.PotentialSaving))}");

        if (!string.IsNullOrWhiteSpace(options.ExportCsvPath))
        {
            await ExportCsvAsync(groups, options.ExportCsvPath, ct);
            Console.WriteLine($"CSV zapisany: {options.ExportCsvPath}");
        }
    }

    private static async Task<List<string>> GetPathsForGroupAsync(SqliteConnection connection, long size, string hash, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT path
FROM files
WHERE size = $size AND hash = $hash AND status = 'ok'
ORDER BY path;";
        cmd.Parameters.AddWithValue("$size", size);
        cmd.Parameters.AddWithValue("$hash", hash);

        var paths = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            paths.Add(reader.GetString(0));
        return paths;
    }

    private static async Task ExportCsvAsync(List<DuplicateGroup> groups, string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        await writer.WriteLineAsync("group,size_bytes,size,hint,hash,copies,potential_saving_bytes,potential_saving,path");
        foreach (var group in groups)
        {
            for (var i = 0; i < group.Paths.Count; i++)
            {
                var hint = i == 0 ? "keep?" : "dup";
                var line = string.Join(',',
                    group.GroupNumber.ToString(),
                    group.Size.ToString(),
                    CsvUtil.Escape(SizeParser.FormatBytes(group.Size)),
                    hint,
                    group.Hash,
                    group.Copies.ToString(),
                    group.PotentialSaving.ToString(),
                    CsvUtil.Escape(SizeParser.FormatBytes(group.PotentialSaving)),
                    CsvUtil.Escape(group.Paths[i]));

                await writer.WriteLineAsync(line.AsMemory(), ct);
            }
        }
    }
}
