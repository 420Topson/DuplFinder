using System.Threading.Channels;
using DuplicateFinder.Models;
using DuplicateFinder.Utils;

namespace DuplicateFinder.Services;

public sealed class FileEnumeratorService
{
    public async Task EnumerateAsync(
        ScanOptions options,
        ChannelWriter<FileCandidate> candidates,
        ChannelWriter<FileHashResult> results,
        ConsoleProgress progress,
        CancellationToken ct)
    {
        try
        {
            var root = NormalizeRoot(options.RootPath);
            var rootInfo = new DirectoryInfo(root);
            if (!rootInfo.Exists)
                throw new DirectoryNotFoundException(root);

            var stack = new Stack<DirectoryInfo>();
            stack.Push(rootInfo);

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var current = stack.Pop();
                if (PathFilters.ShouldSkipDirectory(current, options, out var dirReason))
                {
                    progress.Skipped();
                    if (options.RecordSkipped)
                        await results.WriteAsync(FileHashResult.FromSkipped(current.FullName, dirReason), ct);
                    continue;
                }

                using var entries = await TryEnumerateAsync(current, results, progress, ct);
                if (entries is null)
                    continue;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    FileSystemInfo entry;
                    try
                    {
                        if (!entries.MoveNext())
                            break;

                        entry = entries.Current;
                    }
                    catch (Exception ex) when (IsEnumerationException(ex))
                    {
                        progress.Error();
                        await results.WriteAsync(FileHashResult.FromSkipped(current.FullName, ex.GetType().Name + ": " + ex.Message), ct);
                        break;
                    }

                    try
                    {
                        if (entry is DirectoryInfo dir)
                        {
                            if (PathFilters.ShouldSkipDirectory(dir, options, out var reason))
                            {
                                progress.Skipped();
                                if (options.RecordSkipped)
                                    await results.WriteAsync(FileHashResult.FromSkipped(dir.FullName, reason), ct);
                            }
                            else
                            {
                                stack.Push(dir);
                            }
                        }
                        else if (entry is FileInfo file)
                        {
                            if (PathFilters.ShouldSkipFile(file, options, out var fileReason))
                            {
                                progress.Skipped();
                                if (options.RecordSkipped)
                                    await results.WriteAsync(FileHashResult.FromSkipped(file.FullName, fileReason), ct);
                                continue;
                            }

                            if (options.MinSizeBytes > 0 && file.Length < options.MinSizeBytes)
                            {
                                progress.Skipped();
                                if (options.RecordSkipped)
                                    await results.WriteAsync(FileHashResult.FromSkipped(file.FullName, $"below minimum size ({SizeParser.FormatBytes(options.MinSizeBytes)})"), ct);
                                continue;
                            }

                            var candidate = new FileCandidate(
                                file.FullName,
                                file.Name,
                                file.Extension.ToLowerInvariant(),
                                file.Length,
                                file.LastWriteTimeUtc);

                            progress.Candidate();
                            await candidates.WriteAsync(candidate, ct);
                        }
                    }
                    catch (Exception ex) when (IsEnumerationException(ex) || ex is FileNotFoundException)
                    {
                        progress.Error();
                        await results.WriteAsync(FileHashResult.FromSkipped(entry.FullName, ex.GetType().Name + ": " + ex.Message), ct);
                    }
                }
            }
        }
        finally
        {
            candidates.TryComplete();
        }
    }

    private static async Task<IEnumerator<FileSystemInfo>?> TryEnumerateAsync(
        DirectoryInfo directory,
        ChannelWriter<FileHashResult> results,
        ConsoleProgress progress,
        CancellationToken ct)
    {
        try
        {
            return directory.EnumerateFileSystemInfos().GetEnumerator();
        }
        catch (Exception ex) when (IsEnumerationException(ex))
        {
            progress.Error();
            await results.WriteAsync(FileHashResult.FromSkipped(directory.FullName, ex.GetType().Name + ": " + ex.Message), ct);
            return null;
        }
    }

    private static bool IsEnumerationException(Exception ex)
    {
        return ex is UnauthorizedAccessException or IOException or PathTooLongException or DirectoryNotFoundException;
    }

    private static string NormalizeRoot(string root)
    {
        if (root.Length == 2 && char.IsLetter(root[0]) && root[1] == ':')
            return root + Path.DirectorySeparatorChar;
        return root;
    }
}
