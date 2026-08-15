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

    // CDP network interception for image downloads
    private readonly Dictionary<string, NetworkResponseInfo> _networkResponses = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _networkLock = new();
    private bool _cdpEnabled;

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
    /// Enable CDP network interception to capture image response bodies.
    /// Called once at the start of the first scan.
    /// </summary>
    private async Task EnableCdpAsync()
    {
        if (_cdpEnabled) return;
        _cdpEnabled = true;

        _webView.GetDevToolsProtocolEventReceiver("Network.responseReceived").DevToolsProtocolEventReceived += OnNetworkResponseReceived;
        await _webView.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        _log.Write("CDP_NETWORK_ENABLED");
    }

    /// <summary>
    /// Track image network responses for later retrieval via Network.getResponseBody.
    /// </summary>
    private void OnNetworkResponseReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            var json = e.ParameterObjectAsJson;
            if (string.IsNullOrEmpty(json)) return;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("response", out var response)) return;
            if (!response.TryGetProperty("url", out var urlProp)) return;
            if (!response.TryGetProperty("mimeType", out var mimeProp)) return;

            var url = urlProp.GetString() ?? "";
            var mimeType = mimeProp.GetString() ?? "";
            var requestId = root.TryGetProperty("requestId", out var reqIdProp) ? reqIdProp.GetString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(requestId)) return;

            // Only track image responses
            if (!mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return;

            lock (_networkLock)
            {
                _networkResponses[url] = new NetworkResponseInfo { RequestId = requestId, MimeType = mimeType, Url = url };
            }
        }
        catch
        {
        }
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

        // Enable CDP network interception (idempotent — only runs once)
        await EnableCdpAsync();

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

        // Marker ancestry diagnostics (when marker found but not associated)
        if (unreadMarkersFound > 0)
        {
            var markerHtml = node?["markerHtml"]?.GetValue<string>() ?? "";
            var parent1 = node?["parent1"]?.GetValue<string>() ?? "";
            var parent2 = node?["parent2"]?.GetValue<string>() ?? "";
            var parent3 = node?["parent3"]?.GetValue<string>() ?? "";
            var matchedChatRow = node?["matchedChatRow"]?.GetValue<bool>() ?? false;
            var matchedChatName = node?["matchedChatName"]?.GetValue<string>() ?? "";
            _log.Write("UNREAD_MARKER_HTML", markerHtml);
            _log.Write("PARENT_1", parent1);
            _log.Write("PARENT_2", parent2);
            _log.Write("PARENT_3", parent3);
            _log.Write("MATCHED_CHAT_ROW", matchedChatRow.ToString());
            _log.Write("MATCHED_CHAT_NAME", matchedChatName);
        }

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

            // Log skipped placeholder GIFs and duplicate srcs
            var filteredPlaceholder = imagesNode?["filteredPlaceholder"]?.GetValue<int>() ?? 0;
            var filteredDup = imagesNode?["filteredDup"]?.GetValue<int>() ?? 0;
            if (filteredPlaceholder > 0)
                _log.Write("MEDIA_SKIPPED", $"reason=placeholder_gif count={filteredPlaceholder}");
            if (filteredDup > 0)
                _log.Write("MEDIA_DUPLICATE_SRC", $"count={filteredDup}");

            if (images == null || images.Count == 0)
            {
                var totalImgs = imagesNode?["totalImgs"]?.GetValue<int>() ?? 0;
                var mainFound = imagesNode?["mainFound"]?.GetValue<bool>() ?? false;
                var filteredSrc = imagesNode?["filteredSrc"]?.GetValue<int>() ?? 0;
                var filteredSize = imagesNode?["filteredSize"]?.GetValue<int>() ?? 0;
                _log.Write("MEDIA_DETECTED", $"count=0 mainFound={mainFound} totalImgs={totalImgs} filteredSrc={filteredSrc} filteredSize={filteredSize} filteredPlaceholder={filteredPlaceholder} filteredDup={filteredDup}");
                continue;
            }

            _log.Write("MEDIA_DETECTED", $"count={images.Count}");
            ImagesDetectedChanged?.Invoke(images.Count);
            var detected = images.Count;
            var saved = 0;
            var downloaded = 0;
            var duplicates = 0;
            var failed = 0;

            var chatId = $"{customerName}|{phone}";
            var orderFolderBase = _db.GetOrderFolderBase(chatId);

            foreach (var image in images)
            {
                var src = image?["src"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(src))
                {
                    failed++;
                    _log.Write("MEDIA_FAILURE", "stage=SRC reason=empty_src");
                    continue;
                }
                var srcShort = src.Length > 80 ? src[..80] : src;

                // Download real image bytes via JavaScript fetch
                var imageBytes = await DownloadImageAsync(src);
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    failed++;
                    _log.Write("MEDIA_FAILURE", $"stage=DOWNLOAD reason=download_failed src={srcShort}");
                    continue;
                }

                downloaded++;
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
                try
                {
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
                }
                catch (Exception ex)
                {
                    failed++;
                    _log.Write("MEDIA_FAILURE", $"stage=FOLDER reason={ex.Message} src={srcShort}");
                    continue;
                }

                // Save image file
                try
                {
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
                catch (Exception ex)
                {
                    failed++;
                    _log.Write("MEDIA_FAILURE", $"stage=SAVE reason={ex.Message} src={srcShort}");
                }
            }

            // Reconciliation: detected = saved + duplicates + failed
            totalDuplicates += duplicates;
            _log.Write("RECONCILIATION", $"detected={detected} downloaded={downloaded} duplicates={duplicates} saved={saved} failed={failed}");
            if (detected != saved + duplicates + failed)
            {
                _log.Write("RECONCILIATION_ERROR", $"detected={detected} downloaded={downloaded} duplicates={duplicates} saved={saved} failed={failed}");
                LastErrorChanged?.Invoke($"Reconciliation mismatch: detected={detected} downloaded={downloaded} saved={saved} dup={duplicates} failed={failed}");
            }

            CurrentChatChanged?.Invoke($"{customerName} — {saved} new, {duplicates} dup, {failed} failed");
            UpdateStatus($"Processed: {customerName} — {saved} new, {duplicates} dup, {failed} failed");
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
            // Source type 1: data URL — bytes are already in the src, parse directly in C#
            if (url.StartsWith("data:"))
            {
                _log.Write("MEDIA_SOURCE_TYPE", "DATA_URL");
                var bytes = ParseDataUrl(url);
                if (bytes == null || bytes.Length == 0)
                {
                    _log.Write("MEDIA_DOWNLOAD_FAILED", "error=data_url_parse_failed");
                    return null;
                }
                _log.Write("DATA_URL_DECODED", $"bytes={bytes.Length}");
                _log.Write("MEDIA_DOWNLOADED", $"bytes={bytes.Length}");
                return bytes;
            }

            // Source type 2 & 3: blob: or http(s): — use CDP Network.getResponseBody
            _log.Write("MEDIA_SOURCE_TYPE", url.StartsWith("blob:") ? "BLOB_URL" : "HTTP_URL");

            // Brief delay to ensure network response is fully captured
            await Task.Delay(500);

            NetworkResponseInfo? info;
            lock (_networkLock)
            {
                if (!_networkResponses.TryGetValue(url, out info))
                {
                    // Fuzzy match by URL prefix (query params may differ)
                    info = _networkResponses.Values.FirstOrDefault(r =>
                        url.StartsWith(r.Url, StringComparison.OrdinalIgnoreCase) ||
                        r.Url.StartsWith(url, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (info == null)
            {
                var urlShort = url.Length > 80 ? url[..80] : url;
                _log.Write("MEDIA_FAILURE", $"stage=NETWORK_MATCH reason=no_matching_response url={urlShort}");
                return null;
            }

            _log.Write("NETWORK_REQUEST_MATCHED", $"requestId={info.RequestId} url={(url.Length > 80 ? url[..80] : url)}");

            var paramsJson = JsonSerializer.Serialize(new { requestId = info.RequestId });
            var resultJson = await _webView.CallDevToolsProtocolMethodAsync("Network.getResponseBody", paramsJson);

            using var resultDoc = JsonDocument.Parse(resultJson);
            var body = resultDoc.RootElement.GetProperty("body").GetString() ?? "";
            var base64Encoded = resultDoc.RootElement.TryGetProperty("base64Encoded", out var b64) && b64.GetBoolean();

            if (string.IsNullOrEmpty(body))
            {
                _log.Write("MEDIA_FAILURE", $"stage=NETWORK_MATCH reason=empty_body url={(url.Length > 80 ? url[..80] : url)}");
                return null;
            }

            byte[] imageBytes;
            if (base64Encoded)
            {
                imageBytes = Convert.FromBase64String(body);
            }
            else
            {
                imageBytes = System.Text.Encoding.UTF8.GetBytes(body);
            }

            _log.Write("RESPONSE_BODY_RECEIVED", $"bytes={imageBytes.Length} base64Encoded={base64Encoded}");
            _log.Write("MEDIA_DOWNLOADED", $"bytes={imageBytes.Length}");
            return imageBytes;
        }
        catch (Exception ex)
        {
            _log.Write("MEDIA_DOWNLOAD_FAILED", $"error={ex.Message}");
            LastErrorChanged?.Invoke($"Download exception: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parse a data URL (data:image/jpeg;base64,...) and return the raw bytes.
    /// Returns null for non-image types or placeholder GIFs.
    /// </summary>
    private static byte[]? ParseDataUrl(string dataUrl)
    {
        try
        {
            var commaIndex = dataUrl.IndexOf(',');
            if (commaIndex < 0) return null;
            var header = dataUrl[..commaIndex]; // e.g. "data:image/jpeg;base64"
            var base64 = dataUrl[(commaIndex + 1)..];

            // Validate MIME type — only accept image types
            if (!header.StartsWith("data:image/")) return null;

            // Skip placeholder GIFs (1x1 transparent)
            if (header.StartsWith("data:image/gif")) return null;

            if (string.IsNullOrEmpty(base64)) return null;
            return Convert.FromBase64String(base64);
        }
        catch
        {
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
            if (string.IsNullOrEmpty(raw)) return null;
            return ParseScriptResult(raw);
        }
        catch (Exception ex)
        {
            _log.Write("SCRIPT_ERROR", $"error={ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parse the raw result from ExecuteScriptAsync.
    /// Sync scripts returning JSON.stringify produce a JSON-encoded string (starts with ").
    /// Async scripts returning a resolved value may produce raw JSON directly (starts with { or [).
    /// Handle both cases.
    /// </summary>
    private JsonNode? ParseScriptResult(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("\""))
        {
            // JSON-encoded string — deserialize to get inner JSON, then parse
            var innerJson = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(innerJson)) return null;
            return JsonNode.Parse(innerJson);
        }
        // Already raw JSON — parse directly
        return JsonNode.Parse(raw);
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

    private sealed class NetworkResponseInfo
    {
        public string RequestId { get; set; } = "";
        public string MimeType { get; set; } = "";
        public string Url { get; set; } = "";
    }

    private static class Scripts
    {
        public const string GetUnreadChats = """
            (() => {
                const pane = document.querySelector('#pane-side');
                if (!pane) return JSON.stringify({ chats: [], chatRowsFound: 0, unreadMarkersFound: 0, markerHtml: '', parent1: '', parent2: '', parent3: '', matchedChatRow: false, matchedChatName: '' });
                
                var items = pane.querySelectorAll('[role="listitem"]');
                if (items.length === 0) items = pane.querySelectorAll('[data-testid="cell-frame-container"]');
                if (items.length === 0) items = pane.querySelectorAll('div[data-id]');
                if (items.length === 0) items = pane.querySelectorAll('div[role="button"]');
                
                const chatRowsFound = items.length;
                var unreadMarkersFound = 0;
                const chats = [];
                var markerHtml = '';
                var parent1 = '';
                var parent2 = '';
                var parent3 = '';
                var matchedChatRow = false;
                var matchedChatName = '';
                
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
                        
                        // Try to find name: first within item, then walk UP from badge
                        var nameEl = item.querySelector('span[title]');
                        var name = nameEl ? (nameEl.getAttribute('title') || '') : '';
                        
                        if (!name) {
                            // Walk up from badge to find an ancestor containing span[title]
                            var walker = badge;
                            for (var level = 0; level < 10 && walker; level++) {
                                walker = walker.parentElement;
                                if (!walker) break;
                                var titleEl = walker.querySelector('span[title]');
                                if (titleEl) {
                                    name = titleEl.getAttribute('title') || '';
                                    break;
                                }
                            }
                        }
                        
                        if (unreadCount === 0) {
                            var badgeText = (badge.textContent || '').trim();
                            unreadCount = parseInt(badgeText) || 1;
                        }
                        
                        // Collect diagnostics for first marker only
                        if (unreadMarkersFound === 1) {
                            markerHtml = (badge.outerHTML || '').substring(0, 300);
                            var p = badge.parentElement;
                            if (p) { parent1 = (p.outerHTML || '').substring(0, 300); p = p.parentElement; }
                            if (p) { parent2 = (p.outerHTML || '').substring(0, 300); p = p.parentElement; }
                            if (p) { parent3 = (p.outerHTML || '').substring(0, 300); }
                            matchedChatRow = !!name;
                            matchedChatName = name || '';
                        }
                        
                        if (name) {
                            chats.push({ index: idx, name: name, unreadCount: unreadCount });
                        }
                    }
                });
                
                return JSON.stringify({ 
                    chats: chats, 
                    chatRowsFound: chatRowsFound, 
                    unreadMarkersFound: unreadMarkersFound,
                    markerHtml: markerHtml,
                    parent1: parent1,
                    parent2: parent2,
                    parent3: parent3,
                    matchedChatRow: matchedChatRow,
                    matchedChatName: matchedChatName
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
                if (!main) return JSON.stringify({ images: [], mainFound: false, totalImgs: 0, filteredSrc: 0, filteredSize: 0, filteredPlaceholder: 0, filteredDup: 0 });
                const imgs = main.querySelectorAll('img');
                const totalImgs = imgs.length;
                const seen = new Set();
                const images = [];
                var filteredSrc = 0;
                var filteredSize = 0;
                var filteredPlaceholder = 0;
                var filteredDup = 0;
                for (const img of imgs) {
                    const src = img.getAttribute('src') || '';
                    if (!src) { filteredSrc++; continue; }
                    if (!src.startsWith('blob:') && !src.startsWith('data:') && !src.startsWith('http')) { filteredSrc++; continue; }
                    // Skip 1x1 transparent GIF placeholders
                    if (src.startsWith('data:image/gif;base64,R0lGODlh')) { filteredPlaceholder++; continue; }
                    const w = img.naturalWidth || img.width || 0;
                    const h = img.naturalHeight || img.height || 0;
                    if (w <= 50 && h <= 50 && !src.startsWith('data:')) { filteredSize++; continue; }
                    // Deduplicate by src — same image appears multiple times in DOM
                    if (seen.has(src)) { filteredDup++; continue; }
                    seen.add(src);
                    images.push({ src: src });
                }
                return JSON.stringify({ images: images, mainFound: true, totalImgs: totalImgs, filteredSrc: filteredSrc, filteredSize: filteredSize, filteredPlaceholder: filteredPlaceholder, filteredDup: filteredDup });
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