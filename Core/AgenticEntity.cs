using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

public class AgenticEntityConfig {
	// Timing controls - maintains exact feature parity with original AgenticNPC
	public double OptimalTurnaroundTime { get; set; } = 20.0; // Minimum time before ThinkingFinished fires
	public double MaxTurnaroundTime { get; set; } = 30.0; // When ThinkingTakingTooLong event fires
	public double InitialStartDelay { get; set; } = 3.0; // Delay before first thinking cycle

	// Communication settings
	public bool EnableStreaming { get; set; } = true;
	public int MaxRetries { get; set; } = 3;
	public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

	// Warnings and guidance
	public bool WarnOnNoToolCalls { get; set; } = true;
	public bool IncludeNoToolWarningInEphemeral { get; set; } = true;

	// Context management
	// 0 or less disables compaction
	public int MaxHistoryMessages { get; set; } = 0;
}

public interface IAgenticBehavior {
	// Required implementations
	List<LLMMessage> BuildEphemeralContext(List<LLMMessage> persistentContext);
	List<Tool> GetAvailableTools();

	// Expose the underlying AgenticEntity for sidecars (UI, telemetry, etc.)
	AgenticEntity Agentic { get; }

	// Default implementations (can be overridden)
	virtual async Task<ToolCallResult> ExecuteToolCall(ToolCall toolCall) {
		return Results.FailText($"Tool '{toolCall.Function.Name}' not implemented for this entity type.",
			"unsupported");
	}

	virtual ToolCallResult OnToolCallError(ToolCall toolCall, Exception error) {
		return Results.FailText($"Tool call '{toolCall.Function.Name}' failed: {error.Message}", "exception");
	}

	virtual void OnLLMError(Exception error) {
		GD.PrintErr($"[AgenticBehavior] LLM Error: {error.Message}");
	}

	virtual void OnThinkingStarted() { }
	virtual void OnThinkingCompleted(bool wasInterrupted = false) { }
	virtual void OnThinkingTakingTooLong() { }

	// Debug surface for UI renderers; optional to implement
	virtual string GetEphemeralPromptDebugInfo() {
		return string.Empty;
	}
}

public class AgenticEntity {
	// Direct access to conversation history
	public List<LLMMessage> PersistentContext { get; private set; } = new();

	// Behavior provider for entity-specific logic
	private IAgenticBehavior _behavior;

	// Pluggable LLM client (defaults to OpenRouter client)
	private LLMClient _llmClient;
	private bool _llmClientExternallyProvided;

	// Events for timing and lifecycle
	public event Action<bool> ThinkingFinished;
	public event Action ThinkingTakingTooLong;
	public event Action LLMProcessingStarted; // Fires when a Think cycle begins sending to LLM
	public event Action LLMProcessingCompleted; // Fires when LLM response completes (before any tool calls)
	public event Action ToolCallExecuteStarted; // Fires when a tool call starts executing
	public event Action<bool> ToolCallExecuteFinished; // True if the tool call was successful, false if it failed
	public event Action<ToolCallStatus, string, string> ToolCallExecuteFinishedDetailed; // (status, code, message)

	// Completion signaling (alternative to events)
	public TaskCompletionSource<bool> ThinkingCompleted { get; private set; }

	// Stop completion signaling
	private TaskCompletionSource<bool> _stopCompleted;

	// Configuration
	public AgenticEntityConfig Config { get; set; } = new();

	// LLM Loop State (extracted from original AgenticNPC)
	private bool _isLLMDoneThinking = false;
	private bool _hasRequestedLLMResponse = false;
	private double _currentLLMTurnaroundTime = 0;
	private bool _hasNotifiedLLMIsTakingTooLong = false;
	private double _initialStartDelay = 0;
	private bool _stopRequested = false; // Flag for safe stop mechanism

	// Per-cycle observation state
	private bool _sawToolCallsThisCycle = false;
	private bool _compactionPending = false;
	private bool _compactionInProgress = false;

