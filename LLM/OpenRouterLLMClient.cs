using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

public sealed class OpenRouterLLMClient : LLMClient {
	private const int MaxErrorBodyBytes = 16 * 1024;
	private const int ParseTokensPerSlice = 512;
	private const int ParseMillisPerSlice = 2;
	private const int MaxCacheBreakpoints = 4;
	private const int AssistantCacheBreakpoints = 2;

	private readonly string openRouterApiKey;
	private readonly string model;
	private readonly float temperature;
	private readonly ProviderRoutingOptions providerRoutingOptions;
	private readonly System.Net.Http.HttpClient httpClient;
	private readonly bool promptCacheEnabled;
	private readonly string promptCacheTtl;

	public OpenRouterLLMClient() {
		GD.Print("[OpenRouterLLMClient] Initialize");
		openRouterApiKey = AgenticConfig.GetValue("OPEN_ROUTER_API_KEY", "");
		model = AgenticConfig.GetValue("MODEL", "openai/gpt-4o-mini");
		temperature = AgenticConfig.GetValue("TEMPERATURE", 1.0f);
		promptCacheEnabled = ParseOptionalBoolLoose(
			AgenticConfig.GetValue("OPEN_ROUTER_PROMPT_CACHE_ENABLED", "true"),
			"OPEN_ROUTER_PROMPT_CACHE_ENABLED") ?? true;
		promptCacheTtl = AgenticConfig.GetValue("OPEN_ROUTER_PROMPT_CACHE_TTL", "5m");
		var providerOnlyList = ParseProviderList(AgenticConfig.GetValue("OPEN_ROUTER_PROVIDER_ONLY", ""));
		var allowFallbacks = ParseOptionalBool(AgenticConfig.GetValue("OPEN_ROUTER_PROVIDER_ALLOW_FALLBACKS", ""));
		if (providerOnlyList != null && providerOnlyList.Count > 0 && allowFallbacks == null) {
			allowFallbacks = false;
		}

		if ((providerOnlyList != null && providerOnlyList.Count > 0) || allowFallbacks != null) {
			providerRoutingOptions = new ProviderRoutingOptions {
				Only = providerOnlyList,
				AllowFallbacks = allowFallbacks
			};
			string onlySummary = providerOnlyList != null && providerOnlyList.Count > 0
				? string.Join(", ", providerOnlyList)
				: "none";
			string allowFallbacksSummary = allowFallbacks != null ? allowFallbacks.Value.ToString() : "default";
			GD.Print(
				$"[OpenRouterLLMClient] Provider routing override - only: {onlySummary}, allow_fallbacks: {allowFallbacksSummary}");
		}

		GD.Print($"[OpenRouterLLMClient] Configuration loaded - Model: {model}, Temperature: {temperature}");
		GD.Print($"[OpenRouterLLMClient] Prompt cache enabled={promptCacheEnabled} ttl={promptCacheTtl}");
		httpClient = new System.Net.Http.HttpClient();
		httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", openRouterApiKey);
		httpClient.DefaultRequestHeaders.Add("User-Agent", "Godot-LLM-Interface");
		GD.Print("[OpenRouterLLMClient] HttpClient initialized successfully");
	}

	public async Task SendWithIndefiniteRetry(List<LLMMessage> messages, List<Tool> tools,
		Action<LLMMessage> onComplete, Action<List<ToolCall>, LLMMessage> onToolCalls) {
		var postprocessedMessages = LLMClientPostprocessor.MergeConsecutiveUserMessages(messages);
		var cachedMessages = ApplyPromptCaching(postprocessedMessages);
		GD.Print(
			$"[OpenRouterLLMClient] Starting SendWithIndefiniteRetry with {cachedMessages.Count} messages and {tools?.Count ?? 0} tools");
		int retryCount = 0;
		while (true) {
			try {
				GD.Print($"[OpenRouterLLMClient] Attempt #{retryCount + 1} - Calling Send()");
				await Send(cachedMessages, tools, onComplete, onToolCalls).ConfigureAwait(false);
				GD.Print("[OpenRouterLLMClient] Send() completed successfully, breaking retry loop");
				break;
			}
			catch (Exception e) {
				retryCount++;
				GD.PrintErr($"[OpenRouterLLMClient] Attempt #{retryCount} failed with error: {e.Message}");
				GD.PrintErr($"[OpenRouterLLMClient] Error calling LLM API: {e.StackTrace}");
				GD.PrintErr($"[OpenRouterLLMClient] Retrying in 5 seconds...");
				await Task.Delay(5000).ConfigureAwait(false);
				GD.Print($"[OpenRouterLLMClient] Retry delay completed, starting attempt #{retryCount + 1}");
			}
		}
	}

