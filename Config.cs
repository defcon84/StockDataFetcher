namespace StockSelector;

public static class Config
{
    public static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

    public static readonly string DataDir = Path.Combine(ProjectRoot, "data");
    public static readonly string CacheDir = Path.Combine(ProjectRoot, "cache");

    // SEC EDGAR
    public const string SecEdgarBaseUrl = "https://data.sec.gov/submissions";
    public const double SecApiDelay = 0.1; // seconds between requests

    // Minimum years of data required
    public const int MinYearsRequired = 5;

    // US GAAP XBRL concept tags.
    // Each label maps to an ordered list of candidate concept names; the first
    // one present in a company's filing is used (fallback handling).
    public static readonly Dictionary<string, string[]> XbrlConceptsUs = new()
    {
        ["Revenue"]                    = ["Revenues", "RevenueFromContractWithCustomerExcludingAssessedTax", "SalesRevenueNet"],
        ["Gross_Profit"]               = ["GrossProfit"],
        ["Operating_Profit"]           = ["OperatingIncomeLoss"],
        ["Net_Profit"]                 = ["NetIncomeLoss"],
        ["Cash_From_Operations"]       = ["NetCashProvidedByUsedInOperatingActivities"],
        ["PPE"]                        = ["PropertyPlantAndEquipmentNet", "PropertyPlantAndEquipmentGross", "PropertyPlantAndEquipment"],
        ["Diluted_Shares_Outstanding"] = ["WeightedAverageNumberOfDilutedSharesOutstanding"],
    };

    // CSV column order
    public static readonly string[] CsvSchema =
    [
        "Ticker", "Country", "Year", "Currency",
        "Revenue", "Gross_Profit", "Operating_Profit", "Net_Profit",
        "Cash_From_Operations", "PPE", "Diluted_Shares_Outstanding",
    ];

    // Ticker suffix → country code
    public static readonly Dictionary<string, string> TickerSuffixMap = new()
    {
        [".NL"] = "eu", [".DE"] = "eu", [".FR"] = "eu", [".IT"] = "eu",
        [".ES"] = "eu", [".BE"] = "eu", [".AT"] = "eu", [".GR"] = "eu",
        [".PT"] = "eu", [".FI"] = "eu", [".IE"] = "eu",
        [".TO"] = "ca", [".V"]  = "ca",
        [".AX"] = "au",
        [".T"]  = "jp",
    };

    // Timeouts
    public const int ApiTimeout = 30; // seconds
}
