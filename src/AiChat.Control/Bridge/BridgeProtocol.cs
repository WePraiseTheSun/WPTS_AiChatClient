using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiChat.Control.Bridge
{
    /// <summary>
    /// JS → C# envelope: { "cmd": "...", "requestId": "...", "payload": { ... } }.
    /// Every command must be whitelisted and its payload validated before dispatch (spec §3.7).
    /// JS can never pass a file-system path across the bridge — paths only ever come from a
    /// SaveFileDialog / OpenFileDialog the C# side shows.
    /// </summary>
    public sealed class BridgeCommand
    {
        public string Cmd { get; private set; }
        public string RequestId { get; private set; }
        public JsonElement Payload { get; private set; }

        private static readonly Regex IdPattern = new Regex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

        /// <summary>The complete whitelist. Anything else is rejected before dispatch.</summary>
        public static readonly HashSet<string> Whitelist = new HashSet<string>(StringComparer.Ordinal)
        {
            "init",
            "listSessions", "createSession", "renameSession", "deleteSession",
            "pinSession", "tagSession", "loadSession", "setSystemPrompt",
            "sendMessage", "stopGeneration", "regenerate", "editAndResend", "branchSession",
            "runCode", "stopRun",
            "search",
            "listModels", "setSessionModel",
            "saveApiKey", "deleteApiKey",
            "getSettings", "setSetting",
            "listTemplates", "saveTemplate", "deleteTemplate",
            "exportSession", "importSession",
            "saveBinaryFile", "pickAttachment",
            "openExternal"
        };

        public static bool TryParse(string json, out BridgeCommand command, out string error)
        {
            command = null;
            error = null;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) { error = "Envelope must be an object."; return false; }

                    if (!root.TryGetProperty("cmd", out var cmdProp) || cmdProp.ValueKind != JsonValueKind.String)
                    { error = "Missing 'cmd'."; return false; }

                    string cmd = cmdProp.GetString();
                    if (!Whitelist.Contains(cmd)) { error = $"Command '{cmd}' is not whitelisted."; return false; }

                    string requestId = root.TryGetProperty("requestId", out var rid) && rid.ValueKind == JsonValueKind.String
                        ? rid.GetString() : null;
                    if (requestId != null && !IdPattern.IsMatch(requestId))
                    { error = "Invalid requestId."; return false; }

                    JsonElement payload = root.TryGetProperty("payload", out var pl)
                        ? pl.Clone()
                        : default;

                    command = new BridgeCommand { Cmd = cmd, RequestId = requestId, Payload = payload };
                    return true;
                }
            }
            catch (JsonException ex)
            {
                error = "Malformed JSON: " + ex.Message;
                return false;
            }
        }

        // ---- strict payload accessors -------------------------------------------------

        public string GetString(string name, int maxLength = 1_000_000, bool required = true)
        {
            if (Payload.ValueKind == JsonValueKind.Object &&
                Payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                string s = v.GetString() ?? "";
                if (s.Length > maxLength) throw new BridgeValidationException($"'{name}' exceeds {maxLength} chars.");
                return s;
            }
            if (required) throw new BridgeValidationException($"Missing string field '{name}'.");
            return null;
        }

        /// <summary>Identifiers (session / message / template ids) must match a strict pattern.</summary>
        public string GetId(string name, bool required = true)
        {
            string s = GetString(name, 64, required);
            if (s == null) return null;
            if (!IdPattern.IsMatch(s)) throw new BridgeValidationException($"'{name}' is not a valid id.");
            return s;
        }

        public bool GetBool(string name, bool fallback = false)
        {
            if (Payload.ValueKind == JsonValueKind.Object &&
                Payload.TryGetProperty(name, out var v) &&
                (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                return v.GetBoolean();
            return fallback;
        }

        public JsonElement? GetArray(string name)
        {
            if (Payload.ValueKind == JsonValueKind.Object &&
                Payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
                return v;
            return null;
        }
    }

    public sealed class BridgeValidationException : Exception
    {
        public BridgeValidationException(string message) : base(message) { }
    }
}
