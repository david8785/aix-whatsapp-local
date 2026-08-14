using System.Windows.Forms;

namespace AIXWhatsAppLocal;

/// <summary>
/// Entry point for AIX WhatsApp Local — a standalone Windows app
/// that opens WhatsApp Web and saves photos to a local folder.
/// No cloud, no APIs, no Base44, no StoreAIX.
/// </summary>
internal static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}