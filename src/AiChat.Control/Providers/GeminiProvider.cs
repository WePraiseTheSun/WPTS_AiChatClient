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
    /// <summary>Google Gemini via generativelanguage.googleapis.com, SSE streaming.</summary>
    public sealed class GeminiProvider : ProviderBase, IAiProvider
    {
        private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";
        private readonly Func<string> _apiKeyAccessor;

        public GeminiProvider(Func<string> apiKeyAccessor) => _apiKeyAccessor = apiKeyAccessor;

        public string Name => "gemini";
        public bool RequiresApiKey => true;

        private static readonly ModelInfo[] FallbackModels =
        {
            new ModelInfo { Id = "gemini-2.0-flash", DisplayName = "Gemini 2.0 Flash", Provider = "gemini", SupportsVision = true, InputPricePerMTok = 0.1m,  OutputPricePerMTok = 0.4m },
            new ModelInfo { Id = "gemini-1.5-pro",   DisplayName = "Gemini 1.5 Pro",   Provider = "gemini", SupportsVision = true, InputPricePerMTok = 1.25m, OutputPricePerMTok = 5m },
        };

        public async Task<IEnumerable<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
        {
            string key = _apiKeyAccessor();
            if (string.IsNullOrWhiteSpace(key)) return FallbackModels;

            try
            {
                using (var resp = await Http.GetAsync($"{BaseUrl}/models?key={Uri.EscapeDataString(key)}", ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return FallbackModels;
                    string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using (var doc = JsonDocument.Parse(json))
                    {
                        var list = new List<ModelInfo>();
                        foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
                        {
                            var methods = m.TryGetProperty("supportedGenerationMethods", out var sm)
                                ? sm.EnumerateArray().Select(x => x.GetString()).ToArray()
                                : Array.Empty<string>();
                            if (!methods.Contains("generateContent")) continue;

                            string id = m.GetProperty("name").GetString()?.Replace("models/", "");
                            var known = FallbackModels.FirstOrDefault(f => f.Id == id);
                            list.Add(new ModelInfo
                            {
                                Id = id,
                                DisplayName = m.TryGetProperty("displayName", out var dn) ? dn.GetString() : id,
                                Provider = Name,
                                SupportsVision = true,
                                InputPricePerMTok = known?.InputPricePerMTok ?? 0m,
                                OutputPricePerMTok = known?.OutputPricePerMTok ?? 0m
                            });
                        }
                        return list.Count > 0 ? list : (IEnumerable<ModelInfo>)FallbackModels;
                    }
                }
            }
            catch
            {
                return FallbackModels;
            }
        }

        public async IAsyncEnumerable<ChatDelta> StreamChatAsync(
            ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            string key = _apiKeyAccessor();
            if (string.IsNullOrWhiteSpace(key))
            {
                yield return ChatDelta.OfError("No Gemini API key configured. Add one in Settings.");
                yield break;
            }

            string payload = BuildPayload(request);
            string url = $"{BaseUrl}/models/{request.Model}:streamGenerateContent?alt=sse&key={Uri.EscapeDataString(key)}";

            HttpResponseMessage response = await SendWithRetryAsync(() =>
                new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                }, ct).ConfigureAwait(false);

            using (response)
            {
                int? inputTokens = null, outputTokens = null;

                await foreach (SseEvent evt in SseReader.ReadAsync(response, ct).ConfigureAwait(false))
                {
                    ChatDelta mapped = null;
                    try { mapped = MapEvent(evt.Data, ref inputTokens, ref outputTokens); }
                    catch (JsonException) { }

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

                if (root.TryGetProperty("error", out var err))
                {
                    string msg = err.TryGetProperty("message", out var em) ? em.GetString() : err.ToString();
                    return ChatDelta.OfError(msg);
                }

                if (root.TryGetProperty("usageMetadata", out var usage))
                {
                    if (usage.TryGetProperty("promptTokenCount", out var pt)) inputTokens = pt.GetInt32();
                    if (usage.TryGetProperty("candidatesTokenCount", out var ctk)) outputTokens = ctk.GetInt32();
                }

                if (root.TryGetProperty("candidates", out var cands) && cands.GetArrayLength() > 0)
                {
                    var cand = cands[0];
                    if (cand.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts))
                    {
                        var sb = new StringBuilder();
                        foreach (var part in parts.EnumerateArray())
                            if (part.TryGetProperty("text", out var t)) sb.Append(t.GetString());
                        if (sb.Length > 0) return ChatDelta.OfText(sb.ToString());
                    }
                }
                return null;
            }
        }

        private static string BuildPayload(ChatRequest request)
        {
            using (var ms = new System.IO.MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();

                    if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
                    {
                        w.WriteStartObject("systemInstruction");
                        w.WriteStartArray("parts");
                        w.WriteStartObject();
                        w.WriteString("text", request.SystemPrompt);
                        w.WriteEndObject();
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }

                    w.WriteStartObject("generationConfig");
                    w.WriteNumber("maxOutputTokens", request.MaxTokens);
                    if (request.Temperature.HasValue) w.WriteNumber("temperature", request.Temperature.Value);
                    w.WriteEndObject();

                    w.WriteStartArray("contents");
                    foreach (var m in request.Messages.Where(m => m.Role == ChatRole.User || m.Role == ChatRole.Assistant))
                    {
                        w.WriteStartObject();
                        w.WriteString("role", m.Role == ChatRole.User ? "user" : "model");
                        w.WriteStartArray("parts");

                        if (m.Attachments != null)
                        {
                            foreach (var a in m.Attachments)
                            {
                                w.WriteStartObject();
                                w.WriteStartObject("inlineData");
                                w.WriteString("mimeType", a.MimeType);
                                w.WriteString("data", Convert.ToBase64String(a.Data));
                                w.WriteEndObject();
                                w.WriteEndObject();
                            }
                        }

                        w.WriteStartObject();
                        w.WriteString("text", m.Content ?? "");
                        w.WriteEndObject();

                        w.WriteEndArray();
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
