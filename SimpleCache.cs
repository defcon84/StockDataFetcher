using System.Text.Json;

namespace StockSelector;

/// <summary>File-based JSON cache with 24-hour expiry.</summary>
public class SimpleCache
{
    private readonly string _root;
    private static readonly TimeSpan Expiry = TimeSpan.FromHours(24);

    public SimpleCache(string? root = null)
    {
        _root = root ?? Config.CacheDir;
        Directory.CreateDirectory(_root);
    }

    private string GetPath(string market, string identifier, string name)
    {
        var dir = Path.Combine(_root, market, identifier);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{name}.json");
    }

    // Sidecar metadata file stores only the download timestamp so that the
    // primary .json file is the exact raw response received from the source.
    private static string MetaPath(string dataPath) => dataPath + ".meta";

    public JsonElement? Get(string market, string identifier, string name)
    {
        var path = GetPath(market, identifier, name);
        if (!File.Exists(path)) return null;

        try
        {
            // Check expiry from sidecar meta file
            var metaPath = MetaPath(path);
            if (File.Exists(metaPath))
            {
                using var metaStream = File.OpenRead(metaPath);
                var meta = JsonDocument.Parse(metaStream);
                if (meta.RootElement.TryGetProperty("cached_at", out var cachedAtEl) &&
                    DateTime.TryParse(cachedAtEl.GetString(), out var cachedAt) &&
                    DateTime.Now - cachedAt > Expiry)
                {
                    return null; // expired
                }
            }

            Logger.Debug($"Cache hit: {path}");
            using var stream = File.OpenRead(path);
            var doc = JsonDocument.Parse(stream);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            Logger.Debug($"Cache read error: {ex.Message}");
            return null;
        }
    }

    public void Set(string market, string identifier, string name, JsonElement data)
    {
        var path = GetPath(market, identifier, name);
        try
        {
            // Write the original filing JSON exactly as received - no wrapping or transformation
            File.WriteAllText(path, data.GetRawText());

            // Store download timestamp in a separate sidecar file
            var meta = JsonSerializer.Serialize(new { cached_at = DateTime.Now.ToString("O") });
            File.WriteAllText(MetaPath(path), meta);

            Logger.Debug($"Cached to: {path}");
        }
        catch (Exception ex)
        {
            Logger.Debug($"Cache write error: {ex.Message}");
        }
    }

    public void Clear()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
            Directory.CreateDirectory(_root);
            Logger.Info("Cache cleared");
        }
        catch (Exception ex)
        {
            Logger.Warning($"Cache clear error: {ex.Message}");
        }
    }
}
