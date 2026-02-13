using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

public class LLMMessage {
    [JsonIgnore] public string Id { get; private set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("role")] public string Role { get; set; }
    [JsonPropertyName("content")] public object Content { get; set; }
    [JsonPropertyName("tool_call_id")] public string ToolCallId { get; set; }
    [JsonPropertyName("name")] public string ToolName { get; set; }
    [JsonPropertyName("tool_calls")] public List<ToolCall> ToolCalls { get; set; }

    // OpenRouter reasoning fields (persist for continuity)
    [JsonPropertyName("reasoning")] public string Reasoning { get; set; }
    [JsonPropertyName("reasoning_details")] public JsonNode ReasoningDetails { get; set; }

    public LLMMessage() { }

    // Copy constructor preserves stable Id
    public LLMMessage(LLMMessage other) {
        if (other == null) return;
        Id = other.Id;
        Role = other.Role;
        ToolCallId = other.ToolCallId;
        ToolName = other.ToolName;
        Content = CloneContent(other.Content);
        Reasoning = other.Reasoning;
        ReasoningDetails = CloneJsonNode(other.ReasoningDetails);
        if (other.ToolCalls != null) {
            ToolCalls = new List<ToolCall>(other.ToolCalls.Count);
            foreach (var tc in other.ToolCalls) ToolCalls.Add(CloneToolCall(tc));
        }
    }

    public void AppendContentPart(ContentPart part) {
        if (Content == null) Content = new List<ContentPart>();
        if (Content is string) Content = new List<ContentPart> { part };
        ((List<ContentPart>)Content).Add(part);
    }

    public static LLMMessage FromText(string role, string text) => new() { Role = role, Content = text };
    public static LLMMessage FromMultiModal(string role, List<ContentPart> parts) => new() { Role = role, Content = parts };
    public static LLMMessage FromToolCallResponse(ToolCall toolCall, string content) => new() {
        Role = "tool",
        ToolCallId = toolCall.Id,
        ToolName = toolCall.Function.Name,
        Content = new List<ContentPart> { ContentPart.FromText(content) }
    };
    public static LLMMessage FromToolCallResponse(ToolCall toolCall, List<ContentPart> parts) => new() {
        Role = "tool", ToolCallId = toolCall.Id, ToolName = toolCall.Function.Name, Content = parts
    };

    // Helpers for cloning content/tool calls
    private static object CloneContent(object content) {
        if (content == null) return null;
        if (content is string s) return s; // strings are immutable
        if (content is List<ContentPart> parts) {
            var list = new List<ContentPart>(parts.Count);
            foreach (var p in parts) list.Add(new ContentPart(p));
            return list;
        }
        // Fallback: keep reference for unknown types (debug-only rendering will ToString())
        return content;
    }

    private static JsonNode CloneJsonNode(JsonNode node) {
        if (node == null) return null;
        try { return JsonNode.Parse(node.ToJsonString()); } catch { return null; }
    }

    private static ToolCall CloneToolCall(ToolCall tc) {
        if (tc == null) return null;
        return new ToolCall {
            Index = tc.Index,
            Id = tc.Id,
            Type = tc.Type,
            Function = tc.Function != null ? new ToolFunction { Name = tc.Function.Name, RawArguments = tc.Function.RawArguments } : null
        };
    }
}

