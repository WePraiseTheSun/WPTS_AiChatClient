using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using AiChat.Control.Bridge;
using AiChat.Control.Data;
using AiChat.Control.Models;
using AiChat.Control.Providers;
using AiChat.Control.Security;
using AiChat.Control.Services;

namespace AiChat.Control
{
    /// <summary>
    /// Embeddable AI chat client (spec §1). WebView2 renders the UI; all provider calls,
    /// streaming, persistence and secrets live on the C# side. Data flow:
    /// WebView2 (UI) → C# (provider call) → API → C# (parse/stream) → WebView2 (render).
    /// </summary>
    public partial class ChatControl : UserControl
    {
        private const string VirtualHost = "app.local";

        private readonly WebView2 _webView = new WebView2();
        private ChatDatabase _db;
        private SecretStore _secrets;
        private ProviderRegistry _providers;
        private CostEstimator _cost;
        private ConversationExporter _exporter;
        private CodeRunner _runner;
        private string _dataDirectory;
        private bool _initialized;

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>One cancellation source per in-flight generation, keyed by session (spec §3.1).</summary>
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight =
            new ConcurrentDictionary<string, CancellationTokenSource>();

        private const long MaxAttachmentBytes = 20 * 1024 * 1024;

        public ChatControl()
        {
            Dock = DockStyle.Fill;
            _webView.Dock = DockStyle.Fill;
            _webView.DefaultBackgroundColor = Color.FromArgb(0xFA, 0xF9, 0xF5);
            Controls.Add(_webView);
        }

        /// <summary>
        /// Optional: set before the control is shown to control where the SQLite db, secrets
        /// and WebView2 profile live. Defaults to %LocalAppData%\AiChatControl.
        /// </summary>
        public string DataDirectory
        {
            get => _dataDirectory;
            set { if (!_initialized) _dataDirectory = value; }
        }

        protected override async void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (DesignMode || _initialized) return;
            _initialized = true;

            try
            {
                await InitializeAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to initialize the chat control:\n" + ex.Message,
                    "AI Chat", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task InitializeAsync()
        {
            _dataDirectory = _dataDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiChatControl");
            Directory.CreateDirectory(_dataDirectory);

            _db = new ChatDatabase(_dataDirectory);
            _secrets = new SecretStore(_dataDirectory);
            _providers = new ProviderRegistry(_secrets);
            _cost = new CostEstimator();
            _exporter = new ConversationExporter(_db);
            _runner = new CodeRunner(_dataDirectory);

            foreach (var p in _providers.All.OfType<ProviderBase>())
            {
                var provider = p;
                provider.OnRetry += (attempt, delay, reason) =>
                    PostEvent("retrying", new { attempt, delaySeconds = (int)delay.TotalSeconds, reason });
            }

            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(_dataDirectory, "WebView2Profile"));
            await _webView.EnsureCoreWebView2Async(env);

            var core = _webView.CoreWebView2;

            // Serve local assets over https://app.local instead of file:// (spec §2).
            string assets = Path.Combine(Path.GetDirectoryName(typeof(ChatControl).Assembly.Location) ?? ".", "WebAssets");
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, assets, CoreWebView2HostResourceAccessKind.Allow);

            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreHostObjectsAllowed = false; // bridge is postMessage only (spec §2)

            core.WebMessageReceived += OnWebMessageReceived;

            // Links to the outside world open in the default browser, not inside the control.
            core.NewWindowRequested += (s, args) =>
            {
                args.Handled = true;
                TryOpenExternal(args.Uri);
            };

            core.Navigate($"https://{VirtualHost}/index.html");
        }

        // =====================================================================
        // Bridge: JS → C#
        // =====================================================================

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string json;
            try { json = e.WebMessageAsJson; }
            catch { return; }

            if (!BridgeCommand.TryParse(json, out var cmd, out string parseError))
            {
                PostEvent("error", new { message = parseError });
                return;
            }

