using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using AiChat.Control.Models;

namespace AiChat.Control.Data
{
    /// <summary>
    /// SQLite persistence in the C# host (spec §2, §3.2): Sessions / Messages / Attachments,
    /// full-text search via FTS5 (external-content table kept in sync by triggers), plus a
    /// small Settings key/value table for UI preferences and prompt templates.
    /// </summary>
    public sealed class ChatDatabase : IDisposable
    {
        private readonly string _connectionString;

        public ChatDatabase(string dataDirectory)
        {
            Directory.CreateDirectory(dataDirectory);
            string dbPath = Path.Combine(dataDirectory, "aichat.db");
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();
            Initialize();
        }

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
                cmd.ExecuteNonQuery();
            }
            return conn;
        }

        private void Initialize()
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Sessions(
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    provider TEXT,
    model TEXT,
    systemPrompt TEXT NOT NULL DEFAULT '',
    createdAt TEXT NOT NULL,
    updatedAt TEXT NOT NULL,
    pinned INTEGER NOT NULL DEFAULT 0,
    tags TEXT NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS Messages(
    id TEXT PRIMARY KEY,
    sessionId TEXT NOT NULL REFERENCES Sessions(id) ON DELETE CASCADE,
    role TEXT NOT NULL,
    content TEXT NOT NULL,
    thinking TEXT,
    contentType TEXT NOT NULL DEFAULT 'text/markdown',
    tokens INTEGER NOT NULL DEFAULT 0,
    model TEXT,
    createdAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Messages_Session ON Messages(sessionId, createdAt);

CREATE TABLE IF NOT EXISTS Attachments(
    id TEXT PRIMARY KEY,
    messageId TEXT NOT NULL REFERENCES Messages(id) ON DELETE CASCADE,
    fileName TEXT,
    mimeType TEXT,
    data BLOB
);
CREATE INDEX IF NOT EXISTS IX_Attachments_Message ON Attachments(messageId);

CREATE TABLE IF NOT EXISTS Settings(
    key TEXT PRIMARY KEY,
    value TEXT
);

CREATE VIRTUAL TABLE IF NOT EXISTS MessagesFts USING fts5(
    content,
    content='Messages',
    content_rowid='rowid'
);

CREATE TRIGGER IF NOT EXISTS Messages_ai AFTER INSERT ON Messages BEGIN
    INSERT INTO MessagesFts(rowid, content) VALUES (new.rowid, new.content);
END;
CREATE TRIGGER IF NOT EXISTS Messages_ad AFTER DELETE ON Messages BEGIN
    INSERT INTO MessagesFts(MessagesFts, rowid, content) VALUES ('delete', old.rowid, old.content);
END;
CREATE TRIGGER IF NOT EXISTS Messages_au AFTER UPDATE OF content ON Messages BEGIN
    INSERT INTO MessagesFts(MessagesFts, rowid, content) VALUES ('delete', old.rowid, old.content);
    INSERT INTO MessagesFts(rowid, content) VALUES (new.rowid, new.content);
END;";
                cmd.ExecuteNonQuery();
            }

            // Guarded migration: databases created before the thinking column existed.
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                try
                {
                    cmd.CommandText = "ALTER TABLE Messages ADD COLUMN thinking TEXT";
                    cmd.ExecuteNonQuery();
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Column already exists — nothing to migrate.
                }
            }
        }

        public List<ChatSession> ListSessions()
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT id,title,provider,model,systemPrompt,createdAt,updatedAt,pinned,tags
                                    FROM Sessions ORDER BY pinned DESC, updatedAt DESC";
                using (var r = cmd.ExecuteReader())
                {
                    var list = new List<ChatSession>();
                    while (r.Read()) list.Add(ReadSession(r));
                    return list;
                }
            }
        }

        public ChatSession GetSession(string id)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT id,title,provider,model,systemPrompt,createdAt,updatedAt,pinned,tags
                                    FROM Sessions WHERE id=@id";
                cmd.Parameters.AddWithValue("@id", id);
                using (var r = cmd.ExecuteReader())
                    return r.Read() ? ReadSession(r) : null;
            }
        }

        private static ChatSession ReadSession(SqliteDataReader r) => new ChatSession
        {
            Id = r.GetString(0),
            Title = r.GetString(1),
            Provider = r.IsDBNull(2) ? null : r.GetString(2),
            Model = r.IsDBNull(3) ? null : r.GetString(3),
            SystemPrompt = r.GetString(4),
            CreatedAt = DateTimeOffset.Parse(r.GetString(5)),
            UpdatedAt = DateTimeOffset.Parse(r.GetString(6)),
            Pinned = r.GetInt32(7) != 0,
            Tags = r.GetString(8)
        };

        public void UpsertSession(ChatSession s)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO Sessions(id,title,provider,model,systemPrompt,createdAt,updatedAt,pinned,tags)
