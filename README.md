# Financial Data Aggregator

A C# / .NET tool for fetching 10+ years of financial data from official regulatory filings worldwide and exporting to CSV format.

**Status**: ✅ **OPERATIONAL** - SEC EDGAR (US) + ESEF (EU) functional

## Features

### ✅ Currently Working
- **SEC EDGAR API Integration** - Fetch official 10-K filings for US companies
- **ESEF API Integration** - Fetch official iXBRL filings for EU companies via filings.xbrl.org
- **Automatic CIK Lookup** - Map stock tickers to SEC identifiers
- **Automatic LEI Lookup** - Map EU tickers to legal entity identifiers (LEI)
- **XBRL Financial Metrics** - Extract standardized financial data:
  - Revenue
  - Gross Profit
  - Operating Profit
  - Net Profit
  - Cash from Operations
  - PPE
  - Diluted Shares Outstanding
- **CSV Export** - Single or multi-company output files
- **Batch Processing** - Process multiple stocks at once
- **Retry Logic** - Automatic retry with exponential backoff
- **Rate Limiting** - Respectful SEC API access
- **File Cache** - 24-hour cache for API responses (US + EU)

### 🔄 Planned (v2)
- SEDAR+ for Canadian companies
- ASX data for Australian companies
- EDINET for Japanese companies
- Fallback to quarterly (10-Q) data when annual unavailable

## Installation

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows, macOS, or Linux

### Setup

```bash
# Navigate to project directory
cd c:\Projects\Test\StockDataFetcher

# Restore NuGet packages
dotnet restore
```

## Usage

### Basic Usage

**Fetch data for a single stock:**
```bash
dotnet run -- AAPL
```

**Fetch multiple stocks:**
```bash
dotnet run -- AAPL MSFT NVDA GOOGL
```

**Fetch EU stocks (ESEF):**
```bash
dotnet run -- ASML.NL MC.FR
```

**Merge multiple stocks into one CSV:**
```bash
dotnet run -- AAPL MSFT NVDA --merge
```

**Enable verbose logging:**
```bash
dotnet run -- AAPL --verbose
```

### Command-Line Arguments

```
Usage: StockSelector <TICKER> [TICKER...] [options]

positional arguments:
  tickers               Stock ticker symbols (e.g., AAPL MSFT ASML.NL)

options:
  -h, --help            Show this help message and exit
  -o, --output <file>   Output CSV filename (default: auto-generated)
  -m, --merge           Merge all companies into a single CSV file
  -v, --verbose         Enable verbose logging
      --clear-cache     Clear the data cache before fetching
      --no-cache        Disable caching (always fetch fresh data)
      --cache-only      Only use cached data, do not fetch from API
```

## Output

### CSV Format

The tool generates CSV files with the following columns:

```
Ticker, Country, Year, Currency, Revenue, Gross_Profit, Operating_Profit,
Net_Profit, Cash_From_Operations, PPE, Diluted_Shares_Outstanding, CIK, Filed, End_Date
```

### Example Output

```csv
Ticker,Country,Year,Currency,Revenue,Gross_Profit,Operating_Profit,Net_Profit,Cash_From_Operations
AAPL,,2018,USD,62900000000,24084000000,70898000000,14125000000,77434000000
MSFT,,2010,USD,16039000000,12869000000,24098000000,4518000000,24073000000
NVDA,,2010,USD,886376000,426359000,255747000,171651000,675797000
GOOGL,,2015,USD,74989000000,,19360000000,16348000000,26024000000
```

### File Location

