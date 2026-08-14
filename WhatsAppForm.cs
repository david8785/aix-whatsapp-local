using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AIXWhatsAppLocal;

/// <summary>
/// WhatsApp Web window — opens WhatsApp in WebView2 with a persistent profile.
/// Session is stored in %LocalAppData%\AIXWhatsAppLocal\webview-profile\
/// so QR is only needed once.
///
/// Also runs the media capture auto-scan: every 15 seconds, scans the chat list
/// for unread chats, opens each one, downloads new images, and saves them to
/// local customer folders.
///
/// Reports live status back to MainForm via events (single dashboard, no separate window).
/// </summary>
public sealed class WhatsAppForm : Form
{
    private readonly LogService _log;
    private readonly AppConfig _config;
    private WebView2 _webView = null!;
    private Label _statusLabel = null!;
    private Label _captureLabel = null!;
    private System.Windows.Forms.Timer? _pollTimer;
    private System.Windows.Forms.Timer? _scanTimer;
    private bool _connected;
    private bool _isScanning;
    private MediaDatabase? _mediaDb;
    private MediaCaptureService? _mediaCapture;

    // Events for MainForm live status
    public event Action<string>? WhatsAppStatusChanged;
    public event Action<string>? ScannerStatusChanged;
    public event Action<int>? UnreadChatsChanged;
    public event Action<string>? CurrentChatChanged;
    public event Action<int>? ImagesDetectedChanged;
    public event Action<int>? ImagesSavedChanged;
    public event Action<string>? LastSavedFileChanged;
    public event Action<string>? LastErrorChanged;

    // Legacy status event (kept for backward compat)
    public event Action<string>? StatusChanged;

    public WhatsAppForm(LogService log, AppConfig config)
    {
        _log = log;
        _config = config;
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

        _captureLabel = new Label
        {
            Text = "Media: Idle",
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 12, 0),
            BackColor = Color.FromArgb(250, 250, 250),
            Font = new Font(Font.FontFamily, 8.5F),
            ForeColor = Color.Gray
        };

        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };

        Controls.Add(_webView);
        Controls.Add(_captureLabel);
        Controls.Add(_statusLabel);
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var profileDir = ConfigService.WebViewProfileDirectory;

            // Detect session reuse BEFORE creating
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
            LastErrorChanged?.Invoke(ex.Message);
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            UpdateStatus("WhatsApp: Failed to load");
            _log.Write("whatsapp_navigation_failed");
            LastErrorChanged?.Invoke("WhatsApp navigation failed");
            return;
        }

        _log.Write("whatsapp_navigation_completed");
        await CheckConnectionStatusAsync();

        // Start polling for connection status changes
        _pollTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _pollTimer.Tick += async (_, _) => await CheckConnectionStatusAsync();
        _pollTimer.Start();

        // Initialize media capture
        InitializeMediaCapture();
    }

    private void InitializeMediaCapture()
    {
        var ordersRoot = _config.SelectedFolder;
        if (string.IsNullOrWhiteSpace(ordersRoot))
        {
            _log.Write("MEDIA_CAPTURE_SKIP", "reason=orders_root_not_set");
            UpdateCaptureStatus("Media: Set orders folder first");
            ScannerStatusChanged?.Invoke("Set orders folder first");
            return;
        }

        try
        {
            var dbPath = Path.Combine(ConfigService.AppDataDirectory, "local.db");
            _mediaDb = new MediaDatabase(dbPath);
            _mediaCapture = new MediaCaptureService(_webView.CoreWebView2, _log, _mediaDb, ordersRoot);

            // Wire up capture service events to MainForm
            _mediaCapture.CaptureStatusChanged += status => UpdateCaptureStatus($"Media: {status}");
            _mediaCapture.ScannerStatusChanged += status => ScannerStatusChanged?.Invoke(status);
            _mediaCapture.UnreadChatsChanged += count => UnreadChatsChanged?.Invoke(count);
            _mediaCapture.CurrentChatChanged += name => CurrentChatChanged?.Invoke(name);
            _mediaCapture.ImagesDetectedChanged += count => ImagesDetectedChanged?.Invoke(count);
            _mediaCapture.ImagesSavedChanged += count => ImagesSavedChanged?.Invoke(count);
            _mediaCapture.LastSavedFileChanged += path => LastSavedFileChanged?.Invoke(path);
            _mediaCapture.LastErrorChanged += error => LastErrorChanged?.Invoke(error);

            // Start auto-scan timer (every 15 seconds)
            _scanTimer = new System.Windows.Forms.Timer { Interval = 15000 };
            _scanTimer.Tick += async (_, _) => await OnScanTimerTick();
            _scanTimer.Start();

            _log.Write("MEDIA_CAPTURE_ENABLED", $"orders_root={ordersRoot}");
            UpdateCaptureStatus("Media: Ready — auto-scanning");
            ScannerStatusChanged?.Invoke("Auto-scanning");
        }
        catch (Exception ex)
        {
            _log.Write("MEDIA_CAPTURE_INIT_FAILED", ex.Message);
            UpdateCaptureStatus($"Media: Error — {ex.Message}");
            ScannerStatusChanged?.Invoke($"Error: {ex.Message}");
            LastErrorChanged?.Invoke(ex.Message);
        }
    }

    private async Task OnScanTimerTick()
    {
        if (_isScanning || !_connected || _mediaCapture == null) return;

        _isScanning = true;
        try
        {
            await _mediaCapture.ScanAndCaptureAsync();
        }
        catch (Exception ex)
        {
            _log.Write("scan_error", ex.Message);
            LastErrorChanged?.Invoke(ex.Message);
        }
        finally
        {
            _isScanning = false;
        }
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
                        const chatList = document.querySelector('#pane-side [role="listitem"], [data-testid="chat-list"], [data-testid="chatlist-header"]');
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
        WhatsAppStatusChanged?.Invoke(status);
        StatusChanged?.Invoke(status);
    }

    private void UpdateCaptureStatus(string status)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { Invoke(() => UpdateCaptureStatus(status)); return; }
        _captureLabel.Text = status;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _pollTimer?.Stop();
        _pollTimer?.Dispose();
        _scanTimer?.Stop();
        _scanTimer?.Dispose();
        _mediaCapture?.Dispose();
        _mediaDb?.Dispose();
        _log.Write("whatsapp_closing");
    }
}