	private const string CompactionSystemPrompt =
		"You are a compaction assistant. You will receive a transcript of a conversation. " +
		"Summarize the entire conversation from start to finish, capturing only important facts, decisions, plans, and open questions. " +
		"Ignore boilerplate warnings and meta chatter. Output only the summary text.";

	// Debug model for renderers
	public List<LLMMessage> LastSentContext { get; private set; } = new();
	public int PersistentCountAtLastSend { get; private set; } = 0;
	public int DebugVersion { get; private set; } = 0;
	public event Action DebugStateChanged;


	public bool DebugPreviewEnabled { get; set; } = false;

	private readonly string _agentLabel;
	private readonly TelemetryClient _telemetry;

	private void BumpDebug() {
		DebugVersion++;
		DebugStateChanged?.Invoke();
	}

	public AgenticEntity(IAgenticBehavior behavior, LLMClient llmClient = null) {
		_behavior = behavior;
		_llmClientExternallyProvided = llmClient != null;
		_llmClient = llmClient ?? CreateConfiguredLLMClient();
		_initialStartDelay = Config.InitialStartDelay;
		ThinkingCompleted = new TaskCompletionSource<bool>();
		// Capture main-thread SynchronizationContext for background LLM callbacks
		MainThread.TryInitFromCurrentThread();

		_agentLabel = ResolveAgentLabel(behavior);
		_telemetry = TelemetryClient.Get();

		AgenticConfig.ConfigChanged += OnConfigChanged;
	}

	// Allow swapping the LLM client at runtime (e.g., tests)
	public void SetLLMClient(LLMClient client) {
		_llmClientExternallyProvided = client != null;
		_llmClient = client ?? CreateConfiguredLLMClient();
	}

	/// <summary>
	/// Unsubscribe from config changes. Call when this entity is no longer needed.
	/// </summary>
	public void Dispose() {
		AgenticConfig.ConfigChanged -= OnConfigChanged;
	}

	private static readonly HashSet<string> LlmConfigKeys = new(StringComparer.OrdinalIgnoreCase) {
		"llm_backend", "LLM_BACKEND",
		"MODEL", "TEMPERATURE",
		"OPEN_ROUTER_API_KEY",
		"CHUTES_API_KEY", "CHUTES_BASE_URL", "CHUTES_MODEL", "CHUTES_AUTH_MODE", "CHUTES_MAX_TOKENS",
		"CODEX_MODEL",
	};

	private void OnConfigChanged(string key, string value) {
		if (_llmClientExternallyProvided) return;
		if (!LlmConfigKeys.Contains(key)) return;
		GD.Print($"[AgenticEntity] LLM config key '{key}' changed, rebuilding LLM client.");
		_llmClient = CreateConfiguredLLMClient();
	}

	private static LLMClient CreateConfiguredLLMClient() {
		string backend = AgenticConfig.GetValue("llm_backend", null);
		if (string.IsNullOrWhiteSpace(backend)) {
			backend = AgenticConfig.GetValue("LLM_BACKEND", "openrouter");
		}
		if (string.Equals(backend, "codex_chatgpt", StringComparison.OrdinalIgnoreCase)) {
			return new CodexChatGPTLLMClient();
		}
		if (string.Equals(backend, "chutes", StringComparison.OrdinalIgnoreCase)
		    || string.Equals(backend, "chutes_ai", StringComparison.OrdinalIgnoreCase)) {
			return new ChutesLLMClient();
		}
		if (!string.Equals(backend, "openrouter", StringComparison.OrdinalIgnoreCase)) {
			GD.PushWarning($"[AgenticEntity] Unknown llm_backend '{backend}', defaulting to OpenRouter.");
		}
		return new OpenRouterLLMClient();
	}

