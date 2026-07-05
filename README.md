# AI Chat Client — WinForms UserControl (WebView2)

A ChatGPT/Claude-Desktop-style, multi-provider AI chat client packaged as a **C# WinForms
UserControl** targeting **.NET Framework 4.8**. The UI is a web frontend rendered inside
WebView2; all provider calls, streaming, persistence and secrets live in C#.

```
WebView2 (UI) → C# (provider call) → API → C# (parse/stream) → WebView2 (render)
```

## Solution layout

```
AiChatClient.sln
└─ src/
   ├─ AiChat.Control/            the embeddable UserControl (class library)
   │  ├─ ChatControl.cs          WebView2 host, JS bridge dispatcher, orchestration
   │  ├─ Bridge/                 command envelope + whitelist/schema validation
   │  ├─ Providers/              IAiProvider + Anthropic, OpenAI, DeepSeek, Gemini, Ollama
   │  ├─ Data/                   SQLite (Microsoft.Data.Sqlite) + FTS5 search + branching
   │  ├─ Security/               DPAPI-encrypted API-key store
   │  ├─ Services/               cost estimator, Markdown/JSON exporter + importer
   │  └─ WebAssets/              index.html / app.css / app.js (served via app.local)
   └─ AiChat.DemoApp/            minimal WinForms host showing how to embed the control
```

## Requirements

