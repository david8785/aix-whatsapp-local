using System.Reflection;
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

        // === STARTUP BUILD VERIFICATION ===
        // Log exact version, commit hash, and process path so the physical log
        // proves which build is running. If these are missing or show "dev-local",
        // the installed EXE is NOT the latest CI build.
        var log = new LogService();
        var asmVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        log.Write("APP_VERSION", $"{BuildInfo.Version} (assembly={asmVersion})");
        log.Write("BUILD_COMMIT", BuildInfo.Commit);
        log.Write("BUILD_DATE", BuildInfo.BuildDate);
        log.Write("PROCESS_PATH", Environment.ProcessPath ?? AppContext.BaseDirectory);
        log.Write("APP_STARTED");

        Application.Run(new MainForm());
    }
}