using System.Text.Json;

namespace StockDataFetcher;

/// <summary>Metadata for one ESEF filing retrieved from filings.xbrl.org.</summary>
public record FilingInfo(string JsonUrl, string PeriodEnd, string FxoId, string Processed, string DocumentUrl);

/// <summary>
/// Fetches financial data for EU companies from ESEF Inline XBRL filings
/// published at filings.xbrl.org.
///
/// Flow:
///   ticker (e.g. "ASML") → LEI via entity search API
///   LEI → list of ESEF filing entries
///   filing entry → xBRL-JSON facts document
///   facts → FinancialRecord per fiscal year
/// </summary>
public sealed class EsefFetcher : IDisposable
{
    private readonly HttpClient  _http;
    private readonly SimpleCache _cache;
    private readonly bool        _useCache;

    // Ticker (base, upper-case) → LEI mapping loaded from file + runtime lookups
    private readonly Dictionary<string, string> _tickerLeiMap =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim _rateLimiter = new(1, 1);

    private const string BaseUrl = "https://filings.xbrl.org";

    // Reverse lookup built at startup from Config.XbrlConceptsIfrs
    private static readonly Dictionary<string, string> _conceptToLabel = BuildConceptToLabel();

    private static Dictionary<string, string> BuildConceptToLabel()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, candidates) in Config.XbrlConceptsIfrs)
            foreach (var concept in candidates)
                map.TryAdd(concept, label);
        return map;
    }

    public EsefFetcher(SimpleCache cache, bool useCache = true)
    {
        _cache    = cache;
        _useCache = useCache;
        _http     = new HttpClient { Timeout = TimeSpan.FromSeconds(Config.ApiTimeout) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "StockSelector financial-data-aggregator (+http://example.com/bot)");
        LoadTickerMapping();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Ticker-to-LEI mapping (optional CSV file for manual overrides)
    // ──────────────────────────────────────────────────────────────────────────

    private void LoadTickerMapping()
    {
        var file = Path.Combine(Config.DataDir, "esef_ticker_lei_mapping.csv");
        if (!File.Exists(file)) return;
        try
        {
            foreach (var line in File.ReadLines(file).Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length >= 2)
                {
                    var t = parts[0].Trim().ToUpperInvariant();
                    var l = parts[1].Trim();
                    if (!string.IsNullOrEmpty(t) && !string.IsNullOrEmpty(l))
                        _tickerLeiMap[t] = l;
                }
            }
            Logger.Info($"Loaded {_tickerLeiMap.Count} EU ticker-LEI mapping(s)");
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not load EU ticker-LEI mapping: {ex.Message}");
        }
    }

    /// <summary>Returns true when the LEI for <paramref name="baseTicker"/> is
    /// already known (file or previous API lookup) without making an HTTP call.</summary>
    internal bool HasCachedLei(string baseTicker) =>
        _tickerLeiMap.ContainsKey(baseTicker.ToUpperInvariant());

    // ──────────────────────────────────────────────────────────────────────────
    // HTTP helpers
    // ──────────────────────────────────────────────────────────────────────────

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
                    Logger.Warning($"Request failed (attempt {attempt + 1}), retrying in {delay:F0}s: {ex.Message}");
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

    // ──────────────────────────────────────────────────────────────────────────
    // Entity (LEI) lookup
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a company's base ticker (e.g. "ASML") to its LEI by searching
    /// the filings.xbrl.org entity API.  Results are cached in-memory.
    /// </summary>
    public async Task<string?> GetLeiFromTickerAsync(string baseTicker,
                                                     CancellationToken ct = default)
    {
        if (_tickerLeiMap.TryGetValue(baseTicker, out var cached)) return cached;

        try
        {
            // flask-combo-jsonapi complex filter: case-insensitive partial name match
            var filter = $"[{{\"name\":\"name\",\"op\":\"ilike\",\"val\":\"%{baseTicker}%\"}}]";
            var url    = $"{BaseUrl}/api/entities?filter={Uri.EscapeDataString(filter)}&page[size]=10";
            Logger.Debug($"Searching ESEF entities for '{baseTicker}'");

            using var doc = await GetJsonAsync(url, ct);
            if (!doc.RootElement.TryGetProperty("data", out var data))
                return null;

            string? bestLei   = null;
            int     bestScore = int.MaxValue;

            foreach (var entity in data.EnumerateArray())
            {
                if (!entity.TryGetProperty("attributes", out var attrs)) continue;
                var name = attrs.TryGetProperty("name",       out var n) ? n.GetString() ?? "" : "";
                var lei  = attrs.TryGetProperty("identifier", out var i) ? i.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(lei)) continue;

                int score = ScoreName(name, baseTicker);
                Logger.Debug($"  Candidate: {name} ({lei}) score={score}");
                if (score < bestScore) { bestScore = score; bestLei = lei; }
            }

            if (bestLei is not null)
            {
                _tickerLeiMap[baseTicker] = bestLei;
                Logger.Info($"Found LEI for {baseTicker}: {bestLei}");
                return bestLei;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"ESEF entity search failed for '{baseTicker}': {ex.Message}");
        }

        Logger.Warning(
            $"Could not find LEI for EU ticker '{baseTicker}'. " +
            "Add an entry to data/esef_ticker_lei_mapping.csv (columns: Ticker,LEI) " +
            "to provide the mapping manually.");
        return null;
    }

    /// <summary>
    /// Returns an integer quality score for a candidate entity name against a
    /// ticker query.  Lower is better; 0 means the name starts exactly with the ticker.
    /// </summary>
    private static int ScoreName(string name, string ticker)
    {
        var n = name.ToUpperInvariant();
        var t = ticker.ToUpperInvariant();
        if (n == t)                                                               return 0;
        if (n.StartsWith(t + " ") || n.StartsWith(t + ".") || n.StartsWith(t + ",")) return 1;
        if (n.StartsWith(t))                                                      return 2;
        return 3;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Filing list
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<List<FilingInfo>> GetFilingsForEntityAsync(string lei,
                                                                  CancellationToken ct = default)
    {
        if (_useCache)
        {
            var hit = _cache.Get("eu", lei, "filings_index");
            if (hit is JsonElement el)
            {
                Logger.Info($"Cache hit: ESEF filings index for {lei}");
                return ParseFilingList(el);
            }
        }

        try
        {
            // Newest filings first; 50 is enough to cover 10+ years with language variants
            var url = $"{BaseUrl}/api/entities/{lei}/filings?sort=-period_end&page[size]=50";
            Logger.Info($"Fetching ESEF filing list for LEI {lei}");
            using var doc = await GetJsonAsync(url, ct);
            var element = doc.RootElement.Clone();
            if (_useCache) _cache.Set("eu", lei, "filings_index", element);
            return ParseFilingList(element);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to fetch ESEF filings for LEI {lei}: {ex.Message}");
            return [];
        }
    }

    private static List<FilingInfo> ParseFilingList(JsonElement root)
    {
        var result = new List<FilingInfo>();
        if (!root.TryGetProperty("data", out var data)) return result;
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("attributes", out var attrs)) continue;
            var jsonUrl   = attrs.TryGetProperty("json_url",   out var ju) ? ju.GetString() ?? "" : "";
            var periodEnd = attrs.TryGetProperty("period_end", out var pe) ? pe.GetString() ?? "" : "";
            var fxoId     = attrs.TryGetProperty("fxo_id",     out var fi) ? fi.GetString() ?? "" : "";
            var processed = attrs.TryGetProperty("processed",  out var pr) ? pr.GetString() ?? "" : "";
            var reportUrl = attrs.TryGetProperty("report_url", out var ru) ? ru.GetString() ?? "" : "";
            var htmlUrl   = attrs.TryGetProperty("html_url",   out var hu) ? hu.GetString() ?? "" : "";
            var xhtmlUrl  = attrs.TryGetProperty("xhtml_url",  out var xu) ? xu.GetString() ?? "" : "";

            var documentUrl = reportUrl;
            if (string.IsNullOrWhiteSpace(documentUrl)) documentUrl = htmlUrl;
            if (string.IsNullOrWhiteSpace(documentUrl)) documentUrl = xhtmlUrl;
            if (string.IsNullOrWhiteSpace(documentUrl) && jsonUrl.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                documentUrl = jsonUrl[..^5] + ".xhtml";

            if (!string.IsNullOrWhiteSpace(documentUrl) &&
                !documentUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                documentUrl = documentUrl.StartsWith("/", StringComparison.Ordinal)
                    ? BaseUrl + documentUrl
                    : $"{BaseUrl}/{documentUrl}";
            }

            if (!string.IsNullOrEmpty(jsonUrl))
                result.Add(new FilingInfo(jsonUrl, periodEnd, fxoId, processed, documentUrl));
        }
        return result;
    }

    private static bool NeedsDocumentFallback(FinancialRecord rec) =>
        !rec.Revenue.HasValue ||
        !rec.Gross_Profit.HasValue ||
        !rec.Operating_Profit.HasValue ||
        !rec.Net_Profit.HasValue ||
        !rec.Cash_From_Operations.HasValue ||
        !rec.PPE.HasValue ||
        !rec.Diluted_Shares_Outstanding.HasValue;

    // ──────────────────────────────────────────────────────────────────────────
    // Filing facts document
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<JsonElement?> GetFilingFactsAsync(string lei, FilingInfo filing,
                                                         CancellationToken ct = default)
    {
        // Use the FxoId as a filesystem-safe cache key; fall back to a hash
        var cacheKey = string.IsNullOrEmpty(filing.FxoId)
            ? $"facts_{Math.Abs(filing.JsonUrl.GetHashCode()):x}"
            : $"facts_{filing.FxoId}";

        if (_useCache)
        {
            var hit = _cache.Get("eu", lei, cacheKey);
            if (hit is JsonElement el)
            {
                Logger.Debug($"Cache hit: ESEF facts {filing.FxoId}");
                return el;
            }
        }

        try
        {
            var url = $"{BaseUrl}{filing.JsonUrl}";
            Logger.Info($"Fetching ESEF filing: {url}");
            using var doc = await GetJsonAsync(url, ct);
            var element = doc.RootElement.Clone();
            if (_useCache) _cache.Set("eu", lei, cacheKey, element);
            return element;
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to fetch ESEF filing {filing.FxoId}: {ex.Message}");
            return null;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // xBRL-JSON fact extraction
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses financial metrics out of one xBRL-JSON ESEF filing document.
    ///
    /// Filtering rules applied to facts:
    ///   • Only facts from the standard IFRS taxonomy (ifrs-full: namespace).
    ///   • Consolidated totals only – facts with any extra dimension (axis member)
    ///     are breakdowns and are skipped.
    ///   • Duration concepts: only accept full-year periods (335–395 days).
    ///   • Instant concepts (e.g. PPE): any period is allowed.
    ///   • First matching fact per (year, label) wins (filing is already newest-first).
    /// </summary>
    public List<FinancialRecord> ExtractFromFiling(
        JsonElement filingJson,
        string      ticker,
        string      lei,
        string      country,
        string      filingProcessed)
    {
        if (!filingJson.TryGetProperty("facts", out var facts))
            return [];

        // (year, label) → (value, currency, endDateStr)
        var yearData = new Dictionary<(int Year, string Label), (long Value, string Currency, string EndDate)>();

        foreach (var factProp in facts.EnumerateObject())
        {
            var factEl = factProp.Value;
            if (!factEl.TryGetProperty("dimensions", out var dims)) continue;
            if (!factEl.TryGetProperty("value",      out var valEl)) continue;

            // ── Skip breakdown facts (those with extra axes) ──────────────────
            bool hasExtraAxes = false;
            foreach (var dim in dims.EnumerateObject())
            {
                var k = dim.Name;
                if (k != "concept" && k != "entity" && k != "period" && k != "unit")
                {
                    hasExtraAxes = true;
                    break;
                }
            }
            if (hasExtraAxes) continue;

            if (!dims.TryGetProperty("concept", out var conceptEl)) continue;
            if (!dims.TryGetProperty("period",  out var periodEl))  continue;

            var concept   = conceptEl.GetString() ?? "";
            var periodStr = periodEl.GetString()  ?? "";

            // ── Only concepts we care about ───────────────────────────────────
            if (!_conceptToLabel.TryGetValue(concept, out var label)) continue;

            // ── Determine currency / unit ─────────────────────────────────────
            string currency = "EUR";
            if (dims.TryGetProperty("unit", out var unitEl))
            {
                var unit = unitEl.GetString() ?? "";
                if (unit.StartsWith("iso4217:", StringComparison.OrdinalIgnoreCase))
                    currency = unit["iso4217:".Length..];
                else if (unit.Equals("xbrli:shares", StringComparison.OrdinalIgnoreCase))
                    currency = "shares";
                else
                    continue; // skip non-monetary, non-share units (e.g. pure ratios)
            }

            // ── Parse the canonical value string ──────────────────────────────
            // xBRL-JSON with canonicalValues=true stores values as strings.
            // Take only the integer part to handle both "6860000000" and "4.10".
            var valueStr = valEl.ValueKind == JsonValueKind.String
                ? valEl.GetString() ?? ""
                : valEl.GetRawText();
            var intPart = valueStr.Split('.')[0];
            if (!long.TryParse(intPart,
                               System.Globalization.NumberStyles.AllowLeadingSign,
                               null,
                               out long value))
                continue;

            // ── Parse period → fiscal year ────────────────────────────────────
            bool     isDuration = periodStr.Contains('/');
            DateTime endDate;
            string   endDateStr;

            if (isDuration)
            {
                var slash = periodStr.IndexOf('/');
                if (!DateTime.TryParse(periodStr[..slash],        out var startDate)) continue;
                if (!DateTime.TryParse(periodStr[(slash + 1)..],  out endDate))       continue;
                endDateStr = periodStr[(slash + 1)..].Split('T')[0];

                // Only accept full-year (annual) periods
                var days = (endDate - startDate).TotalDays;
                if (days < 335 || days > 395) continue;
            }
            else
            {
                if (!DateTime.TryParse(periodStr, out endDate)) continue;
                endDateStr = periodStr.Split('T')[0];
            }

            // Derive fiscal year.  ESEF often uses end-exclusive ISO 8601 dates,
            // so "2022-01-01T00:00:00/2023-01-01T00:00:00" is FY 2022.
            int year = (endDate.Month == 1 && endDate.Day == 1) ? endDate.Year - 1 : endDate.Year;

            // First fact per (year, label) wins
            var key = (year, label);
            if (!yearData.ContainsKey(key))
                yearData[key] = (value, currency, endDateStr);
        }

        // ── Assemble FinancialRecord objects ──────────────────────────────────
        var byYear = new Dictionary<int, FinancialRecord>();

        foreach (var ((year, label), (value, currency, endDateStr)) in yearData)
        {
            if (!byYear.TryGetValue(year, out var rec))
            {
                rec = new FinancialRecord
                {
                    Ticker   = ticker,
                    Country  = country,
                    Year     = year,
                    Currency = currency == "shares" ? "EUR" : currency,
                    CIK      = lei,           // CIK field repurposed to store the LEI
                    Filed    = filingProcessed,
                    End_Date = endDateStr,
                };
                byYear[year] = rec;
            }
            else if (currency != "shares")
            {
                // Update currency if we learn a non-EUR denomination
                rec.Currency = currency;
            }

            switch (label)
            {
                case "Revenue":                    rec.Revenue                    ??= value; break;
                case "Gross_Profit":               rec.Gross_Profit               ??= value; break;
                case "Operating_Profit":           rec.Operating_Profit           ??= value; break;
                case "Net_Profit":                 rec.Net_Profit                 ??= value; break;
                case "Cash_From_Operations":       rec.Cash_From_Operations       ??= value; break;
                case "PPE":                        rec.PPE                        ??= value; break;
                case "Diluted_Shares_Outstanding": rec.Diluted_Shares_Outstanding ??= value; break;
            }
        }

        return byYear.Values.ToList();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // High-level entry point per ticker
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<List<FinancialRecord>> FetchTickerDataAsync(
        string rawTicker, CancellationToken ct = default)
    {
        var (baseTicker, _) = TickerUtils.Normalize(rawTicker);
        var country         = GetCountryCode(rawTicker);

        var lei = await GetLeiFromTickerAsync(baseTicker, ct);
        if (lei is null) return [];

        var filings = await GetFilingsForEntityAsync(lei, ct);
        if (filings.Count == 0)
        {
            Logger.Warning($"No ESEF filings found for {baseTicker} (LEI: {lei})");
            return [];
        }

        Logger.Info($"Processing {filings.Count} ESEF filing(s) for {baseTicker}");

        // Filings are sorted newest-first.  Process in order so that the first
        // value encountered for each (year, label) pair is the most recent one.
        var allByYear = new Dictionary<int, FinancialRecord>();
        var docUrlByYear = new Dictionary<int, string>();

        foreach (var filing in filings)
        {
            if (DateTime.TryParse(filing.PeriodEnd, out var periodDate) &&
                !string.IsNullOrWhiteSpace(filing.DocumentUrl) &&
                !docUrlByYear.ContainsKey(periodDate.Year))
            {
                docUrlByYear[periodDate.Year] = filing.DocumentUrl;
            }

            var facts = await GetFilingFactsAsync(lei, filing, ct);
            if (facts is null) continue;

            foreach (var rec in ExtractFromFiling(facts.Value, baseTicker, lei, country, filing.Processed))
            {
                if (!allByYear.TryGetValue(rec.Year, out var existing))
                {
                    allByYear[rec.Year] = rec;
                }
                else
                {
                    // Older filings may contain comparative-year data missing from newer ones
                    existing.Revenue                    ??= rec.Revenue;
                    existing.Gross_Profit               ??= rec.Gross_Profit;
                    existing.Operating_Profit           ??= rec.Operating_Profit;
                    existing.Net_Profit                 ??= rec.Net_Profit;
                    existing.Cash_From_Operations       ??= rec.Cash_From_Operations;
                    existing.PPE                        ??= rec.PPE;
                    existing.Diluted_Shares_Outstanding ??= rec.Diluted_Shares_Outstanding;
                }
            }
        }

        var extractor = new PdfStatementExtractor(_http, _cache, _useCache);
        foreach (var rec in allByYear.Values)
        {
            if (!NeedsDocumentFallback(rec))
                continue;

            if (!docUrlByYear.TryGetValue(rec.Year, out var docUrl) || string.IsNullOrWhiteSpace(docUrl))
                continue;

            var cacheKey = $"{rec.Year}_{Math.Abs(docUrl.GetHashCode()):x}";
            var extracted = await extractor.ExtractFromDocumentAsync(
                "eu",
                lei,
                cacheKey,
                docUrl,
                ct);

            PdfStatementExtractor.FillMissingMetrics(rec, extracted);
        }

        var result = allByYear
            .Where(kv => kv.Value.Revenue.HasValue)
            .OrderBy(kv => kv.Key)
            .Select(kv => kv.Value)
            .ToList();

        Logger.Info($"Extracted {result.Count} year(s) of ESEF data for {baseTicker}");

        if (result.Count < Config.MinYearsRequired)
            Logger.Warning(
                $"Only found {result.Count} year(s) for {baseTicker}; " +
                $"minimum required is {Config.MinYearsRequired}");

        return result;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static string GetCountryCode(string rawTicker)
    {
        var upper = rawTicker.ToUpperInvariant();
        foreach (var suffix in Config.TickerSuffixMap.Keys)
            if (upper.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return suffix.TrimStart('.');
        return "EU";
    }

    public void Dispose() => _http.Dispose();
}

// ──────────────────────────────────────────────────────────────────────────────
// Helper – batch-fetch multiple EU tickers
// ──────────────────────────────────────────────────────────────────────────────

public static class EsefFetcherHelper
{
    public static async Task<Dictionary<string, List<FinancialRecord>>> FetchEuStocksAsync(
        IEnumerable<string> rawTickers,
        bool              useCache   = true,
        bool              clearCache = false,
        bool              cacheOnly  = false,
        CancellationToken ct         = default)
    {
        var cache = new SimpleCache();
        if (clearCache) cache.Clear();

        // When cacheOnly is requested, still enable the on-disk cache so
        // previously fetched data is returned without new HTTP calls.
        using var fetcher  = new EsefFetcher(cache, useCache: useCache || cacheOnly);
        var       results  = new Dictionary<string, List<FinancialRecord>>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawTicker in rawTickers)
        {
            var (baseTicker, _) = TickerUtils.Normalize(rawTicker);
            try
            {
                if (cacheOnly && !fetcher.HasCachedLei(baseTicker))
                {
                    Logger.Warning(
                        $"No cached LEI for '{baseTicker}' and --cache-only is set, skipping. " +
                        "Add an entry to data/esef_ticker_lei_mapping.csv to pre-seed the mapping.");
                    continue;
                }

                var records = await fetcher.FetchTickerDataAsync(rawTicker, ct);
                if (records.Count > 0)
                    results[baseTicker] = records;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error fetching ESEF data for {rawTicker}: {ex.Message}");
            }
        }

        return results;
    }
}