- Windows 10/11 with the **WebView2 Evergreen Runtime** installed
  (preinstalled on Win 11; installer: https://developer.microsoft.com/microsoft-edge/webview2/)
- Visual Studio 2019 16.8+ or 2022, or the .NET SDK (`dotnet build` works for net48
  SDK-style projects on Windows)
- .NET Framework 4.8 developer pack

## Build & run

```
git clone <this repo>
cd AiChatClient
dotnet restore
dotnet build -c Release
dotnet run --project src/AiChat.DemoApp
```

or open `AiChatClient.sln` in Visual Studio, set **AiChat.DemoApp** as startup, F5.

First run: open **Settings** (bottom of the sidebar), paste an API key for Anthropic,
OpenAI, DeepSeek and/or Gemini, pick a model in the header, chat. Ollama needs no key —
just have it running on `localhost:11434`.

## Embedding in your own app

```csharp
var chat = new AiChat.Control.ChatControl
{
    // Optional: where the SQLite db, encrypted keys and WebView2 profile live.
    // Defaults to %LocalAppData%\AiChatControl.
    DataDirectory = Path.Combine(appDataDir, "AiChat")
};
myPanel.Controls.Add(chat);   // that's it — Dock = Fill by default
```

## Architecture decisions (mirrors the spec)

| Decision | Where |
|---|---|
| Provider calls only in C#; API key never reaches JS/DOM | `ChatControl` + `Providers/*`; keys attached as HTTP headers in C# only |
| Bridge via `PostWebMessageAsJson` + `WebMessageReceived` (host objects disabled) | `ChatControl.OnWebMessageReceived`, `Settings.AreHostObjectsAllowed = false` |
| Assets over `https://app.local` via `SetVirtualHostNameToFolderMapping` | `ChatControl.InitializeAsync` |
| SQLite persistence in the host, FTS5 full-text search, session branching | `Data/ChatDatabase.cs` |
| DPAPI (CurrentUser) key storage; keys never in SQLite or logs, never echoed to JS | `Security/SecretStore.cs` |
| One normalized `ChatDelta` stream shape across all providers; tool-call blocks carried now for future MCP-style tools | `Models/ChatModels.cs`, each provider's `MapEvent` |
| Cancel = one `CancellationTokenSource` per in-flight request wired to the Stop button | `ChatControl._inFlight` |
| Retry-with-backoff on 429/5xx, surfaced in the UI as a toast, honouring `Retry-After` | `Providers/ProviderBase.SendWithRetryAsync` |
| Whitelisted JS→C# commands with strict payload validation; JS can never pass a file path | `Bridge/BridgeProtocol.cs`; file paths come only from native dialogs |
| Markdown sanitized with DOMPurify before DOM injection | `app.js renderMarkdown` |
| Sticky code-block action header (`position: sticky; top: 0` against the chat scroller), no scroll listeners | `app.css .code-head` |
| Message-ID ↔ DOM-anchor map maintained with an IntersectionObserver | `app.js anchorMap` / `io` |
| Text downloads via Blob + `<a download>` in JS with URL revocation; binary/native saves via `SaveFileDialog` in C# | `app.js` code-block actions, `ChatControl.HandleSaveBinaryFile` |
| Generated-file convention: ` ```python:script.py ` or `<file name="...">` | `app.js` marked code renderer |

## Feature checklist

- Token-by-token streaming (`{type:"delta", sessionId, text}` WebMessages), Stop, Regenerate,
  Edit-&-resend (fork in place), Branch (duplicate up to any message)
- **Thinking / reasoning control** in the header, shown only when the selected model supports
  it: Claude models get Off / Adaptive / Low / Medium / High (extended thinking with
  `budget_tokens`, adaptive with `display: summarized`); DeepSeek reasoning models get
  Off / Low / Medium / High (`reasoning_effort` + `{"thinking":{"type":"enabled"}}`).
  Streamed reasoning renders in a collapsible "Thinking" block above the answer and is
  persisted with the message.
- **Run code locally**: code blocks in a configured language get a ▶ Run button; the host
  writes the code to a temp file and executes the per-language command template
  (Settings → Run commands, `{file}` placeholder), streaming stdout/stderr into a panel
  under the block with Stop, a configurable timeout, process-tree kill, and an optional
  confirmation prompt.
- **Anthropic Files API uploads**: attachments are uploaded once via `POST /v1/files`
  (`anthropic-beta: files-api-2025-04-14`) and referenced as
  `{"type":"document","source":{"type":"file","file_id":…}}` (images use `"type":"image"`),
  with automatic inline-base64 fallback if an upload fails.
- Assistant messages end with an always-reachable action row — Copy · Regenerate · Branch
  here (Regenerate appears on the newest answer only).
- Code-block header (language · filename · Run/Copy/Download/Save-as) is `position: sticky`
  against the chat scroller, so the buttons follow while you scroll through a long block and
  disappear once its end passes.
- Sessions: list / rename / delete / pin / tags, FTS5 content search, auto-titling
- Per-session provider+model, per-message override, model lists from `/models` with static fallback
- Syntax highlighting (highlight.js), per-block Download / native Save-as
- Attachments (drag-drop or picker): images & PDFs to vision-capable models; inline image
  rendering by MIME type with a lightbox
- System prompt per session + reusable persona templates (defaults included)
- Token & cost estimate per message and running per-session total (provider-reported usage
  when available, chars÷4 heuristic otherwise)
- Error / rate-limit handling with visible retry countdowns
- Export Markdown / JSON, full JSON backup import; dark & light themes; code font-size control

## Notes & extension points

- **Frontend libraries** (`marked`, `DOMPurify`, `highlight.js`) load from cdnjs with pinned
  versions. For fully offline deployments, download those three files into
  `WebAssets/vendor/` and point the `<script>`/`<link>` tags in `index.html` at them —
  no other change needed.
- **Tool execution / MCP** is deliberately deferred (spec §5): `ChatRequest.Tools` and the
  `ToolCallStart/ToolCallDelta` delta types already flow end-to-end, and the UI surfaces
  attempted tool calls, so execution can be added without breaking changes.
- **Adding a provider**: implement `IAiProvider` (or subclass `OpenAiCompatibleProvider`
  if the API is OpenAI-shaped) and register it in `ProviderRegistry`.
- **Prompt inspector / plugins / embeddings memory** remain future work per spec §5.