	public async Task Send(List<LLMMessage> messages, List<Tool> tools, Action<LLMMessage> onComplete,
		Action<List<ToolCall>, LLMMessage> onToolCalls) {
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		GD.Print($"[OpenRouterLLMClient] Send() started with {messages.Count} messages");

		var requestData = new ChatCompletionRequest {
			Model = model,
			Messages = messages,
			Tools = tools,
			Temperature = temperature,
			MaxTokens = 10000,
			Provider = providerRoutingOptions,
			Reasoning = new ReasoningOptions {
				// Effort = "low",
				// Exclude = false,
				Enabled = true
			}
		};

		GD.Print("[OpenRouterLLMClient] Request data object created, serializing to JSON");
		var jsonOptions = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
		string jsonPayload = JsonSerializer.Serialize(requestData, jsonOptions);
		GD.Print($"[OpenRouterLLMClient] JSON payload serialized, length: {jsonPayload.Length} characters");

		GD.Print("[OpenRouterLLMClient] Creating HTTP request message");
		var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions") {
			Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
		};

		GD.Print("[OpenRouterLLMClient] Sending HTTP request to OpenRouter API...");
		var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
			.ConfigureAwait(false);
		GD.Print(
			$"[OpenRouterLLMClient] HTTP response received with status: {(int)response.StatusCode} {response.StatusCode}");

		if (!response.IsSuccessStatusCode) {
			string errorContent = await ReadLimitedContentAsync(response.Content, MaxErrorBodyBytes).ConfigureAwait(false);
			GD.PrintErr(
				$"[OpenRouterLLMClient] Request failed with status {(int)response.StatusCode} {response.StatusCode}. Reason: {response.ReasonPhrase}. Content: {errorContent}");
			throw new HttpRequestException(
				$"Request failed with status {(int)response.StatusCode} {response.StatusCode}. " +
				$"Reason: {response.ReasonPhrase}. Content: {errorContent}");
		}

