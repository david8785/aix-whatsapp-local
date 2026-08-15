using System.Windows.Forms;

namespace AIXWhatsAppLocal;

/// <summary>
/// Single unified dashboard for AIX WhatsApp Local.
/// Shows WhatsApp status, scanner status, capture stats, and folder info — all in one screen.
/// No separate dashboard window — this IS the dashboard.
/// </summary>
public sealed class MainForm : Form
{
    private readonly ConfigService _configService = new();
    private readonly LogService _log = new();
    private AppConfig _appConfig;

    // Buttons
    private Button _chooseFolderButton = null!;
    private Button _openWhatsAppButton = null!;

    // Status labels — live data from WhatsAppForm
    private Label _whatsappStatusLabel = null!;
    private Label _scannerStatusLabel = null!;
    private Label _unreadChatsLabel = null!;
    private Label _currentChatLabel = null!;
    private Label _imagesDetectedLabel = null!;
    private Label _imagesSavedLabel = null!;
    private Label _ordersFolderLabel = null!;
    private Label _lastSavedFileLabel = null!;
    private Label _lastErrorLabel = null!;

    // Live stats
    private int _imagesDetectedToday;
    private int _imagesSavedToday;

    public MainForm()
    {
        _appConfig = _configService.Load();
        InitializeUI();
        UpdateFolderDisplay();
        _log.Write("app_started");
    }

    private void InitializeUI()
    {
        Text = "AIX WhatsApp Local";
        Width = 560;
        Height = 520;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;

        // === Header ===
        var title = new Label
        {
            Text = "AIX WhatsApp Local",
            Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
            Location = new Point(24, 16),
            AutoSize = true
        };

        var subtitle = new Label
        {
            Text = "WhatsApp → Local Order Folders",
            Font = new Font(Font.FontFamily, 9F),
            ForeColor = Color.Gray,
            Location = new Point(24, 46),
            AutoSize = true
        };

        // === Buttons ===
        _chooseFolderButton = new Button
        {
            Text = "Choose Orders Folder",
            Location = new Point(24, 76),
            Size = new Size(240, 44),
            Font = new Font(Font.FontFamily, 10F)
        };
        _chooseFolderButton.Click += OnChooseFolder;

        _openWhatsAppButton = new Button
        {
            Text = "Open WhatsApp",
            Location = new Point(276, 76),
            Size = new Size(240, 44),
            Font = new Font(Font.FontFamily, 10F)
        };
        _openWhatsAppButton.Click += OnOpenWhatsApp;

        // === Status Panel ===
        var panelY = 134;
        var panelHeight = 360;
        var statusPanel = new Panel
        {
            Location = new Point(24, panelY),
            Size = new Size(492, panelHeight),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(250, 250, 252)
        };

        var statusTitle = new Label
        {
            Text = "Live Status",
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            Location = new Point(12, 8),
            AutoSize = true,
            BackColor = Color.FromArgb(250, 250, 252)
        };
        statusPanel.Controls.Add(statusTitle);

        int row = 34;
        int rowHeight = 34;
        int label1X = 16;
        int label2X = 140;

        _whatsappStatusLabel = CreateStatusRow(statusPanel, "WhatsApp Status:", "Not Connected", label1X, label2X, row); row += rowHeight;
        _scannerStatusLabel = CreateStatusRow(statusPanel, "Scanner Status:", "Idle", label1X, label2X, row); row += rowHeight;
        _unreadChatsLabel = CreateStatusRow(statusPanel, "Unread Chats:", "0", label1X, label2X, row); row += rowHeight;
        _currentChatLabel = CreateStatusRow(statusPanel, "Current Chat:", "—", label1X, label2X, row); row += rowHeight;
        _imagesDetectedLabel = CreateStatusRow(statusPanel, "Images Detected Today:", "0", label1X, label2X, row); row += rowHeight;
        _imagesSavedLabel = CreateStatusRow(statusPanel, "Images Saved Today:", "0", label1X, label2X, row); row += rowHeight;
        _ordersFolderLabel = CreateStatusRow(statusPanel, "Orders Folder:", "(none)", label1X, label2X, row); row += rowHeight;
        _lastSavedFileLabel = CreateStatusRow(statusPanel, "Last Saved File:", "—", label1X, label2X, row); row += rowHeight;
        _lastErrorLabel = CreateStatusRow(statusPanel, "Last Error:", "—", label1X, label2X, row, Color.DarkRed);

        Controls.AddRange(new Control[] { title, subtitle, _chooseFolderButton, _openWhatsAppButton, statusPanel });
    }

