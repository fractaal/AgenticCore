using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using HttpClient = System.Net.Http.HttpClient;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

public sealed class CodexChatGPTLLMClient : LLMClient {
	private const int MaxErrorBodyBytes = 16 * 1024;
	private const string CodexEndpoint = "https://chatgpt.com/backend-api/codex/responses";
	private const string InputTextType = "input_text";
	private const string OutputTextType = "output_text";
	private const string InputImageType = "input_image";

	private readonly HttpClient httpClient;
	private readonly CodexOAuth oauth;
	private readonly string model;
	private readonly string reasoningEffort;
	private readonly string reasoningSummary;
	private readonly string textVerbosity;

	public CodexChatGPTLLMClient() {
		GD.Print("[CodexChatGPTLLMClient] Initialize");
		model = AgenticConfig.GetValue("CODEX_MODEL", "gpt-4.1-mini");
		reasoningEffort = AgenticConfig.GetValue("CODEX_REASONING_EFFORT", string.Empty);
		reasoningSummary = AgenticConfig.GetValue("CODEX_REASONING_SUMMARY", string.Empty);
		textVerbosity = AgenticConfig.GetValue("CODEX_TEXT_VERBOSITY", string.Empty);
		httpClient = new HttpClient();
		oauth = new CodexOAuth(httpClient);
		GD.Print($"[CodexChatGPTLLMClient] Configuration loaded - Model: {model}");
		if (!string.IsNullOrWhiteSpace(reasoningEffort) || !string.IsNullOrWhiteSpace(reasoningSummary)
			|| !string.IsNullOrWhiteSpace(textVerbosity)) {
			GD.Print($"[CodexChatGPTLLMClient] Reasoning effort={reasoningEffort}, summary={reasoningSummary}, text verbosity={textVerbosity}");
		}
	}

	public async Task SendWithIndefiniteRetry(List<LLMMessage> messages, List<Tool> tools,
		Action<LLMMessage> onComplete, Action<List<ToolCall>, LLMMessage> onToolCalls) {
		var postprocessedMessages = LLMClientPostprocessor.MergeConsecutiveUserMessages(messages);
		int retryCount = 0;
		while (true) {
			try {
				GD.Print($"[CodexChatGPTLLMClient] Attempt #{retryCount + 1} - Calling Send()");
				await Send(postprocessedMessages, tools, onComplete, onToolCalls).ConfigureAwait(false);
				GD.Print("[CodexChatGPTLLMClient] Send() completed successfully, breaking retry loop");
				break;
			} catch (Exception e) {
				retryCount++;
				GD.PrintErr($"[CodexChatGPTLLMClient] Attempt #{retryCount} failed: {e.Message}");
				await Task.Delay(5000).ConfigureAwait(false);
			}
		}
	}

	private async Task Send(List<LLMMessage> messages, List<Tool> tools,
		Action<LLMMessage> onComplete, Action<List<ToolCall>, LLMMessage> onToolCalls) {
		var requestBody = BuildRequestBody(messages, tools);
		string jsonPayload = requestBody.ToJsonString(new JsonSerializerOptions {
			TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		});

		var auth = await oauth.EnsureValidAuthAsync().ConfigureAwait(false);
		var response = await SendRequestAsync(auth, jsonPayload).ConfigureAwait(false);
		if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden) {
			GD.Print("[CodexChatGPTLLMClient] Access token rejected, forcing refresh.");
			auth = await oauth.ForceRefreshAsync().ConfigureAwait(false);
			response = await SendRequestAsync(auth, jsonPayload).ConfigureAwait(false);
		}

		if (!response.IsSuccessStatusCode) {
			string errorContent = await ReadLimitedContentAsync(response.Content, MaxErrorBodyBytes).ConfigureAwait(false);
			throw new HttpRequestException(
				$"Request failed with status {(int)response.StatusCode} {response.StatusCode}. Content: {errorContent}");
		}

		using var responseDoc = await ReadResponseDocumentAsync(response).ConfigureAwait(false);
		var assistantText = ExtractAssistantText(responseDoc.RootElement);
		var toolCalls = ExtractToolCalls(responseDoc.RootElement);
		var message = new LLMMessage { Role = "assistant", Content = assistantText ?? string.Empty };

		if (toolCalls.Count > 0) {
			message.ToolCalls = toolCalls;
			MainThread.Post(() => onToolCalls?.Invoke(toolCalls, message));
			return;
		}

