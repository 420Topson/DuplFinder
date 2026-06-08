using System.Globalization;

namespace DuplicateFinder.Utils;

public static class SizeParser
{
    public static long ParseBytes(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Empty size value.");

        var s = value.Trim().ToUpperInvariant().Replace(" ", "");
        var multiplier = 1L;

        if (s.EndsWith("KB")) { multiplier = 1024L; s = s[..^2]; }
        else if (s.EndsWith("K")) { multiplier = 1024L; s = s[..^1]; }
        else if (s.EndsWith("MB")) { multiplier = 1024L * 1024L; s = s[..^2]; }
        else if (s.EndsWith("M")) { multiplier = 1024L * 1024L; s = s[..^1]; }
        else if (s.EndsWith("GB")) { multiplier = 1024L * 1024L * 1024L; s = s[..^2]; }
        else if (s.EndsWith("G")) { multiplier = 1024L * 1024L * 1024L; s = s[..^1]; }
        else if (s.EndsWith("B")) { multiplier = 1L; s = s[..^1]; }

        if (!decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            throw new ArgumentException($"Invalid size value: {value}");

        return (long)(number * multiplier);
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
