# AIX WhatsApp Local

A standalone Windows application that opens WhatsApp Web and saves photos to a local folder.

## What This Is

- **Standalone** — no cloud, no APIs, no Base44, no StoreAIX, no website integration.
- **Local only** — config, logs, and WebView2 profile are stored in `%LocalAppData%\AIXWhatsAppLocal\`.
- **Persistent session** — WhatsApp QR is only needed once. The WebView2 profile preserves the session.

## Phase 0 (Current)

Minimal app with:
1. **Choose Local Folder** — user selects where photos will go.
2. **Open WhatsApp** — opens WhatsApp Web in a WebView2 window with persistent session.
3. **Status** — shows WhatsApp connection state and selected folder.

### Pass Criteria

1. Install the app on a Windows machine.
2. It opens without the old connector.
3. Choose a folder.
4. Open WhatsApp.
5. Scan QR once if needed.
6. Close the app.
7. Reopen.
8. WhatsApp session is preserved — no QR needed again.
9. Selected folder is preserved.

## Build

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Tech

- .NET 8 / Windows / x64
- WinForms
- Microsoft Edge WebView2