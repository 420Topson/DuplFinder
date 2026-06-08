using DuplicateFinder.Models;

namespace DuplicateFinder.Utils;

public static class PathFilters
{
    public static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".txt", ".md", ".rtf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".raw", ".cr2", ".nef", ".arw",
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".mpeg", ".mpg", ".m4v",
        ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a"
    };

    public static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".sys", ".exe", ".msi", ".drv", ".ocx", ".lnk", ".tmp", ".cache", ".dat"
    };

    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg", "pycache", "__pycache__", ".cache", "cache", "temp", "tmp",
        "$Recycle.Bin", "System Volume Information"
    };

    private static readonly string[] ExcludedFullPathPrefixes =
    [
        @"C:\Windows",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\ProgramData",
        @"C:\$Recycle.Bin"
    ];

    private static readonly string[] ExcludedPathFragments =
    [
        @"\AppData\Local\Microsoft\Windows",
        @"\AppData\Local\Temp"
    ];

    public static bool ShouldSkipDirectory(DirectoryInfo directory, ScanOptions options, out string reason)
    {
        reason = "";

        if (!options.FollowReparsePoints && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            reason = "reparse point / symlink / junction";
            return true;
        }

        if (ExcludedDirectoryNames.Contains(directory.Name))
        {
            reason = "excluded directory name";
            return true;
        }

        var full = Normalize(directory.FullName);

        foreach (var prefix in ExcludedFullPathPrefixes)
        {
            var normalizedPrefix = Normalize(prefix);
            if (full.Equals(normalizedPrefix, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(normalizedPrefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                reason = "excluded system directory";
                return true;
            }
        }

        foreach (var fragment in ExcludedPathFragments)
        {
            if (full.Contains(Normalize(fragment), StringComparison.OrdinalIgnoreCase))
            {
                reason = "excluded technical Windows/AppData directory";
                return true;
            }
        }

        return false;
    }

    public static bool ShouldSkipFile(FileInfo file, ScanOptions options, out string reason)
    {
        reason = "";

        if (!options.FollowReparsePoints && file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            reason = "reparse point / symlink";
            return true;
        }

        var ext = file.Extension;
        if (ExcludedExtensions.Contains(ext))
        {
            reason = "excluded technical extension";
            return true;
        }

        if (!AllowedExtensions.Contains(ext))
        {
            reason = "not in whitelist";
            return true;
        }

        return false;
    }

    private static string Normalize(string path)
    {
        return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
    }
}