    private Label CreateStatusRow(Panel parent, string labelText, string value, int x1, int x2, int y, Color? valueColor = null)
    {
        var lab = new Label
        {
            Text = labelText,
            Location = new Point(x1, y),
            AutoSize = true,
            Font = new Font(Font.FontFamily, 9F),
            ForeColor = Color.FromArgb(80, 80, 80),
            BackColor = parent.BackColor
        };
        var val = new Label
        {
            Text = value,
            Location = new Point(x2, y),
            Size = new Size(340, 20),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
            ForeColor = valueColor ?? Color.FromArgb(30, 30, 30),
            BackColor = parent.BackColor,
            AutoEllipsis = true
        };
        parent.Controls.Add(lab);
        parent.Controls.Add(val);
        return val;
    }

    private void UpdateFolderDisplay()
    {
        var folder = string.IsNullOrWhiteSpace(_appConfig.SelectedFolder) ? "(none)" : _appConfig.SelectedFolder;
        _ordersFolderLabel.Text = folder;
    }

    private void OnChooseFolder(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where to save WhatsApp order images",
            ShowNewFolderButton = true
        };

        if (!string.IsNullOrWhiteSpace(_appConfig.SelectedFolder) && Directory.Exists(_appConfig.SelectedFolder))
        {
            dialog.SelectedPath = _appConfig.SelectedFolder;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _appConfig.SelectedFolder = dialog.SelectedPath;
            _configService.Save(_appConfig);
            UpdateFolderDisplay();
            _log.Write("folder_selected", _appConfig.SelectedFolder);
        }
    }

    private void OnOpenWhatsApp(object? sender, EventArgs e)
    {
        _log.Write("whatsapp_opening");
        _imagesDetectedToday = 0;
        _imagesSavedToday = 0;
        UpdateLabel(_imagesDetectedLabel, "0");
        UpdateLabel(_imagesSavedLabel, "0");
        UpdateLabel(_unreadChatsLabel, "0");
        var waForm = new WhatsAppForm(_log, _appConfig);

        // Wire up live status updates from WhatsAppForm
        waForm.WhatsAppStatusChanged += status => UpdateLabel(_whatsappStatusLabel, status);
        waForm.ScannerStatusChanged += status => UpdateLabel(_scannerStatusLabel, status);
        waForm.UnreadChatsChanged += count => UpdateLabel(_unreadChatsLabel, count.ToString());
        waForm.CurrentChatChanged += name => UpdateLabel(_currentChatLabel, name);
        waForm.ImagesDetectedChanged += count =>
        {
            _imagesDetectedToday += count;
            UpdateLabel(_imagesDetectedLabel, _imagesDetectedToday.ToString());
        };
        waForm.ImagesSavedChanged += count =>
        {
            _imagesSavedToday += count;
            UpdateLabel(_imagesSavedLabel, _imagesSavedToday.ToString());
        };
        waForm.LastSavedFileChanged += path => UpdateLabel(_lastSavedFileLabel, path);
        waForm.LastErrorChanged += error => UpdateLabel(_lastErrorLabel, error);

        waForm.ShowDialog(this);
        waForm.Dispose();

        // Reset scanner status when WhatsApp window closes
        UpdateLabel(_scannerStatusLabel, "Idle");
        UpdateLabel(_currentChatLabel, "—");
    }

    private void UpdateLabel(Label label, string text)
    {
        if (label.InvokeRequired)
            label.Invoke(() => label.Text = text);
        else
            label.Text = text;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _log.Write("app_closing");
        base.OnFormClosing(e);
    }
}