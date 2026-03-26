using System.Text.Json;

namespace StockDataFetcher;

/// <summary>One year of financial data for a company.</summary>
public class FinancialRecord
{
    public string Ticker  { get; set; } = "";
    public string Country { get; set; } = "US";
    public int    Year    { get; set; }
    public string Currency{ get; set; } = "USD";
    public string CIK     { get; set; } = "";
    public string Filed   { get; set; } = "";
    public string End_Date{ get; set; } = "";

    public long?  Revenue                    { get; set; }
    public long?  Gross_Profit               { get; set; }
    public long?  Operating_Profit           { get; set; }
    public long?  Net_Profit                 { get; set; }
    public long?  Cash_From_Operations       { get; set; }
    public long?  PPE                        { get; set; }
    public long?  Diluted_Shares_Outstanding { get; set; }
}

public class SecEdgarFetcher : IDisposable
{
    private readonly HttpClient _http;
    private readonly SimpleCache _cache;
    private readonly bool _useCache;

    // In-memory CIK map (ticker → zero-padded 10-digit CIK)
    private readonly Dictionary<string, string> _cikMap = new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim _rateLimiter = new(1, 1);

    public SecEdgarFetcher(SimpleCache cache, bool useCache = true)
    {
        _cache = cache;
        _useCache = useCache;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "StockSelector financial-data-aggregator (+http://example.com/bot)");
        _http.Timeout = TimeSpan.FromSeconds(Config.ApiTimeout);

