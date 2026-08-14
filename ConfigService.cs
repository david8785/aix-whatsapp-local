using System.Text;
using System.Text.Json;

namespace AIXWhatsAppLocal;

/// <summary>
/// Loads and saves config.json from %LocalAppData%\AIXWhatsAppLocal\
/// Config and logs are NEVER stored in the user's selected photo folder.
/// </summary>
public sealed class ConfigService
{
    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIXWhatsAppLocal");

    public static string ConfigPath { get; } = Path.Combine(AppDataDirectory, "config.json");
    public static string WebViewProfileDirectory { get; } = Path.Combine(AppDataDirectory, "webview-profile");
    public static string LogsDirectory { get; } = Path.Combine(AppDataDirectory, "logs");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new AppConfig();
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(AppDataDirectory);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigPath, json, Encoding.UTF8);
        }
        catch
        {
            // Config persistence is best-effort — never crash the app
        }
    }
}