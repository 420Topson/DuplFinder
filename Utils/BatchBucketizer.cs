using DuplicateFinder.Models;

namespace DuplicateFinder.Utils;

public static class BatchBucketizer
{
    public static Dictionary<long, Dictionary<int, List<FileHashResult>>> BuildBuckets(IEnumerable<FileHashResult> results)
    {
        var matrix = new Dictionary<long, Dictionary<int, List<FileHashResult>>>();

        foreach (var result in results)
        {
            if (result.Hash is null || result.HashPrefix is null || result.Status != "ok")
                continue;

            if (!matrix.TryGetValue(result.Size, out var byPrefix))
            {
                byPrefix = new Dictionary<int, List<FileHashResult>>();
                matrix[result.Size] = byPrefix;
            }

            if (!byPrefix.TryGetValue(result.HashPrefix.Value, out var list))
            {
                list = new List<FileHashResult>();
                byPrefix[result.HashPrefix.Value] = list;
            }

            list.Add(result);
        }

        return matrix;
    }
}
