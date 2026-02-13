using Godot;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using HttpClient = System.Net.Http.HttpClient;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

public sealed class ChutesLLMClient : LLMClient {
	private const int MaxErrorBodyBytes = 16 * 1024;

	private readonly string chutesApiKey;
	private readonly string baseUrl;
	private readonly string model;
	private readonly float temperature;
	private readonly int maxTokens;
	private readonly ChutesAuthMode authMode;
	private readonly HttpClient httpClient;

	public ChutesLLMClient() {
		GD.Print("[ChutesLLMClient] Initialize");
		chutesApiKey = AgenticConfig.GetValue("CHUTES_API_KEY", "");
		if (string.IsNullOrWhiteSpace(chutesApiKey)) {
			throw new InvalidOperationException(
				"[ChutesLLMClient] Missing CHUTES_API_KEY. Set CHUTES_API_KEY in user://AgenticConfig.txt.");
		}

		baseUrl = ResolveBaseUrl();
		model = AgenticConfig.GetValue("CHUTES_MODEL", "");
		if (string.IsNullOrWhiteSpace(model)) {
			model = baseUrl;
		}

		temperature = AgenticConfig.GetValue("TEMPERATURE", 1.0f);
		maxTokens = AgenticConfig.GetValue("CHUTES_MAX_TOKENS", 10000);
		authMode = ParseAuthMode(AgenticConfig.GetValue("CHUTES_AUTH_MODE", ""));

		httpClient = new HttpClient();
		httpClient.DefaultRequestHeaders.Add("User-Agent", "Godot-LLM-Interface");
		if (authMode == ChutesAuthMode.Bearer) {
			httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", chutesApiKey);
		} else {
			httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", chutesApiKey);
		}

		GD.Print($"[ChutesLLMClient] Configuration loaded - BaseUrl: {baseUrl}, Model: {model}, Temperature: {temperature}, MaxTokens: {maxTokens}, AuthMode: {authMode}");
	}

	public async Task SendWithIndefiniteRetry(List<LLMMessage> messages, List<Tool> tools, Action<LLMMessage> onComplete,
		Action<List<ToolCall>, LLMMessage> onToolCalls) {
		var postprocessedMessages = LLMClientPostprocessor.MergeConsecutiveUserMessages(messages);
		int retryCount = 0;
		while (true) {
			try {
				GD.Print($"[ChutesLLMClient] Attempt #{retryCount + 1} - Calling Send()");
				await Send(postprocessedMessages, tools, onComplete, onToolCalls).ConfigureAwait(false);
				GD.Print("[ChutesLLMClient] Send() completed successfully, breaking retry loop");
				break;
			} catch (Exception e) {
				retryCount++;
				GD.PrintErr($"[ChutesLLMClient] Attempt #{retryCount} failed: {e.Message}");
				await Task.Delay(5000).ConfigureAwait(false);
			}
		}
	}

	private async Task Send(List<LLMMessage> messages, List<Tool> tools, Action<LLMMessage> onComplete,
		Action<List<ToolCall>, LLMMessage> onToolCalls) {
		var requestData = new ChatCompletionRequest {
			Model = model,
			Messages = messages,
			Tools = tools,
			Temperature = temperature,
			MaxTokens = maxTokens,
			Stream = false
		};

		var jsonOptions = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
		string jsonPayload = JsonSerializer.Serialize(requestData, jsonOptions);
		var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions") {
			Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
		};

