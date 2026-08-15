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

        // Process ONLY ONE unread chat per scan cycle.
        // WhatsApp virtualizes/recycles chat list DOM cells, so we cannot iterate
        // through a previously captured list. After one click + verify + media,
        // return; the timer will trigger a fresh scan for the next unread.
        {
            var clickNode = await ExecuteScriptJsonAsync(Scripts.ClickNextUnreadChat);
            var clicked = clickNode?["clicked"]?.GetValue<bool>() ?? false;
            var name = clickNode?["name"]?.GetValue<string>() ?? "";
            var clickTargetHtml = clickNode?["clickTargetHtml"]?.GetValue<string>() ?? "";
            var clickTargetIndex = clickNode?["clickTargetIndex"]?.GetValue<int>() ?? -1;
            var chatUnreadCount = clickNode?["unreadCount"]?.GetValue<int>() ?? 0;
            var atomicClickTargetName = clickNode?["atomicClickTargetName"]?.GetValue<string>() ?? "";
            var atomicClickConnected = clickNode?["atomicClickConnected"]?.GetValue<bool>() ?? false;
            var atomicClickUnreadPresent = clickNode?["atomicClickUnreadPresent"]?.GetValue<bool>() ?? false;

            if (!clicked || string.IsNullOrWhiteSpace(name))
            {
                _log.Write("NO_MORE_UNREAD", "reason=no_unread_found");
                goto scan_complete;
            }

            // === CHAT SELECTION VERIFICATION ===
            // The name and click happen in the SAME script — the row is found,
            // scrolled into view, and clicked atomically in one JavaScript execution.
            _log.Write("MATCHED_CHAT_NAME", name);
            _log.Write("ATOMIC_CLICK_TARGET_NAME", atomicClickTargetName);
            _log.Write("ATOMIC_CLICK_CONNECTED", atomicClickConnected.ToString().ToLowerInvariant());
            _log.Write("ATOMIC_CLICK_UNREAD_PRESENT", atomicClickUnreadPresent.ToString().ToLowerInvariant());
            _log.Write("CLICK_TARGET_NAME", name);
            _log.Write("CLICK_TARGET_HTML", (clickTargetHtml.Length > 300 ? clickTargetHtml[..300] : clickTargetHtml));
            _log.Write("CLICK_TARGET_INDEX", clickTargetIndex.ToString());
            _log.Write("CHAT_NAME", name);
            _log.Write("UNREAD_COUNT", chatUnreadCount.ToString());
            _log.Write("CHAT_CLICKED", $"name={name}");
            CurrentChatChanged?.Invoke(name);
            UpdateStatus($"Opening: {name}");

            await Task.Delay(2500); // Wait for conversation panel to load

            // Read the actual active conversation name from the conversation header
            var infoNode = await ExecuteScriptJsonAsync(Scripts.GetCustomerInfo);
            var activeChatName = infoNode?["name"]?.GetValue<string>() ?? "";
            var phone = infoNode?["phone"]?.GetValue<string>() ?? "";

            // Header diagnostics — identify why active name may be empty
            var mainPanelFound = infoNode?["mainFound"]?.GetValue<bool>() ?? false;
            var mainHtml = infoNode?["mainHtml"]?.GetValue<string>() ?? "";
            var mainHeadersFound = infoNode?["mainHeadersFound"]?.GetValue<int>() ?? 0;
            var headerFound = infoNode?["headerFound"]?.GetValue<bool>() ?? false;
            var headerHtml = infoNode?["headerHtml"]?.GetValue<string>() ?? "";
            var headerTestId = infoNode?["headerTestId"]?.GetValue<string>() ?? "";
            var nameSource = infoNode?["nameSource"]?.GetValue<string>() ?? "";
            var spanTitles = infoNode?["spanTitles"]?.AsArray();
            var ariaLabels = infoNode?["ariaLabels"]?.AsArray();
            var textCandidates = infoNode?["textCandidates"]?.AsArray();
            var mainSpanTitles = infoNode?["mainSpanTitles"]?.AsArray();
            var mainAriaLabels = infoNode?["mainAriaLabels"]?.AsArray();

            _log.Write("MAIN_FOUND", mainPanelFound.ToString().ToLowerInvariant());
            _log.Write("MAIN_HTML", (mainHtml.Length > 2000 ? mainHtml[..2000] : mainHtml));
            _log.Write("MAIN_HEADERS_FOUND", mainHeadersFound.ToString());
            _log.Write("HEADER_ROOT_FOUND", headerFound.ToString().ToLowerInvariant());
            _log.Write("HEADER_TESTID", headerTestId);
            _log.Write("HEADER_HTML", (headerHtml.Length > 500 ? headerHtml[..500] : headerHtml));
            _log.Write("HEADER_SPAN_TITLES", spanTitles != null ? string.Join(" | ", spanTitles.Select(s => s?.GetValue<string>() ?? "")) : "");
            _log.Write("HEADER_ARIA_LABELS", ariaLabels != null ? string.Join(" | ", ariaLabels.Select(s => s?.GetValue<string>() ?? "")) : "");
            _log.Write("HEADER_TEXT_CANDIDATES", textCandidates != null ? string.Join(" | ", textCandidates.Select(s => s?.GetValue<string>() ?? "")) : "");
            _log.Write("MAIN_SPAN_TITLES", mainSpanTitles != null ? string.Join(" | ", mainSpanTitles.Select(s => s?.GetValue<string>() ?? "")) : "");
            _log.Write("MAIN_ARIA_LABELS", mainAriaLabels != null ? string.Join(" | ", mainAriaLabels.Select(s => s?.GetValue<string>() ?? "")) : "");
            _log.Write("ACTIVE_CHAT_NAME_SOURCE", nameSource);
            _log.Write("ACTIVE_CHAT_NAME", activeChatName);
            _log.Write("ACTIVE_CHAT_READY", $"name={activeChatName}");

            // Phone / JID diagnostics
            _log.Write("CUSTOMER_NAME", activeChatName);
            var phoneSource = infoNode?["phoneSource"]?.GetValue<string>() ?? "";
            var dataIds = infoNode?["dataIds"]?.AsArray();
            var phoneCandidates = infoNode?["phoneCandidates"]?.AsArray();
            _log.Write("HEADER_DATA_IDS", dataIds != null ? string.Join(" | ", dataIds.Select(s => s?.GetValue<string>() ?? "")) : "");
            _log.Write("PHONE_CANDIDATES", phoneCandidates != null ? string.Join(" | ", phoneCandidates.Select(s => s?.GetValue<string>() ?? "")) : "");
            _log.Write("CUSTOMER_PHONE_SOURCE", phoneSource);
            _log.Write("CUSTOMER_PHONE", phone);

            // 5. Compare target vs active
            var chatMatch = !string.IsNullOrWhiteSpace(activeChatName) &&
                string.Equals(activeChatName, name, StringComparison.OrdinalIgnoreCase);
            _log.Write("CHAT_MATCH", chatMatch.ToString().ToLowerInvariant());

            // 6. If mismatch — do not process media
            if (!chatMatch)
            {
                _log.Write("CHAT_OPEN_MISMATCH", $"target={name} active={activeChatName}");
                CurrentChatChanged?.Invoke($"{name} — mismatch (active: {activeChatName})");
                UpdateStatus($"Chat mismatch: target={name} active={activeChatName}");
                goto scan_complete;
            }

            // Confirmed correct chat — use verified active name as customer name
            var customerName = activeChatName;

            // === Contact phone discovery ===
            // GetCustomerInfo found only internal IDs (AC...) in #main data-id.
            // GetContactPhone scans #pane-side JIDs, #main attributes, visible text,
            // and as a last resort opens the contact-info panel.
            var phoneNode = await ExecuteScriptJsonAsync(Scripts.GetContactPhone);
            var contactPhone = phoneNode?["phone"]?.GetValue<string>() ?? "";
            var phoneAttrCands = phoneNode?["phoneAttrCandidates"]?.AsArray();
            var phoneTextCands = phoneNode?["phoneTextCandidates"]?.AsArray();
            var phoneJidCands = phoneNode?["phoneJidCandidates"]?.AsArray();
            var contactPanelOpened = phoneNode?["openedContactPanel"]?.GetValue<bool>() ?? false;
            _log.Write("PHONE_ATTR_CANDIDATES", phoneAttrCands != null ? string.Join(" | ", phoneAttrCands.Select(s => s?.GetValue<string>() ?? "")) : "");
            _log.Write("PHONE_TEXT_CANDIDATES", phoneTextCands != null ? string.Join(" | ", phoneTextCands.Select(s => s?.GetValue<string>() ?? "")) : "");
            _log.Write("PHONE_JID_CANDIDATES", phoneJidCands != null ? string.Join(" | ", phoneJidCands.Select(s => s?.GetValue<string>() ?? "")) : "");
            _log.Write("CONTACT_PANEL_OPENED", contactPanelOpened.ToString().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(contactPhone))
            {
                phone = contactPhone;
                _log.Write("CUSTOMER_PHONE_SOURCE", phoneNode?["phoneSource"]?.GetValue<string>() ?? "contact_phone");
                _log.Write("CUSTOMER_PHONE", phone);
            }

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

            // Log skipped placeholder GIFs, duplicate srcs, and filtered previews
            var filteredPlaceholder = imagesNode?["filteredPlaceholder"]?.GetValue<int>() ?? 0;
            var filteredDup = imagesNode?["filteredDup"]?.GetValue<int>() ?? 0;
            var filteredPreview = imagesNode?["filteredPreview"]?.GetValue<int>() ?? 0;
            var messageGroups = imagesNode?["messageGroups"]?.GetValue<int>() ?? 0;
            if (filteredPlaceholder > 0)
                _log.Write("MEDIA_SKIPPED", $"reason=placeholder_gif count={filteredPlaceholder}");
            if (filteredDup > 0)
                _log.Write("MEDIA_DUPLICATE_SRC", $"count={filteredDup}");
            if (filteredPreview > 0)
                _log.Write("MEDIA_SKIPPED", $"reason=preview_or_thumbnail count={filteredPreview}");

            // Log classification for every candidate (ORIGINAL/PREVIEW/UNKNOWN)
            var candidates = imagesNode?["candidates"]?.AsArray();
            if (candidates != null)
            {
                foreach (var c in candidates)
                {
                    var cSrc = c?["source"]?.GetValue<string>() ?? "";
                    var cClass = c?["classification"]?.GetValue<string>() ?? "";
                    var cBytes = c?["bytes"]?.GetValue<int>() ?? 0;
                    _log.Write("MEDIA_CLASSIFICATION", $"{cClass} source={cSrc} bytes={cBytes}");
                }
            }

            if (images == null || images.Count == 0)
            {
                var totalImgs = imagesNode?["totalImgs"]?.GetValue<int>() ?? 0;
                var mainFound = imagesNode?["mainFound"]?.GetValue<bool>() ?? false;
                var filteredSrc = imagesNode?["filteredSrc"]?.GetValue<int>() ?? 0;
                var filteredSize = imagesNode?["filteredSize"]?.GetValue<int>() ?? 0;
                _log.Write("MEDIA_DETECTED", $"count=0 mainFound={mainFound} totalImgs={totalImgs} filteredSrc={filteredSrc} filteredSize={filteredSize} filteredPlaceholder={filteredPlaceholder} filteredDup={filteredDup} filteredPreview={filteredPreview} messageGroups={messageGroups}");
                goto scan_complete;
            }

            _log.Write("MEDIA_DETECTED", $"count={images.Count} messageGroups={messageGroups} filteredPreview={filteredPreview}");
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
                        orderFolderBase = CustomerFolderService.GetBasePathFromFolder(folderPath);
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

        scan_complete:
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
                
                // Primary selector MUST match OpenChat for index consistency.
                // cell-frame-container = the actual chat row in WhatsApp Web.
                var items = pane.querySelectorAll('[data-testid="cell-frame-container"]');
                if (items.length === 0) items = pane.querySelectorAll('[role="listitem"]');
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
                        
                        // Find the chat row container that holds THIS badge.
                        // Use closest() to scope the name search to the same cell-frame-container
                        // as the unread badge — NOT a broader item that may span multiple rows.
                        var chatRow = badge.closest('[data-testid="cell-frame-container"]') ||
                                      badge.closest('[role="listitem"]') ||
                                      item;
                        
                        // Get name from the same container as the badge
                        var nameEl = chatRow.querySelector('span[title]');
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
                if (!pane) return JSON.stringify({ clicked: false, reason: 'no_pane' });
                // Use the SAME selector as GetUnreadChats for index consistency.
                // Click the cell-frame-container directly — NOT a child badge/element
                // that might open a drawer or context menu instead of the conversation.
                var items = pane.querySelectorAll('[data-testid="cell-frame-container"]');
                if (items.length === 0) items = pane.querySelectorAll('[role="listitem"]');
                if (items.length === 0) items = pane.querySelectorAll('div[data-id]');
                if (items[__INDEX__]) {
                    // Click the row container itself, not a nested element
                    items[__INDEX__].click();
                    return JSON.stringify({ clicked: true, selector: items.length > 0 ? 'cell-frame-container' : 'listitem' });
                }
                return JSON.stringify({ clicked: false, reason: 'no_item_at_index', itemCount: items.length });
            })();
            """;

        /// <summary>
        /// Find the first unread badge, resolve the row from that badge via
        /// badge.closest('[data-testid="cell-frame-container"]'), derive the chat
        /// name from that exact row, and click it — all in the same DOM snapshot.
        ///
        /// This guarantees the same DOM node used to derive MATCHED_CHAT_NAME is
        /// the exact node whose row is clicked. No re-finding by index or text.
        /// </summary>
        public const string ClickNextUnreadChat = """
            (() => {
                const pane = document.querySelector('#pane-side');
                if (!pane) return JSON.stringify({ clicked: false, reason: 'no_pane', name: '', clickTargetHtml: '', clickTargetIndex: -1, unreadCount: 0 });

                var items = pane.querySelectorAll('[data-testid="cell-frame-container"]');
                if (items.length === 0) items = pane.querySelectorAll('[role="listitem"]');
                if (items.length === 0) items = pane.querySelectorAll('div[data-id]');

                for (var idx = 0; idx < items.length; idx++) {
                    var item = items[idx];
                    var badge = null;
                    var unreadCount = 0;

                    // Badge detection — same logic as GetUnreadChats
                    badge = item.querySelector('span[aria-label*="unread" i]');
                    if (!badge) badge = item.querySelector('div[aria-label*="unread" i]');
                    if (!badge) badge = item.querySelector('span[aria-label*="הודעות"]');
                    if (!badge) badge = item.querySelector('[data-testid*="unread" i]');

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
                        // Resolve the row from the badge — the EXACT row that has the unread marker
                        var row = badge.closest('[data-testid="cell-frame-container"]') ||
                                  badge.closest('[role="listitem"]') ||
                                  item;

                        // Derive name from that exact row
                        var nameEl = row.querySelector('span[title]');
                        var name = nameEl ? (nameEl.getAttribute('title') || '') : '';

                        if (!name) {
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

                        if (name) {
                            if (unreadCount === 0) {
                                var badgeText = (badge.textContent || '').trim();
                                unreadCount = parseInt(badgeText) || 1;
                            }

                            // Scroll the row into view — WhatsApp virtualizes the chat
                            // list, so the row must be rendered before clicking.
                            try { row.scrollIntoView({block: 'center'}); } catch(e) {}

                            // Click that exact live row — the same DOM node from which
                            // the name was derived. No re-query by index or selector.
                            row.click();

                            return JSON.stringify({
                                clicked: true,
                                name: name,
                                clickTargetHtml: (row.outerHTML || '').substring(0, 300),
                                clickTargetIndex: idx,
                                unreadCount: unreadCount,
                                atomicClickTargetName: name,
                                atomicClickConnected: true,
                                atomicClickUnreadPresent: true
                            });
                        }
                    }
                }

                return JSON.stringify({ clicked: false, reason: 'no_unread', name: '', clickTargetHtml: '', clickTargetIndex: -1, unreadCount: 0 });
            })();
            """;

        public const string GetCustomerInfo = """
            (() => {
                // === #main diagnostics ===
                var main = document.querySelector('#main');
                var mainFound = !!main;
                var mainHtml = main ? (main.outerHTML || '').substring(0, 2500) : '';
                var mainHeaders = main ? main.querySelectorAll('header') : [];
                var mainHeadersFound = mainHeaders.length;

                // === Find the CONVERSATION header — explicitly NOT chatlist-header ===
                // Try multiple selectors inside #main, reject any chatlist-header
                var header = null;
                if (main) {
                    // Selector 1: header inside #main that is NOT chatlist-header
                    var headersInMain = main.querySelectorAll('header');
                    for (var h = 0; h < headersInMain.length; h++) {
                        var testId = headersInMain[h].getAttribute('data-testid') || '';
                        if (testId !== 'chatlist-header') {
                            header = headersInMain[h];
                            break;
                        }
                    }
                    // Selector 2: conversation header by data-testid
                    if (!header) {
                        header = main.querySelector('header[data-testid="conversation-panel-header"]')
                            || main.querySelector('header[data-testid="conversation-header"]')
                            || main.querySelector('header:not([data-testid="chatlist-header"])');
                    }
                }
                // Selector 3: fallback — any header in document that is NOT chatlist-header
                if (!header) {
                    var allHeaders = document.querySelectorAll('header');
                    for (var h2 = 0; h2 < allHeaders.length; h2++) {
                        var tid = allHeaders[h2].getAttribute('data-testid') || '';
                        if (tid !== 'chatlist-header') {
                            header = allHeaders[h2];
                            break;
                        }
                    }
                }

                var headerFound = !!header;
                var headerHtml = header ? (header.outerHTML || '').substring(0, 500) : '';
                var headerTestId = header ? (header.getAttribute('data-testid') || '') : '';

                if (!header) {
                    return JSON.stringify({
                        name: '', phone: '',
                        mainFound: mainFound, mainHtml: mainHtml, mainHeadersFound: mainHeadersFound,
                        headerFound: false, headerHtml: '', headerTestId: '',
                        spanTitles: [], ariaLabels: [], textCandidates: [], nameSource: '',
                        mainSpanTitles: [], mainAriaLabels: []
                    });
                }

                // Collect span[title] from the conversation header
                var titleSpans = header.querySelectorAll('span[title]');
                var spanTitles = [];
                for (var i = 0; i < titleSpans.length && i < 10; i++) {
                    spanTitles.push(titleSpans[i].getAttribute('title') || '');
                }

                // Collect aria-labels from the conversation header
                var ariaElements = header.querySelectorAll('[aria-label]');
                var ariaLabels = [];
                for (var j = 0; j < ariaElements.length && j < 10; j++) {
                    var label = ariaElements[j].getAttribute('aria-label') || '';
                    if (label) ariaLabels.push(label);
                }

                // Collect text candidates from the conversation header.
                // ONLY span[dir="auto"] — these hold the contact name.
                // NOT div[role="button"] — those are action buttons (profile, call, video)
                // whose textContent is UI labels like "פרטי הפרופיל", "שיחה קולית".
                var textCandidates = [];
                var textEls = header.querySelectorAll('span[dir="auto"]');
                for (var k = 0; k < textEls.length && k < 15; k++) {
                    var text = (textEls[k].textContent || '').trim();
                    if (text && text.length > 0 && text.length < 100) {
                        textCandidates.push(text);
                    }
                }

                // Also collect span[title] and aria-labels from ALL of #main (fallback)
                var mainSpanTitles = [];
                var mainAriaLabels = [];
                if (main) {
                    var mTitles = main.querySelectorAll('span[title]');
                    for (var mt = 0; mt < mTitles.length && mt < 10; mt++) {
                        mainSpanTitles.push(mTitles[mt].getAttribute('title') || '');
                    }
                    var mArias = main.querySelectorAll('[aria-label]');
                    for (var ma = 0; ma < mArias.length && ma < 10; ma++) {
                        var ml = mArias[ma].getAttribute('aria-label') || '';
                        if (ml) mainAriaLabels.push(ml);
                    }
                }

                var name = '';
                var nameSource = '';

                // UI labels to reject as contact names (Hebrew + English)
                // Includes: navigation tabs, action buttons, profile/info labels, call/video buttons
                var uiPattern = /^(Back|Menu|Search|Call|Video|Info|Send|Attach|Emoji|Mute|Pin|Archive|Delete|Settings|online|typing|פרטי הפרופיל|פרטים|צ'אטים|צ׳אטים|שיחות|סטטוס|ערוצים|קהילות|מדיה|את\/ה|את\\ה|את\/אתה|WhatsApp|חיפוש|תפריט|שיחה קולית|שיחת וידאו|הודעה|סמן כלא נקרא|הגדרות|יציאה|חזרה|פתח|סגור|בטל|אישור|ערוך|מחק|העתק|שתף|הורד|קדימה|אחורה)/i;

                // Strategy 1: text candidate from span[dir="auto"] — FIRST priority.
                // This is the most reliable source: the contact name is always in a
                // span[dir="auto"] inside the conversation header.
                for (var c = 0; c < textCandidates.length; c++) {
                    var candidate = textCandidates[c];
                    if (candidate && !uiPattern.test(candidate)) {
                        name = candidate;
                        nameSource = 'text_candidate';
                        break;
                    }
                }

                // Strategy 2: span[title] — second priority
                if (!name) {
                    for (var t = 0; t < titleSpans.length; t++) {
                        var title = titleSpans[t].getAttribute('title') || '';
                        if (title && title.length > 0 && !uiPattern.test(title)) {
                            name = title;
                            nameSource = 'span_title';
                            break;
                        }
                    }
                }

                // Strategy 3: aria-label that's not a UI button/tab (LAST resort — almost never correct)
                if (!name) {
                    for (var a = 0; a < ariaElements.length; a++) {
                        var label = ariaElements[a].getAttribute('aria-label') || '';
                        if (label && !uiPattern.test(label) && !label.match(/^(Back|Menu|Search|Call|Video|Info|Send|Attach|Emoji|Mute|Pin|Archive|Delete|Settings)/i)) {
                            name = label;
                            nameSource = 'aria_label';
                            break;
                        }
                    }
                }

                // Strategy 4: span[title] from ALL of #main (broader fallback)
                if (!name && main) {
                    var mTitles2 = main.querySelectorAll('span[title]');
                    for (var mt2 = 0; mt2 < mTitles2.length && mt2 < 15; mt2++) {
                        var mTitle = mTitles2[mt2].getAttribute('title') || '';
                        if (mTitle && mTitle.length > 0 && !uiPattern.test(mTitle)) {
                            name = mTitle;
                            nameSource = 'main_span_title';
                            break;
                        }
                    }
                }

                // === Phone / JID diagnostics ===
                // WhatsApp Web exposes the contact JID (phone@c.us) in data-id attributes
                // on message containers and conversation elements inside #main.
                var dataIds = [];
                var phoneCandidates = [];
                if (main) {
                    var idEls = main.querySelectorAll('[data-id]');
                    for (var d = 0; d < idEls.length && d < 30; d++) {
                        var did = idEls[d].getAttribute('data-id') || '';
                        if (did) dataIds.push(did);
                    }
                }
                if (header) {
                    var hId = header.getAttribute('data-id') || '';
                    if (hId) dataIds.unshift('HEADER:' + hId);
                }
                // Extract phone numbers from JIDs: format "phone@c.us" or "true_phone@..."
                for (var di = 0; di < dataIds.length; di++) {
                    var raw = dataIds[di].replace(/^HEADER:/, '');
                    var atIdx = raw.indexOf('@');
                    if (atIdx > 0) {
                        var localPart = raw.substring(0, atIdx);
                        var domain = raw.substring(atIdx + 1);
                        if (domain === 'c.us' || domain === 's.whatsapp.net') {
                            if (localPart.indexOf('true_') === 0) localPart = localPart.substring(5);
                            var digitsOnly = localPart.replace(/\D/g, '');
                            if (digitsOnly.length >= 7) {
                                phoneCandidates.push(digitsOnly);
                            }
                        }
                    }
                }

                // Phone extraction — prefer JID, fall back to header text
                var phone = '';
                var phoneSource = '';
                if (phoneCandidates.length > 0) {
                    phone = phoneCandidates[0];
                    phoneSource = 'jid_data_id';
                }
                if (!phone) {
                    var spans = header.querySelectorAll('span[dir="auto"]');
                    for (var s = 0; s < spans.length; s++) {
                        var text = spans[s].textContent || '';
                        var match = text.match(/[\+]?\d[\d\s\-()]{7,}/);
                        if (match) { phone = match[0].replace(/[\s\-()]/g, ''); phoneSource = 'header_text'; break; }
                    }
                }

                return JSON.stringify({
                    name: name,
                    phone: phone,
                    phoneSource: phoneSource,
                    dataIds: dataIds,
                    phoneCandidates: phoneCandidates,
                    mainFound: mainFound,
                    mainHtml: mainHtml,
                    mainHeadersFound: mainHeadersFound,
                    headerFound: headerFound,
                    headerHtml: headerHtml,
                    headerTestId: headerTestId,
                    spanTitles: spanTitles,
                    ariaLabels: ariaLabels,
                    textCandidates: textCandidates,
                    nameSource: nameSource,
                    mainSpanTitles: mainSpanTitles,
                    mainAriaLabels: mainAriaLabels
                });
            })();
            """;

        public const string GetContactPhone = """
            (async () => {
                var phoneAttrCandidates = [];
                var phoneTextCandidates = [];
                var phoneJidCandidates = [];
                var phone = '';
                var phoneSource = '';
                var openedContactPanel = false;
                var activeName = '';

                function extractPhone(s) {
                    if (!s) return '';
                    var patterns = [/\+?972[\d\s\-()]{7,15}/, /0?5[\d][\d\s\-()]{6,12}/, /\+[\d][\d\s\-()]{6,14}/];
                    for (var p = 0; p < patterns.length; p++) {
                        var m = s.match(patterns[p]);
                        if (m) {
                            var digits = m[0].replace(/\D/g, '');
                            if (digits.length >= 7) return digits;
                        }
                    }
                    return '';
                }

                function extractPhoneFromJid(jid) {
                    if (!jid) return '';
                    var atIdx = jid.indexOf('@');
                    if (atIdx <= 0) return '';
                    var local = jid.substring(0, atIdx);
                    var domain = jid.substring(atIdx + 1);
                    if (domain === 'c.us' || domain === 's.whatsapp.net') {
                        if (local.indexOf('true_') === 0) local = local.substring(5);
                        var digits = local.replace(/\D/g, '');
                        return digits.length >= 7 ? digits : '';
                    }
                    return '';
                }

                var main = document.querySelector('#main');
                var header = main ? main.querySelector('header') : null;

                // Get active chat name from header (to match chat list row)
                if (header) {
                    var nameSpans = header.querySelectorAll('span[dir="auto"]');
                    for (var ns = 0; ns < nameSpans.length; ns++) {
                        var t = (nameSpans[ns].textContent || '').trim();
                        if (t && t.length > 0 && t.length < 100) { activeName = t; break; }
                    }
                }

                // 1. Scan #pane-side (chat list) for data-id — chat JIDs (phone@c.us)
                var pane = document.querySelector('#pane-side');
                if (pane) {
                    var allDataId = pane.querySelectorAll('[data-id]');
                    for (var i = 0; i < allDataId.length && i < 50; i++) {
                        var did = allDataId[i].getAttribute('data-id') || '';
                        if (!did) continue;
                        phoneAttrCandidates.push('pane:' + did);
                        var p = extractPhoneFromJid(did);
                        if (p && phoneJidCandidates.indexOf(p) < 0) phoneJidCandidates.push(p);
                    }
                }

                // 2. Scan #main for data-id, data-lid, data-user, data-jid
                if (main) {
                    var attrs = ['data-id', 'data-lid', 'data-user', 'data-jid'];
                    for (var a = 0; a < attrs.length; a++) {
                        var els = main.querySelectorAll('[' + attrs[a] + ']');
                        for (var j = 0; j < els.length && j < 20; j++) {
                            var val = els[j].getAttribute(attrs[a]) || '';
                            if (!val) continue;
                            phoneAttrCandidates.push(attrs[a] + ':' + val);
                            var p2 = extractPhoneFromJid(val);
                            if (p2 && phoneJidCandidates.indexOf(p2) < 0) phoneJidCandidates.push(p2);
                        }
                    }
                }

                // 3. Scan visible text in #main for phone patterns (+972, 05x)
                if (main) {
                    var allText = main.innerText || '';
                    var lines = allText.split('\n');
                    for (var t = 0; t < lines.length && t < 100; t++) {
                        var line = lines[t].trim();
                        if (line.length === 0 || line.length > 30) continue;
                        var tp = extractPhone(line);
                        if (tp) phoneTextCandidates.push(line + ' -> ' + tp);
                    }
                }

                // 4. Scan aria-label and title in #main for phone patterns
                if (main) {
                    var labeled = main.querySelectorAll('[aria-label], [title]');
                    for (var l = 0; l < labeled.length && l < 50; l++) {
                        var al = labeled[l].getAttribute('aria-label') || '';
                        var ti = labeled[l].getAttribute('title') || '';
                        var lp = extractPhone(al) || extractPhone(ti);
                        if (lp) phoneTextCandidates.push('label:' + (al || ti) + ' -> ' + lp);
                    }
                }

                // 5. Scan tel: links
                var telLinks = document.querySelectorAll('a[href^="tel:"]');
                for (var h = 0; h < telLinks.length; h++) {
                    var hp = extractPhone(telLinks[h].getAttribute('href') || '');
                    if (hp) phoneTextCandidates.push('tel:' + hp);
                }

                // 6. Last resort: open contact-info panel, read phone, close it
                if (phoneJidCandidates.length === 0 && phoneTextCandidates.length === 0 && header) {
                    try {
                        var contactBtn = header.querySelector('div[role="button"]') || header;
                        contactBtn.click();
                        openedContactPanel = true;
                        await new Promise(function(r) { setTimeout(r, 2000); });

                        // Scan the whole document for phone text (panel is a new overlay)
                        var allSpans = document.querySelectorAll('span[dir="auto"], span[title], [aria-label]');
                        for (var sp = 0; sp < allSpans.length && sp < 300; sp++) {
                            var spText = ((allSpans[sp].textContent || '').trim()) || (allSpans[sp].getAttribute('title') || '') || (allSpans[sp].getAttribute('aria-label') || '');
                            var spp = extractPhone(spText);
                            if (spp) {
                                var cand = 'panel:' + spText + ' -> ' + spp;
                                if (phoneTextCandidates.indexOf(cand) < 0) phoneTextCandidates.push(cand);
                            }
                        }

                        // Close the panel — Escape, then close button
                        document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, which: 27, bubbles: true, cancelable: true }));
                        await new Promise(function(r) { setTimeout(r, 500); });
                        var closeBtn = document.querySelector('button[aria-label="Close"], button[aria-label*="סגור" i], [data-testid="close"]');
                        if (closeBtn) closeBtn.click();
                        await new Promise(function(r) { setTimeout(r, 400); });
                    } catch(e) {
                        phoneAttrCandidates.push('panel_error:' + (e.message || ''));
                    }
                }

                // Pick best phone — prefer JID, then text
                if (phoneJidCandidates.length > 0) {
                    phone = phoneJidCandidates[0];
                    phoneSource = 'jid';
                } else if (phoneTextCandidates.length > 0) {
                    var last = phoneTextCandidates[phoneTextCandidates.length - 1];
                    var arrowIdx = last.lastIndexOf('->');
                    phone = arrowIdx >= 0 ? last.substring(arrowIdx + 1).trim() : extractPhone(last);
                    phoneSource = openedContactPanel ? 'contact_panel' : 'text';
                }

                return JSON.stringify({
                    phone: phone,
                    phoneSource: phoneSource,
                    phoneAttrCandidates: phoneAttrCandidates,
                    phoneTextCandidates: phoneTextCandidates,
                    phoneJidCandidates: phoneJidCandidates,
                    openedContactPanel: openedContactPanel,
                    activeName: activeName
                });
            })();
            """;

        public const string DetectImages = """
            (() => {
                const main = document.querySelector('#main');
                if (!main) return JSON.stringify({ images: [], candidates: [], mainFound: false, totalImgs: 0, filteredSrc: 0, filteredSize: 0, filteredPlaceholder: 0, filteredDup: 0, filteredPreview: 0, messageGroups: 0 });
                const imgs = main.querySelectorAll('img');
                const totalImgs = imgs.length;
                const seen = new Set();
                const allEntries = [];
                const candidates = [];
                const messageGroups = new Map();
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

                    // Classify source type
                    let sourceType = 'HTTP';
                    if (src.startsWith('blob:')) sourceType = 'BLOB';
                    else if (src.startsWith('data:')) sourceType = 'DATA';

                    // Estimate bytes for DATA URLs (base64 payload)
                    let estBytes = 0;
                    if (sourceType === 'DATA') {
                        const commaIdx = src.indexOf(',');
                        if (commaIdx > 0) {
                            const b64 = src.substring(commaIdx + 1);
                            const padding = (b64.endsWith('==') ? 2 : (b64.endsWith('=') ? 1 : 0));
                            estBytes = Math.floor((b64.length * 3) / 4) - padding;
                        }
                    }

                    // Classify ORIGINAL / PREVIEW / UNKNOWN
                    // DATA URLs < 30KB are thumbnails/avatars/previews (not customer originals).
                    // BLOB/HTTP are full-resolution media responses captured by CDP.
                    let classification = 'UNKNOWN';
                    if (sourceType === 'DATA') {
                        classification = (estBytes > 0 && estBytes < 30720) ? 'PREVIEW' : 'UNKNOWN';
                    } else {
                        classification = 'ORIGINAL';
                    }

                    const w = img.naturalWidth || img.width || 0;
                    const h = img.naturalHeight || img.height || 0;
                    if (w > 0 && h > 0 && w <= 50 && h <= 50 && sourceType !== 'DATA') { filteredSize++; continue; }

                    // Deduplicate by src — same image appears multiple times in DOM
                    if (seen.has(src)) { filteredDup++; continue; }
                    seen.add(src);

                    // Find message container for message-level correlation
                    let msgEl = img.closest('[data-id]') || img.closest('[data-testid="msg-bubble"]') || null;
                    let msgId = msgEl ? (msgEl.getAttribute('data-id') || msgEl.getAttribute('data-testid') || '') : '';
                    if (!msgId) msgId = 'nomsg_' + allEntries.length;

                    const entry = { src: src, source: sourceType, bytes: estBytes, classification: classification, width: w, height: h, messageId: msgId };
                    allEntries.push(entry);
                    candidates.push({ source: sourceType, classification: classification, bytes: estBytes, messageId: msgId });
                    if (!messageGroups.has(msgId)) messageGroups.set(msgId, []);
                    messageGroups.get(msgId).push(entry);
                }

                // Message-level correlation:
                // - Drop PREVIEW (small DATA thumbnails) — never customer originals.
                // - If an ORIGINAL (BLOB/HTTP) exists in the same message, also drop
                //   UNKNOWN (large DATA) entries for that message — the blob is the real photo.
                const images = [];
                var filteredPreview = 0;
                for (const [msgId, group] of messageGroups) {
                    const hasOriginal = group.some(e => e.classification === 'ORIGINAL');
                    for (const e of group) {
                        if (e.classification === 'PREVIEW') { filteredPreview++; continue; }
                        if (e.classification === 'UNKNOWN' && hasOriginal) { filteredPreview++; continue; }
                        images.push(e);
                    }
                }

                return JSON.stringify({ images: images, candidates: candidates, mainFound: true, totalImgs: totalImgs, filteredSrc: filteredSrc, filteredSize: filteredSize, filteredPlaceholder: filteredPlaceholder, filteredDup: filteredDup, filteredPreview: filteredPreview, messageGroups: messageGroups.size });
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