public class ContentPart {
    [JsonPropertyName("type")] public string Type { get; set; }
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Text { get; set; }
    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ImageUrl ImageUrl { get; set; }
    [JsonPropertyName("cache_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public CacheControl CacheControl { get; set; }

    public ContentPart() { }
    public ContentPart(ContentPart other) {
        if (other == null) return;
        Type = other.Type;
        Text = other.Text;
        ImageUrl = other.ImageUrl != null ? new ImageUrl { Url = other.ImageUrl.Url } : null;
        CacheControl = other.CacheControl != null ? new CacheControl(other.CacheControl) : null;
    }

    public static ContentPart FromText(string text) => new() { Type = "text", Text = text };
    public static ContentPart FromImageUrl(string url) => new() { Type = "image_url", ImageUrl = new ImageUrl { Url = url } };
}

public class ImageUrl { [JsonPropertyName("url")] public string Url { get; set; } }

public class CacheControl {
    [JsonPropertyName("type")] public string Type { get; set; }
    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Ttl { get; set; }

    public CacheControl() { }
    public CacheControl(CacheControl other) {
        if (other == null) return;
        Type = other.Type;
        Ttl = other.Ttl;
    }
}

public class Tool {
    [JsonPropertyName("type")] public string Type { get; set; }
    [JsonPropertyName("function")] public Function Function { get; set; }
}

public class Function {
    [JsonPropertyName("name")] public string Name { get; set; }
    [JsonPropertyName("description")] public string Description { get; set; }
    [JsonPropertyName("parameters")] public object Parameters { get; set; }
    [JsonPropertyName("required")] public List<string> Required { get; set; }
}

public class Parameters {
    [JsonPropertyName("type")] public string Type { get; set; }
    [JsonPropertyName("properties")] public Dictionary<string, Property> Properties { get; set; }
    [JsonPropertyName("required")] public List<string> Required { get; set; }
}

public partial class Property {
    [JsonPropertyName("name")] public string Name { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; }
    [JsonPropertyName("description")] public string Description { get; set; }
    [JsonPropertyName("items")] public Dictionary<string, string> Items { get; set; }
}

internal class ProviderRoutingOptions {
    [JsonPropertyName("only")] public List<string> Only { get; set; }
    [JsonPropertyName("allow_fallbacks")] public bool? AllowFallbacks { get; set; }
}

internal class ChatCompletionRequest {
    [JsonPropertyName("model")] public string Model { get; set; }
    [JsonPropertyName("messages")] public List<LLMMessage> Messages { get; set; }
    [JsonPropertyName("tools")] public List<Tool> Tools { get; set; }
    [JsonPropertyName("reasoning")] public ReasoningOptions Reasoning { get; set; }
    [JsonPropertyName("provider")] public ProviderRoutingOptions Provider { get; set; }
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    [JsonPropertyName("temperature")] public float? Temperature { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }

}

internal class ReasoningOptions {
    [JsonPropertyName("effort")] public string Effort { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    [JsonPropertyName("exclude")] public bool Exclude { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
}

internal class StreamResponse { [JsonPropertyName("choices")] public List<StreamChoice> Choices { get; set; } }
internal class StreamChoice {
    [JsonPropertyName("delta")] public StreamDelta Delta { get; set; }
    [JsonPropertyName("finish_reason")] public string FinishReason { get; set; }
}

internal class CompletionResponse { [JsonPropertyName("choices")] public List<CompletionChoice> Choices { get; set; } }
internal class CompletionChoice {
    [JsonPropertyName("message")] public LLMMessage Message { get; set; }
    [JsonPropertyName("finish_reason")] public string FinishReason { get; set; }
}

internal class StreamDelta {
    [JsonPropertyName("content")] public string Content { get; set; }
    [JsonPropertyName("tool_calls")] public List<ToolCall> ToolCalls { get; set; }
    [JsonPropertyName("reasoning")] public string Reasoning { get; set; }
}

public class ToolCall {
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; }
    [JsonPropertyName("function")] public ToolFunction Function { get; set; }
}

public class ToolFunction {
    [JsonPropertyName("name")] public string Name { get; set; }
    [JsonPropertyName("arguments")] public string RawArguments { get; set; }

    [JsonIgnore] public JsonNode Arguments {
        get {
            if (string.IsNullOrEmpty(RawArguments)) return null;
            try { return JsonNode.Parse(RawArguments); } catch { return null; }
        }
    }
}

public class OpenRouterUsage {
    [JsonPropertyName("prompt_tokens")] public int? PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public int? CompletionTokens { get; set; }
    [JsonPropertyName("total_tokens")] public int? TotalTokens { get; set; }
    [JsonPropertyName("cost")] public double? Cost { get; set; }
    [JsonPropertyName("prompt_tokens_details")] public OpenRouterUsagePromptDetails PromptTokensDetails { get; set; }
    [JsonPropertyName("completion_tokens_details")] public OpenRouterUsageCompletionDetails CompletionTokensDetails { get; set; }
}

public class OpenRouterUsagePromptDetails {
    [JsonPropertyName("cached_tokens")] public int? CachedTokens { get; set; }
    [JsonPropertyName("cache_write_tokens")] public int? CacheWriteTokens { get; set; }
    [JsonPropertyName("audio_tokens")] public int? AudioTokens { get; set; }
}

public class OpenRouterUsageCompletionDetails {
    [JsonPropertyName("reasoning_tokens")] public int? ReasoningTokens { get; set; }
}
