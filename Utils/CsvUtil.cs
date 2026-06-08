namespace DuplicateFinder.Utils;

public static class CsvUtil
{
    public static string Escape(string? value)
    {
        value ??= "";
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
