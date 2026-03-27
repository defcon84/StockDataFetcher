using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace StockDataFetcher;

public sealed class PdfExtractionMetrics
{
    public long? Revenue { get; set; }
    public long? Gross_Profit { get; set; }
    public long? Operating_Profit { get; set; }
    public long? Net_Profit { get; set; }
    public long? Cash_From_Operations { get; set; }
    public long? PPE { get; set; }
    public long? Diluted_Shares_Outstanding { get; set; }

    public bool HasAny() =>
        Revenue.HasValue ||
        Gross_Profit.HasValue ||
        Operating_Profit.HasValue ||
        Net_Profit.HasValue ||
        Cash_From_Operations.HasValue ||
        PPE.HasValue ||
        Diluted_Shares_Outstanding.HasValue;
}

public sealed class PdfStatementExtractor
{
    private readonly HttpClient _http;
    private readonly SimpleCache _cache;
    private readonly bool _useCache;

    private static readonly Dictionary<string, string[]> MetricKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Revenue"] =
        [
            "revenue",
            "total revenue",
            "sales revenue",
            "net sales",
            "turnover",
        ],
        ["Gross_Profit"] =
        [
            "gross profit",
        ],
        ["Operating_Profit"] =
        [
            "operating profit",
            "operating income",
            "operating earnings",
            "ebit",
            "profit from operations",
        ],
        ["Net_Profit"] =
        [
            "net profit",
            "net income",
            "profit for the year",
            "profit after tax",
            "net earnings",
        ],
        ["Cash_From_Operations"] =
        [
            "cash flow from operating activities",
            "net cash provided by operating activities",
            "cash generated from operations",
            "cash from operating activities",
            "operating cash flow",
        ],
        ["PPE"] =
        [
            "property plant and equipment",
            "property, plant and equipment",
            "property plant & equipment",
            "ppe",
        ],
        ["Diluted_Shares_Outstanding"] =
        [
            "weighted average number of diluted shares",
            "weighted average diluted shares",
            "diluted shares",
            "shares outstanding",
        ],
    };

    public PdfStatementExtractor(HttpClient http, SimpleCache cache, bool useCache)
    {
        _http = http;
        _cache = cache;
        _useCache = useCache;
    }

    public async Task<PdfExtractionMetrics?> ExtractFromDocumentAsync(
        string market,
        string identifier,
        string cacheKey,
        string documentUrl,
        CancellationToken ct = default)
    {
        var resultCacheKey = $"doc_extract_{cacheKey}";
        if (_useCache)
        {
            var hit = _cache.Get(market, identifier, resultCacheKey);
            if (hit is JsonElement el)
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<PdfExtractionMetrics>(el.GetRawText());
                    if (cached is not null) return cached;
                }
                catch
                {
                    // Ignore corrupt extraction cache and retry extraction.
                }
            }
        }

        var text = await TryGetDocumentTextAsync(market, identifier, cacheKey, documentUrl, ct);
        if (string.IsNullOrWhiteSpace(text)) return null;

        var extracted = ExtractFromText(text);
        if (_useCache)
            _cache.Set(market, identifier, resultCacheKey, JsonSerializer.SerializeToElement(extracted));

        return extracted;
    }

    private async Task<string?> TryGetDocumentTextAsync(
        string market,
        string identifier,
        string cacheKey,
        string documentUrl,
        CancellationToken ct)
    {
        var normalized = documentUrl.Trim();
        if (normalized.Length == 0) return null;

        if (normalized.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return await GetPdfTextAsync(market, identifier, cacheKey, normalized, ct);

        string html;
        try
        {
            html = await _http.GetStringAsync(normalized, ct);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Document fetch failed for {normalized}: {ex.Message}");
            return null;
        }

        var resolvedPdf = ResolvePdfUrlFromHtml(normalized, html);
        if (!string.IsNullOrWhiteSpace(resolvedPdf))
        {
            var pdfText = await GetPdfTextAsync(market, identifier, cacheKey, resolvedPdf!, ct);
            if (!string.IsNullOrWhiteSpace(pdfText)) return pdfText;
        }

        // Fallback: parse visible HTML text if no PDF could be resolved.
        return HtmlToText(html);
    }

    private async Task<string?> GetPdfTextAsync(
        string market,
        string identifier,
        string cacheKey,
        string pdfUrl,
        CancellationToken ct)
    {
        var bytesCacheKey = $"doc_pdf_{cacheKey}";
        byte[]? bytes = null;

        if (_useCache)
            bytes = _cache.GetBytes(market, identifier, bytesCacheKey);

        if (bytes is null)
        {
            try
            {
                bytes = await _http.GetByteArrayAsync(pdfUrl, ct);
                if (_useCache && bytes.Length > 0)
                    _cache.SetBytes(market, identifier, bytesCacheKey, bytes);
            }
            catch (Exception ex)
            {
                Logger.Debug($"PDF download failed for {pdfUrl}: {ex.Message}");
                return null;
            }
        }

        if (bytes.Length == 0) return null;

        try
        {
            using var stream = new MemoryStream(bytes);
            using var document = PdfDocument.Open(stream);
            var sb = new StringBuilder(1024 * 32);
            int pages = 0;
            foreach (var page in document.GetPages())
            {
                sb.AppendLine(page.Text);
                pages++;
                if (pages >= 40) break;
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Logger.Debug($"PDF parse failed for {pdfUrl}: {ex.Message}");
            return null;
        }
    }

    private static string? ResolvePdfUrlFromHtml(string sourceUrl, string html)
    {
        // ASX terms wrapper contains hidden pdfURL input.
        var hiddenPdf = Regex.Match(
            html,
            "name=\"pdfURL\"\\s+value=\"(?<url>[^\"]+\\.pdf[^\"]*)\"",
            RegexOptions.IgnoreCase);
        if (hiddenPdf.Success)
            return System.Net.WebUtility.HtmlDecode(hiddenPdf.Groups["url"].Value.Trim());

        var hrefPdf = Regex.Match(
            html,
            "href=\"(?<url>[^\"]+\\.pdf[^\"]*)\"",
            RegexOptions.IgnoreCase);
        if (hrefPdf.Success)
        {
            var raw = System.Net.WebUtility.HtmlDecode(hrefPdf.Groups["url"].Value.Trim());
            if (raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return raw;

            if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var baseUri) &&
                Uri.TryCreate(baseUri, raw, out var absolute))
            {
                return absolute.ToString();
            }
            return raw;
        }

        return null;
    }

    private static string HtmlToText(string html)
    {
        var noTags = Regex.Replace(html, "<.*?>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        return Regex.Replace(decoded, "\\s+", " ").Trim();
    }

    private static PdfExtractionMetrics ExtractFromText(string text)
    {
        var metrics = new PdfExtractionMetrics();
        var lines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => Regex.Replace(l, "\\s+", " ").Trim())
            .Where(l => l.Length > 0)
            .ToList();

        metrics.Revenue = FindMetric(lines, MetricKeywords["Revenue"]);
        metrics.Gross_Profit = FindMetric(lines, MetricKeywords["Gross_Profit"]);
        metrics.Operating_Profit = FindMetric(lines, MetricKeywords["Operating_Profit"]);
        metrics.Net_Profit = FindMetric(lines, MetricKeywords["Net_Profit"]);
        metrics.Cash_From_Operations = FindMetric(lines, MetricKeywords["Cash_From_Operations"]);
        metrics.PPE = FindMetric(lines, MetricKeywords["PPE"]);
        metrics.Diluted_Shares_Outstanding = FindMetric(lines, MetricKeywords["Diluted_Shares_Outstanding"]);

        return metrics;
    }

    private static long? FindMetric(IReadOnlyList<string> lines, string[] keywords)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!keywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
                continue;

            var candidates = new List<long>();
            candidates.AddRange(ParseNumberCandidates(line));
            if (i + 1 < lines.Count) candidates.AddRange(ParseNumberCandidates(lines[i + 1]));

            // Prefer realistic financial magnitudes and first-in-line ordering.
            var best = candidates.FirstOrDefault(v => Math.Abs(v) >= 1_000);
            if (best != 0) return best;

            if (candidates.Count > 0) return candidates[0];
        }

        return null;
    }

    private static IEnumerable<long> ParseNumberCandidates(string line)
    {
        var matches = Regex.Matches(
            line,
            @"\(?-?\d{1,3}(?:,\d{3})+(?:\.\d+)?\)?");
        foreach (Match m in matches)
        {
            var raw = m.Value.Trim();
            var isNegative = raw.StartsWith("(", StringComparison.Ordinal) && raw.EndsWith(")", StringComparison.Ordinal);
            var normalized = raw
                .Replace("(", "", StringComparison.Ordinal)
                .Replace(")", "", StringComparison.Ordinal)
                .Replace(",", "", StringComparison.Ordinal)
                .Replace(" ", "", StringComparison.Ordinal);

            if (!decimal.TryParse(normalized, out var dec)) continue;

            var roundedDecimal = Math.Round(dec, MidpointRounding.AwayFromZero);
            if (roundedDecimal > long.MaxValue || roundedDecimal < long.MinValue)
                continue;

            var rounded = (long)roundedDecimal;

            // Skip likely year tokens.
            if (rounded is > 1900 and < 2101) continue;

            yield return isNegative ? -rounded : rounded;
        }
    }

    public static void FillMissingMetrics(FinancialRecord target, PdfExtractionMetrics? extracted)
    {
        if (extracted is null || !extracted.HasAny()) return;

        target.Revenue ??= extracted.Revenue;
        target.Gross_Profit ??= extracted.Gross_Profit;
        target.Operating_Profit ??= extracted.Operating_Profit;
        target.Net_Profit ??= extracted.Net_Profit;
        target.Cash_From_Operations ??= extracted.Cash_From_Operations;
        target.PPE ??= extracted.PPE;
        target.Diluted_Shares_Outstanding ??= extracted.Diluted_Shares_Outstanding;
    }
}
