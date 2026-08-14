using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AIXWhatsAppLocal;

/// <summary>
/// WhatsApp Web window — opens WhatsApp in WebView2 with a persistent profile.
/// Session is stored in %LocalAppData%\AIXWhatsAppLocal\webview-profile\
/// so QR is only needed once.
/// </summary>
public sealed class WhatsAppForm : Form
{
    private readonly LogService _log;
    private WebView2 _webView = null!;
    private Label _statusLabel = null!;
    private System.Windows.Forms.Timer? _pollTimer;
    private bool _connected;

    public event Action<string>? StatusChanged;

    public WhatsAppForm(LogService log)
    {
        _log = log;
        InitializeUI();
        Load += async (_, _) => await InitializeWebViewAsync();
        FormClosing += OnFormClosing;
    }

    private void InitializeUI()
    {
        Text = "AIX WhatsApp Local — WhatsApp Web";
        Width = 1200;
        Height = 800;
        StartPosition = FormStartPosition.CenterScreen;

        _statusLabel = new Label
        {
            Text = "Loading WhatsApp...",
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 12, 0),
            BackColor = Color.FromArgb(245, 245, 245),
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold)
        };

        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };

        Controls.Add(_webView);
        Controls.Add(_statusLabel);
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var profileDir = ConfigService.WebViewProfileDirectory;

            // Detect session reuse BEFORE creating — if the profile dir already has content,
            // the WhatsApp session should persist without a new QR.
            var profileExists = Directory.Exists(profileDir) && Directory.EnumerateFileSystemEntries(profileDir).Any();
            _log.Write("WEBVIEW_PROFILE_PATH", profileDir);
            _log.Write("SESSION_REUSED", profileExists ? "YES" : "NO");

            Directory.CreateDirectory(profileDir);

            var options = new CoreWebView2EnvironmentOptions(
                "--force-device-scale-factor=1 --high-dpi-support=1 --touch-events=disabled");
            var environment = await CoreWebView2Environment.CreateAsync(null, profileDir, options);
            await _webView.EnsureCoreWebView2Async(environment);

            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2.Navigate("https://web.whatsapp.com/");
            _log.Write("WHATSAPP_STATUS", "LOADING");
            UpdateStatus("Opening WhatsApp...");
        }
        catch (Exception ex)
        {
            _log.Write("whatsapp_init_failed", ex.Message);
            UpdateStatus("Error — install Microsoft Edge WebView2 Runtime");
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            UpdateStatus("WhatsApp: Failed to load");
            _log.Write("whatsapp_navigation_failed");
            return;
        }

        _log.Write("whatsapp_navigation_completed");
        await CheckConnectionStatusAsync();

        // Start polling for connection status changes
        _pollTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _pollTimer.Tick += async (_, _) => await CheckConnectionStatusAsync();
        _pollTimer.Start();
    }

    private async Task CheckConnectionStatusAsync()
    {
        if (_webView?.CoreWebView2 == null) return;

        try
        {
            var raw = await _webView.ExecuteScriptAsync("""
                (() => {
                    try {
                        const qr = document.querySelector('[data-testid="qrcode"] canvas, div[data-ref] canvas, canvas[aria-label*="QR"]');
                        const chatList = document.querySelector('#pane-side [role="listitem"], [data-testid="chat-list"], [data-testid="chatlist-header"], [data-testid="chat-list-search"]');
                        if (qr) return JSON.stringify({ status: 'qr' });
                        if (chatList) return JSON.stringify({ status: 'connected' });
                        return JSON.stringify({ status: 'loading' });
                    } catch (e) {
                        return JSON.stringify({ status: 'error', error: e.message });
                    }
                })();
                """);

            var json = JsonSerializer.Deserialize<string>(raw) ?? "{}";
            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.GetProperty("status").GetString();

            if (status == "connected")
            {
                if (!_connected)
                {
                    _connected = true;
                    _log.Write("WHATSAPP_STATUS", "CONNECTED");
                }
                UpdateStatus("Connected");
            }
            else if (status == "qr")
            {
                if (_connected)
                {
                    _connected = false;
                    _log.Write("WHATSAPP_STATUS", "QR");
                }
                UpdateStatus("Scan QR with your phone");
            }
            else
            {
                UpdateStatus("Loading...");
            }
        }
        catch
        {
            // WebView not ready yet — will retry on next poll
        }
    }

    private void UpdateStatus(string status)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { Invoke(() => UpdateStatus(status)); return; }
        _statusLabel.Text = $"WhatsApp: {status}";
        StatusChanged?.Invoke(status);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _pollTimer?.Stop();
        _pollTimer?.Dispose();
        _log.Write("whatsapp_closing");
    }
}