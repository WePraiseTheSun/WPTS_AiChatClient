using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using AiChat.Control.Data;
using AiChat.Control.Models;

namespace AiChat.Control.Services
{
    /// <summary>Conversation export to Markdown / JSON, and full JSON backup import (spec §4).</summary>
    public sealed class ConversationExporter
    {
        private readonly ChatDatabase _db;

        public ConversationExporter(ChatDatabase db) => _db = db;

        public string ExportMarkdown(string sessionId)
        {
            var session = _db.GetSession(sessionId);
            if (session == null) return null;

            var sb = new StringBuilder();
            sb.AppendLine("# " + session.Title);
            sb.AppendLine();
            sb.AppendLine($"*Provider: {session.Provider ?? "-"} · Model: {session.Model ?? "-"} · Created: {session.CreatedAt:yyyy-MM-dd HH:mm} UTC*");
            if (!string.IsNullOrWhiteSpace(session.SystemPrompt))
            {
                sb.AppendLine();
                sb.AppendLine("> **System prompt:** " + session.SystemPrompt.Replace("\n", "\n> "));
            }
            sb.AppendLine();

            foreach (var m in _db.GetMessages(sessionId, includeAttachmentData: false))
            {
                string who = m.Role == ChatRole.User ? "You"
                           : m.Role == ChatRole.Assistant ? "Assistant" + (m.Model != null ? $" ({m.Model})" : "")
                           : m.Role.ToString();
                sb.AppendLine($"## {who}");
                sb.AppendLine();
                sb.AppendLine(m.Content);
                foreach (var a in m.Attachments)
                    sb.AppendLine($"*[attachment: {a.FileName} ({a.MimeType})]*");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        public string ExportJson(string sessionId)
        {
            var session = _db.GetSession(sessionId);
            if (session == null) return null;
            var messages = _db.GetMessages(sessionId);

            var dto = new
            {
                format = "aichat-session/v1",
                session = new
                {
                    session.Id,
                    session.Title,
                    session.Provider,
                    session.Model,
                    session.SystemPrompt,
                    session.CreatedAt,
                    session.Pinned,
                    session.Tags
                },
                messages = messages.Select(m => new
                {
                    m.Id,
                    role = m.Role.ToString().ToLowerInvariant(),
                    m.Content,
                    m.ContentType,
                    m.Tokens,
                    m.Model,
                    m.CreatedAt,
                    attachments = m.Attachments.Select(a => new
                    {
                        a.FileName,
                        a.MimeType,
                        dataBase64 = a.Data != null ? Convert.ToBase64String(a.Data) : null
                    })
                })
            };
            return JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>Restores a session previously exported with <see cref="ExportJson"/>. Returns the new session id.</summary>
        public string ImportJson(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                var s = root.GetProperty("session");

                var session = new ChatSession
                {
                    Title = s.GetProperty("Title").GetString() + " (imported)",
                    Provider = s.TryGetProperty("Provider", out var p) ? p.GetString() : null,
                    Model = s.TryGetProperty("Model", out var mo) ? mo.GetString() : null,
                    SystemPrompt = s.TryGetProperty("SystemPrompt", out var sp) ? sp.GetString() ?? "" : "",
                    Tags = s.TryGetProperty("Tags", out var tg) ? tg.GetString() ?? "" : ""
                };
                _db.UpsertSession(session);

                foreach (var m in root.GetProperty("messages").EnumerateArray())
                {
                    var msg = new ChatMessage
                    {
                        SessionId = session.Id,
                        Role = (ChatRole)Enum.Parse(typeof(ChatRole), m.GetProperty("role").GetString(), true),
                        Content = m.GetProperty("Content").GetString() ?? "",
                        ContentType = m.TryGetProperty("ContentType", out var ctp) ? ctp.GetString() : "text/markdown",
                        Tokens = m.TryGetProperty("Tokens", out var tk) ? tk.GetInt32() : 0,
                        Model = m.TryGetProperty("Model", out var md) && md.ValueKind == JsonValueKind.String ? md.GetString() : null,
                        CreatedAt = m.TryGetProperty("CreatedAt", out var ca) ? ca.GetDateTimeOffset() : DateTimeOffset.UtcNow
                    };

                    if (m.TryGetProperty("attachments", out var atts))
                    {
                        foreach (var a in atts.EnumerateArray())
                        {
                            string b64 = a.TryGetProperty("dataBase64", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                            msg.Attachments.Add(new ChatAttachment
                            {
                                FileName = a.TryGetProperty("FileName", out var fn) ? fn.GetString() : null,
                                MimeType = a.TryGetProperty("MimeType", out var mt) ? mt.GetString() : null,
                                Data = b64 != null ? Convert.FromBase64String(b64) : null
                            });
                        }
                    }
                    _db.InsertMessage(msg);
                }
                return session.Id;
            }
        }
    }
}
