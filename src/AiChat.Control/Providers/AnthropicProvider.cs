using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiChat.Control.Models;

namespace AiChat.Control.Providers
{
    /// <summary>
    /// Anthropic Messages API (SSE streaming). The API key stays in C# — it is attached as
    /// an HTTP header here and never enters the WebView2/JS context (spec §2).
    ///
    /// Attachments are uploaded through the Files API (anthropic-beta: files-api-2025-04-14)
    /// and referenced by file_id in message content, per
    /// https://platform.claude.com/docs/en/build-with-claude/files#using-a-file-in-messages
    ///
    /// Thinking: supports both extended thinking ({"type":"enabled","budget_tokens":N}) and
    /// adaptive thinking ({"type":"adaptive","display":"summarized"}).
    /// </summary>
    public sealed class AnthropicProvider : ProviderBase, IAiProvider
    {
        private const string BaseUrl = "https://api.anthropic.com/v1";
        private const string ApiVersion = "2023-06-01";
        private const string FilesBeta = "files-api-2025-04-14";

        private readonly Func<string> _apiKeyAccessor;

        /// <summary>attachment id → uploaded file_id, so the same bytes upload once per process.</summary>
        private readonly ConcurrentDictionary<string, string> _fileIdCache =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        public AnthropicProvider(Func<string> apiKeyAccessor) => _apiKeyAccessor = apiKeyAccessor;

        public string Name => "anthropic";
        public bool RequiresApiKey => true;

        private static readonly ModelInfo[] FallbackModels =
        {
            new ModelInfo { Id = "claude-sonnet-4-6",   DisplayName = "Claude Sonnet 4.6", Provider = "anthropic", SupportsVision = true, InputPricePerMTok = 3m,  OutputPricePerMTok = 15m,  SupportsThinking = true, ThinkingKind = "anthropic" },
            new ModelInfo { Id = "claude-opus-4-8",     DisplayName = "Claude Opus 4.8",   Provider = "anthropic", SupportsVision = true, InputPricePerMTok = 15m, OutputPricePerMTok = 75m,  SupportsThinking = true, ThinkingKind = "anthropic" },
            new ModelInfo { Id = "claude-haiku-4-5-20251001", DisplayName = "Claude Haiku 4.5", Provider = "anthropic", SupportsVision = true, InputPricePerMTok = 0.8m, OutputPricePerMTok = 4m, SupportsThinking = true, ThinkingKind = "anthropic" },
        };

