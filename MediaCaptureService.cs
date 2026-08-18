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

    // Diagnostics: track lastMessageId per chat across scans to detect new messages
    // even when unreadCount=0 (WhatsApp Web auto-marks messages as read when focused).
    private readonly Dictionary<string, string> _lastMessageIds = new(StringComparer.OrdinalIgnoreCase);

    // First scan catch-up: process top 5 recent chats to handle messages that
    // arrived before the app started (unreadCount=0, no baseline to compare).
    private bool _firstScanDone = false;
    private readonly List<(string id, string name, string lastMsg)> _catchupChats = new();

    // Tracks unread EVENT keys that have been successfully processed in this session.
    // Event key = chatId + "|" + lastMessageId — so the SAME customer sending NEW
    // messages later creates a NEW event key and gets processed again.
    private readonly HashSet<string> _processedEventKeys = new(StringComparer.OrdinalIgnoreCase);

    // Failed events: retry count + cooldown. On failure, the event is NOT marked
    // processed — it gets retried after a cooldown. After MaxEventRetries, we give
    // up and mark it processed to avoid infinite loops.
    private readonly Dictionary<string, int> _failedEventRetries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _failedEventCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxEventRetries = 3;
    private static readonly TimeSpan FailedEventCooldown = TimeSpan.FromMinutes(2);

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

        await EnableCdpAsync();

        ScannerStatusChanged?.Invoke("Scanning...");
        UpdateStatus("Scanning chats...");

        // === Store-based unread detection (primary) ===
        var excludeKeys = new HashSet<string>(_processedEventKeys, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.Now;
        foreach (var kv in _failedEventCooldowns)
        {
            if (now < kv.Value && !excludeKeys.Contains(kv.Key))
                excludeKeys.Add(kv.Key);
        }
        var excludeKeysJson = JsonSerializer.Serialize(excludeKeys);
        var storeScript = MediaCaptureScripts.FindAndClickUnreadViaStore.Replace("__EXCLUDE_NAMES__", excludeKeysJson);
        var node = await ExecuteScriptJsonAsync(storeScript);
        var storeSource = node?["source"]?.GetValue<string>() ?? "";
        var storeClicked = node?["clicked"]?.GetValue<bool>() ?? false;
        var storeChatCount = node?["storeChatCount"]?.GetValue<int>() ?? 0;
        var storeUnreadTotal = node?["storeUnreadTotal"]?.GetValue<int>() ?? 0;
        var allUnreadTotal = node?["allUnreadTotal"]?.GetValue<int>() ?? -1;

        _log.Write("UNREAD_DETECTION_SOURCE", storeSource);
        _log.Write("UNREAD_DETECTION_CLICKED", storeClicked.ToString().ToLowerInvariant());
        if (storeChatCount > 0)
            _log.Write("STORE_CHAT_COUNT", storeChatCount.ToString());

        var storeUnreadChats = node?["storeUnreadChats"]?.AsArray();
        if (storeUnreadChats != null)
        {
            foreach (var uc in storeUnreadChats)
            {
                var ucId = uc?["id"]?.GetValue<string>() ?? "";
                var ucName = uc?["name"]?.GetValue<string>() ?? "";
                var ucCount = uc?["unreadCount"]?.GetValue<int>() ?? 0;
                var ucKey = uc?["eventKey"]?.GetValue<string>() ?? "";
                _log.Write("STORE_UNREAD_CHAT", $"id={ucId} name={ucName} unreadCount={ucCount} eventKey={ucKey}");
            }
        }
        // === DIAGNOSTICS: log top 20 chats + detect new messages by lastMessageId change ===
        var storeTopChats = node?["storeTopChats"]?.AsArray();
        if (storeTopChats != null)
        {
            foreach (var tc in storeTopChats)
            {
                var tcId = tc?["id"]?.GetValue<string>() ?? "";
                var tcName = tc?["name"]?.GetValue<string>() ?? "";
                var tcUnread = tc?["unreadCount"]?.GetValue<int>() ?? 0;
                var tcLmid = tc?["lastMessageId"]?.GetValue<string>() ?? "";
                var tcT = tc?["t"]?.ToString() ?? "0";
                var tcMuted = tc?["muted"]?.GetValue<bool>() ?? false;
                var tcArchived = tc?["archived"]?.GetValue<bool>() ?? false;
                _log.Write("STORE_TOP_CHAT", $"id={tcId} name={tcName} unread={tcUnread} lastMsg={tcLmid} t={tcT} muted={tcMuted} archived={tcArchived}");
            }
        }

        // === lastMessageId change detection (top 50 chats) ===
        var storeAllChatLastMsgs = node?["storeAllChatLastMsgs"]?.AsArray();
        string? lastMsgChangedChatId = null;
        string? lastMsgChangedName = null;
        string? lastMsgChangedNewId = null;

        // === FIRST SCAN CATCH-UP ===
        // On the first scan, queue top 5 recent chats for processing.
        // This handles messages that arrived before the app started
        // (unreadCount=0, no baseline for lastMessageId change detection).
        // MUST run BEFORE any early-return for no-unread.
        // Uses storeAllChatLastMsgs (50 chats) or storeTopChats (20) as fallback.
        _log.Write("FIRST_SCAN_ENTERED", _firstScanDone.ToString().ToLowerInvariant());
        _log.Write("FIRST_SCAN_STORE_CHAT_COUNT", storeChatCount.ToString());
        if (!_firstScanDone)
        {
            _firstScanDone = true;
            var catchupSource = storeAllChatLastMsgs ?? storeTopChats;
            var sourceName = storeAllChatLastMsgs != null ? "storeAllChatLastMsgs" : (storeTopChats != null ? "storeTopChats" : "null");
            _log.Write("FIRST_SCAN_CATCHUP_SOURCE", sourceName);
            if (catchupSource != null)
            {
                int catchupCount = 0;
                foreach (var lc in catchupSource)
                {
                    if (catchupCount >= 5) break;
                    var lcId = lc?["id"]?.GetValue<string>() ?? "";
                    var lcName = lc?["name"]?.GetValue<string>() ?? "";
                    var lcLmid = lc?["lastMessageId"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(lcId))
                    {
                        _catchupChats.Add((lcId, lcName, lcLmid));
                        catchupCount++;
                    }
                }
            }
            _log.Write("FIRST_SCAN_CATCHUP_QUEUED", $"count={_catchupChats.Count}");
        }
        _log.Write("CATCHUP_QUEUE_COUNT", _catchupChats.Count.ToString());

        if (storeAllChatLastMsgs != null)
        {
            foreach (var lc in storeAllChatLastMsgs)
            {
                var lcId = lc?["id"]?.GetValue<string>() ?? "";
                var lcLmid = lc?["lastMessageId"]?.GetValue<string>() ?? "";
                var lcName = lc?["name"]?.GetValue<string>() ?? "";
                var lcUnread = lc?["unreadCount"]?.GetValue<int>() ?? 0;
                if (!string.IsNullOrEmpty(lcId) && !string.IsNullOrEmpty(lcLmid))
                {
                    if (_lastMessageIds.TryGetValue(lcId, out var prevLmid) && prevLmid != lcLmid)
                    {
                        _log.Write("NEW_MESSAGE_BY_LASTMSG_CHANGE", $"chatId={lcId} name={lcName} old={prevLmid} new={lcLmid} unread={lcUnread}");
                        if (lastMsgChangedChatId == null)
                        {
                            lastMsgChangedChatId = lcId;
                            lastMsgChangedName = lcName;
                            lastMsgChangedNewId = lcLmid;
                        }
                    }
                    _lastMessageIds[lcId] = lcLmid;
                }
            }
        }
        _log.Write("ACTIVE_CHAT_FROM_STORE", node?["activeChatBefore"]?.GetValue<string>() ?? "");
        _log.Write("STORE_UNREAD_TOTAL", storeUnreadTotal.ToString());
        if (allUnreadTotal >= 0)
            _log.Write("ALL_UNREAD_TOTAL", allUnreadTotal.ToString());
        _log.Write("PROCESSED_EVENT_COUNT", _processedEventKeys.Count.ToString());
        _log.Write("COOLDOWN_EVENT_COUNT", _failedEventCooldowns.Count.ToString());

        if (!storeClicked)
        {
            _log.Write("UNREAD_DETECTION_FALLBACK", $"{storeSource} -> dom");
            node = await ExecuteScriptJsonAsync(MediaCaptureScripts.FindAndClickUnreadChat);
        }
        var chatRowsFound = node?["chatRowsFound"]?.GetValue<int>() ?? 0;
        var unreadMarkersFound = node?["unreadMarkersFound"]?.GetValue<int>() ?? 0;
        var clicked = node?["clicked"]?.GetValue<bool>() ?? false;
        var name = node?["name"]?.GetValue<string>() ?? "";
        var eventKey = node?["eventKey"]?.GetValue<string>() ?? "";
        var chatId = node?["chatId"]?.GetValue<string>() ?? "";
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

        _log.Write("CHAT_ROWS_FOUND", chatRowsFound.ToString());
        _log.Write("UNREAD_MARKERS_FOUND", unreadMarkersFound.ToString());
        _log.Write("UNREAD_CHAT_MATCHES", unreadMarkersFound.ToString());

        if (chatRowsFound == 0)
        {
            var reason = node?["reason"]?.GetValue<string>() ?? "null_response";
            _log.Write("SCAN_NO_ROWS", $"reason={reason}");
        }

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
            // === Decide which chat to process ===
            string triggerSource = "lastmsg_change";
            if (lastMsgChangedChatId != null)
            {
                _log.Write("LASTMSG_CHANGE_DETECTED", $"chatId={lastMsgChangedChatId} name={lastMsgChangedName} newLastMsg={lastMsgChangedNewId}");
            }
            else if (_catchupChats.Count > 0)
            {
                var catchup = _catchupChats[0];
                _catchupChats.RemoveAt(0);
                lastMsgChangedChatId = catchup.id;
                lastMsgChangedName = catchup.name;
                lastMsgChangedNewId = catchup.lastMsg;
                triggerSource = "catchup";
                _log.Write("CATCHUP_PROCESSING", $"chatId={catchup.id} name={catchup.name} remaining={_catchupChats.Count}");
                _log.Write("CATCHUP_TARGET_CHAT_ID", catchup.id);
            }
            else
            {
                ScannerStatusChanged?.Invoke($"Idle — {chatRowsFound} chats, 0 markers");
                UpdateStatus("Idle — no unread chats");
                return;
            }

            // === Open the chat by chatId ===
            var openScript = MediaCaptureScripts.OpenChatByChatId.Replace("__CHAT_ID_JSON__", JsonSerializer.Serialize(lastMsgChangedChatId));
            var openNode = await ExecuteScriptJsonAsync(openScript);
            var openClicked = openNode?["clicked"]?.GetValue<bool>() ?? false;
            var openName = openNode?["name"]?.GetValue<string>() ?? "";
            var openStrategy = openNode?["clickStrategy"]?.GetValue<string>() ?? "";
            var openNavConfirmed = openNode?["navigationConfirmed"]?.GetValue<bool>() ?? false;
            var openActiveBefore = openNode?["activeChatBefore"]?.GetValue<string>() ?? "";

            _log.Write("CATCHUP_CHAT_OPEN_RESULT", $"clicked={openClicked} name={openName} strategy={openStrategy} navConfirmed={openNavConfirmed}");

            if (openClicked && !string.IsNullOrWhiteSpace(openName))
            {
                _log.Write("CHAT_OPEN", $"source={triggerSource} name={openName} chatId={lastMsgChangedChatId} strategy={openStrategy} navConfirmed={openNavConfirmed}");
                _log.Write("MEDIA_SCAN_STARTED", $"source={triggerSource} name={openName} chatId={lastMsgChangedChatId}");
                clicked = true;
                name = openName;
                chatId = lastMsgChangedChatId!;
                eventKey = $"{lastMsgChangedChatId}|{lastMsgChangedNewId}";
                chatUnreadCount = 20;
                navigationConfirmed = openNavConfirmed;
                activeChatBefore = openActiveBefore;
                clickStrategy = $"{triggerSource}:{openStrategy}";
            }
            else
            {
                _log.Write("CHAT_OPEN_FAILED", $"source={triggerSource} chatId={lastMsgChangedChatId} reason=row_not_found_or_no_name");
                ScannerStatusChanged?.Invoke($"Idle — {triggerSource} open failed");
                UpdateStatus($"Idle — {triggerSource} open failed");
                return;
            }
        }

        _log.Write("SCAN_START", $"unread_chats={unreadMarkersFound}");
        ScannerStatusChanged?.Invoke($"Scanning {unreadMarkersFound} chats");
        var totalSaved = 0;
        var totalDuplicates = 0;
        var scanSucceeded = false;

        if (!string.IsNullOrEmpty(eventKey))
            _log.Write("UNREAD_EVENT_KEY", $"chatId={chatId} eventKey={eventKey} name={name}");

        _log.Write("UNREAD_HANDOFF_NAME", unreadHandoffName);
        _log.Write("UNREAD_HANDOFF_ROW_CONNECTED", unreadHandoffRowConnected.ToString().ToLowerInvariant());
        _log.Write("UNREAD_HANDOFF_BADGE_STILL_PRESENT", unreadHandoffBadgeStillPresent.ToString().ToLowerInvariant());
        _log.Write("CLICK_ATTEMPTED", clickAttempted.ToString().ToLowerInvariant());

        if (!clicked || string.IsNullOrWhiteSpace(name))
        {
            _log.Write("NO_MORE_UNREAD", "reason=unread_lost_between_detection_and_click");
            goto scan_complete;
        }

        // === NAVIGATION VERIFICATION ===
        // Skip if the script already confirmed navigation (already_active case).
        if (!navigationConfirmed)
        {
            await Task.Delay(2000);
            var navScript = MediaCaptureScripts.VerifyNavigation
                .Replace("__CHAT_ID_JSON__", JsonSerializer.Serialize(chatId ?? ""))
                .Replace("__TARGET_NAME_JSON__", JsonSerializer.Serialize(name ?? ""));
            var navNode = await ExecuteScriptJsonAsync(navScript);
            activeChatAfter = navNode?["activeChatName"]?.GetValue<string>() ?? "";
            var headerChatId = navNode?["headerChatId"]?.GetValue<string>() ?? "";
            var headerName = navNode?["headerName"]?.GetValue<string>() ?? "";
            var validationMethod = navNode?["validationMethod"]?.GetValue<string>() ?? "";
            var navConfirmedFromScript = navNode?["navigationConfirmed"]?.GetValue<bool>() ?? false;

            _log.Write("NAV_TARGET_CHAT_ID", chatId ?? "");
            _log.Write("NAV_TARGET_NAME", name ?? "");
            _log.Write("NAV_HEADER_NAME", headerName);
            _log.Write("NAV_HEADER_CHAT_ID", headerChatId);
            _log.Write("NAV_PANEL_CHANGED", (!string.IsNullOrWhiteSpace(headerName) && headerName != activeChatBefore).ToString().ToLowerInvariant());
            _log.Write("NAV_VALIDATION_METHOD", validationMethod);

            navigationConfirmed = navConfirmedFromScript;
        }

        _log.Write("TARGET_CHAT_ID", chatId);
        _log.Write("TARGET_CHAT_NAME", name);
        _log.Write("RESOLVED_ROW_NAME", node?["resolvedRowName"]?.GetValue<string>() ?? "");
        _log.Write("ROW_CLICKED", (node?["rowClicked"]?.GetValue<bool>() ?? false).ToString().ToLowerInvariant());
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

        if (!navigationConfirmed)
        {
            _log.Write("CHAT_OPEN_FAILED", $"target={name} before={activeChatBefore} after={activeChatAfter}");
            CurrentChatChanged?.Invoke($"{name} — open failed (stayed on {activeChatAfter})");
            UpdateStatus($"Open failed: target={name} active={activeChatAfter}");
            goto scan_complete;
        }

        var infoNode = await ExecuteScriptJsonAsync(MediaCaptureScripts.GetCustomerInfo);
        var activeChatName = activeChatAfter;
        var phone = infoNode?["phone"]?.GetValue<string>() ?? "";

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

            _log.Write("CUSTOMER_NAME", activeChatName);
            var phoneSource = infoNode?["phoneSource"]?.GetValue<string>() ?? "";
            var dataIds = infoNode?["dataIds"]?.AsArray();
            var phoneCandidates = infoNode?["phoneCandidates"]?.AsArray();
            _log.Write("HEADER_DATA_IDS", dataIds != null ? string.Join(" | ", dataIds.Select(s => s?.GetValue<string>() ?? "")) : "");
            _log.Write("PHONE_CANDIDATES", phoneCandidates != null ? string.Join(" | ", phoneCandidates.Select(s => s?.GetValue<string>() ?? "")) : "");
            _log.Write("CUSTOMER_PHONE_SOURCE", phoneSource);
            _log.Write("CUSTOMER_PHONE", phone);

            var chatMatch = !string.IsNullOrWhiteSpace(activeChatName) &&
                (string.Equals(activeChatName, name, StringComparison.OrdinalIgnoreCase) ||
                 (name.Length > 2 && activeChatName.Contains(name)) ||
                 (activeChatName.Length > 2 && name.Contains(activeChatName)));
            _log.Write("CHAT_MATCH", chatMatch.ToString().ToLowerInvariant());

            if (!chatMatch)
            {
                _log.Write("CHAT_OPEN_MISMATCH", $"target={name} active={activeChatName}");
                CurrentChatChanged?.Invoke($"{name} — mismatch (active: {activeChatName})");
                UpdateStatus($"Chat mismatch: target={name} active={activeChatName}");
                goto scan_complete;
            }

            _log.Write("MEDIA_SCAN_STARTED_AFTER_UNREAD_OPEN", $"chat={activeChatName}");

            var customerName = activeChatName;

            var phoneNode = await ExecuteScriptJsonAsync(MediaCaptureScripts.GetContactPhone);
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

            await ExecuteScriptJsonAsync(MediaCaptureScripts.ScrollChat);
            await Task.Delay(3000);
            await ExecuteScriptJsonAsync(MediaCaptureScripts.ScrollChatTop);
            await Task.Delay(2000);

            var detectScript = MediaCaptureScripts.DetectImages.Replace("__UNREAD_COUNT__", chatUnreadCount.ToString());
            var imagesNode = await ExecuteScriptJsonAsync(detectScript);
            var images = imagesNode?["images"]?.AsArray();
            _log.Write("MEDIA_CANDIDATES_FOUND", $"{images?.Count ?? 0}");

            var filteredPlaceholder = imagesNode?["filteredPlaceholder"]?.GetValue<int>() ?? 0;
            var filteredDup = imagesNode?["filteredDup"]?.GetValue<int>() ?? 0;
            var filteredPreview = imagesNode?["filteredPreview"]?.GetValue<int>() ?? 0;
            var filteredOutgoing = imagesNode?["filteredOutgoing"]?.GetValue<int>() ?? 0;
            var filteredOld = imagesNode?["filteredOld"]?.GetValue<int>() ?? 0;
            var messageGroups = imagesNode?["messageGroups"]?.GetValue<int>() ?? 0;
            if (filteredPlaceholder > 0)
                _log.Write("MEDIA_SKIPPED", $"reason=placeholder_gif count={filteredPlaceholder}");
            if (filteredDup > 0)
                _log.Write("MEDIA_DUPLICATE_SRC", $"count={filteredDup}");
            if (filteredPreview > 0)
                _log.Write("MEDIA_SKIPPED", $"reason=preview_or_thumbnail count={filteredPreview}");
            if (filteredOutgoing > 0)
                _log.Write("MEDIA_SKIPPED", $"reason=outgoing_message count={filteredOutgoing}");
            if (filteredOld > 0)
                _log.Write("MEDIA_SKIPPED", $"reason=old_message count={filteredOld}");

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

            // === DIAGNOSTICS: log every media candidate with accept/reject ===
            var diagArray = imagesNode?["diagnostics"]?.AsArray();
            if (diagArray != null)
            {
                var accImg = 0; var accVid = 0; var profImg = 0;
                foreach (var d in diagArray)
                {
                    var dt = d?["type"]?.GetValue<string>() ?? "";
                    var ds = d?["srcType"]?.GetValue<string>() ?? "";
                    var dDir = d?["direction"]?.GetValue<string>() ?? "";
                    var dMid = d?["messageId"]?.GetValue<string>() ?? "";
                    var dMts = d?["messageTimestamp"]?.GetValue<string>() ?? "";
                    var dw = d?["width"]?.GetValue<int>() ?? 0;
                    var dh = d?["height"]?.GetValue<int>() ?? 0;
                    var da = d?["accepted"]?.GetValue<bool>() ?? false;
                    var dr = d?["rejectReason"]?.GetValue<string>() ?? "";
                    var dih = d?["inHeader"]?.GetValue<bool>() ?? false;
                    _log.Write("MESSAGE_DIRECTION", dDir);
                    _log.Write("MESSAGE_ID", dMid);
                    _log.Write("MESSAGE_TIMESTAMP", dMts);
                    _log.Write("MEDIA_CANDIDATE", $"type={dt} srcType={ds} direction={dDir} messageId={dMid} width={dw} height={dh} inHeader={dih}");
                    if (da) { _log.Write("MEDIA_ACCEPTED_REASON", "new_incoming"); if (dt == "image") accImg++; else if (dt == "video") accVid++; }
                    else { _log.Write("MEDIA_REJECTED_REASON", dr); if (dih && dt == "image") profImg++; }
                }
                _log.Write("MEDIA_DIAGNOSTICS_SUMMARY", $"accepted_images={accImg} accepted_videos={accVid} profile_images={profImg} total={diagArray.Count}");
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

            var dbChatId = $"{customerName}|{phone}";
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

                if (_processedSrcs.Contains(src))
                {
                    duplicates++;
                    _log.Write("DUPLICATE_CHECK", $"result=DUPLICATE_SRC src={srcShort}");
                    continue;
                }

                _log.Write("MEDIA_DOWNLOAD_STARTED", $"src={srcShort}");

                var imageBytes = await DownloadImageAsync(src);
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    failed++;
                    _log.Write("MEDIA_FAILURE", $"stage=DOWNLOAD reason=download_failed src={srcShort}");
                    continue;
                }

                downloaded++;
                _log.Write("MEDIA_DOWNLOADED", $"bytes={imageBytes.Length} source={(src.StartsWith("blob:") ? "BLOB" : src.StartsWith("data:") ? "DATA" : "HTTP")}");

                if (imageBytes.Length < 15360)
                {
                    _log.Write("MEDIA_SKIPPED", $"reason=too_small bytes={imageBytes.Length} threshold=15360 src={srcShort}");
                    continue;
                }

                var sha256 = ComputeSha256(imageBytes);

                if (_db.IsDuplicate(sha256))
                {
                    duplicates++;
                    _log.Write("DUPLICATE_CHECK", $"result=DUPLICATE sha256={sha256[..12]}");
                    continue;
                }

                _log.Write("DUPLICATE_CHECK", $"result=NEW sha256={sha256[..12]}");

                string folderPath;
                try
                {
                    // === Ensure daily + hour directory structure exists before saving ===
                    var hourPath = Path.GetDirectoryName(orderFolderBase);
                    var dailyPath = hourPath != null ? Path.GetDirectoryName(hourPath) : null;
                    if (dailyPath == null || hourPath == null)
                    {
                        failed++;
                        _log.Write("MEDIA_FAILURE", $"stage=PATH_COMPUTE reason=null_daily_or_hour base={orderFolderBase}");
                        continue;
                    }
                    Directory.CreateDirectory(dailyPath);
                    Directory.CreateDirectory(hourPath);
                    if (!Directory.Exists(dailyPath))
                    {
                        failed++;
                        _log.Write("MEDIA_FAILURE", $"stage=DAILY_FOLDER reason=not_exists_after_create path={dailyPath}");
                        continue;
                    }
                    if (!Directory.Exists(hourPath))
                    {
                        failed++;
                        _log.Write("MEDIA_FAILURE", $"stage=HOUR_FOLDER reason=not_exists_after_create path={hourPath}");
                        continue;
                    }
                    _log.Write("DAILY_FOLDER_VERIFIED", dailyPath);
                    _log.Write("HOUR_FOLDER_VERIFIED", hourPath);

                    var existing = CustomerFolderService.FindExistingFolder(orderFolderBase);
                    if (existing != null)
                    {
                        folderPath = existing;
                    }
                    else
                    {
                        folderPath = CustomerFolderService.CreateOrderFolder(_ordersRoot, customerName, phone);
                        orderFolderBase = CustomerFolderService.GetBasePathFromFolder(folderPath);
                    }
                    _log.Write("CUSTOMER_FOLDER_VERIFIED", folderPath);
                }
                catch (Exception ex)
                {
                    failed++;
                    _log.Write("MEDIA_FAILURE", $"stage=FOLDER reason={ex.Message} src={srcShort}");
                    continue;
                }

                try
                {
                    var imageIndex = CustomerFolderService.GetNextImageIndex(folderPath);
                    var localPath = CustomerFolderService.SaveImage(folderPath, imageBytes, imageIndex);
                    _log.Write("DAILY_FOLDER_CREATED", folderPath);
                    _log.Write("FILE_SAVED", $"path={localPath}");
                    LastSavedFileChanged?.Invoke(localPath);

                    var actualFolder = CustomerFolderService.UpdateFolderCount(folderPath);
                    var newCount = CustomerFolderService.CountFiles(actualFolder);
                    _log.Write("CUSTOMER_COUNT_UPDATED", $"count={newCount}");

                    var mediaId = Guid.NewGuid().ToString("N");
                    _db.InsertMedia(mediaId, dbChatId, customerName, phone,
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

            totalDuplicates += duplicates;
            _log.Write("RECONCILIATION", $"detected={detected} downloaded={downloaded} duplicates={duplicates} saved={saved} failed={failed}");
            if (detected != saved + duplicates + failed)
            {
                _log.Write("RECONCILIATION_ERROR", $"detected={detected} downloaded={downloaded} duplicates={duplicates} saved={saved} failed={failed}");
                LastErrorChanged?.Invoke($"Reconciliation mismatch: detected={detected} downloaded={downloaded} saved={saved} dup={duplicates} failed={failed}");
            }

            CurrentChatChanged?.Invoke($"{customerName} — {saved} new, {duplicates} dup, {failed} failed");
            UpdateStatus($"Processed: {customerName} — {saved} new, {duplicates} dup, {failed} failed");

            scanSucceeded = true;

        scan_complete:
        if (!string.IsNullOrEmpty(eventKey))
        {
            if (scanSucceeded)
            {
                _processedEventKeys.Add(eventKey);
                _failedEventRetries.Remove(eventKey);
                _failedEventCooldowns.Remove(eventKey);
                _log.Write("UNREAD_EVENT_PROCESSED", $"eventKey={eventKey} name={name} total_processed={_processedEventKeys.Count}");
            }
            else
            {
                _failedEventRetries.TryGetValue(eventKey, out var retries);
                retries++;
                _failedEventRetries[eventKey] = retries;
                if (retries >= MaxEventRetries)
                {
                    _processedEventKeys.Add(eventKey);
                    _failedEventRetries.Remove(eventKey);
                    _failedEventCooldowns.Remove(eventKey);
                    _log.Write("UNREAD_EVENT_GIVEUP", $"eventKey={eventKey} name={name} retries={retries}");
                }
                else
                {
                    _failedEventCooldowns[eventKey] = DateTime.Now + FailedEventCooldown;
                    _log.Write("UNREAD_EVENT_RETRY", $"eventKey={eventKey} name={name} retries={retries} cooldown_min=2");
                }
            }
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
                _log.Write("MEDIA_DOWNLOADED", $"bytes={bytes.Length} source=DATA");
                return bytes;
            }

            _log.Write("MEDIA_SOURCE_TYPE", url.StartsWith("blob:") ? "BLOB_URL" : "HTTP_URL");

            if (url.StartsWith("blob:"))
            {
                return await DownloadViaJsFetchAsync(url);
            }

            await Task.Delay(500);

            NetworkResponseInfo? info;
            lock (_networkLock)
            {
                if (!_networkResponses.TryGetValue(url, out info))
                {
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
                        _log.Write("MEDIA_DOWNLOADED", $"bytes={imageBytes.Length} source=CDP");
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
    /// Uses CDP Runtime.Evaluate with awaitPromise=true to properly resolve the fetch Promise.
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
                        const buf = await response.arrayBuffer();
                        if (buf.byteLength === 0) return JSON.stringify({ error: 'empty_buffer' });
                        const bytes = new Uint8Array(buf);
                        let binary = '';
                        const chunkSize = 8192;
                        for (let i = 0; i < bytes.length; i += chunkSize) {
                            const chunk = bytes.subarray(i, Math.min(i + chunkSize, bytes.length));
                            binary += String.fromCharCode.apply(null, chunk);
                        }
                        const base64 = btoa(binary);
                        const contentType = response.headers.get('content-type') || 'application/octet-stream';
                        return JSON.stringify({ base64: base64, size: bytes.length, type: contentType });
                    } catch (e) {
                        return JSON.stringify({ error: e.message });
                    }
                })();
                """;

            var cdpParams = JsonSerializer.Serialize(new { expression = script, awaitPromise = true, returnByValue = true });
            var cdpResultJson = await _webView.CallDevToolsProtocolMethodAsync("Runtime.evaluate", cdpParams);

            using var cdpDoc = JsonDocument.Parse(cdpResultJson);

            if (cdpDoc.RootElement.TryGetProperty("exceptionDetails", out var excDetails))
            {
                var excText = excDetails.TryGetProperty("text", out var et) ? et.GetString() ?? "" : "";
                _log.Write("MEDIA_FAILURE", $"stage=JS_FETCH reason=cdp_exception:{excText} url={(url.Length > 80 ? url[..80] : url)}");
                return null;
            }

            if (!cdpDoc.RootElement.TryGetProperty("result", out var resultEl) ||
                !resultEl.TryGetProperty("value", out var valueEl))
            {
                _log.Write("MEDIA_FAILURE", $"stage=JS_FETCH reason=no_value_in_cdp_result url={(url.Length > 80 ? url[..80] : url)}");
                return null;
            }

            var innerJson = valueEl.GetString() ?? "";
            _log.Write("JS_FETCH_CDP_RESULT", $"length={innerJson.Length} preview={(innerJson.Length > 100 ? innerJson[..100] : innerJson)}");

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

            string base64Str = root.TryGetProperty("base64", out var b64Prop) ? (b64Prop.GetString() ?? "") : "";
            if (string.IsNullOrEmpty(base64Str))
            {
                _log.Write("MEDIA_FAILURE", $"stage=JS_FETCH reason=no_base64_field url={(url.Length > 80 ? url[..80] : url)} innerJson={(innerJson.Length > 200 ? innerJson[..200] : innerJson)}");
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
            var header = dataUrl[..commaIndex];
            var base64 = dataUrl[(commaIndex + 1)..];

            if (!header.StartsWith("data:image/")) return null;
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

    private JsonNode? ParseScriptResult(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("\""))
        {
            var innerJson = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(innerJson)) return null;
            return JsonNode.Parse(innerJson);
        }
        return JsonNode.Parse(raw);
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
}