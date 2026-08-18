using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIXWhatsAppLocal;

/// <summary>
/// All JavaScript scripts injected into the WebView2 for WhatsApp Web automation.
/// Extracted from MediaCaptureService for maintainability.
/// </summary>
internal static class MediaCaptureScripts
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
                    var chatRow = badge.closest('[data-testid="cell-frame-container"]') ||
                                  badge.closest('[role="listitem"]') ||
                                  item;
                    
                    // Get name from the same container as the badge
                    var nameEl = chatRow.querySelector('span[title]');
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
            var items = pane.querySelectorAll('[data-testid="cell-frame-container"]');
            if (items.length === 0) items = pane.querySelectorAll('[role="listitem"]');
            if (items.length === 0) items = pane.querySelectorAll('div[data-id]');
            if (items[__INDEX__]) {
                items[__INDEX__].click();
                return JSON.stringify({ clicked: true, selector: items.length > 0 ? 'cell-frame-container' : 'listitem' });
            }
            return JSON.stringify({ clicked: false, reason: 'no_item_at_index', itemCount: items.length });
        })();
        """;

    /// <summary>
    /// ATOMIC detection + click in a single script execution (DOM fallback path).
    /// Includes already-active check and row-only clicking (no descendant clicks).
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

            var rowHtml1 = items.length > 0 ? (items[0].outerHTML || '').substring(0, 2000) : '';
            var rowHtml2 = items.length > 1 ? (items[1].outerHTML || '').substring(0, 2000) : '';
            var rowHtml3 = items.length > 2 ? (items[2].outerHTML || '').substring(0, 2000) : '';

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
                if (!badge) {
                    var allEls2 = item.querySelectorAll('span, div');
                    for (var g = 0; g < allEls2.length; g++) {
                        var gel = allEls2[g];
                        var gt = (gel.textContent || '').trim();
                        if (!/^\d+$/.test(gt) || gt.length > 3) continue;
                        if (gel.offsetWidth <= 0 || gel.offsetWidth > 35) continue;
                        var gst = window.getComputedStyle(gel);
                        var bg = gst.backgroundColor;
                        var m = bg.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)/);
                        if (!m) continue;
                        var r = parseInt(m[1]), gg = parseInt(m[2]), b = parseInt(m[3]);
                        if (gg > r + 30 && gg > b + 30 && gg > 100) {
                            badge = gel; unreadCount = parseInt(gt); break;
                        }
                    }
                }
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

                        markerHtml = (result.badge.outerHTML || '').substring(0, 300);
                        var p = result.badge.parentElement;
                        if (p) { parent1 = (p.outerHTML || '').substring(0, 300); p = p.parentElement; }
                        if (p) { parent2 = (p.outerHTML || '').substring(0, 300); p = p.parentElement; }
                        if (p) { parent3 = (p.outerHTML || '').substring(0, 300); }
                    }
                }
            }

            // === UNREAD_DIAGNOSTIC — dump detailed element info for first 5 rows ===
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
            var unreadHandoffName = name || '';
            var unreadHandoffRowConnected = !!(row && row.isConnected);
            var unreadHandoffBadgeStillPresent = !!(firstUnreadBadge && firstUnreadBadge.isConnected);

            if (!name || !unreadHandoffRowConnected) {
                return JSON.stringify({ clicked: false, reason: 'handoff_failed', name: name || '', clickTargetHtml: '', clickTargetIndex: -1, unreadCount: firstUnreadCount, activeChatBefore: activeChatBefore, activeChatAfter: '', navigationConfirmed: false, clickStrategy: '', clickElementTag: '', clickElementRole: '', clickElementTabindex: '', chatRowsFound: chatRowsFound, unreadMarkersFound: unreadMarkersFound, markerHtml: markerHtml, parent1: parent1, parent2: parent2, parent3: parent3, matchedChatRow: matchedChatRow, matchedChatName: matchedChatName, unreadHandoffName: unreadHandoffName, unreadHandoffRowConnected: unreadHandoffRowConnected, unreadHandoffBadgeStillPresent: unreadHandoffBadgeStillPresent, clickAttempted: false });
            }

            // === ALREADY-ACTIVE CHECK ===
            // If the target chat is ALREADY the active chat, skip the click entirely.
            // Clicking an already-active row can navigate to a different chat.
            if (activeChatBefore && name &&
                (activeChatBefore === name ||
                 (name.length > 2 && activeChatBefore.indexOf(name) >= 0) ||
                 (activeChatBefore.length > 2 && name.indexOf(activeChatBefore) >= 0))) {
                return JSON.stringify({
                    clicked: true, name: name, clickTargetHtml: '', clickTargetIndex: -1,
                    unreadCount: firstUnreadCount, atomicClickTargetName: name,
                    atomicClickConnected: unreadHandoffRowConnected, atomicClickUnreadPresent: unreadHandoffBadgeStillPresent,
                    activeChatBefore: activeChatBefore, activeChatAfter: activeChatBefore,
                    navigationConfirmed: true, clickStrategy: 'already_active',
                    clickElementTag: '', clickElementRole: '', clickElementTabindex: '',
                    chatRowsFound: chatRowsFound, unreadMarkersFound: unreadMarkersFound,
                    markerHtml: markerHtml, parent1: parent1, parent2: parent2, parent3: parent3,
                    matchedChatRow: matchedChatRow, matchedChatName: matchedChatName,
                    unreadHandoffName: unreadHandoffName, unreadHandoffRowConnected: unreadHandoffRowConnected,
                    unreadHandoffBadgeStillPresent: unreadHandoffBadgeStillPresent, clickAttempted: false
                });
            }

            if (firstUnreadCount === 0) {
                var badgeText = (firstUnreadBadge.textContent || '').trim();
                firstUnreadCount = parseInt(badgeText) || 1;
            }

            // === Click ===
            try { row.scrollIntoView({block: 'center'}); } catch(e) {}

            // ALWAYS click the row container itself — never a descendant
            // (IMG/BUTTON/avatar). Clicking a descendant navigates to the wrong chat.
            var clickTarget = row;
            var strategy = 'row_click';

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
            var targetChatId = __CHAT_ID_JSON__;
            var targetName = __TARGET_NAME_JSON__;
            function getHeaderChatId() {
                var main = document.querySelector('#main');
                if (!main) return '';
                var header = main.querySelector('header');
                if (!header) return '';
                var hid = header.getAttribute('data-id') || '';
                if (hid) return hid;
                var idEls = header.querySelectorAll('[data-id]');
                for (var i = 0; i < idEls.length; i++) {
                    var did = idEls[i].getAttribute('data-id') || '';
                    if (did && did.indexOf('@') > 0) return did;
                }
                if (main) {
                    var mainIdEls = main.querySelectorAll('[data-id]');
                    for (var j = 0; j < mainIdEls.length && j < 30; j++) {
                        var mdid = mainIdEls[j].getAttribute('data-id') || '';
                        if (mdid && mdid.indexOf('@') > 0) return mdid;
                    }
                }
                return '';
            }
            function getActiveChatName() {
                var main = document.querySelector('#main');
                if (!main) return '';
                var header = main.querySelector('header');
                if (!header) return '';
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
            var headerChatId = getHeaderChatId();
            var headerName = getActiveChatName();
            var chatIdMatch = false;
            if (targetChatId && headerChatId) {
                chatIdMatch = headerChatId === targetChatId;
            }
            var nameMatch = false;
            if (!chatIdMatch && targetName && headerName) {
                nameMatch = headerName === targetName ||
                    (targetName.length > 3 && headerName.indexOf(targetName) >= 0) ||
                    (headerName.length > 3 && targetName.indexOf(headerName) >= 0);
            }
            var validationMethod = chatIdMatch ? 'chat_id' : (nameMatch ? 'name' : 'failed');
            var navigationConfirmed = chatIdMatch || nameMatch;
            return JSON.stringify({
                activeChatName: headerName,
                headerChatId: headerChatId,
                headerName: headerName,
                validationMethod: validationMethod,
                navigationConfirmed: navigationConfirmed,
                chatIdMatch: chatIdMatch,
                nameMatch: nameMatch
            });
        })();
        """;

    public const string GetCustomerInfo = """
        (() => {
            var main = document.querySelector('#main');
            var mainFound = !!main;
            var mainHtml = main ? (main.outerHTML || '').substring(0, 2500) : '';
            var mainHeaders = main ? main.querySelectorAll('header') : [];
            var mainHeadersFound = mainHeaders.length;

            var header = null;
            if (main) {
                var headersInMain = main.querySelectorAll('header');
                for (var h = 0; h < headersInMain.length; h++) {
                    var testId = headersInMain[h].getAttribute('data-testid') || '';
                    if (testId !== 'chatlist-header') {
                        header = headersInMain[h];
                        break;
                    }
                }
                if (!header) {
                    header = main.querySelector('header[data-testid="conversation-panel-header"]')
                        || main.querySelector('header[data-testid="conversation-header"]')
                        || main.querySelector('header:not([data-testid="chatlist-header"])');
                }
            }
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

            var titleSpans = header.querySelectorAll('span[title]');
            var spanTitles = [];
            for (var i = 0; i < titleSpans.length && i < 10; i++) {
                spanTitles.push(titleSpans[i].getAttribute('title') || '');
            }

            var ariaElements = header.querySelectorAll('[aria-label]');
            var ariaLabels = [];
            for (var j = 0; j < ariaElements.length && j < 10; j++) {
                var label = ariaElements[j].getAttribute('aria-label') || '';
                if (label) ariaLabels.push(label);
            }

            var textCandidates = [];
            var textEls = header.querySelectorAll('span[dir="auto"]');
            for (var k = 0; k < textEls.length && k < 15; k++) {
                var text = (textEls[k].textContent || '').trim();
                if (text && text.length > 0 && text.length < 100) {
                    textCandidates.push(text);
                }
            }

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

            var uiPattern = /^(Back|Menu|Search|Call|Video|Info|Send|Attach|Emoji|Mute|Pin|Archive|Delete|Settings|online|typing|פרטי הפרופיל|פרטים|צ'אטים|צ׳אטים|שיחות|סטטוס|ערוצים|קהילות|מדיה|את\/ה|את\\ה|את\/אתה|WhatsApp|חיפוש|תפריט|שיחה קולית|שיחת וידאו|הודעה|סמן כלא נקרא|הגדרות|יציאה|חזרה|פתח|סגור|בטל|אישור|ערוך|מחק|העתק|שתף|הורד|קדימה|אחורה)/i;

            for (var c = 0; c < textCandidates.length; c++) {
                var candidate = textCandidates[c];
                if (candidate && !uiPattern.test(candidate)) {
                    name = candidate;
                    nameSource = 'text_candidate';
                    break;
                }
            }

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

            if (!phone && main) {
                var labeledEls = main.querySelectorAll('[aria-label]');
                for (var le = 0; le < labeledEls.length && le < 80; le++) {
                    var alText = labeledEls[le].getAttribute('aria-label') || '';
                    if (!alText) continue;
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
                if (local.indexOf('true_') === 0) local = local.substring(5);
                if (local.indexOf('gid_') === 0) return '';
                var digits = local.replace(/\D/g, '');
                return digits.length >= 7 ? digits : '';
            }

            var main = document.querySelector('#main');
            var header = main ? main.querySelector('header') : null;

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

            var pane = document.querySelector('#pane-side');
            if (pane && activeName) {
                var rows = pane.querySelectorAll('[data-testid="cell-frame-container"], [role="listitem"], div[data-id]');
                for (var r = 0; r < rows.length; r++) {
                    var row = rows[r];
                    var titleEl = row.querySelector('span[title]');
                    var rowName = titleEl ? (titleEl.getAttribute('title') || '') : '';
                    if (rowName && activeName &&
                        (rowName === activeName ||
                         (activeName.length > 2 && rowName.indexOf(activeName) >= 0) ||
                         (rowName.length > 2 && activeName.indexOf(rowName) >= 0))) {
                        var rowJid = row.getAttribute('data-id') || '';
                        if (!rowJid) {
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

            if (!phone && main) {
                var labeled = main.querySelectorAll('[aria-label], [title]');
                for (var l = 0; l < labeled.length && l < 50; l++) {
                    var al = labeled[l].getAttribute('aria-label') || '';
                    var ti = labeled[l].getAttribute('title') || '';
                    var lp = extractPhone(al) || extractPhone(ti);
                    if (lp) phoneTextCandidates.push('label:' + (al || ti) + ' -> ' + lp);
                }
            }

            if (!phone) {
                var telLinks = document.querySelectorAll('a[href^="tel:"]');
                for (var h = 0; h < telLinks.length; h++) {
                    var hp = extractPhone(telLinks[h].getAttribute('href') || '');
                    if (hp) phoneTextCandidates.push('tel:' + hp);
                }
            }

            if (!phone && header) {
                try {
                    var contactBtn = header.querySelector('div[role="button"], [data-testid="conversation-info-button"]') || header;
                    contactBtn.click();
                    openedContactPanel = true;
                    await new Promise(function(r) { setTimeout(r, 2500); });

                    var allSpans = document.querySelectorAll('span[dir="auto"], span[title], [aria-label]');
                    for (var sp = 0; sp < allSpans.length && sp < 300; sp++) {
                        var spText = ((allSpans[sp].textContent || '').trim()) || (allSpans[sp].getAttribute('title') || '') || (allSpans[sp].getAttribute('aria-label') || '');
                        var spp = extractPhone(spText);
                        if (spp) {
                            var cand = 'panel:' + spText + ' -> ' + spp;
                            if (phoneTextCandidates.indexOf(cand) < 0) phoneTextCandidates.push(cand);
                        }
                    }

                    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, which: 27, bubbles: true, cancelable: true }));
                    await new Promise(function(r) { setTimeout(r, 500); });
                    var closeBtn = document.querySelector('button[aria-label="Close"], button[aria-label*="סגור" i], [data-testid="close"]');
                    if (closeBtn) closeBtn.click();
                    await new Promise(function(r) { setTimeout(r, 400); });
                } catch(e) {
                    phoneAttrCandidates.push('panel_error:' + (e.message || ''));
                }
            }

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

    public const string ScrollChat = """
        (() => {
            var main = document.querySelector('#main');
            if (!main) return JSON.stringify({ scrolled: false, reason: 'no_main' });

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

            scrollable.scrollTop = scrollable.scrollHeight;

            return JSON.stringify({
                scrolled: true,
                scrollHeight: scrollable.scrollHeight,
                clientHeight: scrollable.clientHeight,
                className: (scrollable.className || '').substring(0, 80)
            });
        })();
        """;

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
                var totalHeight = scrollable.scrollHeight;
                var step = Math.max(300, Math.floor(totalHeight / 15));
                scrollable.scrollTop = totalHeight;
                scrollable.scrollTop = 0;
            }

            return JSON.stringify({ scrolled: true });
        })();
        """;

    public const string DetectImages = """
        (() => {
            const main = document.querySelector('#main');
            if (!main) return JSON.stringify({ images: [], candidates: [], diagnostics: [], mainFound: false, totalImgs: 0, totalVideos: 0, filteredSrc: 0, filteredSize: 0, filteredPlaceholder: 0, filteredDup: 0, filteredPreview: 0, filteredOutgoing: 0, filteredOld: 0, messageGroups: 0 });
            const unreadCount = __UNREAD_COUNT__;
            function getDir(el) {
                var n = el;
                for (var i = 0; i < 15 && n; i++) {
                    var c = (typeof n.className === 'string') ? n.className : '';
                    if (c.indexOf('message-in') >= 0) return 'incoming';
                    if (c.indexOf('message-out') >= 0) return 'outgoing';
                    n = n.parentElement;
                }
                return 'unknown';
            }
            function getMC(el) { return el.closest('[data-testid="msg-container"]') || el.closest('[data-id]') || el.closest('[data-testid="msg-bubble"]'); }
            function getTS(mc) { var t = mc ? mc.querySelector('time[datetime]') : null; return t ? (t.getAttribute('datetime') || '') : ''; }
            var allMC = main.querySelectorAll('[data-testid="msg-container"], div[data-id]');
            var inMC = [], filteredOutgoing = 0;
            allMC.forEach(function(mc) { var d = getDir(mc); if (d === 'incoming') inMC.push(mc); else if (d === 'outgoing') filteredOutgoing++; });
            var newMC = unreadCount > 0 ? inMC.slice(-unreadCount) : inMC.slice(-20);
            var filteredOld = inMC.length - newMC.length;
            var accSet = new Set(newMC);
            const allImgs = main.querySelectorAll('img');
            const imgs = Array.from(allImgs).filter(function(img) { var mc = getMC(img); return mc && accSet.has(mc); });
            const totalImgs = imgs.length;
            var diagnostics = [];
            var totalVideos = main.querySelectorAll('video').length;
            function diagEl(el, type) {
                var s = el.getAttribute('src') || '';
                var st = s.startsWith('blob:') ? 'BLOB' : s.startsWith('data:') ? 'DATA' : s.startsWith('http') ? 'HTTP' : 'OTHER';
                var mc = getMC(el), dir = mc ? getDir(mc) : 'unknown', mid = mc ? (mc.getAttribute('data-id') || '') : '', mts = getTS(mc);
                var ih = !!(el.closest('header')), w = type === 'video' ? (el.videoWidth || el.width || 0) : (el.naturalWidth || el.width || 0), h = type === 'video' ? (el.videoHeight || el.height || 0) : (el.naturalHeight || el.height || 0);
                var inMsg = !!mc, inAcc = mc && accSet.has(mc);
                var acc = inAcc && dir === 'incoming' && st !== 'OTHER' && !s.startsWith('data:image/gif;base64,R0lGODlh') && !(w > 0 && h > 0 && w <= 80 && h <= 80);
                var rej = !inMsg ? (ih ? 'profile_image_or_header' : 'not_message_media') : (dir === 'outgoing' ? 'outgoing_message' : (!inAcc ? 'old_message' : (st === 'OTHER' ? 'invalid_src' : (s.startsWith('data:image/gif;base64,R0lGODlh') ? 'placeholder_gif' : ((w > 0 && h > 0 && w <= 80 && h <= 80) ? 'too_small' : '')))));
                return { type: type, srcType: st, src: s.substring(0, 80), direction: dir, messageId: mid, messageTimestamp: mts, inHeader: ih, width: w, height: h, accepted: acc, rejectReason: rej };
            }
            allImgs.forEach(function(im) { diagnostics.push(diagEl(im, 'image')); });
            main.querySelectorAll('video').forEach(function(vi) { diagnostics.push(diagEl(vi, 'video')); });
            const seen = new Set(), allEntries = [], candidates = [], messageGroups = new Map();
            var filteredSrc = 0, filteredSize = 0, filteredPlaceholder = 0, filteredDup = 0;
            for (const img of imgs) {
                const src = img.getAttribute('src') || '';
                if (!src) { filteredSrc++; continue; }
                if (!src.startsWith('blob:') && !src.startsWith('data:') && !src.startsWith('http')) { filteredSrc++; continue; }
                if (src.startsWith('data:image/gif;base64,R0lGODlh')) { filteredPlaceholder++; continue; }
                let sourceType = src.startsWith('blob:') ? 'BLOB' : src.startsWith('data:') ? 'DATA' : 'HTTP';
                let estBytes = 0;
                if (sourceType === 'DATA') { const ci = src.indexOf(','); if (ci > 0) { const b64 = src.substring(ci + 1); estBytes = Math.floor((b64.length * 3) / 4) - (b64.endsWith('==') ? 2 : (b64.endsWith('=') ? 1 : 0)); } }
                let classification = sourceType === 'DATA' ? (estBytes > 0 && estBytes < 30720 ? 'PREVIEW' : 'UNKNOWN') : 'ORIGINAL';
                const w = img.naturalWidth || img.width || 0, h = img.naturalHeight || img.height || 0;
                if (w > 0 && h > 0 && w <= 80 && h <= 80) { filteredSize++; continue; }
                if (seen.has(src)) { filteredDup++; continue; }
                seen.add(src);
                let mc = getMC(img), msgId = mc ? (mc.getAttribute('data-id') || '') : '';
                if (!msgId) msgId = 'nomsg_' + allEntries.length;
                let dir = mc ? getDir(mc) : 'unknown', mts = getTS(mc);
                const entry = { src: src, source: sourceType, bytes: estBytes, classification: classification, width: w, height: h, messageId: msgId, direction: dir, messageTimestamp: mts };
                allEntries.push(entry);
                candidates.push({ source: sourceType, classification: classification, bytes: estBytes, messageId: msgId, direction: dir, messageTimestamp: mts });
                if (!messageGroups.has(msgId)) messageGroups.set(msgId, []);
                messageGroups.get(msgId).push(entry);
            }
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
            return JSON.stringify({ images: images, candidates: candidates, diagnostics: diagnostics, mainFound: true, totalImgs: totalImgs, totalVideos: totalVideos, filteredSrc: filteredSrc, filteredSize: filteredSize, filteredPlaceholder: filteredPlaceholder, filteredDup: filteredDup, filteredPreview: filteredPreview, filteredOutgoing: filteredOutgoing, filteredOld: filteredOld, messageGroups: messageGroups.size });
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
    /// Already-active check runs BEFORE row matching to avoid clicking an open chat.
    /// Always clicks the row container — never a descendant.
    /// </summary>
    public const string FindAndClickUnreadViaStore = """
        (() => {
            try {
                if (typeof window.require !== 'function') {
                    return JSON.stringify({ clicked: false, reason: 'no_window_require', source: 'store', storeUnreadTotal: 0, storeUnreadChats: [], storeChatCount: 0, chatRowsFound: 0, unreadMarkersFound: 0, name: '', clickTargetHtml: '', clickTargetIndex: -1, unreadCount: 0, activeChatBefore: '', activeChatAfter: '', navigationConfirmed: false, clickStrategy: '', clickElementTag: '', clickElementRole: '', clickElementTabindex: '', unreadHandoffName: '', unreadHandoffRowConnected: false, unreadHandoffBadgeStillPresent: false, clickAttempted: false });
                }

                var chatCollection = window.require('WAWebCollections');
                if (!chatCollection || !chatCollection.Chat) {
                    return JSON.stringify({ clicked: false, reason: 'no_chat_collection', source: 'store', storeUnreadTotal: 0, storeUnreadChats: [], storeChatCount: 0, chatRowsFound: 0, unreadMarkersFound: 0, name: '', clickTargetHtml: '', clickTargetIndex: -1, unreadCount: 0, activeChatBefore: '', activeChatAfter: '', navigationConfirmed: false, clickStrategy: '', clickElementTag: '', clickElementRole: '', clickElementTabindex: '', unreadHandoffName: '', unreadHandoffRowConnected: false, unreadHandoffBadgeStillPresent: false, clickAttempted: false });
                }

                var chats = chatCollection.Chat.getModelsArray();
                var allUnreadChats = [];
                for (var i = 0; i < chats.length; i++) {
                    try {
                        var chat = chats[i];
                        var uc = chat.unreadCount || 0;
                        if (uc > 0) {
                            var lmid = '';
                            try { lmid = (chat.lastReceivedKey && chat.lastReceivedKey._serialized) ? chat.lastReceivedKey._serialized : ''; } catch(e) {}
                            if (!lmid) { try { lmid = String(chat.t || 0); } catch(e) {} }
                            var cid = (chat.id && chat.id._serialized) ? chat.id._serialized : '';
                            allUnreadChats.push({
                                id: cid,
                                name: chat.formattedTitle || chat.name || '',
                                unreadCount: uc,
                                eventKey: cid + '|' + lmid
                            });
                        }
                    } catch (e) {}
                }

                // === DIAGNOSTICS: top 20 chats by timestamp (newest first) ===
                // Proves whether new messages arrive even when unreadCount=0
                // (WhatsApp Web auto-marks messages as read when window is focused).
                var allChatsForDiag = [];
                for (var di = 0; di < chats.length; di++) {
                    try {
                        var dc = chats[di];
                        var dcId = (dc.id && dc.id._serialized) ? dc.id._serialized : '';
                        var dcName = dc.formattedTitle || dc.name || '';
                        var dcUnread = dc.unreadCount || 0;
                        var dcT = 0; try { dcT = dc.t || 0; } catch(e) {}
                        var dcLmid = ''; try { dcLmid = (dc.lastReceivedKey && dc.lastReceivedKey._serialized) ? dc.lastReceivedKey._serialized : ''; } catch(e) {}
                        if (!dcLmid) { try { dcLmid = (dc.lastMessage && dc.lastMessage.id && dc.lastMessage.id._serialized) ? dc.lastMessage.id._serialized : ''; } catch(e) {} }
                        if (!dcLmid) { try { dcLmid = (dc.msgs && dc.msgs.last && dc.msgs.last().id && dc.msgs.last().id._serialized) ? dc.msgs.last().id._serialized : ''; } catch(e) {} }
                        if (!dcLmid) { try { dcLmid = String(dc.t || 0); } catch(e) {} }
                        var dcMuted = false; try { dcMuted = !!(dc.mute && dc.mute.isMuted); } catch(e) {}
                        var dcArchived = false; try { dcArchived = !!dc.archive; } catch(e) {}
                        allChatsForDiag.push({ id: dcId, name: dcName, unreadCount: dcUnread, lastMessageId: dcLmid, t: dcT, muted: dcMuted, archived: dcArchived });
                    } catch(e) {}
                }
                allChatsForDiag.sort(function(a, b) { return (b.t || 0) - (a.t || 0); });
                var storeTopChats = allChatsForDiag.slice(0, 20);
                var storeAllChatLastMsgs = allChatsForDiag.slice(0, 50).map(function(c) {
                    return { id: c.id, lastMessageId: c.lastMessageId, name: c.name, unreadCount: c.unreadCount, t: c.t };
                });

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

                var excludeKeys = __EXCLUDE_NAMES__;
                var unreadChats = allUnreadChats.filter(function(c) {
                    var k = c.eventKey || '';
                    return k && excludeKeys.indexOf(k) < 0;
                });

                var pane = document.querySelector('#pane-side');
                var domRows = pane ? pane.querySelectorAll('[data-testid="cell-frame-container"], [role="listitem"], div[data-id]') : [];
                var chatRowsFound = domRows.length;

                if (unreadChats.length === 0) {
                    var noUnreadReason = allUnreadChats.length > 0 ? 'no_new_unread_all_processed' : 'no_unread_in_store';
                    return JSON.stringify({ clicked: false, reason: noUnreadReason, source: 'store', storeUnreadTotal: 0, allUnreadTotal: allUnreadChats.length, storeUnreadChats: [], storeChatCount: chats.length, storeTopChats: storeTopChats, storeAllChatLastMsgs: storeAllChatLastMsgs, chatRowsFound: chatRowsFound, unreadMarkersFound: 0, name: '', clickTargetHtml: '', clickTargetIndex: -1, unreadCount: 0, activeChatBefore: activeChatBefore, activeChatAfter: '', navigationConfirmed: false, clickStrategy: '', clickElementTag: '', clickElementRole: '', clickElementTabindex: '', unreadHandoffName: '', unreadHandoffRowConnected: false, unreadHandoffBadgeStillPresent: false, clickAttempted: false });
                }

                var targetChatId = unreadChats[0].id || '';
                var targetName = unreadChats[0].name;

                // === ALREADY-ACTIVE CHECK (before row matching) ===
                // If the target chat is ALREADY the active chat, skip the click entirely.
                // Clicking an already-active row can cause WhatsApp to navigate to a
                // different chat. The chat is already open — proceed to media scanning.
                if (activeChatBefore && targetName &&
                    (activeChatBefore === targetName ||
                     (targetName.length > 2 && activeChatBefore.indexOf(targetName) >= 0) ||
                     (activeChatBefore.length > 2 && targetName.indexOf(activeChatBefore) >= 0))) {
                    return JSON.stringify({
                        clicked: true, source: 'store', name: targetName,
                        eventKey: unreadChats[0].eventKey, chatId: unreadChats[0].id,
                        storeUnreadTotal: unreadChats.length, storeUnreadChats: unreadChats, storeChatCount: chats.length,
                        storeTopChats: storeTopChats, storeAllChatLastMsgs: storeAllChatLastMsgs,
                        chatRowsFound: chatRowsFound, unreadMarkersFound: unreadChats.length,
                        clickTargetHtml: '', clickTargetIndex: -1, unreadCount: unreadChats[0].unreadCount,
                        atomicClickTargetName: targetName, atomicClickConnected: true, atomicClickUnreadPresent: true,
                        activeChatBefore: activeChatBefore, activeChatAfter: activeChatBefore,
                        navigationConfirmed: true, clickStrategy: 'already_active',
                        clickElementTag: '', clickElementRole: '', clickElementTabindex: '',
                        unreadHandoffName: targetName, unreadHandoffRowConnected: true, unreadHandoffBadgeStillPresent: true,
                        clickAttempted: false
                    });
                }

                var clickedRow = null;
                var clickedIndex = -1;
                var resolvedRowName = '';

                // 1. Match by chat ID (JID) — most reliable.
                if (targetChatId) {
                    for (var r = 0; r < domRows.length; r++) {
                        var rowJid = domRows[r].getAttribute('data-id') || '';
                        if (!rowJid) {
                            var childWithId = domRows[r].querySelector('[data-id]');
                            if (childWithId) rowJid = childWithId.getAttribute('data-id') || '';
                        }
                        if (rowJid && rowJid === targetChatId) {
                            clickedRow = domRows[r];
                            clickedIndex = r;
                            var tEl = domRows[r].querySelector('span[title]');
                            resolvedRowName = tEl ? (tEl.getAttribute('title') || '') : '';
                            break;
                        }
                    }
                }

                // 2. Fallback: match by name (fuzzy)
                if (!clickedRow) {
                    for (var r2 = 0; r2 < domRows.length; r2++) {
                        var titleEl = domRows[r2].querySelector('span[title]');
                        var rowName = titleEl ? (titleEl.getAttribute('title') || '') : '';
                        if (rowName && targetName &&
                            (rowName === targetName ||
                             (targetName.length > 2 && rowName.indexOf(targetName) >= 0) ||
                             (rowName.length > 2 && targetName.indexOf(rowName) >= 0))) {
                            clickedRow = domRows[r2];
                            clickedIndex = r2;
                            resolvedRowName = rowName;
                            break;
                        }
                    }
                }

                if (!clickedRow) {
                    return JSON.stringify({ clicked: false, reason: 'row_not_found', source: 'store', storeUnreadTotal: unreadChats.length, storeUnreadChats: unreadChats, storeChatCount: chats.length, storeTopChats: storeTopChats, storeAllChatLastMsgs: storeAllChatLastMsgs, chatRowsFound: chatRowsFound, unreadMarkersFound: unreadChats.length, name: targetName, clickTargetHtml: '', clickTargetIndex: -1, unreadCount: unreadChats[0].unreadCount, activeChatBefore: activeChatBefore, activeChatAfter: '', navigationConfirmed: false, clickStrategy: '', clickElementTag: '', clickElementRole: '', clickElementTabindex: '', unreadHandoffName: targetName, unreadHandoffRowConnected: false, unreadHandoffBadgeStillPresent: false, clickAttempted: false });
                }

                try { clickedRow.scrollIntoView({block: 'center'}); } catch(e) {}

                // ALWAYS click the row container itself — never a descendant.
                var clickTarget = clickedRow;
                var strategy = 'row_click';

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

                return JSON.stringify({
                    clicked: true, source: 'store', name: targetName,
                    eventKey: unreadChats[0].eventKey, chatId: unreadChats[0].id,
                    storeUnreadTotal: unreadChats.length, storeUnreadChats: unreadChats, storeChatCount: chats.length,
                    storeTopChats: storeTopChats, storeAllChatLastMsgs: storeAllChatLastMsgs,
                    chatRowsFound: chatRowsFound, unreadMarkersFound: unreadChats.length,
                    clickTargetHtml: (clickedRow.outerHTML || '').substring(0, 300),
                    clickTargetIndex: clickedIndex, unreadCount: unreadChats[0].unreadCount,
                    atomicClickTargetName: targetName, atomicClickConnected: true, atomicClickUnreadPresent: true,
                    targetChatId: targetChatId, resolvedRowName: resolvedRowName, rowClicked: true,
                    activeChatBefore: activeChatBefore, activeChatAfter: activeChatBefore,
                    navigationConfirmed: false, clickStrategy: strategy,
                    clickElementTag: clickElementTag, clickElementRole: clickElementRole, clickElementTabindex: clickElementTabindex,
                    unreadHandoffName: targetName, unreadHandoffRowConnected: true, unreadHandoffBadgeStillPresent: true,
                    clickAttempted: true
                });
            } catch (e) {
                return JSON.stringify({ clicked: false, reason: 'exception: ' + e.message, source: 'store', storeUnreadTotal: 0, storeUnreadChats: [], storeChatCount: 0, chatRowsFound: 0, unreadMarkersFound: 0, name: '', clickTargetHtml: '', clickTargetIndex: -1, unreadCount: 0, activeChatBefore: '', activeChatAfter: '', navigationConfirmed: false, clickStrategy: '', clickElementTag: '', clickElementRole: '', clickElementTabindex: '', unreadHandoffName: '', unreadHandoffRowConnected: false, unreadHandoffBadgeStillPresent: false, clickAttempted: false });
            }
        })();
        """;

    /// <summary>
    /// Opens a chat by chatId (JID) — used when lastMessageId changed but unreadCount=0.
    /// Already-active check + row-only click (same as FindAndClickUnreadViaStore).
    /// </summary>
    public const string OpenChatByChatId = """
        (() => {
            var targetChatId = __CHAT_ID_JSON__;
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
            var pane = document.querySelector('#pane-side');
            if (!pane) return JSON.stringify({ clicked: false, reason: 'no_pane', name: '', chatId: targetChatId, activeChatBefore: activeChatBefore, activeChatAfter: '', navigationConfirmed: false, clickStrategy: '' });

            var domRows = pane.querySelectorAll('[data-testid="cell-frame-container"], [role="listitem"], div[data-id]');
            var clickedRow = null;

            for (var r = 0; r < domRows.length; r++) {
                var rowJid = domRows[r].getAttribute('data-id') || '';
                if (!rowJid) {
                    var childWithId = domRows[r].querySelector('[data-id]');
                    if (childWithId) rowJid = childWithId.getAttribute('data-id') || '';
                }
                if (rowJid && rowJid === targetChatId) {
                    clickedRow = domRows[r];
                    break;
                }
            }

            if (!clickedRow) return JSON.stringify({ clicked: false, reason: 'row_not_found', name: '', chatId: targetChatId, activeChatBefore: activeChatBefore, activeChatAfter: '', navigationConfirmed: false, clickStrategy: '' });

            var titleEl = clickedRow.querySelector('span[title]');
            var name = titleEl ? (titleEl.getAttribute('title') || '') : '';

            if (activeChatBefore && name &&
                (activeChatBefore === name ||
                 (name.length > 2 && activeChatBefore.indexOf(name) >= 0) ||
                 (activeChatBefore.length > 2 && name.indexOf(activeChatBefore) >= 0))) {
                return JSON.stringify({ clicked: true, name: name, chatId: targetChatId, activeChatBefore: activeChatBefore, activeChatAfter: activeChatBefore, navigationConfirmed: true, clickStrategy: 'already_active' });
            }

            try { clickedRow.scrollIntoView({block: 'center'}); } catch(e) {}

            var clickTarget = clickedRow;
            var clickElementTag = clickTarget.tagName;
            var clickElementRole = clickTarget.getAttribute('role') || '';

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

            return JSON.stringify({ clicked: true, name: name, chatId: targetChatId, activeChatBefore: activeChatBefore, activeChatAfter: '', navigationConfirmed: false, clickStrategy: 'row_click', clickElementTag: clickElementTag, clickElementRole: clickElementRole });
        })();
        """;
}