		MainThread.Post(() => onComplete?.Invoke(message));
	}

	private async Task<HttpResponseMessage> SendRequestAsync(CodexAuthState auth, string jsonPayload) {
		if (auth == null || string.IsNullOrWhiteSpace(auth.AccessToken)) {
			throw new InvalidOperationException("Codex OAuth token missing. Login required.");
		}

		var content = new StringContent(jsonPayload, Encoding.UTF8);
		content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		var request = new HttpRequestMessage(HttpMethod.Post, CodexEndpoint) { Content = content };
		request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);
		request.Headers.Add("OpenAI-Beta", "responses=experimental");
		request.Headers.Add("originator", "codex_cli_rs");
		request.Headers.Add("accept", "text/event-stream");

		if (!string.IsNullOrWhiteSpace(auth.AccountId)) {
			request.Headers.Add("chatgpt-account-id", auth.AccountId);
		} else {
			GD.PrintErr("[CodexChatGPTLLMClient] Missing chatgpt account id. Request may fail.");
		}

		return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
	}

	private JsonObject BuildRequestBody(List<LLMMessage> messages, List<Tool> tools) {
		var (instructions, inputMessages) = ExtractInstructions(messages);
		var input = BuildInputItems(inputMessages);
		var include = new JsonArray { "reasoning.encrypted_content" };
		var body = new JsonObject {
			["model"] = model,
			["input"] = input,
			["store"] = false,
			["stream"] = true,
			["include"] = include
		};

		var reasoningNode = BuildReasoningNode();
		if (reasoningNode != null) {
			body["reasoning"] = reasoningNode;
		}

		var textNode = BuildTextNode();
		if (textNode != null) {
			body["text"] = textNode;
		}

		if (!string.IsNullOrWhiteSpace(instructions)) {
			body["instructions"] = instructions;
		}

		if (tools != null && tools.Count > 0) {
			var toolsNode = BuildToolsArray(tools);
			if (toolsNode.Count > 0) {
				body["tools"] = toolsNode;
			} else {
				GD.PrintErr("[CodexChatGPTLLMClient] All tools were invalid; omitting tools from request.");
			}
		}

		return body;
	}

	private JsonObject BuildReasoningNode() {
		var obj = new JsonObject();
		if (!string.IsNullOrWhiteSpace(reasoningEffort)) {
			obj["effort"] = reasoningEffort;
		}
		if (!string.IsNullOrWhiteSpace(reasoningSummary)) {
			obj["summary"] = reasoningSummary;
		}
		return obj.Count > 0 ? obj : null;
	}

	private JsonObject BuildTextNode() {
		if (string.IsNullOrWhiteSpace(textVerbosity)) return null;
		return new JsonObject { ["verbosity"] = textVerbosity };
	}

	private static JsonArray BuildToolsArray(List<Tool> tools) {
		var array = new JsonArray();
		if (tools == null) return array;
		foreach (var tool in tools) {
			if (tool?.Function == null || string.IsNullOrWhiteSpace(tool.Function.Name)) {
				GD.PrintErr("[CodexChatGPTLLMClient] Tool missing function name; skipping.");
				continue;
			}

			var obj = new JsonObject {
				["type"] = "function",
				["name"] = tool.Function.Name
			};
			if (!string.IsNullOrWhiteSpace(tool.Function.Description)) {
				obj["description"] = tool.Function.Description;
			}
			obj["parameters"] = BuildParametersSchema(tool.Function.Parameters);
			array.Add(obj);
		}
		return array;
	}

	private static JsonNode BuildParametersSchema(object parameters) {
		if (parameters == null) return CreateEmptyParametersSchema();
		if (parameters is JsonNode node) {
			if (node is JsonValue value && value.TryGetValue<bool>(out var boolValue)) {
				return JsonValue.Create(boolValue);
			}
			if (node is JsonObject obj) {
				return EnsureParametersObject(obj);
			}
		}
		if (parameters is Parameters model) {
			return BuildParametersFromModel(model);
		}

		var serialized = JsonSerializer.SerializeToNode(parameters);
		if (serialized is JsonObject serializedObj) {
			return EnsureParametersObject(serializedObj);
		}
		if (serialized is JsonValue serializedValue && serializedValue.TryGetValue<bool>(out var serializedBool)) {
			return JsonValue.Create(serializedBool);
		}
		return CreateEmptyParametersSchema();
	}

	private static JsonObject BuildParametersFromModel(Parameters model) {
		var schema = new JsonObject {
			["type"] = string.IsNullOrWhiteSpace(model.Type) ? "object" : model.Type
		};

		var propertiesObj = new JsonObject();
		if (model.Properties != null) {
			foreach (var (key, prop) in model.Properties) {
				if (string.IsNullOrWhiteSpace(key) || prop == null) continue;
				var propObj = new JsonObject();
				if (!string.IsNullOrWhiteSpace(prop.Type)) propObj["type"] = prop.Type;
				if (!string.IsNullOrWhiteSpace(prop.Description)) propObj["description"] = prop.Description;
				if (prop.Items != null && prop.Items.Count > 0) {
					propObj["items"] = JsonSerializer.SerializeToNode(prop.Items);
				}
				propertiesObj[key] = propObj;
			}
		}

		schema["properties"] = propertiesObj;
		schema["required"] = BuildJsonArray(model.Required);
		return schema;
	}

	private static JsonObject EnsureParametersObject(JsonObject obj) {
		if (!obj.ContainsKey("type")) {
			obj["type"] = "object";
		}
		if (!obj.ContainsKey("properties")) {
			obj["properties"] = new JsonObject();
		}
		if (!obj.ContainsKey("required")) {
			obj["required"] = new JsonArray();
		}
		return obj;
	}

	private static JsonArray BuildJsonArray(List<string> values) {
		var array = new JsonArray();
		if (values == null) return array;
		foreach (var value in values) {
			if (!string.IsNullOrWhiteSpace(value)) array.Add(value);
		}
		return array;
	}

	private static JsonObject CreateEmptyParametersSchema() {
		return new JsonObject {
			["type"] = "object",
			["properties"] = new JsonObject(),
			["required"] = new JsonArray()
		};
	}

	private static (string Instructions, List<LLMMessage> InputMessages) ExtractInstructions(List<LLMMessage> messages) {
		if (messages == null || messages.Count == 0) return (null, messages);
		var inputMessages = new List<LLMMessage>(messages.Count);
		var instructions = new List<string>();

		foreach (var msg in messages) {
			if (msg == null) {
				inputMessages.Add(null);
				continue;
			}

			var role = msg.Role ?? string.Empty;
			if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase)) {
				var text = RenderContentText(msg.Content);
				if (!string.IsNullOrWhiteSpace(text)) {
					instructions.Add(text.Trim());
				}
				continue;
			}

			inputMessages.Add(msg);
		}

		return (instructions.Count > 0 ? string.Join("\n\n", instructions) : null, inputMessages);
	}

	private static JsonArray BuildInputItems(List<LLMMessage> messages) {
		var input = new JsonArray();
		if (messages == null) return input;

		foreach (var msg in messages) {
			if (msg == null) continue;

			if (string.Equals(msg.Role, "tool", StringComparison.Ordinal)) {
				input.Add(BuildFunctionCallOutputItem(msg));
				continue;
			}

			input.Add(BuildMessageItem(msg));

			if (msg.ToolCalls != null && msg.ToolCalls.Count > 0) {
				foreach (var toolCall in msg.ToolCalls) {
					if (toolCall == null) continue;
					input.Add(BuildFunctionCallItem(toolCall));
				}
			}
		}

		return input;
	}

	private static JsonObject BuildMessageItem(LLMMessage msg) {
		var role = msg?.Role ?? "user";
		var content = BuildContentArray(msg?.Content, role);
		return new JsonObject {
			["type"] = "message",
			["role"] = role,
			["content"] = content
		};
	}

	private static JsonObject BuildFunctionCallItem(ToolCall toolCall) {
		string callId = string.IsNullOrWhiteSpace(toolCall?.Id) ? Guid.NewGuid().ToString() : toolCall.Id;
		return new JsonObject {
			["type"] = "function_call",
			["call_id"] = callId,
			["name"] = toolCall.Function?.Name,
			["arguments"] = toolCall.Function?.RawArguments ?? "{}"
		};
	}

	private static JsonObject BuildFunctionCallOutputItem(LLMMessage msg) {
		string outputText = RenderContentText(msg?.Content);
		string callId = string.IsNullOrWhiteSpace(msg?.ToolCallId) ? Guid.NewGuid().ToString() : msg.ToolCallId;
		var obj = new JsonObject {
			["type"] = "function_call_output",
			["call_id"] = callId,
			["output"] = outputText ?? string.Empty
		};
		return obj;
	}

	private static JsonArray BuildContentArray(object content, string role) {
		string textType = ResolveTextContentType(role);
		string imageType = ResolveImageContentType(role);
		var array = new JsonArray();
		if (content == null) return array;
		if (content is string text) {
			if (!string.IsNullOrWhiteSpace(text)) {
				array.Add(new JsonObject {
					["type"] = textType,
					["text"] = text
				});
			}
			return array;
		}
		if (content is List<ContentPart> parts) {
			foreach (var part in parts) {
				if (part == null) continue;
				if (string.Equals(part.Type, "image_url", StringComparison.Ordinal)) {
					if (string.IsNullOrWhiteSpace(imageType)) {
						if (!string.IsNullOrWhiteSpace(part.Text)) {
							array.Add(new JsonObject {
								["type"] = textType,
								["text"] = part.Text
							});
						} else {
							array.Add(new JsonObject {
								["type"] = textType,
								["text"] = "[image]"
							});
						}
						continue;
					}
					var imageObj = new JsonObject {
						["type"] = imageType,
						["image_url"] = part.ImageUrl?.Url ?? string.Empty
					};
					array.Add(imageObj);
				} else {
					if (!string.IsNullOrWhiteSpace(part.Text)) {
						var textObj = new JsonObject {
							["type"] = textType,
							["text"] = part.Text
						};
						array.Add(textObj);
					}
				}
			}
			return array;
		}

		array.Add(new JsonObject {
			["type"] = textType,
			["text"] = content.ToString()
		});
		return array;
	}

	private static string ResolveTextContentType(string role) {
		if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)) return OutputTextType;
		return InputTextType;
	}

	private static string ResolveImageContentType(string role) {
		if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)) return null;
		return InputImageType;
	}

	private static async Task<JsonDocument> ReadResponseDocumentAsync(HttpResponseMessage response) {
		if (response?.Content == null) {
			throw new InvalidOperationException("Codex response missing content.");
		}

		string contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
		if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)) {
			return await ParseSseResponseAsync(response.Content).ConfigureAwait(false);
		}

		string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
		if (LooksLikeSse(body)) {
			return ParseSseText(body);
		}

		return JsonDocument.Parse(body);
	}

	private static bool LooksLikeSse(string body) {
		if (string.IsNullOrWhiteSpace(body)) return false;
		return body.StartsWith("data:", StringComparison.Ordinal) || body.Contains("\ndata:", StringComparison.Ordinal);
	}

	private static async Task<JsonDocument> ParseSseResponseAsync(HttpContent content) {
		using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
		using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
		string line;
		string lastResponseJson = null;

		while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) {
			if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
			string payload = line.Substring(6).Trim();
			if (string.IsNullOrWhiteSpace(payload) || payload == "[DONE]") continue;

			if (TryExtractResponseJsonFromSsePayload(payload, ref lastResponseJson, out bool done) && done) {
				break;
			}
		}

		if (string.IsNullOrWhiteSpace(lastResponseJson)) {
			throw new InvalidOperationException("Could not find final response in SSE stream.");
		}

		return JsonDocument.Parse(lastResponseJson);
	}

	private static JsonDocument ParseSseText(string sseText) {
		string lastResponseJson = null;
		foreach (var line in sseText.Split('\n')) {
			if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
			string payload = line.Substring(6).Trim();
			if (string.IsNullOrWhiteSpace(payload) || payload == "[DONE]") continue;
			TryExtractResponseJsonFromSsePayload(payload, ref lastResponseJson, out _);
		}

		if (string.IsNullOrWhiteSpace(lastResponseJson)) {
			throw new InvalidOperationException("Could not find final response in SSE stream.");
		}

		return JsonDocument.Parse(lastResponseJson);
	}

	private static bool TryExtractResponseJsonFromSsePayload(string payload, ref string lastResponseJson, out bool done) {
		done = false;
		try {
			using var doc = JsonDocument.Parse(payload);
			var root = doc.RootElement;
			if (root.TryGetProperty("response", out var responseElement)) {
				lastResponseJson = responseElement.GetRawText();
			} else if (root.TryGetProperty("error", out _)) {
				lastResponseJson = root.GetRawText();
			}

			if (root.TryGetProperty("type", out var typeElement)
				&& typeElement.ValueKind == JsonValueKind.String) {
				string type = typeElement.GetString();
				if (type == "response.done" || type == "response.completed") {
					done = true;
				} else if (type == "response.error" || type == "response.failed") {
					done = true;
				}
			}
		} catch {
			return false;
		}

		return !string.IsNullOrWhiteSpace(lastResponseJson);
	}

	private static string ExtractAssistantText(JsonElement root) {
		if (root.TryGetProperty("error", out var error)
			&& error.ValueKind != JsonValueKind.Null
			&& error.ValueKind != JsonValueKind.Undefined) {
			string detail = BuildErrorMessage(error);
			if (string.IsNullOrWhiteSpace(detail) || string.Equals(detail, "null", StringComparison.OrdinalIgnoreCase)) {
				throw new InvalidOperationException($"Codex response error: {root.GetRawText()}");
			}
			throw new InvalidOperationException($"Codex response error: {detail}");
		}

		if (root.TryGetProperty("output_text", out var outputTextElement)
			&& outputTextElement.ValueKind == JsonValueKind.String) {
			return outputTextElement.GetString() ?? string.Empty;
		}

		if (root.TryGetProperty("output", out var outputElement)
			&& outputElement.ValueKind == JsonValueKind.Array) {
			var sb = new StringBuilder();
			foreach (var item in outputElement.EnumerateArray()) {
				AppendOutputText(sb, item);
			}
			return sb.ToString();
		}

		if (root.TryGetProperty("choices", out var choices)
			&& choices.ValueKind == JsonValueKind.Array
			&& choices.GetArrayLength() > 0) {
			var first = choices[0];
			if (first.TryGetProperty("message", out var message)
				&& message.TryGetProperty("content", out var content)
				&& content.ValueKind == JsonValueKind.String) {
				return content.GetString() ?? string.Empty;
			}
		}

		return string.Empty;
	}

	private static string BuildErrorMessage(JsonElement errorElement) {
		switch (errorElement.ValueKind) {
			case JsonValueKind.Object: {
				string message = GetStringProperty(errorElement, "message");
				string type = GetStringProperty(errorElement, "type");
				string code = GetStringProperty(errorElement, "code");
				string param = GetStringProperty(errorElement, "param");
				var parts = new List<string>();
				if (!string.IsNullOrWhiteSpace(message)) parts.Add(message);
				if (!string.IsNullOrWhiteSpace(type)) parts.Add($"type={type}");
				if (!string.IsNullOrWhiteSpace(code)) parts.Add($"code={code}");
				if (!string.IsNullOrWhiteSpace(param)) parts.Add($"param={param}");
				if (parts.Count > 0) return string.Join(", ", parts);
				return errorElement.GetRawText();
			}
			case JsonValueKind.String:
				return errorElement.GetString() ?? string.Empty;
			case JsonValueKind.Null:
				return "null";
			default:
				return errorElement.GetRawText();
		}
	}

	private static string GetStringProperty(JsonElement obj, string name) {
		if (obj.ValueKind != JsonValueKind.Object) return null;
		if (!obj.TryGetProperty(name, out var element)) return null;
		if (element.ValueKind != JsonValueKind.String) return null;
		return element.GetString();
	}

	private static void AppendOutputText(StringBuilder sb, JsonElement item) {
		if (item.ValueKind != JsonValueKind.Object) return;
		if (item.TryGetProperty("type", out var typeElement)
			&& typeElement.ValueKind == JsonValueKind.String
			&& typeElement.GetString() == "function_call") {
			return;
		}

		if (item.TryGetProperty("content", out var contentElement)
			&& contentElement.ValueKind == JsonValueKind.Array) {
			foreach (var contentItem in contentElement.EnumerateArray()) {
				AppendContentText(sb, contentItem);
			}
			return;
		}

		if (item.TryGetProperty("text", out var directText)
			&& directText.ValueKind == JsonValueKind.String) {
			AppendWithNewline(sb, directText.GetString());
		}
	}

	private static void AppendContentText(StringBuilder sb, JsonElement contentItem) {
		if (contentItem.ValueKind != JsonValueKind.Object) return;
		if (contentItem.TryGetProperty("text", out var textElement)
			&& textElement.ValueKind == JsonValueKind.String) {
			AppendWithNewline(sb, textElement.GetString());
			return;
		}
		if (contentItem.TryGetProperty("output_text", out var outputText)
			&& outputText.ValueKind == JsonValueKind.String) {
			AppendWithNewline(sb, outputText.GetString());
			return;
		}
	}

	private static void AppendWithNewline(StringBuilder sb, string text) {
		if (string.IsNullOrEmpty(text)) return;
		if (sb.Length > 0) sb.Append("\n");
		sb.Append(text);
	}

	private static List<ToolCall> ExtractToolCalls(JsonElement root) {
		var toolCalls = new List<ToolCall>();

		if (root.TryGetProperty("output", out var output)
			&& output.ValueKind == JsonValueKind.Array) {
			foreach (var item in output.EnumerateArray()) {
				if (TryParseFunctionCallItem(item, toolCalls.Count, out var toolCall)) {
					toolCalls.Add(toolCall);
					continue;
				}

				if (item.ValueKind == JsonValueKind.Object
					&& item.TryGetProperty("tool_calls", out var toolCallsElement)
					&& toolCallsElement.ValueKind == JsonValueKind.Array) {
					foreach (var tc in toolCallsElement.EnumerateArray()) {
						if (TryParseToolCallObject(tc, toolCalls.Count, out var parsed)) {
							toolCalls.Add(parsed);
						}
					}
				}
			}
		}

		if (toolCalls.Count == 0
			&& root.TryGetProperty("choices", out var choices)
			&& choices.ValueKind == JsonValueKind.Array
			&& choices.GetArrayLength() > 0) {
			foreach (var choice in choices.EnumerateArray()) {
				if (choice.TryGetProperty("message", out var message)
					&& message.TryGetProperty("tool_calls", out var messageToolCalls)
					&& messageToolCalls.ValueKind == JsonValueKind.Array) {
					foreach (var tc in messageToolCalls.EnumerateArray()) {
						if (TryParseToolCallObject(tc, toolCalls.Count, out var parsed)) {
							toolCalls.Add(parsed);
						}
					}
				}
			}
		}

		return toolCalls;
	}

	private static bool TryParseFunctionCallItem(JsonElement item, int index, out ToolCall toolCall) {
		toolCall = null;
		if (item.ValueKind != JsonValueKind.Object) return false;
		if (!item.TryGetProperty("type", out var typeElement)
			|| typeElement.ValueKind != JsonValueKind.String
			|| typeElement.GetString() != "function_call") {
			return false;
		}

		string name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
		if (string.IsNullOrWhiteSpace(name)) return false;

		string callId = null;
		if (item.TryGetProperty("call_id", out var callIdElement) && callIdElement.ValueKind == JsonValueKind.String) {
			callId = callIdElement.GetString();
		} else if (item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String) {
			callId = idElement.GetString();
		}

		string argsJson = "{}";
		if (item.TryGetProperty("arguments", out var argsElement)) {
			argsJson = ExtractArgumentsJson(argsElement);
		}

		toolCall = new ToolCall {
			Index = index,
			Id = string.IsNullOrWhiteSpace(callId) ? Guid.NewGuid().ToString() : callId,
			Type = "function",
			Function = new ToolFunction {
				Name = name,
				RawArguments = argsJson
			}
		};
		return true;
	}

	private static bool TryParseToolCallObject(JsonElement item, int index, out ToolCall toolCall) {
		toolCall = null;
		if (item.ValueKind != JsonValueKind.Object) return false;
		if (!item.TryGetProperty("function", out var functionElement) || functionElement.ValueKind != JsonValueKind.Object) {
			return false;
		}

		string name = functionElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
		if (string.IsNullOrWhiteSpace(name)) return false;

		string callId = null;
		if (item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String) {
			callId = idElement.GetString();
		}

		string argsJson = "{}";
		if (functionElement.TryGetProperty("arguments", out var argsElement)) {
			argsJson = ExtractArgumentsJson(argsElement);
		}

		toolCall = new ToolCall {
			Index = index,
			Id = string.IsNullOrWhiteSpace(callId) ? Guid.NewGuid().ToString() : callId,
			Type = "function",
			Function = new ToolFunction {
				Name = name,
				RawArguments = argsJson
			}
		};
		return true;
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

	private static string RenderContentText(object content) {
		if (content == null) return string.Empty;
		if (content is string text) return text;
		if (content is List<ContentPart> parts) {
			var sb = new StringBuilder();
			foreach (var part in parts) {
				if (part == null) continue;
				if (!string.IsNullOrWhiteSpace(part.Text)) {
					if (sb.Length > 0) sb.Append("\n");
					sb.Append(part.Text);
				}
			}
			return sb.ToString();
		}
		return content.ToString();
	}

	private static async Task<string> ReadLimitedContentAsync(HttpContent content, int maxBytes) {
		if (content == null) return string.Empty;
		byte[] bytes = await content.ReadAsByteArrayAsync().ConfigureAwait(false);
		if (bytes.Length <= maxBytes) return Encoding.UTF8.GetString(bytes);
		return Encoding.UTF8.GetString(bytes, 0, maxBytes) + "...";
	}
}