	// Main thinking loop entry point
	public async Task Think() {
		if (_hasRequestedLLMResponse) {
			GD.PrintErr("[AgenticEntity] Think() called while already thinking. Ignoring.");
			return;
		}

		// Reset state for new thinking cycle
		_isLLMDoneThinking = false;
		_hasRequestedLLMResponse = true;
		_currentLLMTurnaroundTime = 0;
		_hasNotifiedLLMIsTakingTooLong = false;
		_stopRequested = false; // Reset stop request for new cycle
		_stopCompleted = null; // Clear any pending stop completion
		ThinkingCompleted = new TaskCompletionSource<bool>();

		_behavior.OnThinkingStarted();
		LLMProcessingStarted?.Invoke();

		await StartThinkingCycle();
	}

	public async Task AwaitThinkingComplete() {
		await ThinkingCompleted.Task;
	}

	// Request safe stop at the next safe point in the conversation flow
	public async Task Stop() {
		if (!_hasRequestedLLMResponse) {
			// Nothing to stop - not currently thinking
			return;
		}

		// If already stopping, wait for existing stop to complete
		if (_stopRequested && _stopCompleted != null) {
			await _stopCompleted.Task;
			return;
		}

		GD.Print("[AgenticEntity] Safe stop requested - will stop at next safe point");
		_stopRequested = true;
		_stopCompleted = new TaskCompletionSource<bool>();
		BumpDebug();

		// Wait for the safe stop to complete
		await _stopCompleted.Task;
	}

	// Actually perform the stop at a safe point
	private void PerformSafeStop() {
		GD.Print("[AgenticEntity] Performing safe stop");

		// Reset all state machine variables to idle state
		_isLLMDoneThinking = false;
		_hasRequestedLLMResponse = false;
		_currentLLMTurnaroundTime = 0;
		_hasNotifiedLLMIsTakingTooLong = false;
		_stopRequested = false;

		// Complete the stop awaiter first
		var stopCompleted = _stopCompleted;
		_stopCompleted = null;

		// Complete the TaskCompletionSource to unblock any awaiting code
		// Use TrySetResult to avoid exceptions if already completed
		ThinkingCompleted.TrySetResult(false); // false indicates interrupted/stopped

		// Notify behavior that thinking was interrupted
		_behavior.OnThinkingCompleted(true);

		// Fire the event to notify any listeners
		ThinkingFinished?.Invoke(true);

		// Complete the stop task to unblock any awaiting Stop() calls
		stopCompleted?.TrySetResult(true);
	}

	// Public method to add user messages into persistent context (no UI here)
	public void AddMessage(LLMMessage message) {
		AppendPersistentMessage(message);
	}

	private void UpdateDebugSnapshotNow() {
		var preview = BuildEphemeralContextWithWarning();
		var postprocessedPreview = LLMClientPostprocessor.MergeConsecutiveUserMessages(preview);
		LastSentContext = postprocessedPreview.Select(m => new LLMMessage(m)).ToList();
		PersistentCountAtLastSend = PersistentContext.Count;
		BumpDebug();
	}

	private void ScheduleDebugSnapshot() {
		MainThread.Enqueue(UpdateDebugSnapshotNow);
	}

	private static string ResolveAgentLabel(IAgenticBehavior behavior) {
		if (behavior is Node node) {
			return node.GetPath().ToString();
		}

		return behavior?.GetType()?.Name ?? "unknown_agent";
	}

	private void AppendPersistentMessage(LLMMessage message) {
		PersistentContext.Add(message ?? new LLMMessage { Role = "assistant", Content = "" });
		HandlePersistentContextChanged();
	}

	private void HandlePersistentContextChanged() {
		MarkCompactionIfNeeded();
		EmitContextStatus();
		BumpDebug();
	}

	private void MarkCompactionIfNeeded() {
		var max = Config?.MaxHistoryMessages ?? 0;
		if (max <= 0) {
			_compactionPending = false;
			return;
		}
		if (PersistentContext.Count > max) _compactionPending = true;
		else if (!_compactionInProgress) _compactionPending = false;
	}

	private bool ShouldCompactNow() {
		var max = Config?.MaxHistoryMessages ?? 0;
		if (max <= 0) return false;
		if (_compactionInProgress) return false;
		return _compactionPending || PersistentContext.Count > max;
	}