Output files are saved in: `c:\Projects\Test\StockDataFetcher\data\`

**Naming Convention:**
- Single company: `financial_data_{TICKER}_{YYYYMMDD}.csv`
- Multiple companies: `financial_data_multi_{N}companies_{YYYYMMDD}.csv`

## Data Limitations

### SEC EDGAR Availability

This tool accesses SEC EDGAR's official XBRL API. Important limitations to understand:

1. **Not all years have complete data** - Historical filings may not include all metrics in digital XBRL format
2. **Revenue data is sparse** - Particularly for years before 2018
3. **Some metrics may be missing** - PPE and share count may have NULL values
4. **Data varies by company** - Different companies report different metrics

### Example Data Availability

| Company | Revenue Years | Total Years | Notes |
|---------|---------------|-------------|-------|
| AAPL    | 1 year        | 17 available | Revenue sparse, but Cash Flow + margins complete |
| NVDA    | 13 years      | 13 available | Most metrics complete |
| GOOGL   | 6 years       | 6 available | No Gross Profit (services company) |

**This is normal** - SEC EDGAR requires companies to report XBRL starting in 2009, but many companies didn't provide complete digital data until more recently.

### Workarounds

For companies with limited data:
- Use quarterly (10-Q) data and aggregate to annual
- Use alternative free APIs (e.g., Financial Modeling Prep)
- Supplement with company investor relations PDFs
- Accept NULL values for unavailable metrics

## Project Structure

```
StockDataFetcher/
├── Program.cs                     # CLI entry point
├── Config.cs                      # XBRL concepts & configuration
├── SecEdgarFetcher.cs             # SEC API integration ✅ WORKING
├── EsefFetcher.cs                 # ESEF API integration ✅ WORKING
├── CsvExporter.cs                 # CSV generation
├── SimpleCache.cs                 # File-based cache (24-hour expiry)
├── TickerUtils.cs                 # Ticker normalisation & country detection
├── Logger.cs                      # Console logger
├── StockSelector.csproj           # .NET 10 project file
├── data/
│   ├── sec_ticker_cik_mapping.csv # Ticker→CIK lookup table
│   ├── esef_ticker_lei_mapping.csv# Ticker→LEI lookup table (optional overrides)
│   └── financial_data_*.csv       # Output files
├── cache/
│   └── us/{CIK}/company_facts.json# Cached SEC API responses
│   └── eu/{LEI}/facts_*.json      # Cached ESEF filing responses
└── README.md                      # This file
```

## Configuration

### Adding More Stock Tickers

Edit `data/sec_ticker_cik_mapping.csv` to add more US stocks:

```csv
ticker,cik
AAPL,0000320193
MSFT,0000789019
NVDA,0001045810
YOURTICKER,YOURCIK
```

Get CIK from: https://www.sec.gov/cgi-bin/browse-edgar?action=getcompany

### EU Ticker Disambiguation (Optional)

Some EU tickers are ambiguous across exchanges/companies. You can force a specific LEI mapping by creating/updating `data/esef_ticker_lei_mapping.csv`:

```csv
ticker,lei
ASML,724500Y6DUVHQD6OXN27
MC,969500M49I5DPEEZQF66
```

This mapping is checked before live API lookup.

### Changing Minimum Years Required

Edit `Config.cs`:
```csharp
public const int MinYearsRequired = 5;  // Default is 5 years (change as needed)
```

## Troubleshooting

### Issue: "Could not find CIK for ticker"
**Solution**: Add ticker to `data/sec_ticker_cik_mapping.csv` with its CIK number

### Issue: "No data retrieved" or only 1 year extracted
**Solution**: This is normal for some companies - SEC EDGAR has sparse historical XBRL data. The tool still exports what's available.

### Issue: EU ticker resolves to wrong company
**Solution**: Add an explicit mapping in `data/esef_ticker_lei_mapping.csv` (Ticker,LEI). This is recommended for short/ambiguous tickers.

### Issue: API 403 errors
**Solution**: Make sure the `User-Agent` header in `SecEdgarFetcher.cs` includes contact info. Check SEC rate limits (typically 10 requests/second allowed).

### Issue: "Exported 0 records"
**Causes**:
- Invalid ticker symbol
- Company not in SEC EDGAR (private company, foreign-only listed)
- CIK number incorrect

## Example Workflows

### Workflow 1: Fetch S&P 500 Sample
```bash
dotnet run -- AAPL MSFT GOOGL AMZN NVDA JPM V JNJ WMT --merge
```

### Workflow 2: Single Deep Dive
```bash
dotnet run -- AAPL --output apple_financials.csv --verbose
```

### Workflow 3: Tech Sector
```bash
dotnet run -- AAPL MSFT NVDA GOOGL META --merge --output tech_sector.csv
```

## Performance

- **Single stock fetch**: ~1-2 seconds
- **5 stocks**: ~10-15 seconds
- **20 stocks**: ~30-40 seconds

(Includes SEC API rate limiting delays)

## Dependencies

- **System.Net.Http** (built-in) - HTTP client for SEC API
- **System.Text.Json** (built-in) - JSON parsing for XBRL/API responses
- **CsvHelper** - CSV generation

All packages are managed via NuGet and restored automatically with `dotnet restore`.

## Legal & Disclaimer

- Data comes from official SEC EDGAR filings (public domain)
- Tool complies with SEC API terms of service
- Rate limiting implemented to be respectful of SEC resources
- Use data for analysis only; not investment advice

## Support & Contributing

This is a personal project. For issues:
1. Review example outputs in `data/` directory
2. Enable `--verbose` flag for detailed debugging
3. Run `dotnet build` to check for compile errors

## Roadmap

- [x] SEC EDGAR (US) - COMPLETE ✅
- [x] EU ESEF (Europe) - COMPLETE ✅
- [ ] SEDAR+ (Canada)
- [ ] ASX (Australia)
- [ ] EDINET (Japan)
- [ ] Database persistence (SQLite)
- [ ] Web UI dashboard
- [ ] Scheduled automatic updates

## License

Open source - use as needed.

---

**Last Updated**: March 26, 2026  
**Status**: Operational - SEC EDGAR + EU ESEF working, other regions planned
