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

    // Runtime src dedup — prevents re-downloading the same blob/data URL across scans
    // within a session (SHA-256 DB dedup handles cross-session; this avoids redundant fetches).
    private readonly HashSet<string> _processedSrcs = new();

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

        // === ATOMIC DETECTION + CLICK ===
        // Previously GetUnreadChats + ClickNextUnreadChat were two separate script
        // calls. WhatsApp mutated the DOM between them, causing the unread badge
        // to disappear before the click (UNREAD found then lost in same scan).
        // Now FindAndClickUnreadChat does detection + click + verify atomically
        // in a single script execution — the same DOM snapshot that finds the
        // unread badge also clicks its row.
        // === Store-based unread detection (primary) ===
        // Uses WhatsApp's internal JS store via window.require('WAWebCollections').Chat
        // to get unreadCount directly — far more reliable than DOM badge scraping.
        // Falls back to DOM-based FindAndClickUnreadChat if the store is unavailable.
        var node = await ExecuteScriptJsonAsync(Scripts.FindAndClickUnreadViaStore);
        var storeSource = node?["source"]?.GetValue<string>() ?? "";
        var storeClicked = node?["clicked"]?.GetValue<bool>() ?? false;
        var storeChatCount = node?["storeChatCount"]?.GetValue<int>() ?? 0;
        var storeUnreadTotal = node?["storeUnreadTotal"]?.GetValue<int>() ?? 0;

        _log.Write("UNREAD_DETECTION_SOURCE", storeSource);
        _log.Write("UNREAD_DETECTION_CLICKED", storeClicked.ToString().ToLowerInvariant());
        if (storeChatCount > 0)
            _log.Write("STORE_CHAT_COUNT", storeChatCount.ToString());

        // Log each unread chat found in the store
        var storeUnreadChats = node?["storeUnreadChats"]?.AsArray();
        if (storeUnreadChats != null)
        {
            foreach (var uc in storeUnreadChats)
            {
                var ucId = uc?["id"]?.GetValue<string>() ?? "";
                var ucName = uc?["name"]?.GetValue<string>() ?? "";
                var ucCount = uc?["unreadCount"]?.GetValue<int>() ?? 0;
                _log.Write("STORE_UNREAD_CHAT", $"id={ucId} name={ucName} unreadCount={ucCount}");
            }
        }
        _log.Write("STORE_UNREAD_TOTAL", storeUnreadTotal.ToString());

        if (!storeClicked)
        {
            _log.Write("UNREAD_DETECTION_FALLBACK", $"{storeSource} -> dom");
            node = await ExecuteScriptJsonAsync(Scripts.FindAndClickUnreadChat);
        }
        var chatRowsFound = node?["chatRowsFound"]?.GetValue<int>() ?? 0;
        var unreadMarkersFound = node?["unreadMarkersFound"]?.GetValue<int>() ?? 0;
        var clicked = node?["clicked"]?.GetValue<bool>() ?? false;
        var name = node?["name"]?.GetValue<string>() ?? "";
        var clickTargetHtml = node?["clickTargetHtml"]?.GetValue<string>() ?? "";
        var clickTargetIndex = node?["clickTargetIndex"]?.GetValue<int>() ?? -1;
        var chatUnreadCount = node?["unreadCount"]?.GetValue<int>() ?? 0;
        var atomicClickTargetName = node?["atomicClickTargetName"]?.GetValue<string>() ?? "";
        var atomicClickConnected = node?["atomicClickConnected"]?.GetValue<bool>() ?? false;
        var atomicClickUnreadPresent = node?["atomicClickUnreadPresent"]?.GetValue<bool>() ?? false;
        var activeChatBefore = node?["activeChatBefore"]?.GetValue<string>() ?? "";
        var activeChatAfter = node?["activeChatAfter"]?.GetValue<string>() ?? "";
        var navigationConfirmed = node?["navigationConfirmed"]?.GetValue<bool>() ?? false;
        var clickStrategy = node?["clickStrategy"]?.GetValue<string>() ?? "";
        var clickElementTag = node?["clickElementTag"]?.GetValue<string>() ?? "";
        var clickElementRole = node?["clickElementRole"]?.GetValue<string>() ?? "";
        var clickElementTabindex = node?["clickElementTabindex"]?.GetValue<string>() ?? "";
        var unreadHandoffName = node?["unreadHandoffName"]?.GetValue<string>() ?? "";
        var unreadHandoffRowConnected = node?["unreadHandoffRowConnected"]?.GetValue<bool>() ?? false;
        var unreadHandoffBadgeStillPresent = node?["unreadHandoffBadgeStillPresent"]?.GetValue<bool>() ?? false;
        var clickAttempted = node?["clickAttempted"]?.GetValue<bool>() ?? false;

        // Diagnostic logging
        _log.Write("CHAT_ROWS_FOUND", chatRowsFound.ToString());
        _log.Write("UNREAD_MARKERS_FOUND", unreadMarkersFound.ToString());
        _log.Write("UNREAD_CHAT_MATCHES", unreadMarkersFound.ToString());

        if (chatRowsFound == 0)
        {
            var reason = node?["reason"]?.GetValue<string>() ?? "null_response";
            _log.Write("SCAN_NO_ROWS", $"reason={reason}");
        }

        // Row HTML diagnostics — dump first 3 chat rows to see actual badge structure
        if (chatRowsFound > 0 && unreadMarkersFound == 0)
        {
            var rowHtml1 = node?["rowHtml1"]?.GetValue<string>() ?? "";
            var rowHtml2 = node?["rowHtml2"]?.GetValue<string>() ?? "";
            var rowHtml3 = node?["rowHtml3"]?.GetValue<string>() ?? "";
            _log.Write("ROW_HTML_1", rowHtml1);
            _log.Write("ROW_HTML_2", rowHtml2);
            _log.Write("ROW_HTML_3", rowHtml3);
            var rowWithNumberHtml = node?["rowWithNumberHtml"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrEmpty(rowWithNumberHtml))
                _log.Write("ROW_WITH_NUMBER_HTML", rowWithNumberHtml);

            // UNREAD_DIAGNOSTIC — detailed element info for first 5 rows
            var unreadDiag = node?["unreadDiagnostic"]?.AsArray();
            if (unreadDiag != null)
            {
                foreach (var diag in unreadDiag)
                {
                    var dIdx = diag?["index"]?.GetValue<int>() ?? 0;
                    var dRowClass = diag?["rowClass"]?.GetValue<string>() ?? "";
                    var dRowTestId = diag?["rowTestId"]?.GetValue<string>() ?? "";
                    var dRowDataId = diag?["rowDataId"]?.GetValue<string>() ?? "";
                    var dRowAria = diag?["rowAriaLabel"]?.GetValue<string>() ?? "";
                    _log.Write("UNREAD_DIAGNOSTIC_ROW", $"index={dIdx} class={dRowClass} testId={dRowTestId} dataId={dRowDataId} aria={dRowAria}");
                    var dElements = diag?["elements"]?.AsArray();
                    if (dElements != null)
                    {
                        foreach (var el in dElements)
                        {
                            var elTag = el?["tag"]?.GetValue<string>() ?? "";
                            var elCls = el?["cls"]?.GetValue<string>() ?? "";
                            var elTestId = el?["testId"]?.GetValue<string>() ?? "";
                            var elAria = el?["ariaLabel"]?.GetValue<string>() ?? "";
                            var elTitle = el?["title"]?.GetValue<string>() ?? "";
                            var elText = el?["text"]?.GetValue<string>() ?? "";
                            var elW = el?["w"]?.GetValue<int>() ?? 0;
                            var elH = el?["h"]?.GetValue<int>() ?? 0;
                            var elBg = el?["bg"]?.GetValue<string>() ?? "";
                            var elFw = el?["fw"]?.GetValue<string>() ?? "";
                            var elColor = el?["color"]?.GetValue<string>() ?? "";
                            _log.Write("UNREAD_DIAGNOSTIC_EL", $"tag={elTag} cls={elCls} testId={elTestId} aria={elAria} title={elTitle} text={elText} w={elW} h={elH} bg={elBg} fw={elFw} color={elColor}");
                        }
                    }
                }
            }
            }

        // Marker ancestry diagnostics
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

        UnreadChatsChanged?.Invoke(unreadMarkersFound);

        if (unreadMarkersFound == 0)
        {
            ScannerStatusChanged?.Invoke($"Idle — {chatRowsFound} chats, 0 markers");
            UpdateStatus("Idle — no unread chats");
            return;
        }

        _log.Write("SCAN_START", $"unread_chats={unreadMarkersFound}");
        ScannerStatusChanged?.Invoke($"Scanning {unreadMarkersFound} chats");
        var totalSaved = 0;
        var totalDuplicates = 0;

        // Handoff diagnostics — proves the row survived between detection and click
        _log.Write("UNREAD_HANDOFF_NAME", unreadHandoffName);
        _log.Write("UNREAD_HANDOFF_ROW_CONNECTED", unreadHandoffRowConnected.ToString().ToLowerInvariant());
        _log.Write("UNREAD_HANDOFF_BADGE_STILL_PRESENT", unreadHandoffBadgeStillPresent.ToString().ToLowerInvariant());
        _log.Write("CLICK_ATTEMPTED", clickAttempted.ToString().ToLowerInvariant());

        if (!clicked || string.IsNullOrWhiteSpace(name))
        {
            _log.Write("NO_MORE_UNREAD", "reason=unread_lost_between_detection_and_click");
            goto scan_complete;
        }

        // === NAVIGATION VERIFICATION (separate sync script) ===
        // The atomic click script is synchronous (no Promise/await) because WebView2
        // ExecuteScriptAsync does not reliably resolve async scripts. Navigation
        // is verified here after a delay, using a separate synchronous script.
        await Task.Delay(2000);
        var navNode = await ExecuteScriptJsonAsync(Scripts.VerifyNavigation);
        activeChatAfter = navNode?["activeChatName"]?.GetValue<string>() ?? "";
        // Fuzzy match — header name may be truncated or differ slightly from chat list title.
        // Accept if one contains the other (handles truncation + minor formatting differences).
        navigationConfirmed = !string.IsNullOrWhiteSpace(activeChatAfter) &&
            (string.Equals(activeChatAfter, name, StringComparison.OrdinalIgnoreCase) ||
             (name.Length > 3 && activeChatAfter.Contains(name)) ||
             (activeChatAfter.Length > 3 && name.Contains(activeChatAfter)));

        // === CHAT SELECTION VERIFICATION ===
        _log.Write("MATCHED_CHAT_NAME", name);
        _log.Write("ATOMIC_CLICK_TARGET_NAME", atomicClickTargetName);
        _log.Write("ATOMIC_CLICK_CONNECTED", atomicClickConnected.ToString().ToLowerInvariant());
        _log.Write("ATOMIC_CLICK_UNREAD_PRESENT", atomicClickUnreadPresent.ToString().ToLowerInvariant());
        _log.Write("CLICK_STRATEGY", clickStrategy);
        _log.Write("CLICK_ELEMENT_TAG", clickElementTag);
        _log.Write("CLICK_ELEMENT_ROLE", clickElementRole);
        _log.Write("CLICK_ELEMENT_TABINDEX", clickElementTabindex);
        _log.Write("ACTIVE_CHAT_BEFORE", activeChatBefore);
        _log.Write("ACTIVE_CHAT_AFTER", activeChatAfter);
        _log.Write("NAVIGATION_CONFIRMED", navigationConfirmed.ToString().ToLowerInvariant());
        if (navigationConfirmed && storeClicked)
        {
            _log.Write("UNREAD_DETECTION_SUCCESS", $"source=store name={name} unread={chatUnreadCount}");
        }
        _log.Write("CHAT_CLICKED", $"name={name}");
        CurrentChatChanged?.Invoke(name);
        UpdateStatus($"Opening: {name}");

        // If navigation not confirmed — do not process media
        if (!navigationConfirmed)
        {
            _log.Write("CHAT_OPEN_FAILED", $"target={name} before={activeChatBefore} after={activeChatAfter}");
            CurrentChatChanged?.Invoke($"{name} — open failed (stayed on {activeChatAfter})");
            UpdateStatus($"Open failed: target={name} active={activeChatAfter}");
            goto scan_complete;
        }

        // Navigation confirmed — #main now shows the correct chat.
        // GetCustomerInfo reads phone/name diagnostics from the verified chat.
        var infoNode = await ExecuteScriptJsonAsync(Scripts.GetCustomerInfo);
        var activeChatName = activeChatAfter;
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

            // 5. Compare target vs active — FUZZY match (contains either direction).
            // The header name (span[dir="auto"]) may be truncated or formatted
            // differently from the chat list name (span[title]). Exact match fails
            // for short names like "Rod" when the header shows "Rod Smith".
            // Contains check handles truncation + minor formatting differences.
            var chatMatch = !string.IsNullOrWhiteSpace(activeChatName) &&
                (string.Equals(activeChatName, name, StringComparison.OrdinalIgnoreCase) ||
                 (name.Length > 2 && activeChatName.Contains(name)) ||
                 (activeChatName.Length > 2 && name.Contains(activeChatName)));
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

            // === Scroll chat to trigger image lazy loading ===
            // WhatsApp Web lazy-loads images — only images scrolled into view get
            // their blob: URLs loaded. Without scrolling, DetectImages only finds
            // placeholder GIFs and small DATA URL previews. Scroll to bottom, wait
            // for images to load, then scroll back to top.
            await ExecuteScriptJsonAsync(Scripts.ScrollChat);
            await Task.Delay(3000);
            await ExecuteScriptJsonAsync(Scripts.ScrollChatTop);
            await Task.Delay(2000);

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
            // Compute folder base fresh per scan using DateTime.Now — scopes to current hour.
            // Previously used DB base which returned OLD hour folders, preventing new hourly
            // folders from being created. FindExistingFolder still reuses same-hour folders
            // across scans because the computed base path matches for the same hour.
            var orderFolderBase = CustomerFolderService.GetOrderFolderBase(_ordersRoot, customerName, phone);

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

                // Runtime dedup — skip src already processed in this session
                if (_processedSrcs.Contains(src))
                {
                    duplicates++;
                    _log.Write("DUPLICATE_CHECK", $"result=DUPLICATE_SRC src={srcShort}");
                    continue;
                }

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

                // Reject small files (< 15KB) — these are thumbnails/avatars/stickers,
                // not real customer photos. A typical WhatsApp photo is > 30KB.
                if (imageBytes.Length < 15360)
                {
                    _log.Write("MEDIA_SKIPPED", $"reason=too_small bytes={imageBytes.Length} threshold=15360 src={srcShort}");
                    continue;
                }

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
                        if (existing != null)
                        {
                            folderPath = existing;
                        }
                        else
                        {
                            // No existing folder in this hour — create one and lock the base
                            folderPath = CustomerFolderService.CreateOrderFolder(_ordersRoot, customerName, phone);
                            orderFolderBase = CustomerFolderService.GetBasePathFromFolder(folderPath);
                        }
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
                    _processedSrcs.Add(src);
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

            // Source type 2 & 3: blob: or http(s):
            // BLOB URLs are client-side objects with NO network response — CDP
            // Network.getResponseBody cannot retrieve them. Use JavaScript fetch()
            // instead, which resolves blob: URLs from in-memory data.
            // HTTP URLs: try CDP first (captures original network response), then
            // fall back to JavaScript fetch() if no matching response was captured.
            _log.Write("MEDIA_SOURCE_TYPE", url.StartsWith("blob:") ? "BLOB_URL" : "HTTP_URL");

            // For blob: URLs — use JavaScript fetch directly (CDP can't handle them)
            if (url.StartsWith("blob:"))
            {
                return await DownloadViaJsFetchAsync(url);
            }

            // For http(s): URLs — try CDP first, then JS fetch fallback
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

            if (info != null)
            {
                _log.Write("NETWORK_REQUEST_MATCHED", $"requestId={info.RequestId} url={(url.Length > 80 ? url[..80] : url)}");

                try
                {
                    var paramsJson = JsonSerializer.Serialize(new { requestId = info.RequestId });
                    var resultJson = await _webView.CallDevToolsProtocolMethodAsync("Network.getResponseBody", paramsJson);

                    using var resultDoc = JsonDocument.Parse(resultJson);
                    var body = resultDoc.RootElement.GetProperty("body").GetString() ?? "";
                    var base64Encoded = resultDoc.RootElement.TryGetProperty("base64Encoded", out var b64) && b64.GetBoolean();

                    if (string.IsNullOrEmpty(body))
                    {
                        _log.Write("MEDIA_FAILURE", $"stage=NETWORK_MATCH reason=empty_body url={(url.Length > 80 ? url[..80] : url)}");
                    }
                    else
                    {
                        byte[] imageBytes;
                        if (base64Encoded)
                        {
                            imageBytes = Convert.FromBase64String(body);
                        }
                        else
                        {
                            imageBytes = System.Text.Encoding.UTF8.GetBytes(body);
                        }

                        _log.Write("RESPONSE_BODY_RECEIVED", $"bytes={imageBytes.Length} base64Encoded={base64Encoded} source=CDP");
                        _log.Write("MEDIA_DOWNLOADED", $"bytes={imageBytes.Length}");
                        return imageBytes;
                    }
                }
                catch (Exception cdpEx)
                {
                    _log.Write("MEDIA_FAILURE", $"stage=CDP_GETBODY reason={cdpEx.Message} url={(url.Length > 80 ? url[..80] : url)}");
                }
            }
            else
            {
                var urlShort = url.Length > 80 ? url[..80] : url;
                _log.Write("MEDIA_FAILURE", $"stage=NETWORK_MATCH reason=no_matching_response url={urlShort} — trying JS fetch fallback");
            }

            // CDP failed or no match — fall back to JavaScript fetch
            return await DownloadViaJsFetchAsync(url);
        }
        catch (Exception ex)
        {
            _log.Write("MEDIA_DOWNLOAD_FAILED", $"error={ex.Message}");
            LastErrorChanged?.Invoke($"Download exception: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Download image bytes via JavaScript fetch() — works for blob: and http(s): URLs.
    /// Uses an async IIFE that fetches the URL, reads it as a data URL via FileReader,
    /// and returns base64. WebView2 resolves async scripts (GetContactPhone uses the
    /// same pattern successfully).
    /// </summary>
    private async Task<byte[]?> DownloadViaJsFetchAsync(string url)
    {
        try
        {
            var urlJson = JsonSerializer.Serialize(url);
            var script = $$"""
                (async () => {
                    try {
                        const response = await fetch({{urlJson}});
                        if (!response.ok) return JSON.stringify({ error: 'HTTP ' + response.status });
                        const blob = await response.blob();
                        if (blob.size === 0) return JSON.stringify({ error: 'empty_blob' });
                        return await new Promise(resolve => {
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

            var raw = await _webView.ExecuteScriptAsync(script);
            if (string.IsNullOrEmpty(raw))
            {
                _log.Write("MEDIA_FAILURE", $"stage=JS_FETCH reason=empty_response url={(url.Length > 80 ? url[..80] : url)}");
                return null;
            }

            // ExecuteScriptAsync returns sync script results as a JSON-encoded string (starts with "),
            // but async script results may return raw JSON directly (starts with { or [).
            // JsonSerializer.Deserialize<string> fails on raw JSON with:
            // "The JSON value could not be converted to System.String" — handle both cases.
            string innerJson;
            var trimmedRaw = raw.Trim();
            if (trimmedRaw.StartsWith("\""))
            {
                innerJson = JsonSerializer.Deserialize<string>(raw) ?? "";
            }
            else
            {
                innerJson = raw;
            }
            if (string.IsNullOrEmpty(innerJson))
            {
                _log.Write("MEDIA_FAILURE", $"stage=JS_FETCH reason=empty_inner_json url={(url.Length > 80 ? url[..80] : url)}");
                return null;
            }

            using var doc = JsonDocument.Parse(innerJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errProp))
            {
                var errMsg = errProp.GetString() ?? "unknown";
                _log.Write("MEDIA_FAILURE", $"stage=JS_FETCH reason={errMsg} url={(url.Length > 80 ? url[..80] : url)}");
                return null;
            }

            if (!root.TryGetProperty("base64", out var b64Prop))
            {
                _log.Write("MEDIA_FAILURE", $"stage=JS_FETCH reason=no_base64_field url={(url.Length > 80 ? url[..80] : url)}");
                return null;
            }

            var base64Str = b64Prop.GetString() ?? "";
            if (string.IsNullOrEmpty(base64Str))
            {
                _log.Write("MEDIA_FAILURE", $"stage=JS_FETCH reason=empty_base64 url={(url.Length > 80 ? url[..80] : url)}");
                return null;
            }

            var imageBytes = Convert.FromBase64String(base64Str);
            var blobSize = root.TryGetProperty("size", out var szProp) ? szProp.GetInt64() : imageBytes.Length;
            var blobType = root.TryGetProperty("type", out var tpProp) ? tpProp.GetString() ?? "" : "";

            _log.Write("RESPONSE_BODY_RECEIVED", $"bytes={imageBytes.Length} blobSize={blobSize} type={blobType} source=JS_FETCH");
            _log.Write("MEDIA_DOWNLOADED", $"bytes={imageBytes.Length} source=BLOB");
            return imageBytes;
        }
        catch (Exception ex)
        {
            _log.Write("MEDIA_DOWNLOAD_FAILED", $"error={ex.Message} stage=JS_FETCH url={(url.Length > 80 ? url[..80] : url)}");
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
        /// <summary>
        /// ATOMIC detection + click in a single script execution.
        /// Previously GetUnreadChats and ClickNextUnreadChat were two separate calls;
        /// WhatsApp mutated the DOM between them, causing the unread badge to
        /// disappear before the click. This script finds the unread, clicks it,
        /// and verifies navigation — all in one DOM snapshot.
        /// </summary>
        public const string FindAndClickUnreadChat = """
            (() => {
                const pane = document.querySelector('#pane-side');
                if (!pane) return JSON.stringify({ clicked: false, reason: 'no_pane', name: '', clickTargetHtml: '', clickTargetIndex: -1, unreadCount: 0, activeChatBefore: '', activeChatAfter: '', navigationConfirmed: false, clickStrategy: '', clickElementTag: '', clickElementRole: '', clickElementTabindex: '', chatRowsFound: 0, unreadMarkersFound: 0, markerHtml: '', parent1: '', parent2: '', parent3: '', matchedChatRow: false, matchedChatName: '', unreadHandoffName: '', unreadHandoffRowConnected: false, unreadHandoffBadgeStillPresent: false, clickAttempted: false });

                function getActiveChatName() {
                    var main = document.querySelector('#main');
                    if (!main) return '';
                    var header = main.querySelector('header');
                    if (!header) return '';
                    var spans = header.querySelectorAll('span[dir="auto"]');
                    for (var i = 0; i < spans.length; i++) {
                        var t = (spans[i].textContent || '').trim();
                        if (t && t.length > 0 && t.length < 100) return t;
                    }
                    return '';
                }

                var activeChatBefore = getActiveChatName();

                var items = pane.querySelectorAll('[data-testid="cell-frame-container"]');
                if (items.length === 0) items = pane.querySelectorAll('[role="listitem"]');
                if (items.length === 0) items = pane.querySelectorAll('div[data-id]');
                if (items.length === 0) items = pane.querySelectorAll('div[role="button"]');

                var chatRowsFound = items.length;
                var unreadMarkersFound = 0;
                var markerHtml = '';
                var parent1 = '';
                var parent2 = '';
                var parent3 = '';
                var matchedChatRow = false;
                var matchedChatName = '';

                // Capture first 3 row HTMLs for badge structure diagnostics (2000 chars to see full row)
                var rowHtml1 = items.length > 0 ? (items[0].outerHTML || '').substring(0, 2000) : '';
                var rowHtml2 = items.length > 1 ? (items[1].outerHTML || '').substring(0, 2000) : '';
                var rowHtml3 = items.length > 2 ? (items[2].outerHTML || '').substring(0, 2000) : '';

                // Also find the first row that contains a number (potential unread badge)
                var rowWithNumberHtml = '';
                for (var rn = 0; rn < items.length; rn++) {
                    var spans = items[rn].querySelectorAll('span, div');
                    for (var sn = 0; sn < spans.length; sn++) {
                        var t = (spans[sn].textContent || '').trim();
                        if (/^\d+$/.test(t) && t.length <= 3 && spans[sn].offsetWidth > 0 && spans[sn].offsetWidth <= 30) {
                            rowWithNumberHtml = (items[rn].outerHTML || '').substring(0, 2000);
                            break;
                        }
                    }
                    if (rowWithNumberHtml) break;
                }

                // === Badge detection helper ===
                function findBadge(item) {
                    var badge = null;
                    var unreadCount = 0;
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
                                    badge = sp; unreadCount = parseInt(t); break;
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
                                    badge = sp2; unreadCount = parseInt(t2); break;
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
                                    badge = el; unreadCount = elText ? parseInt(elText) : 1; break;
                                }
                            }
                        }
                    }
                    // Method 6: Broader green match — any green-ish background (hue ~120-180)
                    // with a number. WhatsApp's green is #25D366 (rgb(37,211,102)) but the
                    // exact shade may vary. Match any element where G > R and G > B and
                    // the element is small (badge-sized) with numeric text.
                    if (!badge) {
                        var allEls2 = item.querySelectorAll('span, div');
                        for (var g = 0; g < allEls2.length; g++) {
                            var gel = allEls2[g];
                            var gt = (gel.textContent || '').trim();
                            if (!/^\d+$/.test(gt) || gt.length > 3) continue;
                            if (gel.offsetWidth <= 0 || gel.offsetWidth > 35) continue;
                            var gst = window.getComputedStyle(gel);
                            var bg = gst.backgroundColor;
                            // Parse rgb(r, g, b) or rgba(r, g, b, a)
                            var m = bg.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)/);
                            if (!m) continue;
                            var r = parseInt(m[1]), gg = parseInt(m[2]), b = parseInt(m[3]);
                            // Green-ish: G significantly > R and G significantly > B
                            if (gg > r + 30 && gg > b + 30 && gg > 100) {
                                badge = gel; unreadCount = parseInt(gt); break;
                            }
                        }
                    }
                    // Method 7: Bold chat name — unread chats have bold font-weight (700 or 500).
                    // WhatsApp Web renders unread chat names in bold. This is a strong signal
                    // that doesn't depend on badge structure.
                    if (!badge) {
                        var nameSpans = item.querySelectorAll('span[title], span[dir="auto"]');
                        for (var ns2 = 0; ns2 < nameSpans.length; ns2++) {
                            var nsEl = nameSpans[ns2];
                            var nsStyle = window.getComputedStyle(nsEl);
                            var nsFw = nsStyle.fontWeight;
                            if (nsFw === '700' || nsFw === '500' || nsFw === 'bold' || nsFw === '600') {
                                badge = nsEl; unreadCount = 1; break;
                            }
                        }
                    }
                    // Method 8: Row-level unread class — check if any element has "unread" in class
                    if (!badge) {
                        var allEls3 = item.querySelectorAll('*');
                        for (var u = 0; u < allEls3.length; u++) {
                            var uEl = allEls3[u];
                            var uCls = uEl.className || '';
                            if (typeof uCls === 'string' && (uCls.indexOf('unread') >= 0 || uCls.indexOf('Unread') >= 0 || uCls.indexOf('UNREAD') >= 0)) {
                                badge = uEl; unreadCount = 1; break;
                            }
                        }
                    }
                    // Method 9: Broader data-testid search — badge/notification/count/pill/indicator
                    if (!badge) {
                        var testIdEls = item.querySelectorAll('[data-testid]');
                        for (var ti2 = 0; ti2 < testIdEls.length; ti2++) {
                            var tid = (testIdEls[ti2].getAttribute('data-testid') || '').toLowerCase();
                            if (tid.indexOf('unread') >= 0 || tid.indexOf('badge') >= 0 || tid.indexOf('notification') >= 0 || tid.indexOf('count') >= 0 || tid.indexOf('pill') >= 0 || tid.indexOf('indicator') >= 0) {
                                badge = testIdEls[ti2];
                                var tidText = (testIdEls[ti2].textContent || '').trim();
                                unreadCount = /^\d+$/.test(tidText) ? parseInt(tidText) : 1;
                                break;
                            }
                        }
                    }
                    // Method 10: Any small element with non-white/non-transparent background
                    // — catches badges that use unexpected colors or structures
                    if (!badge) {
                        var allEls4 = item.querySelectorAll('span, div');
                        for (var s2 = 0; s2 < allEls4.length; s2++) {
                            var el2 = allEls4[s2];
                            if (el2.offsetWidth <= 0 || el2.offsetWidth > 40) continue;
                            var st2 = window.getComputedStyle(el2);
                            var bg2 = st2.backgroundColor;
                            if (bg2 && bg2 !== 'rgba(0, 0, 0, 0)' && bg2 !== 'transparent' && bg2 !== 'rgb(255, 255, 255)' && bg2 !== 'rgb(0, 0, 0, 0)') {
                                var text2 = (el2.textContent || '').trim();
                                if (/^\d+$/.test(text2) || text2 === '') {
                                    badge = el2; unreadCount = text2 ? parseInt(text2) : 1; break;
                                }
                            }
                        }
                    }
                    return badge ? { badge: badge, unreadCount: unreadCount } : null;
                }

                // === First pass: count ALL unread markers + collect ancestry for first ===
                var firstUnreadItem = null;
                var firstUnreadBadge = null;
                var firstUnreadCount = 0;

                for (var idx = 0; idx < items.length; idx++) {
                    var item = items[idx];
                    var result = findBadge(item);
                    if (result) {
                        unreadMarkersFound++;
                        if (unreadMarkersFound === 1) {
                            firstUnreadItem = item;
                            firstUnreadBadge = result.badge;
                            firstUnreadCount = result.unreadCount;

                            // Collect ancestry diagnostics for first marker
                            markerHtml = (result.badge.outerHTML || '').substring(0, 300);
                            var p = result.badge.parentElement;
                            if (p) { parent1 = (p.outerHTML || '').substring(0, 300); p = p.parentElement; }
                            if (p) { parent2 = (p.outerHTML || '').substring(0, 300); p = p.parentElement; }
                            if (p) { parent3 = (p.outerHTML || '').substring(0, 300); }
                        }
                    }
                }

                // === UNREAD_DIAGNOSTIC — dump detailed element info for first 5 rows ===
                // When no unread markers are found, collect ALL element styles/attributes
                // from the first 5 rows so we can see what WhatsApp actually renders.
                var unreadDiagnostic = [];
                if (unreadMarkersFound === 0 && chatRowsFound > 0) {
                    for (var dIdx = 0; dIdx < Math.min(items.length, 5); dIdx++) {
                        var dRow = items[dIdx];
                        var dInfo = {
                            index: dIdx,
                            rowClass: (dRow.className || '').substring(0, 200),
                            rowTestId: dRow.getAttribute('data-testid') || '',
                            rowDataId: dRow.getAttribute('data-id') || '',
                            rowAriaLabel: (dRow.getAttribute('aria-label') || '').substring(0, 120),
                            elements: []
                        };
                        var dEls = dRow.querySelectorAll('span, div, button, svg');
                        for (var dE = 0; dE < dEls.length && dE < 60; dE++) {
                            var dEl = dEls[dE];
                            var dStyle = window.getComputedStyle(dEl);
                            var dBg = dStyle.backgroundColor;
                            var dFw = dStyle.fontWeight;
                            var dInfo2 = {
                                tag: dEl.tagName,
                                cls: (typeof dEl.className === 'string' ? dEl.className : '').substring(0, 100),
                                testId: dEl.getAttribute('data-testid') || '',
                                ariaLabel: (dEl.getAttribute('aria-label') || '').substring(0, 80),
                                title: (dEl.getAttribute('title') || '').substring(0, 80),
                                text: (dEl.textContent || '').trim().substring(0, 40),
                                w: dEl.offsetWidth,
                                h: dEl.offsetHeight,
                                bg: dBg,
                                fw: dFw,
                                color: dStyle.color
                            };
                            // Only include elements with interesting properties
                            if (dInfo2.testId || dInfo2.ariaLabel ||
                                (dBg && dBg !== 'rgba(0, 0, 0, 0)' && dBg !== 'transparent' && dBg !== 'rgb(255, 255, 255)') ||
                                (dFw !== '400' && dFw !== 'normal' && dFw !== '') ||
                                /^\d+$/.test(dInfo2.text)) {
                                dInfo.elements.push(dInfo2);
                            }
                        }
                        unreadDiagnostic.push(dInfo);
                    }
                }

                if (unreadMarkersFound === 0 || !firstUnreadBadge) {
                    return JSON.stringify({ clicked: false, reason: 'no_unread', name: '', clickTargetHtml: '', clickTargetIndex: -1, unreadCount: 0, activeChatBefore: activeChatBefore, activeChatAfter: '', navigationConfirmed: false, clickStrategy: '', clickElementTag: '', clickElementRole: '', clickElementTabindex: '', chatRowsFound: chatRowsFound, unreadMarkersFound: 0, markerHtml: markerHtml, parent1: parent1, parent2: parent2, parent3: parent3, matchedChatRow: false, matchedChatName: '', unreadHandoffName: '', unreadHandoffRowConnected: false, unreadHandoffBadgeStillPresent: false, clickAttempted: false, rowHtml1: rowHtml1, rowHtml2: rowHtml2, rowHtml3: rowHtml3, rowWithNumberHtml: rowWithNumberHtml, unreadDiagnostic: unreadDiagnostic });
                }

                // === Resolve row + name from the SAME badge (atomic handoff) ===
                var row = firstUnreadBadge.closest('[data-testid="cell-frame-container"]') ||
                          firstUnreadBadge.closest('[role="listitem"]') ||
                          firstUnreadItem;

                var nameEl = row.querySelector('span[title]');
                var name = nameEl ? (nameEl.getAttribute('title') || '') : '';

                if (!name) {
                    var walker = firstUnreadBadge;
                    for (var level = 0; level < 10 && walker; level++) {
                        walker = walker.parentElement;
                        if (!walker) break;
                        var titleEl = walker.querySelector('span[title]');
                        if (titleEl) { name = titleEl.getAttribute('title') || ''; break; }
                    }
                }

                matchedChatRow = !!name;
                matchedChatName = name || '';

                // === Handoff diagnostics ===
                // Check if the row and badge survived between detection and click.
                // (They should — this is the same script execution, no DOM mutation between.)
                var unreadHandoffName = name || '';
                var unreadHandoffRowConnected = !!(row && row.isConnected);
                var unreadHandoffBadgeStillPresent = !!(firstUnreadBadge && firstUnreadBadge.isConnected);

                if (!name || !unreadHandoffRowConnected) {
                    return JSON.stringify({ clicked: false, reason: 'handoff_failed', name: name || '', clickTargetHtml: '', clickTargetIndex: -1, unreadCount: firstUnreadCount, activeChatBefore: activeChatBefore, activeChatAfter: '', navigationConfirmed: false, clickStrategy: '', clickElementTag: '', clickElementRole: '', clickElementTabindex: '', chatRowsFound: chatRowsFound, unreadMarkersFound: unreadMarkersFound, markerHtml: markerHtml, parent1: parent1, parent2: parent2, parent3: parent3, matchedChatRow: matchedChatRow, matchedChatName: matchedChatName, unreadHandoffName: unreadHandoffName, unreadHandoffRowConnected: unreadHandoffRowConnected, unreadHandoffBadgeStillPresent: unreadHandoffBadgeStillPresent, clickAttempted: false });
                }

                if (firstUnreadCount === 0) {
                    var badgeText = (firstUnreadBadge.textContent || '').trim();
                    firstUnreadCount = parseInt(badgeText) || 1;
                }

                // === Click ===
                try { row.scrollIntoView({block: 'center'}); } catch(e) {}

                var clickTarget = row;
                var strategy = 'row_click';
                var interactive = row.querySelector('[role="button"], [tabindex], a, button');
                if (interactive) { clickTarget = interactive; strategy = 'interactive_descendant'; }

                var clickElementTag = clickTarget.tagName;
                var clickElementRole = clickTarget.getAttribute('role') || '';
                var clickElementTabindex = clickTarget.getAttribute('tabindex') || '';

                var rect = clickTarget.getBoundingClientRect();
                var cx = rect.left + rect.width / 2;
                var cy = rect.top + rect.height / 2;

                function fire(type, ctor) {
                    try {
                        var ev = new (ctor || MouseEvent)(type, {
                            bubbles: true, cancelable: true, view: window,
                            clientX: cx, clientY: cy, button: 0, buttons: 1
                        });
                        clickTarget.dispatchEvent(ev);
                    } catch(e) {}
                }

                if (window.PointerEvent) {
                    fire('pointerover', PointerEvent);
                    fire('pointerenter', PointerEvent);
                    fire('pointerdown', PointerEvent);
                }
                fire('mouseover', MouseEvent);
                fire('mousedown', MouseEvent);
                if (window.PointerEvent) fire('pointerup', PointerEvent);
                fire('mouseup', MouseEvent);
                try { clickTarget.focus(); } catch(e) {}
                fire('click', MouseEvent);

                var clickAttempted = true;

                // Navigation verification moved to C# side (VerifyNavigation script + Task.Delay)
                // to keep this script synchronous — WebView2 ExecuteScriptAsync does not reliably
                // await async scripts (Promise not resolved → all fields return empty/default).
                var activeChatAfter = activeChatBefore;
                var navigationConfirmed = false;

                return JSON.stringify({
                    clicked: true,
                    name: name,
                    clickTargetHtml: (row.outerHTML || '').substring(0, 300),
                    clickTargetIndex: -1,
                    unreadCount: firstUnreadCount,
                    atomicClickTargetName: name,
                    atomicClickConnected: unreadHandoffRowConnected,
                    atomicClickUnreadPresent: unreadHandoffBadgeStillPresent,
                    activeChatBefore: activeChatBefore,
                    activeChatAfter: activeChatAfter,
                    navigationConfirmed: navigationConfirmed,
                    clickStrategy: strategy,
                    clickElementTag: clickElementTag,
                    clickElementRole: clickElementRole,
                    clickElementTabindex: clickElementTabindex,
                    chatRowsFound: chatRowsFound,
                    unreadMarkersFound: unreadMarkersFound,
                    markerHtml: markerHtml,
                    parent1: parent1,
                    parent2: parent2,
                    parent3: parent3,
                    matchedChatRow: matchedChatRow,
                    matchedChatName: matchedChatName,
                    unreadHandoffName: unreadHandoffName,
                    unreadHandoffRowConnected: unreadHandoffRowConnected,
                    unreadHandoffBadgeStillPresent: unreadHandoffBadgeStillPresent,
                    clickAttempted: clickAttempted
                });
            })();
            """;

        public const string VerifyNavigation = """
            (() => {
                function getActiveChatName() {
                    var main = document.querySelector('#main');
                    if (!main) return '';
                    var header = main.querySelector('header');
                    if (!header) return '';
                    // Prefer span[title] — holds the full contact name (matches chat list title).
                    // span[dir="auto"] textContent may be truncated or a UI label.
                    var titleEl = header.querySelector('span[title]');
                    if (titleEl) {
                        var t = (titleEl.getAttribute('title') || '').trim();
                        if (t) return t;
                    }
                    var spans = header.querySelectorAll('span[dir="auto"]');
                    for (var i = 0; i < spans.length; i++) {
                        var t = (spans[i].textContent || '').trim();
                        if (t && t.length > 0 && t.length < 100) return t;
                    }
                    return '';
                }
                return JSON.stringify({ activeChatName: getActiveChatName() });
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

                // Strategy: Extract phone from aria-labels in #main.
                // WhatsApp wraps phone numbers in Unicode directional isolate characters
                // (U+2066 ⁦, U+2069 ⁩) inside aria-labels like:
                // "פתיחת פרטי הצ'אט עם אולי  ⁦+972 52-248-1657⁩"
                // These chars break standard phone regexes — strip them first.
                if (!phone && main) {
                    var labeledEls = main.querySelectorAll('[aria-label]');
                    for (var le = 0; le < labeledEls.length && le < 80; le++) {
                        var alText = labeledEls[le].getAttribute('aria-label') || '';
                        if (!alText) continue;
                        // Strip Unicode directional isolate/format chars
                        var cleaned = alText.replace(/[\u2066\u2067\u2068\u2069\u202A-\u202E\u200E\u200F]/g, '');
                        var m = cleaned.match(/\+?[\d][\d\s\-()]{6,14}/);
                        if (m) {
                            var digits = m[0].replace(/\D/g, '');
                            if (digits.length >= 7 && digits.length <= 15) {
                                phone = digits;
                                phoneSource = 'aria_label';
                                phoneCandidates.push(digits);
                                break;
                            }
                        }
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
                var matchedJid = '';
                var phone = '';
                var phoneSource = '';
                var openedContactPanel = false;
                var activeName = '';

                function extractPhone(s) {
                    if (!s) return '';
                    // Broad: any digit sequence 7-15 digits long (international + local)
                    var m = s.match(/\+?[\d][\d\s\-()]{6,14}/);
                    if (m) {
                        var digits = m[0].replace(/\D/g, '');
                        if (digits.length >= 7 && digits.length <= 15) return digits;
                    }
                    return '';
                }

                function extractPhoneFromJid(jid) {
                    if (!jid) return '';
                    var atIdx = jid.indexOf('@');
                    if (atIdx <= 0) return '';
                    var local = jid.substring(0, atIdx);
                    // Accept any domain — WhatsApp uses c.us, s.whatsapp.net, and others
                    if (local.indexOf('true_') === 0) local = local.substring(5);
                    if (local.indexOf('gid_') === 0) return ''; // group ID, not a phone
                    var digits = local.replace(/\D/g, '');
                    return digits.length >= 7 ? digits : '';
                }

                var main = document.querySelector('#main');
                var header = main ? main.querySelector('header') : null;

                // Get active chat name from header — prefer span[title] (full name, matches chat list).
                // Fall back to span[dir="auto"] (first non-empty, < 100 chars).
                // No UI filter here — GetCustomerInfo already handles that, and over-filtering
                // risks rejecting valid short names like "Rod".
                if (header) {
                    var titleEl = header.querySelector('span[title]');
                    if (titleEl) {
                        var tn = (titleEl.getAttribute('title') || '').trim();
                        if (tn) activeName = tn;
                    }
                    if (!activeName) {
                        var nameSpans = header.querySelectorAll('span[dir="auto"]');
                        for (var ns = 0; ns < nameSpans.length; ns++) {
                            var t = (nameSpans[ns].textContent || '').trim();
                            if (t && t.length > 0 && t.length < 100) { activeName = t; break; }
                        }
                    }
                }

                // 1. MATCHED ROW: Find the chat list row in #pane-side whose span[title]
                //    matches the active chat name. Extract the JID from THAT row only.
                //    This is the key fix — previously we took the first JID from ALL rows,
                //    which was often a different chat.
                var pane = document.querySelector('#pane-side');
                if (pane && activeName) {
                    var rows = pane.querySelectorAll('[data-testid="cell-frame-container"], [role="listitem"], div[data-id]');
                    for (var r = 0; r < rows.length; r++) {
                        var row = rows[r];
                        var titleEl = row.querySelector('span[title]');
                        var rowName = titleEl ? (titleEl.getAttribute('title') || '') : '';
                        // Fuzzy match — header name may be truncated vs chat list full name.
                        if (rowName && activeName &&
                            (rowName === activeName ||
                             (activeName.length > 2 && rowName.indexOf(activeName) >= 0) ||
                             (rowName.length > 2 && activeName.indexOf(rowName) >= 0))) {
                            // Found the matching row — extract JID from its data-id
                            var rowJid = row.getAttribute('data-id') || '';
                            if (!rowJid) {
                                // data-id might be on a child or parent
                                var childWithId = row.querySelector('[data-id]');
                                if (childWithId) rowJid = childWithId.getAttribute('data-id') || '';
                            }
                            if (rowJid) {
                                phoneAttrCandidates.push('matched_row:' + rowJid);
                                matchedJid = rowJid;
                                var mp = extractPhoneFromJid(rowJid);
                                if (mp) { phone = mp; phoneSource = 'matched_row_jid'; }
                            }
                            break;
                        }
                    }
                }

                // 2. Fallback: scan ALL #pane-side data-ids (if matched row had no JID)
                if (!phone && pane) {
                    var allDataId = pane.querySelectorAll('[data-id]');
                    for (var i = 0; i < allDataId.length && i < 50; i++) {
                        var did = allDataId[i].getAttribute('data-id') || '';
                        if (!did) continue;
                        phoneAttrCandidates.push('pane:' + did);
                        var p = extractPhoneFromJid(did);
                        if (p && phoneJidCandidates.indexOf(p) < 0) phoneJidCandidates.push(p);
                    }
                }

                // 3. Scan #main for data-id, data-lid, data-user, data-jid
                if (!phone && main) {
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

                // 4. Scan visible text in #main for phone patterns
                if (!phone && main) {
                    var allText = main.innerText || '';
                    var lines = allText.split('\n');
                    for (var t = 0; t < lines.length && t < 100; t++) {
                        var line = lines[t].trim();
                        if (line.length === 0 || line.length > 30) continue;
                        var tp = extractPhone(line);
                        if (tp) phoneTextCandidates.push(line + ' -> ' + tp);
                    }
                }

                // 5. Scan aria-label and title in #main for phone patterns
                if (!phone && main) {
                    var labeled = main.querySelectorAll('[aria-label], [title]');
                    for (var l = 0; l < labeled.length && l < 50; l++) {
                        var al = labeled[l].getAttribute('aria-label') || '';
                        var ti = labeled[l].getAttribute('title') || '';
                        var lp = extractPhone(al) || extractPhone(ti);
                        if (lp) phoneTextCandidates.push('label:' + (al || ti) + ' -> ' + lp);
                    }
                }

                // 6. Scan tel: links
                if (!phone) {
                    var telLinks = document.querySelectorAll('a[href^="tel:"]');
                    for (var h = 0; h < telLinks.length; h++) {
                        var hp = extractPhone(telLinks[h].getAttribute('href') || '');
                        if (hp) phoneTextCandidates.push('tel:' + hp);
                    }
                }

                // 7. Last resort: open contact-info panel, read phone, close it
                if (!phone && header) {
                    try {
                        var contactBtn = header.querySelector('div[role="button"], [data-testid="conversation-info-button"]') || header;
                        contactBtn.click();
                        openedContactPanel = true;
                        await new Promise(function(r) { setTimeout(r, 2500); });

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

                // Pick best phone — prefer matched row JID, then any JID, then text
                if (!phone && phoneJidCandidates.length > 0) {
                    phone = phoneJidCandidates[0];
                    phoneSource = 'jid';
                }
                if (!phone && phoneTextCandidates.length > 0) {
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
                    matchedJid: matchedJid,
                    openedContactPanel: openedContactPanel,
                    activeName: activeName
                });
            })();
            """;

        /// <summary>
        /// Scroll the chat message panel to the bottom to trigger lazy loading
        /// of all images. WhatsApp only loads blob: URLs for images that have
        /// been scrolled into view. Without this, DetectImages only finds
        /// placeholder GIFs and small DATA URL previews.
        /// </summary>
        public const string ScrollChat = """
            (() => {
                var main = document.querySelector('#main');
                if (!main) return JSON.stringify({ scrolled: false, reason: 'no_main' });

                // Find the scrollable message container inside #main.
                // It's the div with scrollHeight > clientHeight.
                var scrollable = null;
                var divs = main.querySelectorAll('div');
                for (var i = 0; i < divs.length; i++) {
                    var d = divs[i];
                    if (d.scrollHeight > d.clientHeight + 200 && d.clientHeight > 200) {
                        scrollable = d;
                        break;
                    }
                }

                if (!scrollable) return JSON.stringify({ scrolled: false, reason: 'no_scrollable' });

                // Scroll to bottom in one jump — triggers lazy loading for all
                // intermediate images as the browser fires scroll/intersection events.
                scrollable.scrollTop = scrollable.scrollHeight;

                return JSON.stringify({
                    scrolled: true,
                    scrollHeight: scrollable.scrollHeight,
                    clientHeight: scrollable.clientHeight,
                    className: (scrollable.className || '').substring(0, 80)
                });
            })();
            """;

        /// <summary>
        /// Scroll back to top after ScrollChat — ensures top images are also loaded
        /// and the chat is in a stable state for detection.
        /// </summary>
        public const string ScrollChatTop = """
            (() => {
                var main = document.querySelector('#main');
                if (!main) return JSON.stringify({ scrolled: false });

                var scrollable = null;
                var divs = main.querySelectorAll('div');
                for (var i = 0; i < divs.length; i++) {
                    var d = divs[i];
                    if (d.scrollHeight > d.clientHeight + 200 && d.clientHeight > 200) {
                        scrollable = d;
                        break;
                    }
                }

                if (scrollable) {
                    // Scroll to top gradually — step by step to trigger intersection observers
                    var totalHeight = scrollable.scrollHeight;
                    var step = Math.max(300, Math.floor(totalHeight / 15));
                    scrollable.scrollTop = totalHeight; // start at bottom
                    // The actual gradual scroll happens via multiple C# calls if needed.
                    // For now, just jump to top.
                    scrollable.scrollTop = 0;
                }

                return JSON.stringify({ scrolled: true });
            })();
            """;

        public const string DetectImages = """
            (() => {
                const main = document.querySelector('#main');
                if (!main) return JSON.stringify({ images: [], candidates: [], mainFound: false, totalImgs: 0, filteredSrc: 0, filteredSize: 0, filteredPlaceholder: 0, filteredDup: 0, filteredPreview: 0, messageGroups: 0 });
                // ONLY include images inside message containers ([data-id] or msg-bubble).
                // This is the inverse of trying to exclude the profile picture — instead of
                // guessing where the profile avatar lives, we only capture images that are
                // definitively inside a message bubble. Profile pictures, call buttons,
                // stickers, emoji, and all non-message UI images are automatically excluded.
                const allImgs = main.querySelectorAll('img');
                const imgs = Array.from(allImgs).filter(function(img) {
                    if (!img.closest('[data-id]') && !img.closest('[data-testid="msg-bubble"]')) return false;
                    return true;
                });
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
                    // Filter small images (thumbnails/avatars/stickers) — ≤80x80 for ALL types.
                    // Real customer photos are always > 80x80.
                    if (w > 0 && h > 0 && w <= 80 && h <= 80) { filteredSize++; continue; }

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

        /// <summary>
        /// Store-based unread detection — uses WhatsApp's internal JavaScript store
        /// via window.require('WAWebCollections').Chat to get unreadCount directly.
        /// This is far more reliable than DOM badge scraping because it doesn't
        /// depend on DOM structure. After finding unread chats from the store,
        /// it finds the matching DOM row and clicks it.
        /// Returns the same fields as FindAndClickUnreadChat for seamless integration.
        /// </summary>
        public const string FindAndClickUnreadViaStore = """
            (async () => {
                try {
                    if (typeof window.require !== 'function') {
                        return JSON.stringify({ clicked: false, reason: 'no_window_require', source: 'store', storeUnreadTotal: 0, storeUnreadChats: [], storeChatCount: 0, chatRowsFound: 0, unreadMarkersFound: 0, name: '', clickTargetHtml: '', clickTargetIndex: -1, unreadCount: 0, activeChatBefore: '', activeChatAfter: '', navigationConfirmed: false, clickStrategy: '', clickElementTag: '', clickElementRole: '', clickElementTabindex: '', unreadHandoffName: '', unreadHandoffRowConnected: false, unreadHandoffBadgeStillPresent: false, clickAttempted: false });
                    }

                    var chatCollection = window.require('WAWebCollections');
                    if (!chatCollection || !chatCollection.Chat) {
                        return JSON.stringify({ clicked: false, reason: 'no_chat_collection', source: 'store', storeUnreadTotal: 0, storeUnreadChats: [], storeChatCount: 0, chatRowsFound: 0, unreadMarkersFound: 0, name: '', clickTargetHtml: '', clickTargetIndex: -1, unreadCount: 0, activeChatBefore: '', activeChatAfter: '', navigationConfirmed: false, clickStrategy: '', clickElementTag: '', clickElementRole: '', clickElementTabindex: '', unreadHandoffName: '', unreadHandoffRowConnected: false, unreadHandoffBadgeStillPresent: false, clickAttempted: false });
                    }

                    var chats = chatCollection.Chat.getModelsArray();
                    var unreadChats = [];
                    for (var i = 0; i < chats.length; i++) {
                        try {
                            var chat = chats[i];
                            var uc = chat.unreadCount || 0;
                            if (uc > 0) {
                                unreadChats.push({
                                    id: (chat.id && chat.id._serialized) ? chat.id._serialized : '',
                                    name: chat.formattedTitle || chat.name || '',
                                    unreadCount: uc
                                });
                            }
                        } catch (e) {}
                    }

                    var pane = document.querySelector('#pane-side');
                    var domRows = pane ? pane.querySelectorAll('[data-testid="cell-frame-container"], [role="listitem"], div[data-id]') : [];
                    var chatRowsFound = domRows.length;

                    if (unreadChats.length === 0) {
                        return JSON.stringify({ clicked: false, reason: 'no_unread_in_store', source: 'store', storeUnreadTotal: 0, storeUnreadChats: [], storeChatCount: chats.length, chatRowsFound: chatRowsFound, unreadMarkersFound: 0, name: '', clickTargetHtml: '', clickTargetIndex: -1, unreadCount: 0, activeChatBefore: '', activeChatAfter: '', navigationConfirmed: false, clickStrategy: '', clickElementTag: '', clickElementRole: '', clickElementTabindex: '', unreadHandoffName: '', unreadHandoffRowConnected: false, unreadHandoffBadgeStillPresent: false, clickAttempted: false });
                    }

                    var targetName = unreadChats[0].name;
                    var clickedRow = null;
                    var clickedIndex = -1;

                    for (var r = 0; r < domRows.length; r++) {
                        var titleEl = domRows[r].querySelector('span[title]');
                        var rowName = titleEl ? (titleEl.getAttribute('title') || '') : '';
                        if (rowName && targetName &&
                            (rowName === targetName ||
                             (targetName.length > 2 && rowName.indexOf(targetName) >= 0) ||
                             (rowName.length > 2 && targetName.indexOf(rowName) >= 0))) {
                            clickedRow = domRows[r];
                            clickedIndex = r;
                            break;
                        }
                    }

                    if (!clickedRow) {
                        return JSON.stringify({ clicked: false, reason: 'row_not_found', source: 'store', storeUnreadTotal: unreadChats.length, storeUnreadChats: unreadChats, storeChatCount: chats.length, chatRowsFound: chatRowsFound, unreadMarkersFound: unreadChats.length, name: targetName, clickTargetHtml: '', clickTargetIndex: -1, unreadCount: unreadChats[0].unreadCount, activeChatBefore: '', activeChatAfter: '', navigationConfirmed: false, clickStrategy: '', clickElementTag: '', clickElementRole: '', clickElementTabindex: '', unreadHandoffName: targetName, unreadHandoffRowConnected: false, unreadHandoffBadgeStillPresent: false, clickAttempted: false });
                    }

                    function getActiveChatName() {
                        var main = document.querySelector('#main');
                        if (!main) return '';
                        var header = main.querySelector('header');
                        if (!header) return '';
                        var titleEl = header.querySelector('span[title]');
                        if (titleEl) { var t = (titleEl.getAttribute('title') || '').trim(); if (t) return t; }
                        var spans = header.querySelectorAll('span[dir="auto"]');
                        for (var i = 0; i < spans.length; i++) { var t = (spans[i].textContent || '').trim(); if (t && t.length > 0 && t.length < 100) return t; }
                        return '';
                    }

                    var activeChatBefore = getActiveChatName();

                    try { clickedRow.scrollIntoView({block: 'center'}); } catch(e) {}

                    var interactive = clickedRow.querySelector('[role="button"], [tabindex], a, button');
                    var clickTarget = interactive || clickedRow;
                    var strategy = interactive ? 'interactive_descendant' : 'row_click';

                    var clickElementTag = clickTarget.tagName;
                    var clickElementRole = clickTarget.getAttribute('role') || '';
                    var clickElementTabindex = clickTarget.getAttribute('tabindex') || '';

                    var rect = clickTarget.getBoundingClientRect();
                    var cx = rect.left + rect.width / 2;
                    var cy = rect.top + rect.height / 2;

                    function fire(type, ctor) {
                        try {
                            var ev = new (ctor || MouseEvent)(type, {
                                bubbles: true, cancelable: true, view: window,
                                clientX: cx, clientY: cy, button: 0, buttons: 1
                            });
                            clickTarget.dispatchEvent(ev);
                        } catch(e) {}
                    }

                    if (window.PointerEvent) {
                        fire('pointerover', PointerEvent);
                        fire('pointerenter', PointerEvent);
                        fire('pointerdown', PointerEvent);
                    }
                    fire('mouseover', MouseEvent);
                    fire('mousedown', MouseEvent);
                    if (window.PointerEvent) fire('pointerup', PointerEvent);
                    fire('mouseup', MouseEvent);
                    try { clickTarget.focus(); } catch(e) {}
                    fire('click', MouseEvent);

                    await new Promise(function(r) { setTimeout(r, 1500); });

                    var activeChatAfter = getActiveChatName();
                    var navigationConfirmed = false;
                    if (activeChatAfter && targetName) {
                        if (activeChatAfter === targetName ||
                            (targetName.length > 2 && activeChatAfter.indexOf(targetName) >= 0) ||
                            (activeChatAfter.length > 2 && targetName.indexOf(activeChatAfter) >= 0)) {
                            navigationConfirmed = true;
                        }
                    }

                    return JSON.stringify({
                        clicked: true, source: 'store', name: targetName,
                        storeUnreadTotal: unreadChats.length, storeUnreadChats: unreadChats, storeChatCount: chats.length,
                        chatRowsFound: chatRowsFound, unreadMarkersFound: unreadChats.length,
                        clickTargetHtml: (clickedRow.outerHTML || '').substring(0, 300),
                        clickTargetIndex: clickedIndex, unreadCount: unreadChats[0].unreadCount,
                        atomicClickTargetName: targetName, atomicClickConnected: true, atomicClickUnreadPresent: true,
                        activeChatBefore: activeChatBefore, activeChatAfter: activeChatAfter,
                        navigationConfirmed: navigationConfirmed, clickStrategy: strategy,
                        clickElementTag: clickElementTag, clickElementRole: clickElementRole, clickElementTabindex: clickElementTabindex,
                        unreadHandoffName: targetName, unreadHandoffRowConnected: true, unreadHandoffBadgeStillPresent: true,
                        clickAttempted: true
                    });
                } catch (e) {
                    return JSON.stringify({ clicked: false, reason: 'exception: ' + e.message, source: 'store', storeUnreadTotal: 0, storeUnreadChats: [], storeChatCount: 0, chatRowsFound: 0, unreadMarkersFound: 0, name: '', clickTargetHtml: '', clickTargetIndex: -1, unreadCount: 0, activeChatBefore: '', activeChatAfter: '', navigationConfirmed: false, clickStrategy: '', clickElementTag: '', clickElementRole: '', clickElementTabindex: '', unreadHandoffName: '', unreadHandoffRowConnected: false, unreadHandoffBadgeStillPresent: false, clickAttempted: false });
                }
            })();
            """;
    }
}