using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Lossless serialization and deserialization of LLMMessage histories,
/// including image content parts which are saved as sidecar files.
///
/// Two modes of operation:
///
/// 1. In-memory (JSON string) — for embedding in a larger save file:
///    string json = LLMHistorySerializer.Serialize(messages, options);
///    List&lt;LLMMessage&gt; msgs = LLMHistorySerializer.Deserialize(json);
///
/// 2. File-based (folder on disk) — fully self-contained with image files:
///    LLMHistorySerializer.SaveToFolder(histories, "user://saves/personas");
///    var histories = LLMHistorySerializer.LoadFromFolder("user://saves/personas");
///
///    Creates:
///      user://saves/personas/
///        history.json
///        images/
///          img_0.png
///          img_1.jpg
///
/// Image handling:
///   On save, data URI images (data:image/png;base64,...) are extracted, written
///   as binary files, and replaced with file references (file:images/img_0.png).
///   On load, file references are read back and converted to data URIs.
///   Non-data-URI image URLs are preserved as-is (they're external URLs).
/// </summary>
public static class LLMHistorySerializer {
    private static readonly JsonSerializerOptions JsonOpts = new() {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string HistoryFileName = "history.json";
    private const string ImagesDirName = "images";
    private const string FileRefPrefix = "file:";
    private const string DataUriPngPrefix = "data:image/png;base64,";
    private const string DataUriJpegPrefix = "data:image/jpeg;base64,";

    // ═══════════════════════════════════════════════════════
    // In-memory (JSON string) API
    // ═══════════════════════════════════════════════════════

    /// <summary>Serialize a list of LLMMessages to a JSON string.</summary>
    public static string Serialize(List<LLMMessage> messages, LLMSerializeOptions options = LLMSerializeOptions.None) {
        if (messages == null) return "[]";
        var ctx = new SerializeContext(options);
        var dtos = new List<LLMMessageDTO>(messages.Count);
        foreach (var msg in messages) {
            if (msg == null) continue;
            dtos.Add(LLMMessageDTO.FromLLMMessage(msg, ctx));
        }
        return JsonSerializer.Serialize(dtos, JsonOpts);
    }

    /// <summary>Deserialize a JSON string back into a list of LLMMessages.</summary>
    public static List<LLMMessage> Deserialize(string json) {
        if (string.IsNullOrWhiteSpace(json)) return new List<LLMMessage>();
        var dtos = JsonSerializer.Deserialize<List<LLMMessageDTO>>(json, JsonOpts);
        if (dtos == null) return new List<LLMMessage>();
        var result = new List<LLMMessage>(dtos.Count);
        foreach (var dto in dtos)
            result.Add(dto.ToLLMMessage(null));
        return result;
    }

    /// <summary>Serialize a dictionary of named histories to a JSON string.</summary>
    public static string SerializeAll(Dictionary<string, List<LLMMessage>> histories,
        LLMSerializeOptions options = LLMSerializeOptions.None) {
        if (histories == null) return "{}";
        var ctx = new SerializeContext(options);
        var dtoDict = new Dictionary<string, List<LLMMessageDTO>>();
        foreach (var (key, messages) in histories) {
            var dtos = new List<LLMMessageDTO>();
            if (messages != null) {
                foreach (var msg in messages) {
                    if (msg == null) continue;
                    dtos.Add(LLMMessageDTO.FromLLMMessage(msg, ctx));
                }
            }
            dtoDict[key] = dtos;
        }
        return JsonSerializer.Serialize(dtoDict, JsonOpts);
    }

    /// <summary>Deserialize a JSON string back into a dictionary of named histories.</summary>
    public static Dictionary<string, List<LLMMessage>> DeserializeAll(string json) {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, List<LLMMessage>>();
        var dtoDict = JsonSerializer.Deserialize<Dictionary<string, List<LLMMessageDTO>>>(json, JsonOpts);
        if (dtoDict == null) return new Dictionary<string, List<LLMMessage>>();
        var result = new Dictionary<string, List<LLMMessage>>();
        foreach (var (key, dtos) in dtoDict) {
            var messages = new List<LLMMessage>();
            if (dtos != null) {
                foreach (var dto in dtos)
                    messages.Add(dto.ToLLMMessage(null));
            }
            result[key] = messages;
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════
    // File-based API — self-contained folder with sidecar images
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Save a dictionary of named histories to a folder on disk.
    /// Images are extracted from data URIs and written as sidecar files.
    /// </summary>
    public static void SaveToFolder(Dictionary<string, List<LLMMessage>> histories, string folderPath) {
        if (histories == null) return;

        DirAccess.MakeDirRecursiveAbsolute(folderPath);
        string imagesDir = $"{folderPath}/{ImagesDirName}";

        // Clear old images
        ClearDirectory(imagesDir);
        DirAccess.MakeDirRecursiveAbsolute(imagesDir);

        var ctx = new SerializeContext(LLMSerializeOptions.None) {
            ImageDir = imagesDir,
            ExtractImages = true,
        };

        var dtoDict = new Dictionary<string, List<LLMMessageDTO>>();
        foreach (var (key, messages) in histories) {
            var dtos = new List<LLMMessageDTO>();
            if (messages != null) {
                foreach (var msg in messages) {
                    if (msg == null) continue;
                    dtos.Add(LLMMessageDTO.FromLLMMessage(msg, ctx));
                }
            }
            dtoDict[key] = dtos;
        }

        string json = JsonSerializer.Serialize(dtoDict, JsonOpts);
        string jsonPath = $"{folderPath}/{HistoryFileName}";

        using var file = FileAccess.Open(jsonPath, FileAccess.ModeFlags.Write);
        if (file == null) {
            GD.PushError($"[LLMHistorySerializer] Failed to write {jsonPath}: {FileAccess.GetOpenError()}");
            return;
        }
        file.StoreString(json);

        // Clean up images dir if no images were saved
        if (ctx.ImageCount == 0) {
            ClearDirectory(imagesDir);
            DirAccess.Open(folderPath)?.Remove(ImagesDirName);
        }

        GD.Print($"[LLMHistorySerializer] Saved {histories.Count} histories, {ctx.ImageCount} images → {folderPath}");
    }

    /// <summary>
    /// Load a dictionary of named histories from a folder on disk.
    /// File references in image_url parts are resolved back to data URIs.
    /// </summary>
    public static Dictionary<string, List<LLMMessage>> LoadFromFolder(string folderPath) {
        string jsonPath = $"{folderPath}/{HistoryFileName}";
        if (!FileAccess.FileExists(jsonPath)) {
            GD.Print($"[LLMHistorySerializer] No history file at {jsonPath}");
            return new Dictionary<string, List<LLMMessage>>();
        }

        using var file = FileAccess.Open(jsonPath, FileAccess.ModeFlags.Read);
        if (file == null) {
            GD.PushError($"[LLMHistorySerializer] Failed to read {jsonPath}: {FileAccess.GetOpenError()}");
            return new Dictionary<string, List<LLMMessage>>();
        }

        string json = file.GetAsText();
        var dtoDict = JsonSerializer.Deserialize<Dictionary<string, List<LLMMessageDTO>>>(json, JsonOpts);
        if (dtoDict == null) return new Dictionary<string, List<LLMMessage>>();

        var result = new Dictionary<string, List<LLMMessage>>();
        foreach (var (key, dtos) in dtoDict) {
            var messages = new List<LLMMessage>();
            if (dtos != null) {
                foreach (var dto in dtos)
                    messages.Add(dto.ToLLMMessage(folderPath));
            }
            result[key] = messages;
        }

        GD.Print($"[LLMHistorySerializer] Loaded {result.Count} histories from {folderPath}");
        return result;
    }

    /// <summary>Delete a history folder and all its contents (JSON + images).</summary>
    public static void DeleteFolder(string folderPath) {
        string imagesDir = $"{folderPath}/{ImagesDirName}";
        ClearDirectory(imagesDir);

        var dir = DirAccess.Open(folderPath);
        if (dir != null) {
            dir.Remove(ImagesDirName);
            dir.Remove(HistoryFileName);
        }
        DirAccess.Open(folderPath + "/..")?.Remove(folderPath.GetFile());
    }

    // ═══════════════════════════════════════════════════════
    // Image I/O helpers
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Write a data URI image to disk, returning the relative file reference.
    /// Returns null if the URL is not a data URI or writing fails.
    /// </summary>
    internal static string ExtractAndSaveImage(string dataUri, string imagesDir, int index) {
        string prefix;
        string ext;

        if (dataUri.StartsWith(DataUriPngPrefix, StringComparison.Ordinal)) {
            prefix = DataUriPngPrefix; ext = "png";
        } else if (dataUri.StartsWith(DataUriJpegPrefix, StringComparison.Ordinal)) {
            prefix = DataUriJpegPrefix; ext = "jpg";
        } else {
            // Not a data URI we handle — could be an external URL, preserve as-is
            return null;
        }

        string fileName = $"img_{index}.{ext}";
        string filePath = $"{imagesDir}/{fileName}";

        try {
            string base64 = dataUri.Substring(prefix.Length);
            byte[] bytes = Convert.FromBase64String(base64);

            using var imgFile = FileAccess.Open(filePath, FileAccess.ModeFlags.Write);
            if (imgFile == null) {
                GD.PushError($"[LLMHistorySerializer] Failed to write image: {filePath}");
                return null;
            }
            imgFile.StoreBuffer(bytes);
            return $"{FileRefPrefix}{ImagesDirName}/{fileName}";
        } catch (Exception e) {
            GD.PushError($"[LLMHistorySerializer] Failed to save image {index}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Read an image file reference back into a data URI.
    /// Returns null if the file doesn't exist or reading fails.
    /// </summary>
    internal static string LoadImageAsDataUri(string fileRef, string folderPath) {
        if (!fileRef.StartsWith(FileRefPrefix, StringComparison.Ordinal)) return fileRef;

        string relativePath = fileRef.Substring(FileRefPrefix.Length);
        string filePath = $"{folderPath}/{relativePath}";

        if (!FileAccess.FileExists(filePath)) {
            GD.PushError($"[LLMHistorySerializer] Image file missing: {filePath}");
            return null;
        }

        try {
            using var imgFile = FileAccess.Open(filePath, FileAccess.ModeFlags.Read);
            if (imgFile == null) {
                GD.PushError($"[LLMHistorySerializer] Failed to read image: {filePath}");
                return null;
            }
            byte[] bytes = imgFile.GetBuffer((long)imgFile.GetLength());
            string base64 = Convert.ToBase64String(bytes);

            // Determine MIME type from extension
            string mime = relativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png" : "image/jpeg";
            return $"data:{mime};base64,{base64}";
        } catch (Exception e) {
            GD.PushError($"[LLMHistorySerializer] Failed to load image: {e.Message}");
            return null;
        }
    }

    private static void ClearDirectory(string dirPath) {
        var dir = DirAccess.Open(dirPath);
        if (dir == null) return;
        dir.ListDirBegin();
        string fileName = dir.GetNext();
        while (!string.IsNullOrEmpty(fileName)) {
            if (!dir.CurrentIsDir())
                dir.Remove(fileName);
            fileName = dir.GetNext();
        }
        dir.ListDirEnd();
    }

    // ═══════════════════════════════════════════════════════
    // Serialization context (tracks image index across messages)
    // ═══════════════════════════════════════════════════════

    internal class SerializeContext {
        public LLMSerializeOptions Options { get; }
        public string ImageDir { get; set; }
        public bool ExtractImages { get; set; }
        public int ImageCount { get; set; }

        public SerializeContext(LLMSerializeOptions options) {
            Options = options;
        }
    }
}

/// <summary>Options for history serialization.</summary>
[Flags]
public enum LLMSerializeOptions {
    /// <summary>Full lossless serialization (images kept as data URIs in JSON).</summary>
    None = 0,

    /// <summary>Strip image_url content parts (replace with [image] text placeholder).
    /// Use when you don't need images at all.</summary>
    StripImages = 1,

    /// <summary>Omit tool call messages (role=tool, tool_calls) to save space.
    /// Useful for persona agents that don't use tools.</summary>
    StripToolCalls = 2,
}

// ═══════════════════════════════════════════════════════
// DTOs
// ═══════════════════════════════════════════════════════

/// <summary>
/// JSON-serializable DTO that resolves the polymorphic Content field.
/// Content is stored as either ContentText (string) or ContentParts (list),
/// never both — the deserializer picks the right one.
/// </summary>
public class LLMMessageDTO {
    [JsonPropertyName("role")]
    public string Role { get; set; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ContentText { get; set; }

    [JsonPropertyName("parts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ContentPartDTO> ContentParts { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ToolCallId { get; set; }

    [JsonPropertyName("tool_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ToolName { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolCallDTO> ToolCalls { get; set; }

    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Reasoning { get; set; }

    internal static LLMMessageDTO FromLLMMessage(LLMMessage msg,
        LLMHistorySerializer.SerializeContext ctx) {
        var dto = new LLMMessageDTO {
            Role = msg.Role,
            ToolCallId = msg.ToolCallId,
            ToolName = msg.ToolName,
            Reasoning = msg.Reasoning,
        };

        // Content: disambiguate string vs parts
        if (msg.Content is string text) {
            dto.ContentText = text;
        } else if (msg.Content is List<ContentPart> parts) {
            dto.ContentParts = new List<ContentPartDTO>();
            foreach (var part in parts) {
                if (part == null) continue;

                if (part.Type == "image_url" && part.ImageUrl?.Url != null) {
                    if (ctx.Options.HasFlag(LLMSerializeOptions.StripImages)) {
                        dto.ContentParts.Add(new ContentPartDTO { Type = "text", Text = "[image]" });
                        continue;
                    }

                    // Extract image to file if we're doing file-based save
                    if (ctx.ExtractImages && ctx.ImageDir != null) {
                        string fileRef = LLMHistorySerializer.ExtractAndSaveImage(
                            part.ImageUrl.Url, ctx.ImageDir, ctx.ImageCount);
                        if (fileRef != null) {
                            ctx.ImageCount++;
                            dto.ContentParts.Add(new ContentPartDTO {
                                Type = "image_url", ImageUrl = fileRef
                            });
                            continue;
                        }
                    }
                }

                dto.ContentParts.Add(ContentPartDTO.FromContentPart(part));
            }
        }

        // Tool calls
        if (msg.ToolCalls != null && !ctx.Options.HasFlag(LLMSerializeOptions.StripToolCalls)) {
            dto.ToolCalls = new List<ToolCallDTO>();
            foreach (var tc in msg.ToolCalls) {
                if (tc == null) continue;
                dto.ToolCalls.Add(ToolCallDTO.FromToolCall(tc));
            }
            if (dto.ToolCalls.Count == 0) dto.ToolCalls = null;
        }

        return dto;
    }

    public LLMMessage ToLLMMessage(string folderPath) {
        var msg = new LLMMessage {
            Role = Role,
            ToolCallId = ToolCallId,
            ToolName = ToolName,
            Reasoning = Reasoning,
        };

        // Restore Content
        if (ContentParts != null && ContentParts.Count > 0) {
            var parts = new List<ContentPart>();
            foreach (var dto in ContentParts) {
                parts.Add(dto.ToContentPart(folderPath));
            }
            msg.Content = parts;
        } else if (ContentText != null) {
            msg.Content = ContentText;
        }

        // Restore tool calls
        if (ToolCalls != null) {
            msg.ToolCalls = new List<ToolCall>();
            foreach (var dto in ToolCalls)
                msg.ToolCalls.Add(dto.ToToolCall());
        }

        return msg;
    }
}

/// <summary>JSON-serializable mirror of ContentPart.</summary>
public class ContentPartDTO {
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Text { get; set; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ImageUrl { get; set; }

    public static ContentPartDTO FromContentPart(ContentPart part) => new() {
        Type = part.Type,
        Text = part.Text,
        ImageUrl = part.ImageUrl?.Url,
    };

    public ContentPart ToContentPart(string folderPath = null) {
        var part = new ContentPart { Type = Type, Text = Text };
        if (!string.IsNullOrEmpty(ImageUrl)) {
            string resolvedUrl = ImageUrl;
            // Resolve file references back to data URIs
            if (folderPath != null && ImageUrl.StartsWith("file:", StringComparison.Ordinal)) {
                string dataUri = LLMHistorySerializer.LoadImageAsDataUri(ImageUrl, folderPath);
                resolvedUrl = dataUri ?? ImageUrl;
            }
            part.ImageUrl = new ImageUrl { Url = resolvedUrl };
        }
        return part;
    }
}

/// <summary>JSON-serializable mirror of ToolCall.</summary>
public class ToolCallDTO {
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Id { get; set; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Type { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Name { get; set; }

    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Arguments { get; set; }

    public static ToolCallDTO FromToolCall(ToolCall tc) => new() {
        Id = tc.Id,
        Type = tc.Type,
        Name = tc.Function?.Name,
        Arguments = tc.Function?.RawArguments,
    };

    public ToolCall ToToolCall() => new() {
        Id = Id,
        Type = Type,
        Function = new ToolFunction { Name = Name, RawArguments = Arguments },
    };
}