VALUES(@id,@title,@provider,@model,@sys,@created,@updated,@pinned,@tags)
ON CONFLICT(id) DO UPDATE SET
    title=@title, provider=@provider, model=@model, systemPrompt=@sys,
    updatedAt=@updated, pinned=@pinned, tags=@tags";
                cmd.Parameters.AddWithValue("@id", s.Id);
                cmd.Parameters.AddWithValue("@title", s.Title ?? "New chat");
                cmd.Parameters.AddWithValue("@provider", (object)s.Provider ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@model", (object)s.Model ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@sys", s.SystemPrompt ?? "");
                cmd.Parameters.AddWithValue("@created", s.CreatedAt.ToString("o"));
                cmd.Parameters.AddWithValue("@updated", s.UpdatedAt.ToString("o"));
                cmd.Parameters.AddWithValue("@pinned", s.Pinned ? 1 : 0);
                cmd.Parameters.AddWithValue("@tags", s.Tags ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        public void TouchSession(string id)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE Sessions SET updatedAt=@u WHERE id=@id";
                cmd.Parameters.AddWithValue("@u", DateTimeOffset.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
        }

        public void DeleteSession(string id)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM Sessions WHERE id=@id";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
        }

        // ------------------------------------------------------------------ Messages

        public List<ChatMessage> GetMessages(string sessionId, bool includeAttachmentData = true)
        {
            using (var conn = Open())
            {
                var messages = new List<ChatMessage>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT id,sessionId,role,content,contentType,tokens,model,createdAt,thinking
                                        FROM Messages WHERE sessionId=@sid ORDER BY createdAt, rowid";
                    cmd.Parameters.AddWithValue("@sid", sessionId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            messages.Add(new ChatMessage
                            {
                                Id = r.GetString(0),
                                SessionId = r.GetString(1),
                                Role = (ChatRole)Enum.Parse(typeof(ChatRole), r.GetString(2), true),
                                Content = r.GetString(3),
                                ContentType = r.GetString(4),
                                Tokens = r.GetInt32(5),
                                Model = r.IsDBNull(6) ? null : r.GetString(6),
                                CreatedAt = DateTimeOffset.Parse(r.GetString(7)),
                                Thinking = r.IsDBNull(8) ? null : r.GetString(8)
                            });
                        }
                    }
                }

                foreach (var m in messages)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = includeAttachmentData
                            ? "SELECT id,fileName,mimeType,data FROM Attachments WHERE messageId=@mid"
                            : "SELECT id,fileName,mimeType,NULL FROM Attachments WHERE messageId=@mid";
                        cmd.Parameters.AddWithValue("@mid", m.Id);
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                m.Attachments.Add(new ChatAttachment
                                {
                                    Id = r.GetString(0),
                                    FileName = r.IsDBNull(1) ? null : r.GetString(1),
                                    MimeType = r.IsDBNull(2) ? null : r.GetString(2),
                                    Data = r.IsDBNull(3) ? null : (byte[])r.GetValue(3)
                                });
                            }
                        }
                    }
                }
                return messages;
            }
        }

        public void InsertMessage(ChatMessage m)
        {
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO Messages(id,sessionId,role,content,thinking,contentType,tokens,model,createdAt)
                                        VALUES(@id,@sid,@role,@content,@thinking,@ctype,@tokens,@model,@created)";
                    cmd.Parameters.AddWithValue("@id", m.Id);
                    cmd.Parameters.AddWithValue("@sid", m.SessionId);
                    cmd.Parameters.AddWithValue("@role", m.Role.ToString().ToLowerInvariant());
                    cmd.Parameters.AddWithValue("@content", m.Content ?? "");
                    cmd.Parameters.AddWithValue("@thinking", (object)m.Thinking ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ctype", m.ContentType ?? "text/markdown");
                    cmd.Parameters.AddWithValue("@tokens", m.Tokens);
                    cmd.Parameters.AddWithValue("@model", (object)m.Model ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@created", m.CreatedAt.ToString("o"));
                    cmd.ExecuteNonQuery();
                }

                foreach (var a in m.Attachments ?? Enumerable.Empty<ChatAttachment>())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"INSERT INTO Attachments(id,messageId,fileName,mimeType,data)
                                            VALUES(@id,@mid,@name,@mime,@data)";
                        cmd.Parameters.AddWithValue("@id", a.Id);
                        cmd.Parameters.AddWithValue("@mid", m.Id);
                        cmd.Parameters.AddWithValue("@name", (object)a.FileName ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@mime", (object)a.MimeType ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@data", (object)a.Data ?? DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
        }

        public void UpdateMessageContent(string messageId, string content)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE Messages SET content=@c WHERE id=@id";
                cmd.Parameters.AddWithValue("@c", content ?? "");
                cmd.Parameters.AddWithValue("@id", messageId);
                cmd.ExecuteNonQuery();
            }
        }

        public void UpdateMessageTokens(string messageId, int tokens)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE Messages SET tokens=@t WHERE id=@id";
                cmd.Parameters.AddWithValue("@t", tokens);
                cmd.Parameters.AddWithValue("@id", messageId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Deletes the given message and everything after it in the session (edit &amp; fork, spec §3.1).</summary>
        public void DeleteMessageAndAfter(string sessionId, string messageId, bool inclusive)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
DELETE FROM Messages WHERE sessionId=@sid AND rowid >= (
    SELECT rowid + CASE WHEN @inclusive=1 THEN 0 ELSE 1 END
    FROM Messages WHERE id=@mid)";
                cmd.Parameters.AddWithValue("@sid", sessionId);
                cmd.Parameters.AddWithValue("@mid", messageId);
                cmd.Parameters.AddWithValue("@inclusive", inclusive ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
        }

        // ------------------------------------------------------------------ Branching (spec §3.2)

        /// <summary>
        /// Duplicates a session up to and including <paramref name="upToMessageId"/> (or the whole
        /// session when null) so alternate continuations can be compared side by side.
        /// </summary>
        public ChatSession BranchSession(string sourceSessionId, string upToMessageId)
        {
            var source = GetSession(sourceSessionId);
            if (source == null) return null;

            var branch = new ChatSession
            {
                Title = source.Title + " (branch)",
                Provider = source.Provider,
                Model = source.Model,
                SystemPrompt = source.SystemPrompt,
                Tags = source.Tags
            };
            UpsertSession(branch);

            bool reached = false;
            foreach (var m in GetMessages(sourceSessionId))
            {
                if (reached) break;
                if (m.Id == upToMessageId) reached = true;

                InsertMessage(new ChatMessage
                {
                    SessionId = branch.Id,
                    Role = m.Role,
                    Content = m.Content,
                    Thinking = m.Thinking,
                    ContentType = m.ContentType,
                    Tokens = m.Tokens,
                    Model = m.Model,
                    CreatedAt = m.CreatedAt,
                    Attachments = m.Attachments.Select(a => new ChatAttachment
                    {
                        FileName = a.FileName,
                        MimeType = a.MimeType,
                        Data = a.Data
                    }).ToList()
                });
            }
            return branch;
        }

        // ------------------------------------------------------------------ Search (FTS5, spec §3.2)

        public List<SearchHit> Search(string query, int limit = 40)
        {
            var hits = new List<SearchHit>();
            if (string.IsNullOrWhiteSpace(query)) return hits;

            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT m.sessionId, s.title, m.id, m.role,
       snippet(MessagesFts, 0, '<mark>', '</mark>', '…', 12)
FROM MessagesFts
JOIN Messages m ON m.rowid = MessagesFts.rowid
JOIN Sessions s ON s.id = m.sessionId
WHERE MessagesFts MATCH @q
ORDER BY rank LIMIT @limit";
                // Quote the user text so FTS5 operators in user input can't break the query.
                cmd.Parameters.AddWithValue("@q", "\"" + query.Replace("\"", "\"\"") + "\"");
                cmd.Parameters.AddWithValue("@limit", limit);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        hits.Add(new SearchHit
                        {
                            SessionId = r.GetString(0),
                            SessionTitle = r.GetString(1),
                            MessageId = r.GetString(2),
                            Role = r.GetString(3),
                            Snippet = r.GetString(4)
                        });
                    }
                }
            }
            return hits;
        }

        // ------------------------------------------------------------------ Settings

        public string GetSetting(string key, string fallback = null)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT value FROM Settings WHERE key=@k";
                cmd.Parameters.AddWithValue("@k", key);
                object v = cmd.ExecuteScalar();
                return v == null || v is DBNull ? fallback : (string)v;
            }
        }

        public void SetSetting(string key, string value)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO Settings(key,value) VALUES(@k,@v)
                                    ON CONFLICT(key) DO UPDATE SET value=@v";
                cmd.Parameters.AddWithValue("@k", key);
                cmd.Parameters.AddWithValue("@v", (object)value ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
        }
    }
}