            _ = DispatchAsync(cmd);
        }

        private async Task DispatchAsync(BridgeCommand cmd)
        {
            try
            {
                switch (cmd.Cmd)
                {
                    case "init": await HandleInit(); break;

                    case "listSessions": PostEvent("sessions", new { sessions = _db.ListSessions() }); break;
                    case "createSession": HandleCreateSession(cmd); break;
                    case "renameSession": HandleRenameSession(cmd); break;
                    case "deleteSession": HandleDeleteSession(cmd); break;
                    case "pinSession": HandlePinSession(cmd); break;
                    case "tagSession": HandleTagSession(cmd); break;
                    case "loadSession": HandleLoadSession(cmd); break;
                    case "setSystemPrompt": HandleSetSystemPrompt(cmd); break;
                    case "setSessionModel": HandleSetSessionModel(cmd); break;

                    case "sendMessage": await HandleSendMessage(cmd); break;
                    case "stopGeneration": HandleStop(cmd); break;
                    case "regenerate": await HandleRegenerate(cmd); break;
                    case "editAndResend": await HandleEditAndResend(cmd); break;
                    case "branchSession": HandleBranch(cmd); break;

                    case "search": HandleSearch(cmd); break;
                    case "listModels": await HandleListModels(cmd); break;

                    case "saveApiKey": HandleSaveApiKey(cmd); break;
                    case "deleteApiKey": HandleDeleteApiKey(cmd); break;

                    case "getSettings": HandleGetSettings(); break;
                    case "setSetting": HandleSetSetting(cmd); break;

                    case "listTemplates": HandleListTemplates(); break;
                    case "saveTemplate": HandleSaveTemplate(cmd); break;
                    case "deleteTemplate": HandleDeleteTemplate(cmd); break;

                    case "exportSession": HandleExportSession(cmd); break;
                    case "importSession": HandleImportSession(); break;

                    case "saveBinaryFile": HandleSaveBinaryFile(cmd); break;
                    case "pickAttachment": HandlePickAttachment(cmd); break;

                    case "runCode": await HandleRunCode(cmd); break; // output streams via runOutput events
                    case "stopRun": _runner.Stop(cmd.GetId("runId")); break;

                    case "openExternal": TryOpenExternal(cmd.GetString("url", 2048)); break;
                }
            }
            catch (BridgeValidationException vex)
            {
                PostEvent("error", new { requestId = cmd.RequestId, message = "Invalid request: " + vex.Message });
            }
            catch (Exception ex)
            {
                PostEvent("error", new { requestId = cmd.RequestId, message = ex.Message });
            }
        }

        // =====================================================================
        // Bootstrap / sessions
        // =====================================================================

        private async Task HandleInit()
        {
            var providerStates = _providers.All.Select(p => new
            {
                name = p.Name,
                requiresApiKey = p.RequiresApiKey,
                hasApiKey = !p.RequiresApiKey || _secrets.HasApiKey(p.Name)
            }).ToList();

            PostEvent("bootstrap", new
            {
                sessions = _db.ListSessions(),
                providers = providerStates,
                settings = ReadUiSettings(),
                templates = ReadTemplates()
            });

            // Warm the model catalog for providers that already have a key (or need none).
            foreach (var p in _providers.All.Where(p => !p.RequiresApiKey || _secrets.HasApiKey(p.Name)))
            {
                try
                {
                    var models = (await p.ListModelsAsync()).ToList();
                    _cost.UpdateCatalog(models);
                    PostEvent("models", new { provider = p.Name, models });
                }
                catch { /* provider offline — UI falls back to manual model entry */ }
            }
        }

        private void HandleCreateSession(BridgeCommand cmd)
        {
            var session = new ChatSession
            {
                Provider = cmd.GetString("provider", 64, required: false) ?? "anthropic",
                Model = cmd.GetString("model", 128, required: false),
                SystemPrompt = cmd.GetString("systemPrompt", 100_000, required: false) ?? ""
            };
            _db.UpsertSession(session);
            PostEvent("sessionCreated", new { session });
            PostEvent("sessions", new { sessions = _db.ListSessions() });
        }

        private void HandleRenameSession(BridgeCommand cmd)
        {
            var s = RequireSession(cmd.GetId("sessionId"));
            s.Title = cmd.GetString("title", 300);
            _db.UpsertSession(s);
            PostEvent("sessions", new { sessions = _db.ListSessions() });
        }

        private void HandleDeleteSession(BridgeCommand cmd)
        {
            string id = cmd.GetId("sessionId");
            CancelGeneration(id);
            _db.DeleteSession(id);
            PostEvent("sessionDeleted", new { sessionId = id });
            PostEvent("sessions", new { sessions = _db.ListSessions() });
        }

        private void HandlePinSession(BridgeCommand cmd)
        {
            var s = RequireSession(cmd.GetId("sessionId"));
            s.Pinned = cmd.GetBool("pinned");
            _db.UpsertSession(s);
            PostEvent("sessions", new { sessions = _db.ListSessions() });
        }

        private void HandleTagSession(BridgeCommand cmd)
        {
            var s = RequireSession(cmd.GetId("sessionId"));
            s.Tags = cmd.GetString("tags", 500, required: false) ?? "";
            _db.UpsertSession(s);
            PostEvent("sessions", new { sessions = _db.ListSessions() });
        }

        private void HandleLoadSession(BridgeCommand cmd)
        {
            string id = cmd.GetId("sessionId");
            var session = RequireSession(id);
            var messages = _db.GetMessages(id, includeAttachmentData: true)
                .Select(m => ToMessageDto(m, id)).ToList();
            PostEvent("sessionLoaded", new { session, messages });
        }

        private void HandleSetSystemPrompt(BridgeCommand cmd)
        {
            var s = RequireSession(cmd.GetId("sessionId"));
            s.SystemPrompt = cmd.GetString("systemPrompt", 100_000, required: false) ?? "";
            _db.UpsertSession(s);
            PostEvent("systemPromptSaved", new { sessionId = s.Id });
        }

        private void HandleSetSessionModel(BridgeCommand cmd)
        {
            var s = RequireSession(cmd.GetId("sessionId"));
            s.Provider = cmd.GetString("provider", 64);
            s.Model = cmd.GetString("model", 128);
            _db.UpsertSession(s);
            PostEvent("sessions", new { sessions = _db.ListSessions() });
        }

        private void HandleBranch(BridgeCommand cmd)
        {
            var branch = _db.BranchSession(
                cmd.GetId("sessionId"),
                cmd.GetId("messageId", required: false));
            if (branch == null) throw new BridgeValidationException("Session not found.");
            PostEvent("sessionBranched", new { session = branch });
            PostEvent("sessions", new { sessions = _db.ListSessions() });
        }

        // =====================================================================
        // Chat / streaming (spec §3.1)
        // =====================================================================

        private async Task HandleSendMessage(BridgeCommand cmd)
        {
            string sessionId = cmd.GetId("sessionId");
            var session = RequireSession(sessionId);
            string text = cmd.GetString("text", 500_000, required: false) ?? "";

            // Per-message model override (spec §3.6).
            string provider = cmd.GetString("provider", 64, required: false) ?? session.Provider;
            string model = cmd.GetString("model", 128, required: false) ?? session.Model;

            var attachments = ParseAttachments(cmd.GetArray("attachments"));
            if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0) return;

            var userMessage = new ChatMessage
            {
                SessionId = sessionId,
                Role = ChatRole.User,
                Content = text,
                Tokens = CostEstimator.EstimateTokens(text),
                Attachments = attachments
            };
            _db.InsertMessage(userMessage);

            // Auto-title new chats from the first user message.
            if (session.Title == "New chat" && !string.IsNullOrWhiteSpace(text))
            {
                session.Title = text.Length > 48 ? text.Substring(0, 48).TrimEnd() + "…" : text;
                _db.UpsertSession(session);
                PostEvent("sessions", new { sessions = _db.ListSessions() });
            }

            PostEvent("userMessageSaved", new { sessionId, message = ToMessageDto(userMessage, sessionId) });

            await StreamAssistantReplyAsync(session, provider, model, ParseThinking(cmd));
        }

        /// <summary>Reads flat thinking fields from the payload; null when off/absent.</summary>
        private static ThinkingOptions ParseThinking(BridgeCommand cmd)
        {
            string mode = cmd.GetString("thinkingMode", 16, required: false);
            if (string.IsNullOrEmpty(mode) || mode == "off") return null;
            if (mode != "adaptive" && mode != "enabled") return null;

            string effort = cmd.GetString("thinkingEffort", 16, required: false);
            if (effort != "low" && effort != "medium" && effort != "high") effort = "medium";

            return new ThinkingOptions { Mode = mode, Effort = effort };
        }

        private async Task HandleRegenerate(BridgeCommand cmd)
        {
            string sessionId = cmd.GetId("sessionId");
            var session = RequireSession(sessionId);

            var messages = _db.GetMessages(sessionId, includeAttachmentData: false);
            var lastAssistant = messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            if (lastAssistant != null)
                _db.DeleteMessageAndAfter(sessionId, lastAssistant.Id, inclusive: true);

            PostEvent("historyTruncated", new { sessionId, fromMessageId = lastAssistant?.Id });
            await StreamAssistantReplyAsync(session, session.Provider,
                cmd.GetString("model", 128, required: false) ?? session.Model, ParseThinking(cmd));
        }

        /// <summary>Edit a past user message and fork the conversation from that point (spec §3.1).</summary>
        private async Task HandleEditAndResend(BridgeCommand cmd)
        {
            string sessionId = cmd.GetId("sessionId");
            string messageId = cmd.GetId("messageId");
            string newText = cmd.GetString("text", 500_000);
            var session = RequireSession(sessionId);

            _db.UpdateMessageContent(messageId, newText);
            _db.DeleteMessageAndAfter(sessionId, messageId, inclusive: false);

            PostEvent("historyTruncated", new { sessionId, fromMessageId = messageId, editedText = newText });
            await StreamAssistantReplyAsync(session, session.Provider, session.Model, ParseThinking(cmd));
        }

        private async Task StreamAssistantReplyAsync(
            ChatSession session, string providerName, string model, ThinkingOptions thinking = null)
        {
            string sessionId = session.Id;

            if (!_providers.TryGet(providerName, out var provider))
            {
                PostEvent("streamError", new { sessionId, message = $"Unknown provider '{providerName}'." });
                return;
            }
            if (string.IsNullOrWhiteSpace(model))
            {
                PostEvent("streamError", new { sessionId, message = "No model selected for this session." });
                return;
            }

            // Only one generation per session at a time.
            CancelGeneration(sessionId);
            var cts = new CancellationTokenSource();
            _inFlight[sessionId] = cts;

            var assistantMessage = new ChatMessage
            {
                SessionId = sessionId,
                Role = ChatRole.Assistant,
                Model = model
            };

            PostEvent("streamStart", new { sessionId, messageId = assistantMessage.Id, model, provider = providerName });

            var history = _db.GetMessages(sessionId); // includes attachment bytes for vision
            var request = new ChatRequest
            {
                Model = model,
                SystemPrompt = session.SystemPrompt,
                Messages = history,
                Thinking = thinking
            };

            var buffer = new System.Text.StringBuilder();
            var thinkingBuffer = new System.Text.StringBuilder();
            int? inputTokens = null, outputTokens = null;
            string failure = null;
            bool cancelled = false;

            try
            {
                await Task.Run(async () =>
                {
                    await foreach (var delta in provider.StreamChatAsync(request, cts.Token).ConfigureAwait(false))
                    {
                        switch (delta.Type)
                        {
                            case ChatDeltaType.Text:
                                buffer.Append(delta.Text);
                                PostEvent("delta", new { type = "delta", sessionId, messageId = assistantMessage.Id, text = delta.Text });
                                break;

                            case ChatDeltaType.Thinking:
                                thinkingBuffer.Append(delta.Text);
                                PostEvent("thinkingDelta", new { sessionId, messageId = assistantMessage.Id, text = delta.Text });
                                break;

                            case ChatDeltaType.ToolCallStart:
                            case ChatDeltaType.ToolCallDelta:
                                // Tool execution is deferred (spec §5); surface it so the UI can
                                // show that the model attempted a tool call.
                                PostEvent("toolCall", new
                                {
                                    sessionId,
                                    messageId = assistantMessage.Id,
                                    toolName = delta.ToolName,
                                    argumentsFragment = delta.ToolArgumentsFragment
                                });
                                break;

                            case ChatDeltaType.Usage:
                                inputTokens = delta.InputTokens ?? inputTokens;
                                outputTokens = delta.OutputTokens ?? outputTokens;
                                break;

                            case ChatDeltaType.Error:
                                failure = delta.ErrorMessage;
                                return;

                            case ChatDeltaType.Done:
                                return;
                        }
                    }
                }, cts.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (AiProviderException ex)
            {
                failure = ex.Message;
            }
            catch (Exception ex)
            {
                failure = "Unexpected error: " + ex.Message;
            }
            finally
            {
                _inFlight.TryRemove(sessionId, out _);
                cts.Dispose();
            }

            assistantMessage.Content = buffer.ToString();
            assistantMessage.Thinking = thinkingBuffer.Length > 0 ? thinkingBuffer.ToString() : null;
            int outTokens = outputTokens ?? CostEstimator.EstimateTokens(assistantMessage.Content);
            int inTokens = inputTokens ?? request.Messages.Sum(m => CostEstimator.EstimateTokens(m.Content));
            assistantMessage.Tokens = outTokens;

            // Persist whatever was generated, including partial output on cancel.
            if (assistantMessage.Content.Length > 0 || assistantMessage.Thinking != null)
            {
                _db.InsertMessage(assistantMessage);
                _db.TouchSession(sessionId);
            }

            if (failure != null)
            {
                PostEvent("streamError", new { sessionId, messageId = assistantMessage.Id, message = failure });
                return;
            }

            PostEvent("streamDone", new
            {
                sessionId,
                message = ToMessageDto(assistantMessage, sessionId),
                cancelled,
                usage = new
                {
                    inputTokens = inTokens,
                    outputTokens = outTokens,
                    estimated = inputTokens == null || outputTokens == null,
                    costUsd = _cost.EstimateCostUsd(model, inTokens, outTokens)
                }
            });
        }

        private void HandleStop(BridgeCommand cmd) => CancelGeneration(cmd.GetId("sessionId"));

        // =====================================================================
        // Local code execution (the WebView runs locally, so code blocks can be
        // executed on this machine through user-configurable commands).
        // =====================================================================

        private async Task HandleRunCode(BridgeCommand cmd)
        {
            string runId = cmd.GetId("runId");
            string language = cmd.GetString("language", 64).ToLowerInvariant();
            string code = cmd.GetString("code", 500_000);

            var commands = CodeRunner.ParseCommands(_db.GetSetting("runCommands"));
            if (!commands.TryGetValue(language, out var runCommand) ||
                string.IsNullOrWhiteSpace(runCommand?.Command))
            {
                PostEvent("runDone", new
                {
                    runId,
                    exitCode = (int?)null,
                    error = $"No run command configured for '{language}'. Add one in Settings → Run commands."
                });
                return;
            }

            int timeout = int.TryParse(_db.GetSetting("runTimeoutSeconds", "60"), out int t) ? t : 60;

            PostEvent("runStart", new { runId, language });
            try
            {
                int? exitCode = await Task.Run(() => _runner.RunAsync(
                    runId, language, code, runCommand, timeout,
                    (text, isStderr) => PostEvent("runOutput", new { runId, text, stderr = isStderr }),
                    CancellationToken.None));

                PostEvent("runDone", new { runId, exitCode, error = (string)null });
            }
            catch (Exception ex)
            {
                PostEvent("runDone", new { runId, exitCode = (int?)null, error = ex.Message });
            }
        }

        private void CancelGeneration(string sessionId)
        {
            if (_inFlight.TryRemove(sessionId, out var cts))
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
        }

        // =====================================================================
        // Search / models / keys / settings / templates
        // =====================================================================

        private void HandleSearch(BridgeCommand cmd)
            => PostEvent("searchResults", new { hits = _db.Search(cmd.GetString("query", 500)) });

        private async Task HandleListModels(BridgeCommand cmd)
        {
            string providerName = cmd.GetString("provider", 64);
            var provider = _providers.Get(providerName);
            var models = (await provider.ListModelsAsync()).ToList();
            _cost.UpdateCatalog(models);
            PostEvent("models", new { provider = providerName, models });
        }

        private void HandleSaveApiKey(BridgeCommand cmd)
        {
            string providerName = cmd.GetString("provider", 64);
            string key = cmd.GetString("apiKey", 4096);
            _secrets.SetApiKey(providerName, key);
            // Never echo the key back; confirm status only (spec §3.7).
            PostEvent("apiKeySaved", new { provider = providerName, hasApiKey = true });
        }

        private void HandleDeleteApiKey(BridgeCommand cmd)
        {
            string providerName = cmd.GetString("provider", 64);
            _secrets.DeleteApiKey(providerName);
            PostEvent("apiKeySaved", new { provider = providerName, hasApiKey = false });
        }

        private object ReadUiSettings() => new
        {
            theme = _db.GetSetting("theme", "light"),
            codeFontSize = int.TryParse(_db.GetSetting("codeFontSize", "13"), out int fs) ? fs : 13,
            // Per-language run-command templates (feature: execute code locally). Stored as
            // JSON {"python":{"extension":".py","command":"python \"{file}\""}, ...}
            runCommands = CodeRunner.ParseCommands(_db.GetSetting("runCommands")),
            runTimeoutSeconds = int.TryParse(_db.GetSetting("runTimeoutSeconds", "60"), out int rt) ? rt : 60,
            confirmRun = _db.GetSetting("confirmRun", "true") != "false",
            thinkingMode = _db.GetSetting("thinkingMode", "off"),
            thinkingEffort = _db.GetSetting("thinkingEffort", "medium")
        };

        private void HandleGetSettings() => PostEvent("settings", ReadUiSettings());

        private static readonly string[] WritableSettings =
            { "theme", "codeFontSize", "runCommands", "runTimeoutSeconds", "confirmRun", "thinkingMode", "thinkingEffort" };

        private void HandleSetSetting(BridgeCommand cmd)
        {
            string key = cmd.GetString("key", 64);
            if (Array.IndexOf(WritableSettings, key) < 0)
                throw new BridgeValidationException($"Setting '{key}' is not writable from the UI.");

            // runCommands carries a JSON blob; everything else is a short scalar.
            int max = key == "runCommands" ? 20_000 : 256;
            string value = cmd.GetString("value", max);

            if (key == "runCommands")
                CodeRunner.ParseCommands(value); // validates shape; falls back internally if broken

            _db.SetSetting(key, value);
        }

        private List<PromptTemplate> ReadTemplates()
        {
            string json = _db.GetSetting("promptTemplates");
            if (string.IsNullOrEmpty(json)) return DefaultTemplates();
            try { return JsonSerializer.Deserialize<List<PromptTemplate>>(json, JsonOpts) ?? DefaultTemplates(); }
            catch { return DefaultTemplates(); }
        }

        private static List<PromptTemplate> DefaultTemplates() => new List<PromptTemplate>
        {
            new PromptTemplate { Name = "Coding assistant", Prompt = "You are an expert software engineer. Give correct, idiomatic code with brief explanations. When you output a complete file, use a fenced code block annotated as ```language:filename." },
            new PromptTemplate { Name = "Debugger", Prompt = "You are a debugging partner. Ask for the exact error and minimal repro when missing, reason step by step, and propose the smallest fix first." },
            new PromptTemplate { Name = "Writer", Prompt = "You are a precise, plain-language writing assistant. Prefer short sentences and concrete wording; keep the author's voice." }
        };

        private void HandleListTemplates() => PostEvent("templates", new { templates = ReadTemplates() });

        private void HandleSaveTemplate(BridgeCommand cmd)
        {
            var templates = ReadTemplates();
            string id = cmd.GetId("id", required: false);
            string name = cmd.GetString("name", 200);
            string prompt = cmd.GetString("prompt", 100_000);

            var existing = id != null ? templates.FirstOrDefault(t => t.Id == id) : null;
            if (existing != null) { existing.Name = name; existing.Prompt = prompt; }
            else templates.Add(new PromptTemplate { Name = name, Prompt = prompt });

            _db.SetSetting("promptTemplates", JsonSerializer.Serialize(templates, JsonOpts));
            PostEvent("templates", new { templates });
        }

        private void HandleDeleteTemplate(BridgeCommand cmd)
        {
            var templates = ReadTemplates();
            templates.RemoveAll(t => t.Id == cmd.GetId("id"));
            _db.SetSetting("promptTemplates", JsonSerializer.Serialize(templates, JsonOpts));
            PostEvent("templates", new { templates });
        }

        // =====================================================================
        // Files: export / import / native save / attachment picker (spec §3.4, §4)
        // =====================================================================

        private void HandleExportSession(BridgeCommand cmd)
        {
            string sessionId = cmd.GetId("sessionId");
            string format = cmd.GetString("format", 10); // "markdown" | "json"
            var session = RequireSession(sessionId);

            string content, ext, filter;
            if (format == "json")
            {
                content = _exporter.ExportJson(sessionId);
                ext = "json"; filter = "JSON session backup (*.json)|*.json";
            }
            else
            {
                content = _exporter.ExportMarkdown(sessionId);
                ext = "md"; filter = "Markdown (*.md)|*.md";
            }

            RunOnUi(() =>
            {
                using (var dialog = new SaveFileDialog
                {
                    FileName = SanitizeFileName(session.Title) + "." + ext,
                    Filter = filter,
                    Title = "Export conversation"
                })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        File.WriteAllText(dialog.FileName, content);
                        PostEvent("exported", new { sessionId, format });
                    }
                }
            });
        }

        private void HandleImportSession()
        {
            RunOnUi(() =>
            {
                using (var dialog = new OpenFileDialog
                {
                    Filter = "JSON session backup (*.json)|*.json",
                    Title = "Import conversation"
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        string newId = _exporter.ImportJson(File.ReadAllText(dialog.FileName));
                        PostEvent("sessions", new { sessions = _db.ListSessions() });
                        PostEvent("sessionImported", new { sessionId = newId });
                    }
                    catch (Exception ex)
                    {
                        PostEvent("error", new { message = "Import failed: " + ex.Message });
                    }
                }
            });
        }

        /// <summary>Binary downloads route through a native SaveFileDialog (spec §3.4). JS supplies
        /// only a suggested *name* and base64 data — never a path.</summary>
        private void HandleSaveBinaryFile(BridgeCommand cmd)
        {
            string fileName = SanitizeFileName(cmd.GetString("fileName", 260));
            string base64 = cmd.GetString("dataBase64", (int)(MaxAttachmentBytes * 4 / 3) + 16);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64); }
            catch (FormatException) { throw new BridgeValidationException("dataBase64 is not valid base64."); }

            RunOnUi(() =>
            {
                using (var dialog = new SaveFileDialog { FileName = fileName, Title = "Save file" })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        File.WriteAllBytes(dialog.FileName, bytes);
                        PostEvent("fileSaved", new { fileName });
                    }
                }
            });
        }

        private void HandlePickAttachment(BridgeCommand cmd)
        {
            RunOnUi(() =>
            {
                using (var dialog = new OpenFileDialog
                {
                    Filter = "Images and PDFs|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.pdf|All files|*.*",
                    Title = "Attach a file",
                    Multiselect = true
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;

                    var picked = new List<object>();
                    foreach (string path in dialog.FileNames)
                    {
                        var fi = new FileInfo(path);
                        if (fi.Length > MaxAttachmentBytes)
                        {
                            PostEvent("error", new { message = $"{fi.Name} exceeds the 20 MB attachment limit." });
                            continue;
                        }
                        picked.Add(new
                        {
                            fileName = fi.Name,
                            mimeType = GuessMime(fi.Name),
                            dataBase64 = Convert.ToBase64String(File.ReadAllBytes(path))
                        });
                    }
                    PostEvent("attachmentsPicked", new { attachments = picked });
                }
            });
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private ChatSession RequireSession(string id)
            => _db.GetSession(id) ?? throw new BridgeValidationException("Session not found.");

        private List<ChatAttachment> ParseAttachments(JsonElement? array)
        {
            var result = new List<ChatAttachment>();
            if (array == null) return result;

            foreach (var item in array.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string b64 = item.TryGetProperty("dataBase64", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (string.IsNullOrEmpty(b64)) continue;

                byte[] bytes;
                try { bytes = Convert.FromBase64String(b64); }
                catch (FormatException) { continue; }
                if (bytes.LongLength > MaxAttachmentBytes) continue;

                result.Add(new ChatAttachment
                {
                    FileName = item.TryGetProperty("fileName", out var fn) && fn.ValueKind == JsonValueKind.String
                        ? SanitizeFileName(fn.GetString()) : "attachment",
                    MimeType = item.TryGetProperty("mimeType", out var mt) && mt.ValueKind == JsonValueKind.String
                        ? mt.GetString() : "application/octet-stream",
                    Data = bytes
                });
                if (result.Count >= 10) break;
            }
            return result;
        }

        /// <summary>Message DTO for JS: attachment bytes become data URLs so images render
        /// straight into &lt;img&gt; (spec §3.5); MIME comes from the stored type, not extension.</summary>
        private object ToMessageDto(ChatMessage m, string sessionId)
        {
            var withData = m.Attachments;

            return new
            {
                id = m.Id,
                role = m.Role.ToString().ToLowerInvariant(),
                content = m.Content,
                thinking = m.Thinking,
                contentType = m.ContentType,
                tokens = m.Tokens,
                model = m.Model,
                createdAt = m.CreatedAt,
                attachments = withData.Select(a => new
                {
                    fileName = a.FileName,
                    mimeType = a.MimeType,
                    dataUrl = a.Data != null && a.MimeType != null && a.MimeType.StartsWith("image/")
                        ? $"data:{a.MimeType};base64,{Convert.ToBase64String(a.Data)}"
                        : null
                })
            };
        }

        private void PostEvent(string eventName, object payload)
        {
            string json = JsonSerializer.Serialize(new { @event = eventName, data = payload }, JsonOpts);
            RunOnUi(() =>
            {
                try { _webView.CoreWebView2?.PostWebMessageAsJson(json); }
                catch (InvalidOperationException) { /* control tearing down */ }
            });
        }

        private void RunOnUi(Action action)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }

        private static void TryOpenExternal(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                try { Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true }); }
                catch { }
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "untitled";
            name = Path.GetFileName(name); // strips any directory components JS might sneak in
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length > 120 ? name.Substring(0, 120) : name;
        }

        private static string GuessMime(string fileName)
        {
            switch (Path.GetExtension(fileName).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".pdf": return "application/pdf";
                default: return "application/octet-stream";
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var cts in _inFlight.Values)
                {
                    try { cts.Cancel(); cts.Dispose(); } catch { }
                }
                _inFlight.Clear();
                _runner?.Dispose();
                _webView.Dispose();
                _db?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
