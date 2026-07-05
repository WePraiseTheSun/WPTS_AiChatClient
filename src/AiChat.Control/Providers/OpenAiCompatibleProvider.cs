using System;
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
    /// Base for every "OpenAI chat/completions"-shaped API: OpenAI itself, DeepSeek,
    /// and local Ollama (which exposes an OpenAI-compatible endpoint). One SSE parser,
    /// three providers.
    /// </summary>
    public abstract class OpenAiCompatibleProvider : ProviderBase, IAiProvider
    {
        private readonly Func<string> _apiKeyAccessor;

        protected OpenAiCompatibleProvider(Func<string> apiKeyAccessor) => _apiKeyAccessor = apiKeyAccessor;

        public abstract string Name { get; }
        public abstract bool RequiresApiKey { get; }
        protected abstract string BaseUrl { get; }
        protected abstract IReadOnlyList<ModelInfo> FallbackModels { get; }
        protected virtual bool SupportsVision(string modelId) => false;

        /// <summary>
        /// Returns "effort" when the model exposes a reasoning control (DeepSeek V3.2+/reasoner,
        /// OpenAI o-series), otherwise null. The UI hides the thinking selector when null.
        /// </summary>
        protected virtual string ThinkingKindFor(string modelId) => null;

        private IEnumerable<ModelInfo> WithThinking(IEnumerable<ModelInfo> models)
        {
            foreach (var m in models)
            {
                string kind = ThinkingKindFor(m.Id);
                m.SupportsThinking = kind != null;
                m.ThinkingKind = kind;
                yield return m;
            }
        }

        public virtual async Task<IEnumerable<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
        {
            string key = _apiKeyAccessor();
            if (RequiresApiKey && string.IsNullOrWhiteSpace(key)) return WithThinking(FallbackModels).ToList();

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/models"))
                {
                    if (!string.IsNullOrWhiteSpace(key))
                        req.Headers.Add("Authorization", "Bearer " + key);
                    using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) return WithThinking(FallbackModels).ToList();
                        string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using (var doc = JsonDocument.Parse(json))
                        {
                            var list = new List<ModelInfo>();
                            foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
                            {
                                string id = m.GetProperty("id").GetString();
                                var known = FallbackModels.FirstOrDefault(f => f.Id == id);
                                string kind = ThinkingKindFor(id);
                                list.Add(new ModelInfo
                                {
                                    Id = id,
                                    DisplayName = known?.DisplayName ?? id,
                                    Provider = Name,
                                    SupportsVision = SupportsVision(id),
                                    InputPricePerMTok = known?.InputPricePerMTok ?? 0m,
                                    OutputPricePerMTok = known?.OutputPricePerMTok ?? 0m,
                                    SupportsThinking = kind != null,
                                    ThinkingKind = kind
                                });
                            }
                            return list.Count > 0 ? list : WithThinking(FallbackModels).ToList();
                        }
                    }
                }
            }
            catch
            {
                return WithThinking(FallbackModels).ToList();
            }
        }

        public async IAsyncEnumerable<ChatDelta> StreamChatAsync(
            ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            string key = _apiKeyAccessor();
            if (RequiresApiKey && string.IsNullOrWhiteSpace(key))
            {
                yield return ChatDelta.OfError($"No {Name} API key configured. Add one in Settings.");
                yield break;
            }

            string payload = BuildPayload(request);

            HttpResponseMessage response = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/chat/completions")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(key))
                    req.Headers.Add("Authorization", "Bearer " + key);
                req.Headers.Add("accept", "text/event-stream");
                return req;
            }, ct).ConfigureAwait(false);

            using (response)
            {
                int? inputTokens = null, outputTokens = null;

                await foreach (SseEvent evt in SseReader.ReadAsync(response, ct).ConfigureAwait(false))
                {
                    if (evt.Data == "[DONE]") break;

                    ChatDelta mapped = null;
                    try { mapped = MapEvent(evt.Data, ref inputTokens, ref outputTokens); }
                    catch (JsonException) { /* ignore malformed frames */ }

                    if (mapped != null)
                    {
                        yield return mapped;
                        if (mapped.Type == ChatDeltaType.Error) yield break;
                    }
                }

                yield return ChatDelta.OfUsage(inputTokens, outputTokens);
                yield return ChatDelta.OfDone();
            }
        }

        private static ChatDelta MapEvent(string data, ref int? inputTokens, ref int? outputTokens)
        {
            using (var doc = JsonDocument.Parse(data))
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var errObj))
                {
                    string msg = errObj.TryGetProperty("message", out var em) ? em.GetString() : errObj.ToString();
                    return ChatDelta.OfError(msg);
                }

                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("prompt_tokens", out var pt)) inputTokens = pt.GetInt32();
                    if (usage.TryGetProperty("completion_tokens", out var ctk)) outputTokens = ctk.GetInt32();
                }

                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    return null;

                var delta = choices[0].GetProperty("delta");

                // DeepSeek streams reasoning in delta.reasoning_content (thinking mode guide).
                if (delta.TryGetProperty("reasoning_content", out var reasoning) &&
                    reasoning.ValueKind == JsonValueKind.String)
                {
                    string think = reasoning.GetString();
                    if (!string.IsNullOrEmpty(think)) return ChatDelta.OfThinking(think);
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
                {
                    var tc = toolCalls[0];
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        bool isStart = tc.TryGetProperty("id", out var idProp) &&
                                       idProp.ValueKind == JsonValueKind.String &&
                                       fn.TryGetProperty("name", out var nameProp) &&
                                       !string.IsNullOrEmpty(nameProp.GetString());
                        if (isStart)
                        {
                            return new ChatDelta
                            {
                                Type = ChatDeltaType.ToolCallStart,
                                ToolCallId = idProp.GetString(),
                                ToolName = fn.GetProperty("name").GetString()
                            };
                        }
                        if (fn.TryGetProperty("arguments", out var args))
                        {
                            return new ChatDelta
                            {
                                Type = ChatDeltaType.ToolCallDelta,
                                ToolArgumentsFragment = args.GetString()
                            };
                        }
                    }
                    return null;
                }

                if (delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                {
                    string text = content.GetString();
                    return string.IsNullOrEmpty(text) ? null : ChatDelta.OfText(text);
                }

                return null;
            }
        }

        /// <summary>
        /// Provider-specific reasoning parameters. Called only when request.Thinking is on.
        /// DeepSeek: reasoning_effort + {"thinking":{"type":"enabled"}}; OpenAI: reasoning_effort.
        /// </summary>
        protected virtual void WriteThinkingParams(Utf8JsonWriter w, ChatRequest request) { }

        private string BuildPayload(ChatRequest request)
        {
            using (var ms = new System.IO.MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteString("model", request.Model);
                    w.WriteBoolean("stream", true);
                    w.WriteNumber("max_tokens", request.MaxTokens);
                    if (request.Temperature.HasValue) w.WriteNumber("temperature", request.Temperature.Value);

                    if (request.Thinking != null && request.Thinking.IsOn)
                        WriteThinkingParams(w, request);

                    w.WriteStartObject("stream_options");
                    w.WriteBoolean("include_usage", true);
                    w.WriteEndObject();

                    if (request.Tools.Count > 0)
                    {
                        w.WriteStartArray("tools");
                        foreach (var tool in request.Tools)
                        {
                            w.WriteStartObject();
                            w.WriteString("type", "function");
                            w.WriteStartObject("function");
                            w.WriteString("name", tool.Name);
                            w.WriteString("description", tool.Description ?? "");
                            w.WritePropertyName("parameters");
                            using (var schema = JsonDocument.Parse(tool.InputSchemaJson ?? "{\"type\":\"object\"}"))
                                schema.RootElement.WriteTo(w);
                            w.WriteEndObject();
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }

                    w.WriteStartArray("messages");

                    if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
                    {
                        w.WriteStartObject();
                        w.WriteString("role", "system");
                        w.WriteString("content", request.SystemPrompt);
                        w.WriteEndObject();
                    }

                    foreach (var m in request.Messages.Where(m => m.Role == ChatRole.User || m.Role == ChatRole.Assistant))
                    {
                        w.WriteStartObject();
                        w.WriteString("role", m.Role == ChatRole.User ? "user" : "assistant");

                        bool hasImages = m.Attachments != null &&
                                         m.Attachments.Any(a => a.MimeType != null && a.MimeType.StartsWith("image/"));
                        if (!hasImages)
                        {
                            w.WriteString("content", m.Content ?? "");
                        }
                        else
                        {
                            w.WriteStartArray("content");
                            w.WriteStartObject();
                            w.WriteString("type", "text");
                            w.WriteString("text", m.Content ?? "");
                            w.WriteEndObject();
                            foreach (var a in m.Attachments.Where(a => a.MimeType != null && a.MimeType.StartsWith("image/")))
                            {
                                w.WriteStartObject();
                                w.WriteString("type", "image_url");
                                w.WriteStartObject("image_url");
                                w.WriteString("url", $"data:{a.MimeType};base64,{Convert.ToBase64String(a.Data)}");
                                w.WriteEndObject();
                                w.WriteEndObject();
                            }
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

    public sealed class OpenAiProvider : OpenAiCompatibleProvider
    {
        public OpenAiProvider(Func<string> apiKeyAccessor) : base(apiKeyAccessor) { }
        public override string Name => "openai";
        public override bool RequiresApiKey => true;
        protected override string BaseUrl => "https://api.openai.com/v1";
        protected override bool SupportsVision(string modelId)
            => modelId.StartsWith("gpt-4o") || modelId.StartsWith("gpt-4.1") || modelId.StartsWith("gpt-5") || modelId.StartsWith("o");

        // o-series / gpt-5 accept reasoning_effort.
        protected override string ThinkingKindFor(string modelId)
            => (modelId.StartsWith("o1") || modelId.StartsWith("o3") || modelId.StartsWith("o4") || modelId.StartsWith("gpt-5"))
                ? "effort" : null;

        protected override void WriteThinkingParams(System.Text.Json.Utf8JsonWriter w, ChatRequest request)
        {
            w.WriteString("reasoning_effort", request.Thinking.Effort);
        }

        protected override IReadOnlyList<ModelInfo> FallbackModels { get; } = new[]
        {
            new ModelInfo { Id = "gpt-4o",      DisplayName = "GPT-4o",      Provider = "openai", SupportsVision = true,  InputPricePerMTok = 2.5m,  OutputPricePerMTok = 10m },
            new ModelInfo { Id = "gpt-4o-mini", DisplayName = "GPT-4o mini", Provider = "openai", SupportsVision = true,  InputPricePerMTok = 0.15m, OutputPricePerMTok = 0.6m },
            new ModelInfo { Id = "gpt-4.1",     DisplayName = "GPT-4.1",     Provider = "openai", SupportsVision = true,  InputPricePerMTok = 2m,    OutputPricePerMTok = 8m },
        };
    }

    public sealed class DeepSeekProvider : OpenAiCompatibleProvider
    {
        public DeepSeekProvider(Func<string> apiKeyAccessor) : base(apiKeyAccessor) { }
        public override string Name => "deepseek";
        public override bool RequiresApiKey => true;
        protected override string BaseUrl => "https://api.deepseek.com/v1";

        // deepseek-reasoner is always a thinking model; V3.2+/V4 expose the toggle
        // (https://api-docs.deepseek.com/guides/thinking_mode).
        protected override string ThinkingKindFor(string modelId)
            => (modelId.Contains("reasoner") || modelId.StartsWith("deepseek-v3.2") || modelId.StartsWith("deepseek-v4"))
                ? "effort" : null;

        protected override void WriteThinkingParams(System.Text.Json.Utf8JsonWriter w, ChatRequest request)
        {
            // Equivalent of the documented python: reasoning_effort="high",
            // extra_body={"thinking": {"type": "enabled"}} — extra_body merges into the JSON body.
            w.WriteString("reasoning_effort", request.Thinking.Effort);
            w.WriteStartObject("thinking");
            w.WriteString("type", "enabled");
            w.WriteEndObject();
        }

        protected override IReadOnlyList<ModelInfo> FallbackModels { get; } = new[]
        {
            new ModelInfo { Id = "deepseek-chat",     DisplayName = "DeepSeek Chat (V3)",     Provider = "deepseek", InputPricePerMTok = 0.27m, OutputPricePerMTok = 1.1m },
            new ModelInfo { Id = "deepseek-reasoner", DisplayName = "DeepSeek Reasoner",      Provider = "deepseek", InputPricePerMTok = 0.55m, OutputPricePerMTok = 2.19m },
        };
    }

    /// <summary>Local Ollama through its OpenAI-compatible endpoint. No key required.</summary>
    public sealed class OllamaProvider : OpenAiCompatibleProvider
    {
        private readonly string _baseUrl;

        public OllamaProvider(string baseUrl = "http://localhost:11434/v1") : base(() => null)
            => _baseUrl = baseUrl.TrimEnd('/');

        public override string Name => "ollama";
        public override bool RequiresApiKey => false;
        protected override string BaseUrl => _baseUrl;

        protected override IReadOnlyList<ModelInfo> FallbackModels { get; } = new[]
        {
            new ModelInfo { Id = "llama3.1", DisplayName = "Llama 3.1 (local)", Provider = "ollama" },
        };
    }
}