		var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode) {
			string errorContent = await ReadLimitedContentAsync(response.Content, MaxErrorBodyBytes).ConfigureAwait(false);
			throw new HttpRequestException(
				$"Request failed with status {(int)response.StatusCode} {response.StatusCode}. " +
				$"Reason: {response.ReasonPhrase}. Content: {errorContent}");
		}

		using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
		using var responseDoc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
		if (TryParseAssistantMessage(responseDoc.RootElement, out var message) && message != null) {
			if (message.ToolCalls != null && message.ToolCalls.Count > 0) {
				MainThread.Post(() => onToolCalls?.Invoke(message.ToolCalls, message));
				return;
			}

			MainThread.Post(() => onComplete?.Invoke(message));
			return;
		}

		GD.PrintErr("[ChutesLLMClient] No choices in response");
		MainThread.Post(() => onComplete?.Invoke(new LLMMessage { Role = "assistant", Content = "" }));
	}

	private static bool TryParseAssistantMessage(JsonElement root, out LLMMessage message) {
		message = null;
		if (!root.TryGetProperty("choices", out var choicesElement) || choicesElement.ValueKind != JsonValueKind.Array ||
		    choicesElement.GetArrayLength() <= 0) {
			return false;
		}

		var firstChoice = choicesElement[0];
		if (!firstChoice.TryGetProperty("message", out var messageElement) ||
		    messageElement.ValueKind != JsonValueKind.Object) {
			return false;
		}

		message = new LLMMessage {
			Role = "assistant",
			Content = ParseContent(messageElement)
		};

		if (messageElement.TryGetProperty("tool_calls", out var toolCallsElement) &&
		    toolCallsElement.ValueKind == JsonValueKind.Array) {
			var toolCalls = ParseToolCalls(toolCallsElement);
			if (toolCalls.Count > 0) {
				message.ToolCalls = toolCalls;
			}
		}

		if (messageElement.TryGetProperty("reasoning", out var reasoningElement)) {
			if (reasoningElement.ValueKind == JsonValueKind.String) {
				message.Reasoning = reasoningElement.GetString();
			} else if (reasoningElement.ValueKind != JsonValueKind.Null &&
			           reasoningElement.ValueKind != JsonValueKind.Undefined) {
				message.Reasoning = reasoningElement.GetRawText();
			}
		}

		if (messageElement.TryGetProperty("reasoning_details", out var reasoningDetailsElement) &&
		    reasoningDetailsElement.ValueKind != JsonValueKind.Null &&
		    reasoningDetailsElement.ValueKind != JsonValueKind.Undefined) {
			try {
				message.ReasoningDetails = JsonNode.Parse(reasoningDetailsElement.GetRawText());
			} catch {
				message.ReasoningDetails = null;
			}
		}

		return true;
	}

	private static object ParseContent(JsonElement messageElement) {
		if (!messageElement.TryGetProperty("content", out var contentElement)) {
			return string.Empty;
		}

		if (contentElement.ValueKind == JsonValueKind.String) {
			return contentElement.GetString() ?? string.Empty;
		}

		if (contentElement.ValueKind == JsonValueKind.Array) {
			var parts = ParseContentParts(contentElement);
			return parts.Count > 0 ? parts : string.Empty;
		}

		if (contentElement.ValueKind == JsonValueKind.Null || contentElement.ValueKind == JsonValueKind.Undefined) {
			return string.Empty;
		}

		return contentElement.GetRawText();
	}

	private static List<ContentPart> ParseContentParts(JsonElement contentArray) {
		var parts = new List<ContentPart>();
		foreach (var partElement in contentArray.EnumerateArray()) {
			var parsed = ParseSingleContentPart(partElement);
			if (parsed != null) {
				parts.Add(parsed);
			}
		}
		return parts;
	}

	private static ContentPart ParseSingleContentPart(JsonElement partElement) {
		if (partElement.ValueKind != JsonValueKind.Object) {
			return null;
		}

		string type = GetStringProperty(partElement, "type");
		if (string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase)) {
			string url = null;
			if (partElement.TryGetProperty("image_url", out var imageUrlElement)) {
				if (imageUrlElement.ValueKind == JsonValueKind.String) {
					url = imageUrlElement.GetString();
				} else if (imageUrlElement.ValueKind == JsonValueKind.Object) {
					url = GetStringProperty(imageUrlElement, "url");
				}
			}

			if (!string.IsNullOrWhiteSpace(url)) {
				return new ContentPart {
					Type = "image_url",
					ImageUrl = new ImageUrl { Url = url }
				};
			}
		}

		string text = GetStringProperty(partElement, "text");
		if (!string.IsNullOrWhiteSpace(text)) {
			return new ContentPart {
				Type = string.IsNullOrWhiteSpace(type) ? "text" : type,
				Text = text
			};
		}

		return null;
	}

	private static List<ToolCall> ParseToolCalls(JsonElement toolCallsElement) {
		var toolCalls = new List<ToolCall>();
		int index = 0;
		foreach (var toolCallElement in toolCallsElement.EnumerateArray()) {
			if (toolCallElement.ValueKind != JsonValueKind.Object) continue;
			if (!toolCallElement.TryGetProperty("function", out var functionElement) ||
			    functionElement.ValueKind != JsonValueKind.Object) {
				continue;
			}

			string name = GetStringProperty(functionElement, "name");
			if (string.IsNullOrWhiteSpace(name)) continue;

			string rawArguments = "{}";
			if (functionElement.TryGetProperty("arguments", out var argsElement)) {
				rawArguments = ExtractArgumentsJson(argsElement);
			}

			string id = GetStringProperty(toolCallElement, "id");
			string type = GetStringProperty(toolCallElement, "type");
			toolCalls.Add(new ToolCall {
				Index = index++,
				Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString() : id,
				Type = string.IsNullOrWhiteSpace(type) ? "function" : type,
				Function = new ToolFunction {
					Name = name,
					RawArguments = string.IsNullOrWhiteSpace(rawArguments) ? "{}" : rawArguments
				}
			});
		}

		return toolCalls;
	}

	private static string ExtractArgumentsJson(JsonElement argsElement) {
		if (argsElement.ValueKind == JsonValueKind.String) {
			return argsElement.GetString() ?? "{}";
		}

		if (argsElement.ValueKind == JsonValueKind.Object || argsElement.ValueKind == JsonValueKind.Array) {
			return argsElement.GetRawText();
		}

		return "{}";
	}

	private static string GetStringProperty(JsonElement element, string name) {
		if (element.ValueKind != JsonValueKind.Object) return null;
		if (!element.TryGetProperty(name, out var value)) return null;
		if (value.ValueKind != JsonValueKind.String) return null;
		return value.GetString();
	}

	private static async Task<string> ReadLimitedContentAsync(HttpContent content, int maxBytes) {
		if (content == null) return string.Empty;
		byte[] bytes = await content.ReadAsByteArrayAsync().ConfigureAwait(false);
		if (bytes.Length <= maxBytes) return Encoding.UTF8.GetString(bytes);
		return Encoding.UTF8.GetString(bytes, 0, maxBytes) + "...";
	}

	private static ChutesAuthMode ParseAuthMode(string rawMode) {
		if (string.IsNullOrWhiteSpace(rawMode)) return ChutesAuthMode.XApiKey;

		string normalized = rawMode.Trim().ToLowerInvariant();
		switch (normalized) {
			case "x-api-key":
			case "x_api_key":
			case "xapikey":
			case "api-key":
			case "apikey":
				return ChutesAuthMode.XApiKey;
			case "bearer":
			case "authorization":
				return ChutesAuthMode.Bearer;
			default:
				GD.PushWarning(
					$"[ChutesLLMClient] Invalid CHUTES_AUTH_MODE '{rawMode}'. Using default 'x-api-key'.");
				return ChutesAuthMode.XApiKey;
		}
	}

	private static string ResolveBaseUrl() {
		var configuredBaseUrl = AgenticConfig.GetValue("CHUTES_BASE_URL", "");
		if (!string.IsNullOrWhiteSpace(configuredBaseUrl)) {
			return NormalizeBaseUrl(configuredBaseUrl);
		}

		var username = AgenticConfig.GetValue("CHUTES_USERNAME", "");
		var chuteName = AgenticConfig.GetValue("CHUTES_CHUTE_NAME", "");
		if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(chuteName)) {
			return NormalizeBaseUrl($"https://api.chutes.ai/chutes/{username.Trim()}/{chuteName.Trim()}");
		}

		throw new InvalidOperationException(
			"[ChutesLLMClient] Missing Chutes endpoint configuration. Set CHUTES_BASE_URL or set both CHUTES_USERNAME and CHUTES_CHUTE_NAME in user://AgenticConfig.txt.");
	}

	private static string NormalizeBaseUrl(string url) {
		if (string.IsNullOrWhiteSpace(url)) return string.Empty;
		var normalized = url.Trim().TrimEnd('/');
		const string completionsSuffix = "/v1/chat/completions";
		if (normalized.EndsWith(completionsSuffix, StringComparison.OrdinalIgnoreCase)) {
			GD.PushWarning(
				$"[ChutesLLMClient] CHUTES_BASE_URL should be a base URL, not a full endpoint. Trimming '{completionsSuffix}'.");
			normalized = normalized.Substring(0, normalized.Length - completionsSuffix.Length).TrimEnd('/');
		}
		return normalized;
	}

	private enum ChutesAuthMode {
		XApiKey,
		Bearer
	}
}