	private List<LLMMessage> BuildEphemeralContextWithWarning() {
		var context = _behavior.BuildEphemeralContext(PersistentContext);
		if (Config.IncludeNoToolWarningInEphemeral) {
			context.Add(LLMMessage.FromText("user",
				"⚠️⚠️⚠️ You did not make any tool calls, meaning nothing happened in the world during this time. If this is intentional, disregard this warning. If you were expecting something to happen, then, once more -- commit action by performing tool calls!"));
		}

		return context;
	}

	private async Task MaybeCompactAfterCycleAsync() {
		if (!ShouldCompactNow()) return;
		await RunCompactionAsync();
	}

	private async Task RunCompactionAsync() {
		if (_compactionInProgress) return;
		var max = Config?.MaxHistoryMessages ?? 0;
		if (max <= 0) return;
		_compactionInProgress = true;
		_compactionPending = false;
		EmitCompactionStarted();

		string summaryText = null;
		try {
			var transcript = BuildCompactionTranscript(PersistentContext);
			var summaryMessage = await RequestCompactionSummaryAsync(transcript);
			summaryText = ExtractTextFromContent(summaryMessage?.Content);
		} catch (Exception e) {
			GD.PrintErr($"[AgenticEntity] Compaction failed: {e.Message}");
		}

		if (string.IsNullOrWhiteSpace(summaryText)) summaryText = "Summary unavailable.";
		summaryText = summaryText.Trim();

		PersistentContext.Clear();
		AppendPersistentMessage(LLMMessage.FromText("user", summaryText));

		_compactionInProgress = false;
		EmitCompactionFinished();
	}

	private async Task<LLMMessage> RequestCompactionSummaryAsync(string transcript) {
		var messages = new List<LLMMessage> {
			new LLMMessage { Role = "system", Content = CompactionSystemPrompt },
			new LLMMessage { Role = "user", Content = transcript ?? string.Empty }
		};
		var tools = new List<Tool>();
		var tcs = new TaskCompletionSource<LLMMessage>();
		await _llmClient.SendWithIndefiniteRetry(
			messages,
			tools,
			assistant => tcs.TrySetResult(assistant),
			(toolCalls, assistant) => tcs.TrySetResult(assistant ?? new LLMMessage { Role = "assistant", Content = "" })
		);
		return await tcs.Task;
	}

	private string BuildCompactionTranscript(List<LLMMessage> messages) {
		if (messages == null || messages.Count == 0) return "No conversation history.";
		var sb = new StringBuilder();
		sb.AppendLine("Transcript:");
		foreach (var msg in messages) {
			if (msg == null) continue;
			var role = msg.Role ?? "unknown";
			if (role == "tool" && !string.IsNullOrWhiteSpace(msg.ToolName)) role = $"tool:{msg.ToolName}";
			sb.AppendLine($"[{role}]");
			var content = ExtractTextFromContent(msg.Content);
			if (!string.IsNullOrWhiteSpace(content)) sb.AppendLine(content.Trim());
			if (msg.ToolCalls != null && msg.ToolCalls.Count > 0) {
				sb.AppendLine("Tool calls:");
				foreach (var tc in msg.ToolCalls) {
					if (tc == null) continue;
					var name = tc.Function?.Name ?? "unknown_tool";
					var args = tc.Function?.RawArguments;
					if (!string.IsNullOrWhiteSpace(args)) sb.AppendLine($"- {name} {args}");
					else sb.AppendLine($"- {name}");
				}
			}
			sb.AppendLine();
		}
		return sb.ToString().Trim();
	}

