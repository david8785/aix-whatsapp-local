namespace AIXWhatsAppLocal;

/// <summary>
/// Simple file logger — writes to %LocalAppData%\AIXWhatsAppLocal\logs\
/// </summary>
public sealed class LogService
{
    private static readonly object Lock = new();

    public void Write(string eventType, string? message = null)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.LogsDirectory);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var line = $"[{timestamp}] {eventType}";
            if (!string.IsNullOrWhiteSpace(message))
                line += ": " + message;
            lock (Lock)
            {
                var logPath = Path.Combine(ConfigService.LogsDirectory, $"app-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(logPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging is best-effort
        }
    }
}