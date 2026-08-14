using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace AIXWhatsAppLocal;

/// <summary>
/// Scans WhatsApp Web for new images, downloads real image bytes,
/// deduplicates via SHA-256 + SQLite, and saves to local customer folders.
///
/// Pipeline per image:
/// CHAT_DETECTED → CUSTOMER_IDENTIFIED → MEDIA_DETECTED → MEDIA_DOWNLOADED
/// → DUPLICATE_CHECK → FILE_SAVED → CUSTOMER_COUNT_UPDATED
///
/// Reports live status to WhatsAppForm/MainForm via events.
/// </summary>
public sealed class MediaCaptureService : IDisposable
{
    private readonly CoreWebView2 _webView;
    private readonly LogService _log;
    private readonly MediaDatabase _db;
    private readonly string _ordersRoot;

    // Events for live dashboard
    public event Action<string>? CaptureStatusChanged;
    public event Action<string>? ScannerStatusChanged;
    public event Action<int>? UnreadChatsChanged;
    public event Action<string>? CurrentChatChanged;
    public event Action<int>? ImagesDetectedChanged;
    public event Action<int>? ImagesSavedChanged;
    public event Action<string>? LastSavedFileChanged;
    public event Action<string>? LastErrorChanged;

    public MediaCaptureService(CoreWebView2 webView, LogService log, MediaDatabase db, string ordersRoot)
    {
        _webView = webView;
        _log = log;
        _db = db;
        _ordersRoot = ordersRoot;
    }

