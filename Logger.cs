namespace StockSelector;

/// <summary>Thin console logger matching Python logging format.</summary>
public static class Logger
{
    public static bool IsVerbose { get; set; } = false;

    private static string Stamp => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss,fff");

    public static void Info(string msg)    => Console.Error.WriteLine($"{Stamp} - INFO    - {msg}");
    public static void Warning(string msg) => Console.Error.WriteLine($"{Stamp} - WARNING - {msg}");
    public static void Error(string msg)   => Console.Error.WriteLine($"{Stamp} - ERROR   - {msg}");
    public static void Debug(string msg)   { if (IsVerbose) Console.Error.WriteLine($"{Stamp} - DEBUG   - {msg}"); }
}
