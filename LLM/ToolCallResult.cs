using System.Collections.Generic;

public enum ToolCallStatus {
    Ok,
    Fail,
    Partial
}

public sealed class ToolCallResult {
    public ToolCallStatus Status { get; set; }
    public string Code { get; set; } // internal-only
    public string Message { get; set; } // internal-only
    public List<ContentPart> Parts { get; set; } = new();
}

public static class Results {
    public static ToolCallResult OkText(string text) => new() {
        Status = ToolCallStatus.Ok,
        Parts = new List<ContentPart> { ContentPart.FromText(text) }
    };

    public static ToolCallResult OkParts(List<ContentPart> parts) => new() {
        Status = ToolCallStatus.Ok,
        Parts = parts ?? new List<ContentPart>()
    };

    public static ToolCallResult PartialText(string text, string code = null, string message = null) => new() {
        Status = ToolCallStatus.Partial,
        Code = code,
        Message = message,
        Parts = new List<ContentPart> { ContentPart.FromText(text) }
    };

    public static ToolCallResult FailText(string text, string code = null, string message = null) => new() {
        Status = ToolCallStatus.Fail,
        Code = code,
        Message = message,
        Parts = new List<ContentPart> { ContentPart.FromText(text) }
    };
}

