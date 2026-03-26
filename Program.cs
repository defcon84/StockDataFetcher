using StockSelector;

// ──────────────────────────────────────────────────────────────────────────────
// Simple CLI argument parsing
// ──────────────────────────────────────────────────────────────────────────────

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Financial Data Aggregator – fetch financial data from official filings");
    Console.WriteLine();
    Console.WriteLine("Usage: StockSelectorCS <TICKER> [TICKER...] [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -o, --output <file>   Output CSV filename");
    Console.WriteLine("  -m, --merge           Merge all companies into a single CSV file");
    Console.WriteLine("  -v, --verbose         Enable verbose (debug) logging");
    Console.WriteLine("      --clear-cache     Clear the data cache before fetching");
    Console.WriteLine("      --no-cache        Disable caching (always fetch fresh data)");
    Console.WriteLine("      --cache-only      Only use cached data, do not fetch from API");
    Console.WriteLine("  -h, --help            Show this help message");
    return 0;
}

var tickers    = new List<string>();
string? output = null;
bool merge      = false;
bool verbose    = false;
bool clearCache = false;
bool noCache    = false;
bool cacheOnly  = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-o": case "--output":
            output = args[++i]; break;
        case "-m": case "--merge":
            merge = true; break;
        case "-v": case "--verbose":
            verbose = true; break;
        case "--clear-cache":
            clearCache = true; break;
        case "--no-cache":
            noCache = true; break;
        case "--cache-only":
            cacheOnly = true; break;
        default:
            if (!args[i].StartsWith('-'))
                tickers.Add(args[i]);
            else
                Console.Error.WriteLine($"Unknown option: {args[i]}");
            break;
    }
}

if (tickers.Count == 0)
{
    Console.Error.WriteLine("Error: at least one ticker symbol is required.");
    return 1;
}

Logger.IsVerbose = verbose;
Logger.Info("Starting Financial Data Aggregator");
Logger.Info($"Processing {tickers.Count} ticker(s): {string.Join(", ", tickers)}");

Directory.CreateDirectory(Config.DataDir);
Directory.CreateDirectory(Config.CacheDir);

// Route tickers by market
var usTickers = new List<string>();
var euTickers = new List<string>();
var caTickers = new List<string>();
var auTickers = new List<string>();
var jpTickers = new List<string>();

foreach (var raw in tickers)
{
    var (ticker, country) = TickerUtils.Normalize(raw);
    switch (country)
    {
        case "us": usTickers.Add(ticker); break;
        case "eu": euTickers.Add(raw);    break;
        case "ca": caTickers.Add(raw);    break;
        case "au": auTickers.Add(raw);    break;
        case "jp": jpTickers.Add(raw);    break;
    }
}

var allData = new Dictionary<string, List<FinancialRecord>>(StringComparer.OrdinalIgnoreCase);

// ── US – SEC EDGAR ────────────────────────────────────────────────────────────
if (usTickers.Count > 0)
{
    Logger.Info($"Fetching US stocks from SEC EDGAR: {string.Join(", ", usTickers)}");
    var usData = await SecEdgarFetcherHelper.FetchUsStocksAsync(
        usTickers,
        useCache:   !noCache,
        clearCache: clearCache,
        cacheOnly:  cacheOnly);

    foreach (var kv in usData) allData[kv.Key] = kv.Value;
}

// ── TODO: EU / CA / AU / JP ───────────────────────────────────────────────────
foreach (var (market, list) in new[] {
    ("EU (ESEF)",  (IList<string>)euTickers),
    ("Canada",     caTickers),
    ("Australia",  auTickers),
    ("Japan",      jpTickers) })
{
    if (list.Count > 0)
        Logger.Warning($"{market} market not yet implemented: {string.Join(", ", list)}");
}

// ── Export ────────────────────────────────────────────────────────────────────
if (allData.Count == 0)
{
    Logger.Error("No data retrieved");
    return 1;
}

var exporter = new CsvExporter();

if (merge || allData.Count > 1)
{
    var path = exporter.ExportMultiple(allData, output);
    Logger.Info($"Data exported to: {path}");
}
else
{
    foreach (var (ticker, records) in allData)
    {
        var path = exporter.ExportTocsv(records, ticker, output);
        Logger.Info($"Data exported to: {path}");
    }
}

Logger.Info("Financial Data Aggregator completed successfully");
return 0;