	private static string ExtractTextFromContent(object content) {
		if (content == null) return string.Empty;
		if (content is string s) return s;
		if (content is List<ContentPart> parts) {
			var sb = new StringBuilder();
			foreach (var part in parts) {
				if (part == null) continue;
				if (part.Type == "text" && !string.IsNullOrWhiteSpace(part.Text)) {
					if (sb.Length > 0) sb.AppendLine();
					sb.Append(part.Text.Trim());
					continue;
				}
				if (part.Type == "image_url") {
					if (sb.Length > 0) sb.AppendLine();
					sb.Append("[image]");
					continue;
				}
				if (!string.IsNullOrWhiteSpace(part.Type)) {
					if (sb.Length > 0) sb.AppendLine();
					sb.Append($"[{part.Type}]");
				}
			}
			return sb.ToString();
		}
		return content.ToString();
	}

	private void EmitContextStatus() {
		if (_telemetry == null) return;
		var max = Config?.MaxHistoryMessages ?? 0;
		if (max <= 0) return;
		int num = PersistentContext.Count;
		double percentage = max > 0 ? (num / (double)max) * 100.0 : 0.0;
		_telemetry.Enqueue("remainingContext", _agentLabel, new {
			percentage,
			numMessages = num,
			maxMessages = max
		}, topic: "context");
	}

	private void EmitCompactionStarted() {
		if (_telemetry == null) return;
		var max = Config?.MaxHistoryMessages ?? 0;
		_telemetry.Enqueue("compactionStarted", _agentLabel, new {
			numMessages = PersistentContext.Count,
			maxMessages = max
		}, topic: "context");
	}

	private void EmitCompactionFinished() {
		if (_telemetry == null) return;
		var max = Config?.MaxHistoryMessages ?? 0;
		_telemetry.Enqueue("compactionFinished", _agentLabel, new {
			numMessages = PersistentContext.Count,
			maxMessages = max
		}, topic: "context");
	}

	// Debug state access properties
	public bool IsLLMDoneThinking => _isLLMDoneThinking;
	public bool HasRequestedLLMResponse => _hasRequestedLLMResponse;
	public double CurrentLLMTurnaroundTime => _currentLLMTurnaroundTime;
	public bool HasNotifiedLLMIsTakingTooLong => _hasNotifiedLLMIsTakingTooLong;
	public bool IsCompactionPending => _compactionPending;
	public bool IsCompactionInProgress => _compactionInProgress;
	public bool IsToolExecutionInProgress => ComputeToolExecutionInProgress();

	public bool IsWaitingForOptimalTime => _hasRequestedLLMResponse && _isLLMDoneThinking &&
											   _currentLLMTurnaroundTime < Config.OptimalTurnaroundTime;