        public async Task<IEnumerable<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
        {
            string key = _apiKeyAccessor();
            if (string.IsNullOrWhiteSpace(key)) return FallbackModels;

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/models"))
                {
                    req.Headers.Add("x-api-key", key);
                    req.Headers.Add("anthropic-version", ApiVersion);
                    using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) return FallbackModels;
                        string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using (var doc = JsonDocument.Parse(json))
                        {
                            var list = new List<ModelInfo>();
                            foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
                            {
                                string id = m.GetProperty("id").GetString();
                                string display = m.TryGetProperty("display_name", out var dn) ? dn.GetString() : id;
                                var known = FallbackModels.FirstOrDefault(f => f.Id == id);
                                list.Add(new ModelInfo
                                {
                                    Id = id,
                                    DisplayName = display,
                                    Provider = Name,
                                    SupportsVision = true,
                                    InputPricePerMTok = known?.InputPricePerMTok ?? 0m,
                                    OutputPricePerMTok = known?.OutputPricePerMTok ?? 0m,
                                    // Every current Claude chat model exposes the thinking control.
                                    SupportsThinking = true,
                                    ThinkingKind = "anthropic"
                                });
                            }
                            return list.Count > 0 ? list : (IEnumerable<ModelInfo>)FallbackModels;
                        }
                    }
                }
            }
            catch
            {
                // Static config fallback (spec §3.6).
                return FallbackModels;
            }
        }

        public async IAsyncEnumerable<ChatDelta> StreamChatAsync(
            ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            string key = _apiKeyAccessor();
            if (string.IsNullOrWhiteSpace(key))
            {
                yield return ChatDelta.OfError("No Anthropic API key configured. Add one in Settings.");
                yield break;
            }

            // Upload any attachments that don't have a file_id yet (Files API). Failures fall
            // back to inline base64 so a Files-API hiccup never blocks the conversation.
            bool usesFileIds = await EnsureFilesUploadedAsync(request, key, ct).ConfigureAwait(false);

            string payload = BuildPayload(request);

            HttpResponseMessage response = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/messages")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("x-api-key", key);
                req.Headers.Add("anthropic-version", ApiVersion);
                if (usesFileIds) req.Headers.Add("anthropic-beta", FilesBeta);
                req.Headers.Add("accept", "text/event-stream");
                return req;
            }, ct).ConfigureAwait(false);

            using (response)
            {
                int? inputTokens = null, outputTokens = null;

                await foreach (SseEvent evt in SseReader.ReadAsync(response, ct).ConfigureAwait(false))
                {
                    ChatDelta delta = null;
                    try
                    {
                        delta = MapEvent(evt, ref inputTokens, ref outputTokens);
                    }
                    catch (JsonException)
                    {
                        // Skip malformed keep-alive frames rather than killing the stream.
                    }
                    if (delta != null) yield return delta;
                    if (delta?.Type == ChatDeltaType.Done || delta?.Type == ChatDeltaType.Error) yield break;
                }

                yield return ChatDelta.OfUsage(inputTokens, outputTokens);
                yield return ChatDelta.OfDone();
            }
        }

        // ================================================================= Files API

        /// <summary>
        /// Uploads every attachment lacking a ProviderFileId and records the id. Returns true
        /// when at least one message will reference a file_id (the beta header is then required).
        /// </summary>
        private async Task<bool> EnsureFilesUploadedAsync(ChatRequest request, string key, CancellationToken ct)
        {
            bool any = false;
            foreach (var m in request.Messages)
            {
                if (m.Attachments == null) continue;
                foreach (var a in m.Attachments)
                {
                    if (string.IsNullOrEmpty(a.ProviderFileId) &&
                        _fileIdCache.TryGetValue(a.Id, out string cached))
                    {
                        a.ProviderFileId = cached;
                    }

                    if (string.IsNullOrEmpty(a.ProviderFileId) && a.Data != null && a.Data.Length > 0)
                    {
                        try
                        {
                            a.ProviderFileId = await UploadFileAsync(a, key, ct).ConfigureAwait(false);
                            _fileIdCache[a.Id] = a.ProviderFileId;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch
                        {
                            a.ProviderFileId = null; // fall back to base64 for this attachment
                        }
                    }

                    any |= !string.IsNullOrEmpty(a.ProviderFileId);
                }
            }
            return any;
        }

        private static async Task<string> UploadFileAsync(ChatAttachment a, string key, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/files"))
            {
                req.Headers.Add("x-api-key", key);
                req.Headers.Add("anthropic-version", ApiVersion);
                req.Headers.Add("anthropic-beta", FilesBeta);

                var content = new MultipartFormDataContent();
                var bytes = new ByteArrayContent(a.Data);
                bytes.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(
                        string.IsNullOrWhiteSpace(a.MimeType) ? "application/octet-stream" : a.MimeType);
                content.Add(bytes, "file", string.IsNullOrWhiteSpace(a.FileName) ? "upload.bin" : a.FileName);
                req.Content = content;

                using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new AiProviderException($"File upload failed ({(int)resp.StatusCode}): {Truncate(json, 300)}");

                    using (var doc = JsonDocument.Parse(json))
                        return doc.RootElement.GetProperty("id").GetString();
                }
            }
        }

        // ================================================================= Stream mapping

        private static ChatDelta MapEvent(SseEvent evt, ref int? inputTokens, ref int? outputTokens)
        {
            using (var doc = JsonDocument.Parse(evt.Data))
            {
                var root = doc.RootElement;
                string type = evt.EventName ?? (root.TryGetProperty("type", out var t) ? t.GetString() : null);

                switch (type)
                {
                    case "message_start":
                        if (root.TryGetProperty("message", out var msg) &&
                            msg.TryGetProperty("usage", out var u1) &&
                            u1.TryGetProperty("input_tokens", out var it))
                            inputTokens = it.GetInt32();
                        return null;

                    case "content_block_start":
                        if (root.TryGetProperty("content_block", out var cb) &&
                            cb.GetProperty("type").GetString() == "tool_use")
                        {
                            return new ChatDelta
                            {
                                Type = ChatDeltaType.ToolCallStart,
                                ToolCallId = cb.GetProperty("id").GetString(),
                                ToolName = cb.GetProperty("name").GetString()
                            };
                        }
                        return null;

                    case "content_block_delta":
                        var d = root.GetProperty("delta");
                        string dType = d.GetProperty("type").GetString();
                        if (dType == "text_delta")
                            return ChatDelta.OfText(d.GetProperty("text").GetString());
                        if (dType == "thinking_delta")
                            return ChatDelta.OfThinking(d.GetProperty("thinking").GetString());
                        if (dType == "input_json_delta")
                            return new ChatDelta
                            {
                                Type = ChatDeltaType.ToolCallDelta,
                                ToolArgumentsFragment = d.GetProperty("partial_json").GetString()
                            };
                        return null;

                    case "message_delta":
                        if (root.TryGetProperty("usage", out var u2) &&
                            u2.TryGetProperty("output_tokens", out var ot))
                            outputTokens = ot.GetInt32();
                        return null;

                    case "message_stop":
                        return null; // final Usage/Done emitted by caller

                    case "error":
                        string err = root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var em)
                            ? em.GetString() : "Unknown provider error";
                        return ChatDelta.OfError(err);

                    default:
                        return null; // ping / signature_delta etc.
                }
            }
        }

        // ================================================================= Payload

        private static string BuildPayload(ChatRequest request)
        {
            bool thinkingOn = request.Thinking != null && request.Thinking.IsOn;

            using (var ms = new System.IO.MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteString("model", request.Model);

                    int maxTokens = request.MaxTokens;
                    if (thinkingOn && request.Thinking.Mode == "enabled")
                    {
                        // max_tokens must exceed the thinking budget.
                        maxTokens = Math.Max(maxTokens, request.Thinking.BudgetTokens + 6000);
                    }
                    else if (thinkingOn)
                    {
                        maxTokens = Math.Max(maxTokens, 16000);
                    }
                    w.WriteNumber("max_tokens", maxTokens);
                    w.WriteBoolean("stream", true);

                    if (thinkingOn)
                    {
                        w.WriteStartObject("thinking");
                        if (request.Thinking.Mode == "adaptive")
                        {
                            w.WriteString("type", "adaptive");
                            w.WriteString("display", "summarized");
                        }
                        else
                        {
                            w.WriteString("type", "enabled");
                            w.WriteNumber("budget_tokens", request.Thinking.BudgetTokens);
                        }
                        w.WriteEndObject();
                        // temperature must stay at its default while thinking is enabled.
                    }
                    else if (request.Temperature.HasValue)
                    {
                        w.WriteNumber("temperature", request.Temperature.Value);
                    }

                    if (!string.IsNullOrWhiteSpace(request.SystemPrompt)) w.WriteString("system", request.SystemPrompt);

                    if (request.Tools.Count > 0)
                    {
                        w.WriteStartArray("tools");
                        foreach (var tool in request.Tools)
                        {
                            w.WriteStartObject();
                            w.WriteString("name", tool.Name);
                            w.WriteString("description", tool.Description ?? "");
                            w.WritePropertyName("input_schema");
                            using (var schema = JsonDocument.Parse(tool.InputSchemaJson ?? "{\"type\":\"object\"}"))
                                schema.RootElement.WriteTo(w);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }

                    w.WriteStartArray("messages");
                    foreach (var m in request.Messages.Where(m => m.Role == ChatRole.User || m.Role == ChatRole.Assistant))
                    {
                        w.WriteStartObject();
                        w.WriteString("role", m.Role == ChatRole.User ? "user" : "assistant");

                        bool hasAttachments = m.Attachments != null && m.Attachments.Count > 0;
                        if (!hasAttachments)
                        {
                            w.WriteString("content", m.Content ?? "");
                        }
                        else
                        {
                            w.WriteStartArray("content");
                            foreach (var a in m.Attachments)
                            {
                                bool isImage = a.MimeType != null &&
                                               a.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
                                w.WriteStartObject();
                                w.WriteString("type", isImage ? "image" : "document");
                                w.WriteStartObject("source");
                                if (!string.IsNullOrEmpty(a.ProviderFileId))
                                {
                                    // Files API reference (preferred).
                                    w.WriteString("type", "file");
                                    w.WriteString("file_id", a.ProviderFileId);
                                }
                                else
                                {
                                    // Inline base64 fallback if the upload failed.
                                    w.WriteString("type", "base64");
                                    w.WriteString("media_type", a.MimeType);
                                    w.WriteString("data", Convert.ToBase64String(a.Data));
                                }
                                w.WriteEndObject();
                                w.WriteEndObject();
                            }
                            w.WriteStartObject();
                            w.WriteString("type", "text");
                            w.WriteString("text", string.IsNullOrEmpty(m.Content) ? "(see attachment)" : m.Content);
                            w.WriteEndObject();
                            w.WriteEndArray();
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }
}
