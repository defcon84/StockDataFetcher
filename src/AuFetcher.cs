using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StockDataFetcher;

public sealed record AuFilingInfo(
    string Date,
    string Headline,
    string PdfUrl,
    string IdsId,
    bool IsPriceSensitive);

public sealed class AuFetcher : IDisposable
{
    private const string AsxResearchApiBaseUrl = "https://asx.api.markitdigital.com/asx-research/1.0";

    private readonly HttpClient _http;
    private readonly SimpleCache _cache;
    private readonly bool _useCache;

    private readonly Dictionary<string, string> _tickerAsxMap =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim _rateLimiter = new(1, 1);

    private static readonly string[] AnnualHeadlineKeywords =
    [
        "annual report",
        "annual financial report",
        "full year",
        "fy",
        "appendix 4e",
        "appendix 4e and annual report",
        "preliminary final report",
        "year ended",
    ];

    public AuFetcher(SimpleCache cache, bool useCache = true)
    {
        _cache = cache;
        _useCache = useCache;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Config.ApiTimeout) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "StockSelector financial-data-aggregator (+http://example.com/bot)");

        LoadTickerMapping();
    }

    private void LoadTickerMapping()
    {
        var file = Path.Combine(Config.DataDir, "asx_ticker_mapping.csv");
        if (!File.Exists(file)) return;

        try
        {
            foreach (var line in File.ReadLines(file).Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length < 2) continue;

                var ticker = parts[0].Trim().ToUpperInvariant();
                var asxCode = parts[1].Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(ticker) && !string.IsNullOrEmpty(asxCode))
                    _tickerAsxMap[ticker] = asxCode;
            }

            Logger.Info($"Loaded {_tickerAsxMap.Count} ticker-ASX mapping(s)");
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not load ASX mapping file: {ex.Message}");
        }
    }

    public Task<string?> GetAsxCodeFromTickerAsync(string ticker, CancellationToken ct = default)
    {
        _tickerAsxMap.TryGetValue(ticker, out var asxCode);
        if (string.IsNullOrWhiteSpace(asxCode))
        {
            Logger.Warning(
                $"Could not find ASX code for ticker '{ticker}'. " +
                "Add an entry to data/asx_ticker_mapping.csv (columns: ticker,asx_code).");
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(asxCode);
    }

    internal bool HasCachedAnnouncements(string tickerBase)
    {
        if (!_tickerAsxMap.TryGetValue(tickerBase, out var asxCode))
            return false;

        var currentYear = DateTime.UtcNow.Year;
        for (int year = currentYear; year >= currentYear - 10; year--)
        {
            if (_cache.Get("au", asxCode, $"announcements_{year}") is not null)
                return true;
        }

        return false;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct = default)
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
                    return await response.Content.ReadAsStringAsync(ct);
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    Logger.Warning($"Request failed (attempt {attempt + 1}), retrying in {delay:F0}s: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                    delay *= 2;
                }
            }

            throw new InvalidOperationException($"All retries exhausted for {url}");
        }
        finally
        {
            await Task.Delay(TimeSpan.FromSeconds(Config.AsxApiDelay), ct);
            _rateLimiter.Release();
        }
    }

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct = default)
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
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    return doc.RootElement.Clone();
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    Logger.Warning($"Request failed (attempt {attempt + 1}), retrying in {delay:F0}s: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                    delay *= 2;
                }
            }

            throw new InvalidOperationException($"All retries exhausted for {url}");
        }
        finally
        {
            await Task.Delay(TimeSpan.FromSeconds(Config.AsxApiDelay), ct);
            _rateLimiter.Release();
        }
    }

    private async Task<JsonElement?> GetCompanyKeyStatisticsAsync(string asxCode, CancellationToken ct = default)
    {
        if (_useCache)
        {
            var hit = _cache.Get("au", asxCode, "key_statistics");
            if (hit is JsonElement el)
            {
                Logger.Debug($"Cache hit: ASX key statistics for {asxCode}");
                return el;
            }
        }

        var url = $"{AsxResearchApiBaseUrl}/companies/{Uri.EscapeDataString(asxCode)}/key-statistics";
        Logger.Debug($"Fetching ASX key statistics: {url}");

        var json = await GetJsonAsync(url, ct);
        if (json is JsonElement root && _useCache)
            _cache.Set("au", asxCode, "key_statistics", root);

        return json;
    }

    private async Task<List<AuFilingInfo>> GetFilingsForYearAsync(
        string asxCode,
        int year,
        CancellationToken ct = default)
    {
        var cacheKey = $"announcements_{year}";

        if (_useCache)
        {
            var hit = _cache.Get("au", asxCode, cacheKey);
            if (hit is JsonElement el)
            {
                var cached = ParseCachedFilings(el);
                if (cached.Count > 0)
                {
                    Logger.Debug($"Cache hit: ASX announcements for {asxCode} {year}");
                    return cached;
                }
            }
        }

        var url =
            $"{Config.AsxHistoricalAnnouncementsBaseUrl}?by=asxCode&asxCode={Uri.EscapeDataString(asxCode)}&timeframe=Y&year={year}";
        Logger.Debug($"Fetching ASX announcements: {url}");

        var html = await GetStringAsync(url, ct);
        var parsed = ParseFilingsFromHtml(html);

        if (_useCache)
        {
            var arr = JsonSerializer.SerializeToElement(parsed);
            _cache.Set("au", asxCode, cacheKey, arr);
        }

        return parsed;
    }

    private static List<AuFilingInfo> ParseCachedFilings(JsonElement element)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<List<AuFilingInfo>>(element.GetRawText());
            return parsed ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static List<AuFilingInfo> ParseFilingsFromHtml(string html)
    {
        var outList = new List<AuFilingInfo>();
        if (string.IsNullOrWhiteSpace(html)) return outList;

        var rowRegex = new Regex(
            @"<tr>\s*<td>\s*(?<date>\d{1,2}/\d{1,2}/\d{4})\s*<br>.*?</td>\s*<td class=""pricesens"".*?</td>\s*<td>\s*<a[^>]*href=""(?<href>[^""]*displayAnnouncement\.do\?display=pdf&amp;idsId=(?<ids>\d+)[^""]*)""[^>]*>\s*(?<headline>.*?)<br>.*?</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match m in rowRegex.Matches(html))
        {
            var date = HtmlToPlainText(m.Groups["date"].Value).Trim();
            var headline = HtmlToPlainText(m.Groups["headline"].Value).Trim();
            var href = m.Groups["href"].Value.Replace("&amp;", "&", StringComparison.Ordinal);
            var ids = m.Groups["ids"].Value.Trim();

            if (string.IsNullOrEmpty(date) || string.IsNullOrEmpty(headline) || string.IsNullOrEmpty(href))
                continue;

            var fullUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : "https://www.asx.com.au" + href;

            var isPriceSensitive = m.Value.Contains("pricesens", StringComparison.OrdinalIgnoreCase) &&
                                   !m.Value.Contains("&nbsp;", StringComparison.OrdinalIgnoreCase);

            outList.Add(new AuFilingInfo(date, headline, fullUrl, ids, isPriceSensitive));
        }

        return outList;
    }

    private static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var noTags = Regex.Replace(html, "<.*?>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        return Regex.Replace(decoded, "\\s+", " ").Trim();
    }

    private static bool IsAnnualFilingHeadline(string headline)
    {
        if (string.IsNullOrWhiteSpace(headline)) return false;
        var lower = headline.ToLowerInvariant();
        return AnnualHeadlineKeywords.Any(k => lower.Contains(k, StringComparison.Ordinal));
    }

    private static int ScoreAnnualHeadline(string headline)
    {
        var h = headline.ToLowerInvariant();
        if (h.Contains("annual financial report", StringComparison.Ordinal)) return 100;
        if (h.Contains("annual report", StringComparison.Ordinal)) return 90;
        if (h.Contains("appendix 4e", StringComparison.Ordinal)) return 80;
        if (h.Contains("full year", StringComparison.Ordinal)) return 70;
        if (h.Contains("preliminary final report", StringComparison.Ordinal)) return 60;
        if (h.Contains("year ended", StringComparison.Ordinal)) return 50;
        if (h.Contains("fy", StringComparison.Ordinal)) return 40;
        return 10;
    }

    private static bool TryGetLong(JsonElement obj, string prop, out long value)
    {
        value = 0;
        if (!obj.TryGetProperty(prop, out var el)) return false;

        try
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Number:
                    if (el.TryGetInt64(out value)) return true;
                    if (el.TryGetDouble(out var dNum))
                    {
                        value = (long)Math.Round(dNum, MidpointRounding.AwayFromZero);
                        return true;
                    }
                    return false;

                case JsonValueKind.String:
                    if (long.TryParse(el.GetString(), out value)) return true;
                    if (double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dStr))
                    {
                        value = (long)Math.Round(dStr, MidpointRounding.AwayFromZero);
                        return true;
                    }
                    return false;

                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParsePeriodYear(string period, out int year)
    {
        year = 0;
        if (string.IsNullOrWhiteSpace(period) || period.Length < 4) return false;
        return int.TryParse(period[..4], out year);
    }

    private static void MergeStructuredFinancials(
        Dictionary<int, FinancialRecord> byYear,
        JsonElement keyStatsData,
        string tickerBase,
        string asxCode)
    {
        var defaultCurrency = keyStatsData.TryGetProperty("dividendCurrency", out var ccyEl)
            ? (ccyEl.GetString() ?? "AUD")
            : "AUD";

        long? shares = null;
        if (TryGetLong(keyStatsData, "numOfShares", out var sharesVal))
            shares = sharesVal;

        if (!keyStatsData.TryGetProperty("incomeStatement", out var income) || income.ValueKind != JsonValueKind.Array)
            return;

        foreach (var row in income.EnumerateArray())
        {
            var period = row.TryGetProperty("period", out var pEl) ? pEl.GetString() ?? "" : "";
            if (!TryParsePeriodYear(period, out var year)) continue;

            if (!byYear.TryGetValue(year, out var rec))
            {
                rec = new FinancialRecord
                {
                    Ticker = tickerBase,
                    Country = "au",
                    Year = year,
                    Currency = defaultCurrency,
                    CIK = asxCode,
                    Filed = $"{year}-12-31",
                    End_Date = $"{year}-12-31",
                };
                byYear[year] = rec;
            }

            if (row.TryGetProperty("curCode", out var rowCur) && !string.IsNullOrWhiteSpace(rowCur.GetString()))
                rec.Currency = rowCur.GetString()!;

            if (TryGetLong(row, "revenue", out var revenue))
                rec.Revenue ??= revenue;

            if (TryGetLong(row, "netIncome", out var netIncome))
                rec.Net_Profit ??= netIncome;

            if (shares.HasValue)
                rec.Diluted_Shares_Outstanding ??= shares.Value;
        }
    }

    private static bool NeedsDocumentFallback(FinancialRecord rec) =>
        !rec.Revenue.HasValue;

    public async Task<List<FinancialRecord>> FetchTickerDataAsync(string rawTicker, CancellationToken ct = default)
    {
        var (tickerBase, _) = TickerUtils.Normalize(rawTicker);
        var asxCode = await GetAsxCodeFromTickerAsync(tickerBase, ct);
        if (asxCode is null) return [];

        var nowYear = DateTime.UtcNow.Year;
        var allFilings = new List<AuFilingInfo>();

        // Pull enough annual history to satisfy the minimum years requirement.
        for (int year = nowYear; year >= nowYear - 10; year--)
        {
            var perYear = await GetFilingsForYearAsync(asxCode, year, ct);
            if (perYear.Count > 0)
                allFilings.AddRange(perYear);
        }

        if (allFilings.Count == 0)
        {
            Logger.Warning($"No ASX announcements found for {tickerBase} ({asxCode})");
            return [];
        }

        var annualByYear = allFilings
            .Where(f => IsAnnualFilingHeadline(f.Headline))
            .Select(f =>
            {
                var ok = DateTime.TryParseExact(
                    f.Date,
                    "d/M/yyyy",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed);
                return new { Filing = f, Parsed = ok ? parsed : DateTime.MinValue };
            })
            .Where(x => x.Parsed != DateTime.MinValue)
            .GroupBy(x => x.Parsed.Year)
            .Select(g => g
                .OrderByDescending(x => ScoreAnnualHeadline(x.Filing.Headline))
                .ThenByDescending(x => x.Parsed)
                .First())
            .OrderBy(x => x.Parsed.Year)
            .ToList();

        var byYear = annualByYear
            .Select(x => new FinancialRecord
            {
                Ticker = tickerBase,
                Country = "au",
                Year = x.Parsed.Year,
                Currency = "AUD",
                CIK = asxCode,
                Filed = x.Parsed.ToString("yyyy-MM-dd"),
                End_Date = x.Parsed.ToString("yyyy-MM-dd"),
            })
            .ToDictionary(r => r.Year, r => r);

        var keyStats = await GetCompanyKeyStatisticsAsync(asxCode, ct);
        if (keyStats is JsonElement root &&
            root.TryGetProperty("data", out var keyStatsData) &&
            keyStatsData.ValueKind == JsonValueKind.Object)
        {
            MergeStructuredFinancials(byYear, keyStatsData, tickerBase, asxCode);
        }

        var extractor = new PdfStatementExtractor(_http, _cache, _useCache);
        foreach (var annual in annualByYear)
        {
            if (!byYear.TryGetValue(annual.Parsed.Year, out var rec))
                continue;

            if (!NeedsDocumentFallback(rec))
                continue;

            if (string.IsNullOrWhiteSpace(annual.Filing.PdfUrl))
                continue;

            var docCacheKey = string.IsNullOrWhiteSpace(annual.Filing.IdsId)
                ? $"{annual.Parsed.Year}_{Math.Abs(annual.Filing.PdfUrl.GetHashCode()):x}"
                : annual.Filing.IdsId;

            var extracted = await extractor.ExtractFromDocumentAsync(
                "au",
                asxCode,
                docCacheKey,
                annual.Filing.PdfUrl,
                ct);

            PdfStatementExtractor.FillMissingMetrics(rec, extracted);
        }

        var records = byYear.Values.OrderBy(r => r.Year).ToList();

        Logger.Info($"Extracted {records.Count} annual filing year(s) for {tickerBase} from ASX announcements");

        if (records.Count < Config.MinYearsRequired)
            Logger.Warning(
                $"Only found {records.Count} annual filing year(s) for {tickerBase}; " +
                $"minimum required is {Config.MinYearsRequired}");

        return records;
    }

    public void Dispose() => _http.Dispose();
}

public static class AuFetcherHelper
{
    public static async Task<Dictionary<string, List<FinancialRecord>>> FetchAuStocksAsync(
        IEnumerable<string> rawTickers,
        bool useCache = true,
        bool clearCache = false,
        bool cacheOnly = false,
        CancellationToken ct = default)
    {
        var cache = new SimpleCache();
        if (clearCache) cache.Clear();

        using var fetcher = new AuFetcher(cache, useCache: useCache || cacheOnly);
        var results = new Dictionary<string, List<FinancialRecord>>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawTicker in rawTickers)
        {
            var (tickerBase, _) = TickerUtils.Normalize(rawTicker);
            try
            {
                if (cacheOnly && !fetcher.HasCachedAnnouncements(tickerBase))
                {
                    Logger.Warning(
                        $"No cached ASX announcements for '{tickerBase}' and --cache-only is set, skipping.");
                    continue;
                }

                var records = await fetcher.FetchTickerDataAsync(rawTicker, ct);
                if (records.Count > 0)
                    results[tickerBase] = records;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error fetching ASX data for {rawTicker}: {ex.Message}");
            }
        }

        return results;
    }
}
