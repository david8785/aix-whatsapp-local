namespace AIXWhatsAppLocal;

/// <summary>
/// Local application config — persisted to %LocalAppData%\AIXWhatsAppLocal\config.json
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// The folder where WhatsApp photos will be saved.
    /// This is the user's photo folder, NOT a workspace root.
    /// No system subfolders (orders, incoming, archive, temp, logs) are created here.
    /// </summary>
    public string SelectedFolder { get; set; } = string.Empty;
}