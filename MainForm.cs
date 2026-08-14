using System.Windows.Forms;

namespace AIXWhatsAppLocal;

/// <summary>
/// Main window — Milestone 2: WhatsApp → Local Order Folders.
/// 1. Choose Orders Folder — user picks where customer image folders go.
/// 2. Open WhatsApp — opens WhatsApp Web with persistent session + auto media capture.
/// </summary>
public sealed class MainForm : Form
{
    private readonly ConfigService _configService = new();
    private readonly LogService _log = new();
    private AppConfig _appConfig;

    private Label _statusLabel = null!;
    private Label _folderLabel = null!;
    private Button _chooseFolderButton = null!;
    private Button _openWhatsAppButton = null!;

    public MainForm()
    {
        _appConfig = _configService.Load();
        InitializeUI();
        UpdateStatus();
        _log.Write("app_started");
    }

    private void InitializeUI()
    {
        Text = "AIX WhatsApp Local";
        Width = 500;
        Height = 340;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;

        var title = new Label
        {
            Text = "AIX WhatsApp Local",
            Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
            Location = new Point(24, 20),
            AutoSize = true
        };

        var subtitle = new Label
        {
            Text = "WhatsApp Web → Local Order Folders",
            Font = new Font(Font.FontFamily, 9F),
            ForeColor = Color.Gray,
            Location = new Point(24, 52),
            AutoSize = true
        };

        _chooseFolderButton = new Button
        {
            Text = "Choose Orders Folder",
            Location = new Point(24, 90),
            Size = new Size(220, 48),
            Font = new Font(Font.FontFamily, 10F)
        };
        _chooseFolderButton.Click += OnChooseFolder;

        _openWhatsAppButton = new Button
        {
            Text = "Open WhatsApp",
            Location = new Point(252, 90),
            Size = new Size(220, 48),
            Font = new Font(Font.FontFamily, 10F)
        };
        _openWhatsAppButton.Click += OnOpenWhatsApp;

        _statusLabel = new Label
        {
            Text = "WhatsApp: Not Connected",
            Location = new Point(24, 165),
            Size = new Size(448, 30),
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold)
        };

        _folderLabel = new Label
        {
            Text = "Orders Folder: (none)",
            Location = new Point(24, 200),
            Size = new Size(448, 30),
            Font = new Font(Font.FontFamily, 9F),
            ForeColor = Color.Gray
        };

        var infoLabel = new Label
        {
            Text = "Config and logs: %LocalAppData%\\AIXWhatsAppLocal\\",
            Location = new Point(24, 240),
            Size = new Size(448, 20),
            Font = new Font(Font.FontFamily, 8F),
            ForeColor = Color.LightGray
        };

        Controls.AddRange(new Control[] { title, subtitle, _chooseFolderButton, _openWhatsAppButton, _statusLabel, _folderLabel, infoLabel });
    }

    private void UpdateStatus()
    {
        _folderLabel.Text = $"Orders Folder: {(string.IsNullOrWhiteSpace(_appConfig.SelectedFolder) ? "(none)" : _appConfig.SelectedFolder)}";
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
            UpdateStatus();
            _log.Write("folder_selected", _appConfig.SelectedFolder);
        }
    }

    private void OnOpenWhatsApp(object? sender, EventArgs e)
    {
        _log.Write("whatsapp_opening");
        var waForm = new WhatsAppForm(_log, _appConfig);
        waForm.StatusChanged += status =>
        {
            if (_statusLabel.InvokeRequired)
                _statusLabel.Invoke(() => _statusLabel.Text = $"WhatsApp: {status}");
            else
                _statusLabel.Text = $"WhatsApp: {status}";
        };
        waForm.ShowDialog(this);
        waForm.Dispose();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _log.Write("app_closing");
        base.OnFormClosing(e);
    }
}