	private bool ComputeToolExecutionInProgress() {
		if (PersistentContext == null || PersistentContext.Count == 0) return false;
		for (int i = PersistentContext.Count - 1; i >= 0; i--) {
			var message = PersistentContext[i];
			if (message == null) continue;
			if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)) continue;
			if (message.ToolCalls == null || message.ToolCalls.Count == 0) return false;
			foreach (var toolCall in message.ToolCalls) {
				if (toolCall == null) continue;
				bool hasResult = false;
				for (int j = PersistentContext.Count - 1; j >= 0; j--) {
					var candidate = PersistentContext[j];
					if (candidate == null) continue;
					if (!string.Equals(candidate.Role, "tool", StringComparison.OrdinalIgnoreCase)) continue;
					if (candidate.ToolCallId == toolCall.Id) {
						hasResult = true;
						break;
					}
				}
				if (!hasResult) return true;
			}
			return false;
		}
		return false;
	}

	public bool StopRequested => _stopRequested;
	public bool StopInProgress => _stopCompleted != null;

	// Loop processing (call from consumer's _Process)
	public void Process(double delta) {
		if (_initialStartDelay > 0) {
			_initialStartDelay -= delta;
			return;
		}

		if (_hasRequestedLLMResponse && !_isLLMDoneThinking) {
			_currentLLMTurnaroundTime += delta;

			if (_currentLLMTurnaroundTime > Config.MaxTurnaroundTime && !_hasNotifiedLLMIsTakingTooLong) {
				_hasNotifiedLLMIsTakingTooLong = true;
				ThinkingTakingTooLong?.Invoke();
				_behavior.OnThinkingTakingTooLong();
			}
		} else if (_hasRequestedLLMResponse && _isLLMDoneThinking) {
			// Continue tracking total loop time even after LLM response is complete
			_currentLLMTurnaroundTime += delta;

			// Check if we've met the minimum loop time OR if a stop was requested
			if (_currentLLMTurnaroundTime >= Config.OptimalTurnaroundTime) {
				// Check if a stop was requested before completing normally
				if (_stopRequested) {
					PerformSafeStop();
				} else {
					// We've met the minimum time requirement, complete the thinking cycle normally
					CompleteThinkingCycle();
				}
			}
			// Otherwise, just wait until optimal time has passed before completing
		}
	}

	private void CompleteThinkingCycle() {
		_currentLLMTurnaroundTime = 0;
		_hasNotifiedLLMIsTakingTooLong = false;

		// Reset the loop state to be ready for next cycle
		_isLLMDoneThinking = false;
		_hasRequestedLLMResponse = false;

		_behavior.OnThinkingCompleted(false);
		ThinkingFinished?.Invoke(false);
		ThinkingCompleted.SetResult(true);
	}

	private async Task StartThinkingCycle() {
		// Build full context via behavior's arbitrary transformation (+ optional floating warning)
		_sawToolCallsThisCycle = false;
		var fullContext = BuildEphemeralContextWithWarning();
		var postprocessedContext = LLMClientPostprocessor.MergeConsecutiveUserMessages(fullContext);

		// Snapshot exactly what will be sent
		LastSentContext = postprocessedContext.Select(m => new LLMMessage(m)).ToList();
		PersistentCountAtLastSend = PersistentContext.Count;
		BumpDebug();

		// Get available tools
		var tools = _behavior.GetAvailableTools();

		_telemetry?.Enqueue("llm_send", _agentLabel, new {
			messageCount = postprocessedContext.Count,
			toolCount = tools?.Count ?? 0,
			persistentCount = PersistentContext.Count,
			context = SerializeMessages(postprocessedContext),
			tools = SerializeTools(tools)
		}, topic: "llm_send");

		// Send to LLM via pluggable client
		await _llmClient.SendWithIndefiniteRetry(
			postprocessedContext,
			tools,
			OnLLMResponseComplete,
			OnToolCallsReceived
		);
	}

	private async void OnLLMResponseComplete(LLMMessage assistant) {
		GD.Print($"[AgenticEntity] LLM response complete.");

		// Persist full assistant message (content, reasoning, details)
		AppendPersistentMessage(assistant ?? new LLMMessage { Role = "assistant", Content = "" });
		// Optional persistent warning when no tool calls occurred this cycle
		if (Config.WarnOnNoToolCalls && !_sawToolCallsThisCycle) {
			AppendPersistentMessage(LLMMessage.FromText("user",
				"⚠️⚠️⚠️ You did not make any tool calls, meaning nothing happened in the world during this time. If this is intentional, disregard this warning. If you were expecting something to happen, then, once more -- commit action by performing tool calls!"));
		}

		_telemetry?.Enqueue("llm_response", _agentLabel, new {
			contentType = assistant?.Content?.GetType()?.Name ?? "null",
			toolCalls = assistant?.ToolCalls?.Count ?? 0,
			response = SerializeMessage(assistant)
		}, topic: "llm_response");

		await MaybeCompactAfterCycleAsync();

		_isLLMDoneThinking = true;
		// Debug preview only; gated to avoid per-step churn
		if (DebugPreviewEnabled) {
			ScheduleDebugSnapshot();
		}

		LLMProcessingCompleted?.Invoke();

		// SAFE POINT: LLM response completed without tool calls
		if (_stopRequested) {
			PerformSafeStop();
			return;
		}
	}

	private async void OnToolCallsReceived(List<ToolCall> toolCalls, LLMMessage assistant) {
		var count = toolCalls?.Count ?? 0;
		GD.Print($"[AgenticEntity] Received {count} tool calls");
		if (count > 0) _sawToolCallsThisCycle = true;

		// 1. Persist assistant response with tool calls (includes reasoning/details if present)
		assistant ??= new LLMMessage { Role = "assistant", Content = "", ToolCalls = toolCalls };
		assistant.ToolCalls = toolCalls; // ensure preserved
		AppendPersistentMessage(assistant);
		// Debug preview only; gated to avoid per-step churn
		if (DebugPreviewEnabled) {
			ScheduleDebugSnapshot();
		}

		_telemetry?.Enqueue("tool_calls_received", _agentLabel, new {
			count,
			toolCalls = SerializeToolCalls(toolCalls),
			assistant = SerializeMessage(assistant)
		}, topic: "tool_calls");

		// 2. Execute each tool call
		foreach (var toolCall in toolCalls ?? new List<ToolCall>()) {
			GD.Print($"[AgenticEntity] Executing tool call: {toolCall.Function.Name}");

			try {
				// 3. Delegate to behavior for actual execution
				ToolCallResult result;
				ToolCallExecuteStarted?.Invoke();
				try {
					result = await _behavior.ExecuteToolCall(toolCall);

					if (result.Status == ToolCallStatus.Fail) {
						var _ = result.Message;
						result.Message = $"❌ {toolCall.Function.Name} FAILED: {result.Message}";
					} else if (result.Status == ToolCallStatus.Partial) {
						var _ = result.Message;
						result.Message = $"⚠️ {toolCall.Function.Name} SUCCEEDED PARTIALLY: {result.Message}";
					} else {
						var _ = result.Message;
						result.Message = $"✅ {toolCall.Function.Name} SUCCEEDED: {result.Message}";
					}
				}
				catch (UnknownToolCallException e) {
					GD.PrintErr(
						$"[AgenticEntity] Unknown tool call: {e.Message}\n{e.StackTrace}\nAgentic loop will stop.");
					result = Results.FailText("❌ You tried to use a tool that doesn't exist. Please try again.",
						"unsupported");
				}
				catch (Exception e) {
					GD.PrintErr(
						$"[AgenticEntity] Error executing tool call: {e.Message}\n{e.StackTrace}\nAgentic loop will stop.");

					Stop();

					result = Results.FailText("⚠️ An internal error occured while executing this tool call.",
						"exception");
				}

				// 4. Automatically save tool response
				// Best-practice warning: first part should be text
				if (result?.Parts == null || result.Parts.Count == 0 || (result.Parts[0].Type != "text")) {
					GD.PushWarning(
						"[ToolResult] Best practice: first ContentPart should be 'text' summarizing the outcome.");
				}

				var toolResponseMessage =
					LLMMessage.FromToolCallResponse(toolCall, result.Parts ?? new List<ContentPart>());
				AppendPersistentMessage(toolResponseMessage);
				// Debug preview only; gated to avoid per-step churn
				if (DebugPreviewEnabled) {
					ScheduleDebugSnapshot();
				}

				if (!string.IsNullOrEmpty(result.Code) || !string.IsNullOrEmpty(result.Message)) {
					GD.Print(
						$"[ToolResult] FunctionName={toolCall.Function.Name} Status={result.Status} Code={result.Code} Message={result.Message} ContentParts(text-only)={ContentPartUtils.RenderText(result.Parts)}");
				}

				_telemetry?.Enqueue("tool_call_result", _agentLabel, SerializeToolCallResult(toolCall, result),
					topic: "tool_calls");
				ToolCallExecuteFinished?.Invoke(result.Status == ToolCallStatus.Ok);
				ToolCallExecuteFinishedDetailed?.Invoke(result.Status, result.Code, result.Message);
			}
			catch (Exception e) {
				// 5. Handle errors via behavior
				var errorResult = _behavior.OnToolCallError(toolCall, e);
				var errorResponseMessage =
					LLMMessage.FromToolCallResponse(toolCall, errorResult.Parts ?? new List<ContentPart>());
				AppendPersistentMessage(errorResponseMessage);
				// Debug preview only; gated to avoid per-step churn
				if (DebugPreviewEnabled) {
					ScheduleDebugSnapshot();
				}

				_telemetry?.Enqueue("tool_call_result", _agentLabel, SerializeToolCallResult(toolCall, errorResult),
					topic: "tool_calls");
				ToolCallExecuteFinished?.Invoke(false);
				ToolCallExecuteFinishedDetailed?.Invoke(errorResult.Status, errorResult.Code, errorResult.Message);
			}
		}

		// SAFE POINT: All tool calls executed, before re-prompting LLM
		if (_stopRequested) {
			PerformSafeStop();
			return;
		}

		// 4. Re-prompt LLM with updated context (+ optional floating warning)
		var fullContext = BuildEphemeralContextWithWarning();
		var postprocessedContext = LLMClientPostprocessor.MergeConsecutiveUserMessages(fullContext);
		LastSentContext = postprocessedContext.Select(m => new LLMMessage(m)).ToList();
		PersistentCountAtLastSend = PersistentContext.Count;
		BumpDebug();
		var tools = _behavior.GetAvailableTools();
		_telemetry?.Enqueue("llm_send", _agentLabel, new {
			messageCount = postprocessedContext.Count,
			toolCount = tools?.Count ?? 0,
			persistentCount = PersistentContext.Count,
			context = SerializeMessages(postprocessedContext),
			tools = SerializeTools(tools)
		}, topic: "llm_send");


		await _llmClient.SendWithIndefiniteRetry(
			postprocessedContext,
			tools,
			OnLLMResponseComplete,
			OnToolCallsReceived
		);
	}

	// ---------- Telemetry helpers ----------
	private List<object> SerializeMessages(List<LLMMessage> msgs) {
		if (msgs == null) return null;
		var list = new List<object>();
		for (int i = 0; i < msgs.Count; i++) {
			list.Add(SerializeMessage(msgs[i]));
		}

		return list;
	}

	private object SerializeMessage(LLMMessage msg) {
		if (msg == null) return null;
		return new {
			id = msg.Id,
			role = msg.Role,
			content = PreviewContent(msg.Content),
			toolCalls = SerializeToolCalls(msg.ToolCalls),
			reasoning = msg.Reasoning,
			reasoningDetails = msg.ReasoningDetails
		};
	}

	private List<object> SerializeToolCalls(List<ToolCall> toolCalls) {
		if (toolCalls == null) return null;
		var list = new List<object>();
		foreach (var tc in toolCalls) {
			list.Add(new {
				name = tc.Function?.Name,
				args = tc.Function?.RawArguments,
				id = tc.Id
			});
		}

		return list;
	}

	private List<object> SerializeTools(List<Tool> tools) {
		if (tools == null) return null;
		var list = new List<object>();
		foreach (var t in tools) {
			list.Add(new { name = t.Function?.Name, description = t.Function?.Description });
		}

		return list;
	}

	private object SerializeToolCallResult(ToolCall toolCall, ToolCallResult result) {
		if (toolCall == null || result == null) return null;
		return new {
			name = toolCall.Function?.Name,
			id = toolCall.Id,
			status = result.Status.ToString(),
			code = result.Code,
			message = result.Message,
			partCount = result.Parts?.Count ?? 0,
			content = PreviewContent(result.Parts)
		};
	}

	private object PreviewContent(object content) {
		if (content == null) return null;
		if (content is string s) return s;
		if (content is List<ContentPart> parts) {
			var preview = new List<object>();
			foreach (var p in parts) {
				if (p.Type == "text") preview.Add(new { type = "text", text = p.Text });
				else if (p.Type == "image_url") preview.Add(new { type = "image_url", url = p.ImageUrl?.Url });
				else preview.Add(new { type = p.Type });
			}

			return preview;
		}

		return content.ToString();
	}
}