    /// <summary>
    /// Main scan loop: find unread chats, open each, detect images, download, dedup, save.
    /// </summary>
    public async Task ScanAndCaptureAsync()
    {
        if (string.IsNullOrWhiteSpace(_ordersRoot))
        {
            _log.Write("SCAN_SKIPPED", "reason=orders_root_not_set");
            return;
        }

        ScannerStatusChanged?.Invoke("Scanning...");
        UpdateStatus("Scanning chats...");

        var node = await ExecuteScriptJsonAsync(Scripts.GetUnreadChats);
        var chats = node?["chats"]?.AsArray();
        var chatRowsFound = node?["chatRowsFound"]?.GetValue<int>() ?? 0;
        var unreadMarkersFound = node?["unreadMarkersFound"]?.GetValue<int>() ?? 0;
        var unreadCount = chats?.Count ?? 0;

        // Diagnostic logging
        _log.Write("CHAT_ROWS_FOUND", chatRowsFound.ToString());
        _log.Write("UNREAD_MARKERS_FOUND", unreadMarkersFound.ToString());
        _log.Write("UNREAD_CHAT_MATCHES", unreadCount.ToString());
        UnreadChatsChanged?.Invoke(unreadCount);

        if (chats == null || chats.Count == 0)
        {
            ScannerStatusChanged?.Invoke($"Idle — {chatRowsFound} chats, {unreadMarkersFound} markers");
            UpdateStatus("Idle — no unread chats");
            return;
        }

        _log.Write("SCAN_START", $"unread_chats={chats.Count}");
        ScannerStatusChanged?.Invoke($"Scanning {chats.Count} chats");
        var totalSaved = 0;
        var totalDuplicates = 0;

        foreach (var chat in chats)
        {
            var index = chat?["index"]?.GetValue<int>() ?? 0;
            var name = chat?["name"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(name)) continue;

            var chatUnreadCount = chat?["unreadCount"]?.GetValue<int>() ?? 0;
            _log.Write("CHAT_NAME", name);
            _log.Write("UNREAD_COUNT", chatUnreadCount.ToString());
            CurrentChatChanged?.Invoke(name);
            UpdateStatus($"Opening: {name}");

            // Open the chat
            await ExecuteScriptAsync(Scripts.OpenChat.Replace("__INDEX__", index.ToString()));
            await Task.Delay(2500); // Wait for messages to load

            // Get customer info from the chat header
            var infoNode = await ExecuteScriptJsonAsync(Scripts.GetCustomerInfo);
            var customerName = infoNode?["name"]?.GetValue<string>() ?? "";
            var phone = infoNode?["phone"]?.GetValue<string>() ?? "";

            if (string.IsNullOrWhiteSpace(customerName))
                customerName = name;

            // If name looks like a phone number, use it as phone
            if (string.IsNullOrWhiteSpace(phone))
            {
                var digits = new string(customerName.Where(char.IsDigit).ToArray());
                if (digits.Length >= 8)
                {
                    phone = digits;
                    customerName = "Unknown";
                }
            }

            if (string.IsNullOrWhiteSpace(phone)) phone = "UnknownPhone";
            if (string.IsNullOrWhiteSpace(customerName)) customerName = "Unknown";

            _log.Write("CUSTOMER_IDENTIFIED", $"name={customerName} phone={phone}");
            CurrentChatChanged?.Invoke(customerName);

            // Detect images in the current chat
            var imagesNode = await ExecuteScriptJsonAsync(Scripts.DetectImages);
            var images = imagesNode?["images"]?.AsArray();
            if (images == null || images.Count == 0)
            {
                _log.Write("MEDIA_DETECTED", "count=0");
                continue;
            }

            _log.Write("MEDIA_DETECTED", $"count={images.Count}");
            ImagesDetectedChanged?.Invoke(images.Count);
            var detected = images.Count;
            var saved = 0;
            var duplicates = 0;

            var chatId = $"{customerName}|{phone}";
            var orderFolderBase = _db.GetOrderFolderBase(chatId);

            foreach (var image in images)
            {
                var src = image?["src"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(src)) continue;

                // Download real image bytes via JavaScript fetch
                var imageBytes = await DownloadImageAsync(src);
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    _log.Write("MEDIA_DOWNLOADED", "result=FAILED");
                    continue;
                }

                _log.Write("MEDIA_DOWNLOADED", $"bytes={imageBytes.Length}");

                // Compute SHA-256 for dedup
                var sha256 = ComputeSha256(imageBytes);

                // Check dedup
                if (_db.IsDuplicate(sha256))
                {
                    duplicates++;
                    _log.Write("DUPLICATE_CHECK", $"result=DUPLICATE sha256={sha256[..12]}");
                    continue;
                }

                _log.Write("DUPLICATE_CHECK", $"result=NEW sha256={sha256[..12]}");

                // Find or create customer folder
                string folderPath;
                if (orderFolderBase != null)
                {
                    var existing = CustomerFolderService.FindExistingFolder(orderFolderBase);
                    folderPath = existing ?? CustomerFolderService.CreateOrderFolder(_ordersRoot, customerName, phone);
                }
                else
                {
                    folderPath = CustomerFolderService.CreateOrderFolder(_ordersRoot, customerName, phone);
                    orderFolderBase = CustomerFolderService.GetOrderFolderBase(_ordersRoot, customerName, phone);
                }

                // Save image file
                var imageIndex = CustomerFolderService.GetNextImageIndex(folderPath);
                var localPath = CustomerFolderService.SaveImage(folderPath, imageBytes, imageIndex);
                _log.Write("FILE_SAVED", $"path={localPath}");
                LastSavedFileChanged?.Invoke(localPath);

                // Update folder count (rename folder to match actual file count)
                var actualFolder = CustomerFolderService.UpdateFolderCount(folderPath);
                var newCount = CustomerFolderService.CountFiles(actualFolder);
                _log.Write("CUSTOMER_COUNT_UPDATED", $"count={newCount}");

                // Insert into database for dedup
                var mediaId = Guid.NewGuid().ToString("N");
                _db.InsertMedia(mediaId, chatId, customerName, phone,
                    DateTime.Now.ToString("o"), sha256, localPath, orderFolderBase);

                saved++;
                totalSaved++;
                ImagesSavedChanged?.Invoke(1);
            }

            // Reconciliation: detected = saved + duplicates
            var reconStatus = detected == saved + duplicates ? "OK" : "MISMATCH";
            _log.Write("RECONCILIATION", $"detected={detected} saved={saved} duplicates={duplicates} status={reconStatus}");
            if (reconStatus == "MISMATCH")
            {
                _log.Write("RECONCILIATION_ERROR", $"detected={detected} saved={saved} duplicates={duplicates}");
                LastErrorChanged?.Invoke($"Reconciliation mismatch: detected={detected} saved={saved} dup={duplicates}");
            }

            CurrentChatChanged?.Invoke($"{customerName} — {saved} new, {duplicates} dup");
            UpdateStatus($"Processed: {customerName} — {saved} new, {duplicates} dup");
        }

