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

public sealed class OpenAICompatibleLLMClient : LLMClient {
	private const int MaxErrorBodyBytes = 16 * 1024;

	private readonly OpenAICompatibleLLMClientOptions options;
	private readonly HttpClient httpClient;

	public OpenAICompatibleLLMClient(OpenAICompatibleLLMClientOptions options) {
		this.options = options ?? throw new ArgumentNullException(nameof(options));
		if (string.IsNullOrWhiteSpace(this.options.ClientName)) {
			this.options.ClientName = "OpenAICompatibleLLMClient";
		}
		if (string.IsNullOrWhiteSpace(this.options.BaseUrl)) {
			throw new InvalidOperationException($"[{this.options.ClientName}] Missing base URL.");
		}
		if (string.IsNullOrWhiteSpace(this.options.Model)) {
			throw new InvalidOperationException($"[{this.options.ClientName}] Missing model.");
		}

		this.options.BaseUrl = NormalizeBaseUrl(
			this.options.BaseUrl,
			this.options.BaseUrlConfigKey,
			this.options.ClientName
		);

		httpClient = new HttpClient();
		httpClient.DefaultRequestHeaders.Add("User-Agent", "Godot-LLM-Interface");
		ApplyAuthHeaders(httpClient, this.options);

		string reasoningEffortSummary = string.IsNullOrEmpty(this.options.ReasoningEffort) ? "default" : this.options.ReasoningEffort;
		GD.Print($"[{this.options.ClientName}] Configuration loaded - BaseUrl: {this.options.BaseUrl}, Model: {this.options.Model}, Temperature: {this.options.Temperature}, MaxTokens: {this.options.MaxTokens}, ReasoningEffort: {reasoningEffortSummary}, AuthMode: {this.options.AuthMode}");
	}

	public async Task SendWithIndefiniteRetry(List<LLMMessage> messages, List<Tool> tools, Action<LLMMessage> onComplete,
		Action<List<ToolCall>, LLMMessage> onToolCalls) {
		// Rate limiting lives in RateLimitedLLMClient (decorator); concrete provider wrappers
		// are wrapped by the factory so no acquire is needed here.
		var postprocessedMessages = LLMClientPostprocessor.MergeConsecutiveUserMessages(messages);
		int retryCount = 0;
		while (true) {
			try {
				GD.Print($"[{options.ClientName}] Attempt #{retryCount + 1} - Calling Send()");
				await Send(postprocessedMessages, tools, onComplete, onToolCalls).ConfigureAwait(false);
				GD.Print($"[{options.ClientName}] Send() completed successfully, breaking retry loop");
				break;
			}
			catch (LLMBadRequestException) {
				throw; // Context corruption — don't retry, let caller handle
			}
			catch (Exception e) {
				retryCount++;
				GD.PrintErr($"[{options.ClientName}] Attempt #{retryCount} failed: {e.Message}");
				await Task.Delay(5000).ConfigureAwait(false);
			}
		}
	}

	private async Task Send(List<LLMMessage> messages, List<Tool> tools, Action<LLMMessage> onComplete,
		Action<List<ToolCall>, LLMMessage> onToolCalls) {
		var requestData = new ChatCompletionRequest {
			Model = options.Model,
			Messages = messages,
			Tools = tools,
			Temperature = options.Temperature,
			MaxTokens = options.MaxTokens,
			ReasoningEffort = string.IsNullOrEmpty(options.ReasoningEffort) ? null : options.ReasoningEffort,
			Stream = false
		};

		var jsonOptions = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
		string jsonPayload = JsonSerializer.Serialize(requestData, jsonOptions);
		var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl}/v1/chat/completions") {
			Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
		};

		var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode) {
			int statusCode = (int)response.StatusCode;
			string errorContent = await ReadLimitedContentAsync(response.Content, MaxErrorBodyBytes).ConfigureAwait(false);

			if (statusCode == 400) {
				throw new LLMBadRequestException(statusCode, errorContent,
					$"Bad Request (400): context is likely corrupted. Reason: {response.ReasonPhrase}. Content: {errorContent}");
			}

			throw new HttpRequestException(
				$"Request failed with status {statusCode} {response.StatusCode}. " +
				$"Reason: {response.ReasonPhrase}. Content: {errorContent}");
		}

		using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
		using var responseDoc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
		if (TryParseAssistantMessage(responseDoc.RootElement, out var message) && message != null) {
			if (message.ToolCalls != null && message.ToolCalls.Count > 0) {
				MainThread.Enqueue(() => onToolCalls?.Invoke(message.ToolCalls, message));
				return;
			}

			MainThread.Enqueue(() => onComplete?.Invoke(message));
			return;
		}

		GD.PrintErr($"[{options.ClientName}] No choices in response");
		MainThread.Enqueue(() => onComplete?.Invoke(new LLMMessage { Role = "assistant", Content = "" }));
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

	private static void ApplyAuthHeaders(HttpClient httpClient, OpenAICompatibleLLMClientOptions options) {
		if (options.AuthMode == OpenAICompatibleAuthMode.None) return;
		if (string.IsNullOrWhiteSpace(options.ApiKey)) return;

		if (options.AuthMode == OpenAICompatibleAuthMode.Bearer) {
			httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
			return;
		}

		if (options.AuthMode == OpenAICompatibleAuthMode.XApiKey) {
			httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", options.ApiKey);
		}
	}

	private static string NormalizeBaseUrl(string url, string configKey, string clientName) {
		if (string.IsNullOrWhiteSpace(url)) return string.Empty;
		var normalized = url.Trim().TrimEnd('/');
		const string completionsSuffix = "/v1/chat/completions";
		if (normalized.EndsWith(completionsSuffix, StringComparison.OrdinalIgnoreCase)) {
			string key = string.IsNullOrWhiteSpace(configKey) ? "base URL" : configKey;
			GD.PushWarning(
				$"[{clientName}] {key} should be a base URL, not a full endpoint. Trimming '{completionsSuffix}'.");
			normalized = normalized.Substring(0, normalized.Length - completionsSuffix.Length).TrimEnd('/');
		}
		return normalized;
	}
}

public sealed class OpenAICompatibleLLMClientOptions {
	public string ClientName { get; set; } = "OpenAICompatibleLLMClient";
	public string BaseUrl { get; set; }
	public string BaseUrlConfigKey { get; set; }
	public string Model { get; set; }
	public float Temperature { get; set; } = 1.0f;
	public int MaxTokens { get; set; } = 10000;
	public string ReasoningEffort { get; set; }
	public string ApiKey { get; set; }
	public OpenAICompatibleAuthMode AuthMode { get; set; } = OpenAICompatibleAuthMode.None;
}

public enum OpenAICompatibleAuthMode {
	None,
	Bearer,
	XApiKey
}