        LoadTickerMapping();
    }

    private void LoadTickerMapping()
    {
        var file = Path.Combine(Config.DataDir, "sec_ticker_cik_mapping.csv");
        if (!File.Exists(file)) return;

        try
        {
            foreach (var line in File.ReadLines(file).Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length >= 2)
                {
                    var ticker = parts[0].Trim().ToUpperInvariant();
                    var cik    = parts[1].Trim();
                    if (!string.IsNullOrEmpty(ticker) && !string.IsNullOrEmpty(cik))
                        _cikMap[ticker] = cik;
                }
            }
            Logger.Info($"Loaded {_cikMap.Count} ticker-CIK mappings");
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not load ticker mapping: {ex.Message}");
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct = default)
    {
        await _rateLimiter.WaitAsync(ct);
        try
        {
            const int maxRetries = 3;
            double delay = 1.0;
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var response = await _http.GetAsync(url, ct);
                    response.EnsureSuccessStatusCode();
                    var stream = await response.Content.ReadAsStreamAsync(ct);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    Logger.Warning($"Request failed (attempt {attempt + 1}), retrying in {delay}s: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                    delay *= 2;
                }
            }
            throw new InvalidOperationException($"All retries exhausted for {url}");
        }
        finally
        {
            await Task.Delay(TimeSpan.FromSeconds(Config.SecApiDelay));
            _rateLimiter.Release();
        }
    }

    public async Task<string?> GetCikFromTickerAsync(string ticker, CancellationToken ct = default)
    {
        if (_cikMap.TryGetValue(ticker, out var cached)) return cached;

        try
        {
            using var doc = await GetJsonAsync("https://www.sec.gov/files/company_tickers.json", ct);
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var obj = entry.Value;
                if (obj.TryGetProperty("ticker", out var t) &&
                    string.Equals(t.GetString(), ticker, StringComparison.OrdinalIgnoreCase))
                {
                    var cikStr = obj.GetProperty("cik_str").GetInt64().ToString().PadLeft(10, '0');
                    _cikMap[ticker] = cikStr;
                    Logger.Info($"Found CIK for {ticker}: {cikStr}");
                    return cikStr;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"SEC company tickers lookup failed: {ex.Message}");
        }

        Logger.Warning($"Could not find CIK for {ticker}");
        return null;
    }

    public async Task<JsonElement?> GetCompanyFactsAsync(string cik, CancellationToken ct = default)
    {
        if (_useCache)
        {
            var hit = _cache.Get("us", cik, "company_facts");
            if (hit is not null)
            {
                Logger.Info($"Cache hit for CIK {cik}");
                return hit;
            }
        }

        try
        {
            var url = $"https://data.sec.gov/api/xbrl/companyfacts/CIK{cik}.json";
            Logger.Info($"Fetching company facts from: {url}");

            using var doc = await GetJsonAsync(url, ct);
            var element = doc.RootElement.Clone();

            if (_useCache)
                _cache.Set("us", cik, "company_facts", element);

            return element;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to fetch company facts for CIK {cik}: {ex.Message}");
            return null;
        }
    }

    public List<FinancialRecord> ExtractFinancialMetrics(JsonElement facts, string ticker, string cik)
    {
        var fiscalData = new Dictionary<int, FinancialRecord>();

        try
        {
            if (!facts.TryGetProperty("facts", out var factsEl) ||
                !factsEl.TryGetProperty("us-gaap", out var usGaap))
            {
                Logger.Warning($"No us-gaap data found for {ticker}");
                return [];
            }

            foreach (var (label, candidates) in Config.XbrlConceptsUs)
            {
                // Iterate ALL candidates that exist in the filing and merge their data.
                // AAPL, for example, used `Revenues` through FY2018 then switched to
                // `RevenueFromContractWithCustomerExcludingAssessedTax` — stopping at
                // the first candidate found would miss all years from the other concept.
                // First-candidate wins when a fiscal year is provided by multiple concepts.
                var foundAny = false;
                foreach (var candidate in candidates)
                {
                    if (!usGaap.TryGetProperty(candidate, out var conceptEl))
                        continue;

                    foundAny = true;

                    if (!conceptEl.TryGetProperty("units", out var units))
                        continue;

                    // USD for monetary values, shares for share counts
                    JsonElement usdArr;
                    if (!units.TryGetProperty("USD", out usdArr) &&
                        !units.TryGetProperty("shares", out usdArr))
                        continue;

                    // Build a (endYear → best entry) map.
                    //
                    // Rules:
                    //  1. Only 10-K form entries.
                    //  2. Derive fiscal year from the `end` date, not the `fy` field.
                    //     The `fy` field identifies which 10-K filing the row came from;
                    //     each 10-K includes up to 3 comparative years all tagged with
                    //     the same `fy`, so using `fy` collapses multiple years into one.
                    //  3. For duration concepts (Revenue, Profit, Cash Flow) only accept
                    //     entries whose period (end − start) is 335–395 days, which
                    //     selects the full-year consolidated row and drops quarterly
                    //     segments, geographic sub-totals, and interim comparatives.
                    //     Point-in-time concepts (PPE, Shares) have no `start` date,
                    //     so the period filter does not apply.
                    //  4. When multiple 10-K filings report the same period (a later
                    //     filing restates an earlier year), keep the most recently filed.

                    var bestByYear = new Dictionary<int, (long Val, string Filed, string End)>();

                    foreach (var entry in usdArr.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("form", out var formEl)) continue;
                        if (!string.Equals(formEl.GetString(), "10-K", StringComparison.OrdinalIgnoreCase)) continue;

                        if (!entry.TryGetProperty("end", out var endEl)) continue;
                        if (!DateTime.TryParse(endEl.GetString(), out var endDate)) continue;

                        // Period-length filter for duration (flow) concepts
                        if (entry.TryGetProperty("start", out var startEl) &&
                            DateTime.TryParse(startEl.GetString(), out var startDate))
                        {
                            var days = (endDate - startDate).TotalDays;
                            if (days < 335 || days > 395) continue; // skip non-annual periods
                        }

                        int endYear = endDate.Year;

                        if (!entry.TryGetProperty("val", out var valEl)) continue;
                        long val = valEl.GetInt64();

                        string filed  = entry.TryGetProperty("filed", out var filedEl) ? filedEl.GetString() ?? "" : "";
                        string endStr = endEl.GetString() ?? "";

                        // Keep the most recently filed 10-K for this period
                        if (!bestByYear.TryGetValue(endYear, out var existing) ||
                            string.Compare(filed, existing.Filed, StringComparison.Ordinal) > 0)
                        {
                            bestByYear[endYear] = (val, filed, endStr);
                        }
                    }

                    foreach (var (endYear, best) in bestByYear)
                    {
                        if (!fiscalData.TryGetValue(endYear, out var rec))
                        {
                            rec = new FinancialRecord
                            {
                                Ticker   = ticker,
                                CIK      = cik,
                                Year     = endYear,
                                Currency = "USD",
                                Filed    = best.Filed,
                                End_Date = best.End,
                            };
                            fiscalData[endYear] = rec;
                        }

                        // First candidate wins — do not overwrite a value already set
                        // by a higher-priority concept for this year
                        switch (label)
                        {
                            case "Revenue":                    rec.Revenue                    ??= best.Val; break;
                            case "Gross_Profit":               rec.Gross_Profit               ??= best.Val; break;
                            case "Operating_Profit":           rec.Operating_Profit           ??= best.Val; break;
                            case "Net_Profit":                 rec.Net_Profit                 ??= best.Val; break;
                            case "Cash_From_Operations":       rec.Cash_From_Operations       ??= best.Val; break;
                            case "PPE":                        rec.PPE                        ??= best.Val; break;
                            case "Diluted_Shares_Outstanding": rec.Diluted_Shares_Outstanding ??= best.Val; break;
                        }
                    }
                }

                if (!foundAny)
                    Logger.Debug($"No concept found for '{label}' ({string.Join(", ", candidates)}) in {ticker}");
            } // end foreach (label, candidates)
        }
        catch (Exception ex)
        {
            Logger.Error($"Error extracting metrics for {ticker}: {ex.Message}");
        }

        var result = fiscalData
            .Where(kv => kv.Value.Revenue.HasValue)
            .OrderBy(kv => kv.Key)
            .Select(kv => kv.Value)
            .ToList();

        Logger.Info($"Extracted {result.Count} years of data for {ticker}");

        if (result.Count < Config.MinYearsRequired)
            Logger.Warning($"Only found {result.Count} years for {ticker}, minimum required is {Config.MinYearsRequired}");

        return result;
    }

    public async Task<List<FinancialRecord>> FetchTickerDataAsync(string ticker, CancellationToken ct = default)
    {
        Logger.Info($"Fetching SEC EDGAR data for {ticker}");

        var cik = await GetCikFromTickerAsync(ticker, ct);
        if (cik is null)
        {
            Logger.Error($"Could not find CIK for ticker {ticker}");
            return [];
        }

        var facts = await GetCompanyFactsAsync(cik, ct);
        if (facts is null)
        {
            Logger.Error($"Could not fetch company facts for {ticker}");
            return [];
        }

        return ExtractFinancialMetrics(facts.Value, ticker, cik);
    }

    public void Dispose() => _http.Dispose();
}