        _log.Write("SCAN_COMPLETE", $"total_saved={totalSaved} total_duplicates={totalDuplicates}");
        ScannerStatusChanged?.Invoke($"Done — {totalSaved} new, {totalDuplicates} dup");
        UpdateStatus($"Scan done — {totalSaved} new, {totalDuplicates} dup");
        CurrentChatChanged?.Invoke("—");
    }

    /// <summary>
    /// Download image bytes via JavaScript fetch (works for blob:, data:, and http: URLs).
    /// Returns null on failure.
    /// </summary>
    private async Task<byte[]?> DownloadImageAsync(string url)
    {
        try
        {
            var js = Scripts.FetchImage.Replace("__URL_JSON__", JsonSerializer.Serialize(url));
            var node = await ExecuteScriptJsonAsync(js);
            if (node?["error"] != null)
            {
                var error = node["error"]?.GetValue<string>() ?? "unknown";
                _log.Write("MEDIA_DOWNLOAD_FAILED", $"url={url} error={error}");
                LastErrorChanged?.Invoke($"Download failed: {error}");
                return null;
            }

            var base64 = node?["base64"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(base64)) return null;
            return Convert.FromBase64String(base64);
        }
        catch (Exception ex)
        {
            _log.Write("MEDIA_DOWNLOAD_FAILED", $"url={url} error={ex.Message}");
            LastErrorChanged?.Invoke($"Download exception: {ex.Message}");
            return null;
        }
    }

    private static string ComputeSha256(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<JsonNode?> ExecuteScriptJsonAsync(string script)
    {
        try
        {
            var raw = await _webView.ExecuteScriptAsync(script);
            var innerJson = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(innerJson)) return null;
            return JsonNode.Parse(innerJson);
        }
        catch (Exception ex)
        {
            _log.Write("SCRIPT_ERROR", $"error={ex.Message}");
            return null;
        }
    }

    private async Task ExecuteScriptAsync(string script)
    {
        try
        {
            await _webView.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            _log.Write("SCRIPT_ERROR", $"error={ex.Message}");
        }
    }

    private void UpdateStatus(string status)
    {
        CaptureStatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
    }

    private static class Scripts
    {
        public const string GetUnreadChats = """
            (() => {
                const pane = document.querySelector('#pane-side');
                if (!pane) return JSON.stringify({ chats: [], chatRowsFound: 0, unreadMarkersFound: 0 });
                
                var items = pane.querySelectorAll('[role="listitem"]');
                if (items.length === 0) items = pane.querySelectorAll('[data-testid="cell-frame-container"]');
                if (items.length === 0) items = pane.querySelectorAll('div[data-id]');
                if (items.length === 0) items = pane.querySelectorAll('div[role="button"]');
                
                const chatRowsFound = items.length;
                var unreadMarkersFound = 0;
                const chats = [];
                
                items.forEach(function(item, idx) {
                    var badge = null;
                    var unreadCount = 0;
                    
                    // Method 1: aria-label containing "unread"
                    badge = item.querySelector('span[aria-label*="unread" i]');
                    if (!badge) badge = item.querySelector('div[aria-label*="unread" i]');
                    if (!badge) badge = item.querySelector('span[aria-label*="הודעות"]');
                    
                    // Method 2: data-testid with "unread"
                    if (!badge) badge = item.querySelector('[data-testid*="unread" i]');
                    
                    // Method 3: WhatsApp green badge (rgb(37,211,102)) with a number
                    if (!badge) {
                        var spans = item.querySelectorAll('span');
                        for (var i = 0; i < spans.length; i++) {
                            var sp = spans[i];
                            var t = (sp.textContent || '').trim();
                            if (/^\d+$/.test(t) && t.length <= 3 && sp.offsetWidth > 0 && sp.offsetWidth <= 30) {
                                var st = window.getComputedStyle(sp);
                                var bg = st.backgroundColor;
                                if (bg && (bg.indexOf('37, 211, 102') >= 0 || bg.indexOf('25, 211, 102') >= 0 || bg.indexOf('37,211,102') >= 0 || bg.indexOf('25,211,102') >= 0)) {
                                    badge = sp;
                                    unreadCount = parseInt(t);
                                    break;
                                }
                            }
                        }
                    }
                    
                    // Method 4: Any small number at the right side of the row (badge position)
                    if (!badge) {
                        var spans2 = item.querySelectorAll('span');
                        for (var j = 0; j < spans2.length; j++) {
                            var sp2 = spans2[j];
                            var t2 = (sp2.textContent || '').trim();
                            if (/^\d+$/.test(t2) && t2.length <= 3 && sp2.offsetWidth > 0 && sp2.offsetWidth <= 30) {
                                var rect = sp2.getBoundingClientRect();
                                var itemRect = item.getBoundingClientRect();
                                if (rect.right > itemRect.right - 60 && rect.width > 0) {
                                    badge = sp2;
                                    unreadCount = parseInt(t2);
                                    break;
                                }
                            }
                        }
                    }
                    
                    // Method 5: Green dot/circle indicator (unread without count)
                    if (!badge) {
                        var allEls = item.querySelectorAll('span, div');
                        for (var k = 0; k < allEls.length; k++) {
                            var el = allEls[k];
                            var stl = window.getComputedStyle(el);
                            var bgc = stl.backgroundColor;
                            if (bgc && (bgc.indexOf('37, 211, 102') >= 0 || bgc.indexOf('25, 211, 102') >= 0 || bgc.indexOf('37,211,102') >= 0)) {
                                if (el.offsetWidth > 0 && el.offsetWidth <= 25 && el.offsetHeight <= 25) {
                                    var elText = (el.textContent || '').trim();
                                    badge = el;
                                    unreadCount = elText ? parseInt(elText) : 1;
                                    break;
                                }
                            }
                        }
                    }
                    
                    if (badge) {
                        unreadMarkersFound++;
                        var nameEl = item.querySelector('span[title]');
                        var name = nameEl ? nameEl.getAttribute('title') : '';
                        if (unreadCount === 0) {
                            var badgeText = (badge.textContent || '').trim();
                            unreadCount = parseInt(badgeText) || 1;
                        }
                        if (name) {
                            chats.push({ index: idx, name: name, unreadCount: unreadCount });
                        }
                    }
                });
                
                return JSON.stringify({ 
                    chats: chats, 
                    chatRowsFound: chatRowsFound, 
                    unreadMarkersFound: unreadMarkersFound 
                });
            })();
            """;

        public const string OpenChat = """
            (() => {
                const pane = document.querySelector('#pane-side');
                if (!pane) return JSON.stringify({ clicked: false });
                const items = pane.querySelectorAll('[role="listitem"]');
                if (items[__INDEX__]) {
                    items[__INDEX__].click();
                    return JSON.stringify({ clicked: true });
                }
                return JSON.stringify({ clicked: false });
            })();
            """;

        public const string GetCustomerInfo = """
            (() => {
                const header = document.querySelector('header');
                if (!header) return JSON.stringify({ name: '', phone: '' });
                const nameEl = header.querySelector('span[title]');
                const name = nameEl ? nameEl.getAttribute('title') : '';
                let phone = '';
                const spans = header.querySelectorAll('span[dir="auto"]');
                for (const span of spans) {
                    const text = span.textContent || '';
                    const match = text.match(/[\+]?\d[\d\s\-()]{7,}/);
                    if (match) { phone = match[0].replace(/[\s\-()]/g, ''); break; }
                }
                return JSON.stringify({ name: name, phone: phone });
            })();
            """;

        public const string DetectImages = """
            (() => {
                const main = document.querySelector('#main');
                if (!main) return JSON.stringify({ images: [] });
                const imgs = main.querySelectorAll('img');
                const images = [];
                for (const img of imgs) {
                    const src = img.getAttribute('src') || '';
                    if (!src) continue;
                    if (src.startsWith('blob:') || src.startsWith('data:') || src.startsWith('http')) {
                        const w = img.naturalWidth || img.width || 0;
                        const h = img.naturalHeight || img.height || 0;
                        if (w > 50 || h > 50 || src.startsWith('data:')) {
                            images.push({ src: src });
                        }
                    }
                }
                return JSON.stringify({ images: images });
            })();
            """;

        public const string FetchImage = """
            (async () => {
                try {
                    const response = await fetch(__URL_JSON__);
                    if (!response.ok) return JSON.stringify({ error: 'HTTP ' + response.status });
                    const blob = await response.blob();
                    return new Promise(resolve => {
                        const reader = new FileReader();
                        reader.onloadend = () => {
                            const base64 = reader.result.split(',')[1];
                            resolve(JSON.stringify({ base64: base64, size: blob.size, type: blob.type }));
                        };
                        reader.onerror = () => resolve(JSON.stringify({ error: 'FileReader error' }));
                        reader.readAsDataURL(blob);
                    });
                } catch (e) {
                    return JSON.stringify({ error: e.message });
                }
            })();
            """;
    }
}