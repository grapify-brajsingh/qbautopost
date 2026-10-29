namespace QbAutopost.Core.Dashboard;

public static class Percentiles
{
    /// <summary>Nearest-rank percentile of an ascending list: the value at 1-based rank ceil(p/100 x n).</summary>
    public static int NearestRank(IReadOnlyList<int> sorted, int percent)
    {
        if (sorted.Count == 0) throw new ArgumentException("Cannot take a percentile of an empty list.", nameof(sorted));
        var p = Math.Clamp(percent, 0, 100);
        var rank = (int)Math.Ceiling(p * sorted.Count / 100.0);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }
}