public static class SecEdgarFetcherHelper
{
    public static async Task<Dictionary<string, List<FinancialRecord>>> FetchUsStocksAsync(
        IEnumerable<string> tickers,
        bool useCache    = true,
        bool clearCache  = false,
        bool cacheOnly   = false,
        CancellationToken ct = default)
    {
        var cache = new SimpleCache();

        if (clearCache)
            cache.Clear();

        using var fetcher = new SecEdgarFetcher(cache, useCache);
        var results = new Dictionary<string, List<FinancialRecord>>(StringComparer.OrdinalIgnoreCase);

        foreach (var ticker in tickers)
        {
            try
            {
                var cik = await fetcher.GetCikFromTickerAsync(ticker, ct);
                if (cik is null)
                {
                    Logger.Error($"Could not find CIK for {ticker}");
                    continue;
                }

                if (useCache)
                {
                    var cached = cache.Get("us", cik, "company_facts");
                    if (cached is JsonElement el)
                    {
                        Logger.Info($"Using cached data for {ticker}");
                        var data = fetcher.ExtractFinancialMetrics(el, ticker, cik);
                        if (data.Count > 0) results[ticker] = data;
                        continue;
                    }
                }

                if (cacheOnly)
                {
                    Logger.Warning($"No cached data for {ticker} and cache_only=true, skipping");
                    continue;
                }

                var fresh = await fetcher.FetchTickerDataAsync(ticker, ct);
                if (fresh.Count > 0) results[ticker] = fresh;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error fetching data for {ticker}: {ex.Message}");
            }
        }

        return results;
    }
}
