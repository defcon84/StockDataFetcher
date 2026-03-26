namespace StockDataFetcher;

public static class TickerUtils
{
    /// <summary>Returns (baseTicker, countryCode) pair.</summary>
    public static (string Ticker, string Country) Normalize(string ticker)
    {
        var upper = ticker.ToUpperInvariant().Trim();

        foreach (var (suffix, country) in Config.TickerSuffixMap)
            if (upper.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return (upper[..^suffix.Length], country);

        return (upper, "us");
    }
}
