using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace StockSelector;

public class CsvExporter
{
    private readonly string _outputDir;

    public CsvExporter(string? outputDir = null)
    {
        _outputDir = outputDir ?? Config.DataDir;
        Directory.CreateDirectory(_outputDir);
    }

    public string? ExportTocsv(IList<FinancialRecord> records, string ticker, string? filename = null)
    {
        if (records.Count == 0)
        {
            Logger.Warning($"No data to export for {ticker}");
            return null;
        }

        filename ??= $"financial_data_{ticker}_{DateTime.Now:yyyyMMdd}.csv";
        var path = Path.Combine(_outputDir, filename);

        WriteRecords(records.OrderBy(r => r.Year), path);
        Logger.Info($"Exported {records.Count} records to {path}");
        return path;
    }

    public string? ExportMultiple(Dictionary<string, List<FinancialRecord>> dataDict, string? filename = null)
    {
        var all = dataDict
            .SelectMany(kv => kv.Value.Select(r => { r.Ticker = kv.Key; return r; }))
            .OrderBy(r => r.Ticker)
            .ThenBy(r => r.Year)
            .ToList();

        if (all.Count == 0)
        {
            Logger.Warning("No data to export");
            return null;
        }

        filename ??= $"financial_data_multi_{dataDict.Count}companies_{DateTime.Now:yyyyMMdd}.csv";
        var path = Path.Combine(_outputDir, filename);

        WriteRecords(all, path);
        Logger.Info($"Exported {all.Count} records from {dataDict.Count} companies to {path}");
        return path;
    }

    private static void WriteRecords(IEnumerable<FinancialRecord> records, string path)
    {
        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
        };

        using var writer = new StreamWriter(path);
        using var csv = new CsvWriter(writer, cfg);

        // Write header using the canonical column order from Config.CsvSchema
        foreach (var col in Config.CsvSchema)
            csv.WriteField(col);
        // Extra columns not in schema
        csv.WriteField("CIK");
        csv.WriteField("Filed");
        csv.WriteField("End_Date");
        csv.NextRecord();

        foreach (var r in records)
        {
            csv.WriteField(r.Ticker);
            csv.WriteField(r.Country);
            csv.WriteField(r.Year);
            csv.WriteField(r.Currency);
            csv.WriteField(r.Revenue);
            csv.WriteField(r.Gross_Profit);
            csv.WriteField(r.Operating_Profit);
            csv.WriteField(r.Net_Profit);
            csv.WriteField(r.Cash_From_Operations);
            csv.WriteField(r.PPE);
            csv.WriteField(r.Diluted_Shares_Outstanding);
            csv.WriteField(r.CIK);
            csv.WriteField(r.Filed);
            csv.WriteField(r.End_Date);
            csv.NextRecord();
        }
    }
}