		response.EnsureSuccessStatusCode();
		var parseResult = await ParseCompletionResponseAsync(response.Content).ConfigureAwait(false);
		var message = parseResult?.Message;
		if (parseResult?.Usage != null) {
			MainThread.Post(() => Economics.Get()?.RecordUsage(parseResult.Usage, parseResult.CacheDiscount));
		}
		if (message != null) {
			GD.Print("[OpenRouterLLMClient] Response parsed successfully.");

			if (message.ToolCalls != null && message.ToolCalls.Count > 0) {
				GD.Print($"[OpenRouterLLMClient] LLM wants to perform {message.ToolCalls.Count} tool calls.");
				MainThread.Post(() => onToolCalls?.Invoke(message.ToolCalls, message));
			} else {
				GD.Print("[OpenRouterLLMClient] LLM has finished responding.");
				MainThread.Post(() => onComplete?.Invoke(message));
			}
		} else {
			GD.PrintErr("[OpenRouterLLMClient] No choices in response");
			MainThread.Post(() => onComplete?.Invoke(new LLMMessage { Role = "assistant", Content = "" }));
		}
	}

	private List<LLMMessage> ApplyPromptCaching(List<LLMMessage> messages) {
		if (!promptCacheEnabled) return messages;
		if (messages == null || messages.Count == 0) return messages;

		var cached = messages.Select(m => m != null ? new LLMMessage(m) : null).ToList();
		var usedIndices = new HashSet<int>();
		int breakpointsUsed = 0;

		int systemIndex = FindFirstIndex(cached, "system", 0);
		if (systemIndex >= 0 && TryApplyCacheControl(cached[systemIndex], CreateCacheControl())) {
			usedIndices.Add(systemIndex);
			breakpointsUsed++;
		}

		if (breakpointsUsed < MaxCacheBreakpoints) {
			int firstUserIndex = FindFirstIndex(cached, "user", systemIndex >= 0 ? systemIndex + 1 : 0);
			if (firstUserIndex >= 0 && !usedIndices.Contains(firstUserIndex)) {
				if (TryApplyCacheControl(cached[firstUserIndex], CreateCacheControl())) {
					usedIndices.Add(firstUserIndex);
					breakpointsUsed++;
				}
			}
		}

		if (breakpointsUsed < MaxCacheBreakpoints) {
			var assistantIndices = FindLastAssistantIndices(cached, AssistantCacheBreakpoints, usedIndices);
			foreach (var idx in assistantIndices) {
				if (breakpointsUsed >= MaxCacheBreakpoints) break;
				if (TryApplyCacheControl(cached[idx], CreateCacheControl())) {
					usedIndices.Add(idx);
					breakpointsUsed++;
				}
			}
		}

		return cached;
	}

	private CacheControl CreateCacheControl() {
		if (string.Equals(promptCacheTtl, "1h", StringComparison.OrdinalIgnoreCase)) {
			return new CacheControl { Type = "ephemeral", Ttl = "1h" };
		}
		return new CacheControl { Type = "ephemeral" };
	}

	private static bool IsAnthropicModel(string modelId) {
		return !string.IsNullOrWhiteSpace(modelId)
			&& modelId.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase);
	}

	private static int FindFirstIndex(List<LLMMessage> messages, string role, int startIndex) {
		if (messages == null) return -1;
		for (int i = Math.Max(0, startIndex); i < messages.Count; i++) {
			var msg = messages[i];
			if (msg == null) continue;
			if (string.Equals(msg.Role, role, StringComparison.Ordinal)) return i;
		}
		return -1;
	}

	private static List<int> FindLastAssistantIndices(List<LLMMessage> messages, int maxCount, HashSet<int> used) {
		var indices = new List<int>();
		if (messages == null || maxCount <= 0) return indices;
		for (int i = messages.Count - 1; i >= 0 && indices.Count < maxCount; i--) {
			if (used != null && used.Contains(i)) continue;
			var msg = messages[i];
			if (msg == null) continue;
			if (!string.Equals(msg.Role, "assistant", StringComparison.Ordinal)) continue;
			if (!HasTextContent(msg)) continue;
			indices.Add(i);
		}
		indices.Reverse();
		return indices;
	}

	private static bool HasTextContent(LLMMessage message) {
		if (message == null || message.Content == null) return false;
		if (message.Content is string text) return !string.IsNullOrWhiteSpace(text);
		if (message.Content is List<ContentPart> parts) return parts.Any(IsTextPartWithContent);
		return false;
	}

	private static bool TryApplyCacheControl(LLMMessage message, CacheControl cacheControl) {
		if (message == null || cacheControl == null) return false;
		if (message.Content is string text) {
			if (string.IsNullOrWhiteSpace(text)) return false;
			message.Content = new List<ContentPart> {
				new ContentPart { Type = "text", Text = text, CacheControl = cacheControl }
			};
			return true;
		}
		if (message.Content is List<ContentPart> parts) {
			foreach (var part in parts) {
				if (!IsTextPartWithContent(part)) continue;
				if (part.CacheControl != null) continue;
				part.CacheControl = cacheControl;
				return true;
			}
		}
		return false;
	}

	private static bool IsTextPartWithContent(ContentPart part) {
		if (part == null) return false;
		if (part.ImageUrl != null) return false;
		if (!string.IsNullOrWhiteSpace(part.Type) &&
		    !string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase)) return false;
		return !string.IsNullOrWhiteSpace(part.Text);
	}

	private static List<string> ParseProviderList(string raw) {
		if (string.IsNullOrWhiteSpace(raw)) return null;
		var parts = raw.Split(',');
		var providers = new List<string>();
		for (int i = 0; i < parts.Length; i++) {
			var trimmed = parts[i].Trim();
			if (string.IsNullOrWhiteSpace(trimmed)) continue;
			providers.Add(trimmed);
		}
		return providers.Count > 0 ? providers : null;
	}

	private static bool? ParseOptionalBool(string raw) {
		if (string.IsNullOrWhiteSpace(raw)) return null;
		if (bool.TryParse(raw, out bool parsed)) return parsed;
		if (raw == "1") return true;
		if (raw == "0") return false;
		GD.PushWarning(
			$"[OpenRouterLLMClient] Invalid boolean for OPEN_ROUTER_PROVIDER_ALLOW_FALLBACKS: '{raw}'. Use true, false, 1, or 0.");
		return null;
	}

	private static bool? ParseOptionalBoolLoose(string raw, string key) {
		if (string.IsNullOrWhiteSpace(raw)) return null;
		if (bool.TryParse(raw, out bool parsed)) return parsed;
		if (raw == "1") return true;
		if (raw == "0") return false;
		GD.PushWarning($"[OpenRouterLLMClient] Invalid boolean for {key}: '{raw}'. Use true, false, 1, or 0.");
		return null;
	}

	private sealed class CompletionParseResult {
		public LLMMessage Message { get; set; }
		public OpenRouterUsage Usage { get; set; }
		public double? CacheDiscount { get; set; }
	}

	private static async Task<CompletionParseResult> ParseCompletionResponseAsync(HttpContent content) {
		using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
		using var bufferStream = new MemoryStream();
		await stream.CopyToAsync(bufferStream).ConfigureAwait(false);

		if (!bufferStream.TryGetBuffer(out var bufferSegment)) {
			var fallback = bufferStream.ToArray();
			return await ParseCompletionResponseAsync(fallback, 0, fallback.Length).ConfigureAwait(false);
		}

		return await ParseCompletionResponseAsync(
			bufferSegment.Array,
			bufferSegment.Offset,
			bufferSegment.Count
		).ConfigureAwait(false);
	}

	private static async Task<CompletionParseResult> ParseCompletionResponseAsync(byte[] buffer, int bufferOffset,
		int bufferLength) {
		var message = await ParseCompletionMessageIncrementalAsync(buffer, bufferOffset, bufferLength)
			.ConfigureAwait(false);
		var usage = ParseUsage(buffer, bufferOffset, bufferLength, out var cacheDiscount);
		return new CompletionParseResult {
			Message = message,
			Usage = usage,
			CacheDiscount = cacheDiscount
		};
	}

	private static OpenRouterUsage ParseUsage(byte[] buffer, int bufferOffset, int bufferLength,
		out double? cacheDiscount) {
		cacheDiscount = null;
		try {
			using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, bufferOffset, bufferLength));
			var root = doc.RootElement;
			OpenRouterUsage usage = null;
			if (root.TryGetProperty("usage", out var usageElement)) {
				usage = JsonSerializer.Deserialize<OpenRouterUsage>(usageElement.GetRawText());
			}
			if (root.TryGetProperty("cache_discount", out var discountElement) &&
			    discountElement.ValueKind == JsonValueKind.Number) {
				cacheDiscount = discountElement.GetDouble();
			}
			return usage;
		} catch (Exception e) {
			GD.PrintErr($"[OpenRouterLLMClient] Failed to parse usage: {e.Message}");
			return null;
		}
	}

	private static async Task<string> ReadLimitedContentAsync(HttpContent content, int maxBytes) {
		if (content == null) return string.Empty;

		using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
		var buffer = new byte[Math.Min(4096, maxBytes)];
		int totalRead = 0;
		var builder = new StringBuilder();
		while (totalRead < maxBytes) {
			int remaining = maxBytes - totalRead;
			int readSize = Math.Min(buffer.Length, remaining);
			int read = await stream.ReadAsync(buffer, 0, readSize).ConfigureAwait(false);
			if (read <= 0) break;
			totalRead += read;
			builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
		}

		if (totalRead >= maxBytes) {
			int extra = await stream.ReadAsync(buffer, 0, 1).ConfigureAwait(false);
			if (extra > 0) {
				builder.Append("... (truncated)");
			}
		}

		return builder.ToString();
	}

	private static async Task<LLMMessage> ParseCompletionMessageIncrementalAsync(HttpContent content) {
		using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
		using var bufferStream = new MemoryStream();
		await stream.CopyToAsync(bufferStream).ConfigureAwait(false);

		if (!bufferStream.TryGetBuffer(out var bufferSegment)) {
			var fallback = bufferStream.ToArray();
			return await ParseCompletionMessageIncrementalAsync(fallback, 0, fallback.Length).ConfigureAwait(false);
		}

		return await ParseCompletionMessageIncrementalAsync(
			bufferSegment.Array,
			bufferSegment.Offset,
			bufferSegment.Count
		).ConfigureAwait(false);
	}

	private static async Task<LLMMessage> ParseCompletionMessageIncrementalAsync(byte[] buffer, int bufferOffset,
		int bufferLength) {
		var scanState = new CompletionMessageScanState();
		var readerState = new JsonReaderState();
		int cursor = 0;
		while (cursor < bufferLength && !scanState.MessageFound) {
			int bytesConsumed = ParseCompletionMessageSlice(
				buffer,
				bufferOffset + cursor,
				bufferLength - cursor,
				ref readerState,
				scanState,
				ParseTokensPerSlice,
				ParseMillisPerSlice
			);

			if (bytesConsumed <= 0) break;
			cursor += bytesConsumed;

			if (!scanState.MessageFound && cursor < bufferLength) {
				await Task.Yield();
			}
		}

		if (!scanState.MessageFound) return null;

		int messageLength = scanState.MessageEndOffset - scanState.MessageStartOffset;
		if (messageLength <= 0) return null;

		return JsonSerializer.Deserialize<LLMMessage>(
			new ReadOnlySpan<byte>(buffer, scanState.MessageStartOffset, messageLength)
		);
	}

	private static int ParseCompletionMessageSlice(byte[] buffer, int sliceOffset, int sliceLength,
		ref JsonReaderState readerState, CompletionMessageScanState scanState, int maxTokens, int maxMillis) {
		var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(buffer, sliceOffset, sliceLength), true, readerState);
		var sliceStopwatch = System.Diagnostics.Stopwatch.StartNew();
		int tokensProcessed = 0;

		while (reader.Read()) {
			tokensProcessed++;
			scanState.ProcessToken(ref reader, sliceOffset);
			if (scanState.MessageFound) break;
			if (tokensProcessed >= maxTokens) break;
			if (sliceStopwatch.ElapsedMilliseconds >= maxMillis) break;
		}

		readerState = reader.CurrentState;
		return (int)reader.BytesConsumed;
	}

	private sealed class CompletionMessageScanState {
		public bool MessageFound => MessageStartOffset >= 0 && MessageEndOffset > MessageStartOffset;
		public int MessageStartOffset { get; private set; } = -1;
		public int MessageEndOffset { get; private set; } = -1;

		private int depth;
		private bool expectingChoicesValue;
		private bool inChoicesArray;
		private int choicesArrayDepth;
		private int choiceIndex = -1;
		private bool inFirstChoiceObject;
		private int firstChoiceDepth;
		private bool expectingMessageValue;
		private bool capturingMessage;
		private int messageDepth;

		public void ProcessToken(ref Utf8JsonReader reader, int baseOffset) {
			var tokenType = reader.TokenType;

			if (capturingMessage) {
				if (tokenType == JsonTokenType.StartObject || tokenType == JsonTokenType.StartArray) {
					messageDepth++;
				} else if (tokenType == JsonTokenType.EndObject || tokenType == JsonTokenType.EndArray) {
					messageDepth--;
					if (messageDepth <= 0) {
						MessageEndOffset = baseOffset + (int)reader.BytesConsumed;
						capturingMessage = false;
					}
				}
				return;
			}

			if (expectingMessageValue) {
				expectingMessageValue = false;
				MessageStartOffset = baseOffset + (int)reader.TokenStartIndex;
				capturingMessage = true;
				messageDepth = 0;

				if (tokenType == JsonTokenType.StartObject || tokenType == JsonTokenType.StartArray) {
					messageDepth = 1;
					return;
				}

				MessageEndOffset = baseOffset + (int)reader.BytesConsumed;
				capturingMessage = false;
				return;
			}

			if (tokenType == JsonTokenType.PropertyName) {
				if (depth == 1 && reader.ValueTextEquals("choices")) {
					expectingChoicesValue = true;
					return;
				}

				if (inFirstChoiceObject && depth == firstChoiceDepth && reader.ValueTextEquals("message")) {
					expectingMessageValue = true;
					return;
				}

				return;
			}

			if (tokenType == JsonTokenType.StartObject) {
				depth++;
				if (inChoicesArray && depth == choicesArrayDepth + 1) {
					choiceIndex++;
					if (choiceIndex == 0) {
						inFirstChoiceObject = true;
						firstChoiceDepth = depth;
					}
				}
				return;
			}

			if (tokenType == JsonTokenType.EndObject) {
				if (inFirstChoiceObject && depth == firstChoiceDepth) {
					inFirstChoiceObject = false;
				}
				depth--;
				return;
			}

			if (tokenType == JsonTokenType.StartArray) {
				depth++;
				if (expectingChoicesValue) {
					expectingChoicesValue = false;
					inChoicesArray = true;
					choicesArrayDepth = depth;
					choiceIndex = -1;
				}
				return;
			}

			if (tokenType == JsonTokenType.EndArray) {
				if (inChoicesArray && depth == choicesArrayDepth) {
					inChoicesArray = false;
				}
				depth--;
				return;
			}

			if (expectingChoicesValue) {
				expectingChoicesValue = false;
			}
		}
	}
}
