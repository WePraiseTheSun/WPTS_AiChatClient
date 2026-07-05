using System;
using System.Collections.Generic;

namespace AiChat.Control.Models
{
    public enum ChatRole
    {
        System,
        User,
        Assistant,
        Tool
    }

    /// <summary>One attachment travelling with a message (image / pdf for vision models).</summary>
    public sealed class ChatAttachment
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string FileName { get; set; }
        public string MimeType { get; set; }
        /// <summary>Raw bytes; persisted in the Attachments table, sent base64 to providers.</summary>
        public byte[] Data { get; set; }
        /// <summary>
        /// Provider-side file id once uploaded (Anthropic Files API). Cached per process so
        /// repeated turns in the same conversation don't re-upload the same bytes.
        /// </summary>
        public string ProviderFileId { get; set; }
    }

    public sealed class ChatMessage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string SessionId { get; set; }
        public ChatRole Role { get; set; }
        public string Content { get; set; } = "";
        /// <summary>Model reasoning/thinking text captured alongside the answer, when available.</summary>
        public string Thinking { get; set; }
        /// <summary>"text/markdown" for normal chat, "image/png" etc. for media messages.</summary>
        public string ContentType { get; set; } = "text/markdown";
        public int Tokens { get; set; }
        /// <summary>Model that produced this message (assistant messages only).</summary>
        public string Model { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public List<ChatAttachment> Attachments { get; set; } = new List<ChatAttachment>();
    }

    public sealed class ChatSession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "New chat";
        public string Provider { get; set; }
        public string Model { get; set; }
        public string SystemPrompt { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public bool Pinned { get; set; }
        public string Tags { get; set; } = "";
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class ModelInfo
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Provider { get; set; }
        public bool SupportsVision { get; set; }
        /// <summary>USD per 1M input / output tokens; 0 when unknown (e.g. local models).</summary>
        public decimal InputPricePerMTok { get; set; }
        public decimal OutputPricePerMTok { get; set; }

        /// <summary>True when the model exposes a reasoning / extended-thinking control.</summary>
        public bool SupportsThinking { get; set; }
        /// <summary>
        /// Which thinking dialect the model speaks: "anthropic" (thinking.type enabled/adaptive
        /// + budget_tokens) or "effort" (reasoning_effort low/medium/high, DeepSeek/OpenAI style).
        /// Null when SupportsThinking is false. The UI hides the control for such models.
        /// </summary>
        public string ThinkingKind { get; set; }
    }

    /// <summary>Normalized reasoning options; each provider maps them to its own wire format.</summary>
    public sealed class ThinkingOptions
    {
        /// <summary>"off" | "adaptive" (Anthropic only) | "enabled".</summary>
        public string Mode { get; set; } = "off";
        /// <summary>"low" | "medium" | "high" — effort level / budget bucket.</summary>
        public string Effort { get; set; } = "medium";

        public bool IsOn => Mode == "adaptive" || Mode == "enabled";

        /// <summary>Anthropic budget_tokens for extended thinking, derived from Effort.</summary>
        public int BudgetTokens =>
            Effort == "low" ? 4000 :
            Effort == "high" ? 24000 : 10000;
    }

    /// <summary>Provider-agnostic request. Providers translate this to their own wire format.</summary>
    public sealed class ChatRequest
    {
        public string Model { get; set; }
        public string SystemPrompt { get; set; }
        public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
        public int MaxTokens { get; set; } = 4096;
        public double? Temperature { get; set; }

        /// <summary>Reasoning controls; null or Mode=="off" means no thinking parameters are sent.</summary>
        public ThinkingOptions Thinking { get; set; }

        /// <summary>
        /// Tool definitions (JSON schema style). Carried now so function-calling / MCP-style
        /// tools can be added later without a breaking interface change (spec §3.6 / §5).
        /// </summary>
        public List<ToolDefinition> Tools { get; set; } = new List<ToolDefinition>();
    }

    public sealed class ToolDefinition
    {
        public string Name { get; set; }
        public string Description { get; set; }
        /// <summary>JSON schema of the tool input, as raw JSON.</summary>
        public string InputSchemaJson { get; set; }
    }

    public enum ChatDeltaType
    {
        /// <summary>Incremental assistant text.</summary>
        Text,
        /// <summary>Incremental reasoning / extended-thinking text.</summary>
        Thinking,
        /// <summary>A tool call has started (name + id known).</summary>
        ToolCallStart,
        /// <summary>Incremental tool-call arguments (JSON fragment).</summary>
        ToolCallDelta,
        /// <summary>Token usage report (usually once, at end of stream).</summary>
        Usage,
        /// <summary>Stream finished normally.</summary>
        Done,
        /// <summary>Provider-reported error inside the stream.</summary>
        Error
    }

    /// <summary>
    /// The single normalized streaming unit every provider emits. The frontend never
    /// needs to know which provider produced it (spec §3.6).
    /// </summary>
    public sealed class ChatDelta
    {
        public ChatDeltaType Type { get; set; }
        public string Text { get; set; }

        public string ToolCallId { get; set; }
        public string ToolName { get; set; }
        public string ToolArgumentsFragment { get; set; }

        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }

        public string StopReason { get; set; }
        public string ErrorMessage { get; set; }

        public static ChatDelta OfText(string text) => new ChatDelta { Type = ChatDeltaType.Text, Text = text };
        public static ChatDelta OfThinking(string text) => new ChatDelta { Type = ChatDeltaType.Thinking, Text = text };
        public static ChatDelta OfUsage(int? input, int? output) => new ChatDelta { Type = ChatDeltaType.Usage, InputTokens = input, OutputTokens = output };
        public static ChatDelta OfDone(string stopReason = null) => new ChatDelta { Type = ChatDeltaType.Done, StopReason = stopReason };
        public static ChatDelta OfError(string message) => new ChatDelta { Type = ChatDeltaType.Error, ErrorMessage = message };
    }

    public sealed class SearchHit
    {
        public string SessionId { get; set; }
        public string SessionTitle { get; set; }
        public string MessageId { get; set; }
        public string Snippet { get; set; }
        public string Role { get; set; }
    }

    public sealed class PromptTemplate
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; }
        public string Prompt { get; set; }
    }
}
