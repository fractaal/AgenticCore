using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

/// <summary>
/// Deterministic mock client that reads a fixed script of steps and replays them.
/// Each SendWithIndefiniteRetry call advances one step.
/// Useful for testing AgenticEntity/AgenticNPC without hitting the network.
/// </summary>
public sealed class MockLLMClient : LLMClient {
	public sealed class Step {
		public LLMMessage Assistant { get; set; } // Assistant message to emit (may be null for pure delay)
		public int DelayMs { get; set; } = 0;      // Optional delay before executing this step; if Assistant==null, this is a pure delay step
	}

	private readonly Queue<Step> _steps;

	public MockLLMClient(IEnumerable<Step> steps) {
		GD.Print("[MockLLMClient] Initialize");
		_steps = new Queue<Step>(steps ?? Array.Empty<Step>());
		GD.Print($"[MockLLMClient] Initialized with {_steps.Count} scripted steps");
	}

	public async Task SendWithIndefiniteRetry(
		List<LLMMessage> messages,
		List<Tool> tools,
		Action<LLMMessage> onComplete,
		Action<List<ToolCall>, LLMMessage> onToolCalls
	) {
		var postprocessedMessages = LLMClientPostprocessor.MergeConsecutiveUserMessages(messages);
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		GD.Print($"[MockLLMClient] Starting SendWithIndefiniteRetry with {postprocessedMessages.Count} messages and {tools?.Count ?? 0} tools");
		GD.Print($"[MockLLMClient] {_steps.Count} steps remaining in script");

		if (_steps.Count == 0) {
			GD.Print("[MockLLMClient] No scripted steps remaining, returning empty completion");
			onComplete?.Invoke(new LLMMessage { Role = "assistant", Content = "" });
			GD.Print($"[MockLLMClient] SendWithIndefiniteRetry completed in {stopwatch.ElapsedMilliseconds}ms");
			return;
		}

		// Consume any leading delay-only steps
		while (_steps.Count > 0) {
			var peek = _steps.Peek();
			if (peek.DelayMs > 0) {
				GD.Print($"[MockLLMClient] Delay step: waiting {peek.DelayMs}ms before next action");
				await Task.Delay(peek.DelayMs);
			}
			// If it's a pure delay (no assistant message), discard and continue
			if (peek.Assistant == null) { _steps.Dequeue(); continue; }
			break;
		}

		if (_steps.Count == 0) {
			GD.Print("[MockLLMClient] Script ended on delay-only steps; returning empty completion");
			onComplete?.Invoke(new LLMMessage { Role = "assistant", Content = "" });
			GD.Print($"[MockLLMClient] SendWithIndefiniteRetry completed in {stopwatch.ElapsedMilliseconds}ms");
			return;
		}

		var step = _steps.Dequeue();
		GD.Print($"[MockLLMClient] Dequeued step: {_steps.Count} steps remaining");

		// Optional per-step delay prior to executing assistant/tool calls
		if (step.DelayMs > 0) {
			GD.Print($"[MockLLMClient] Per-step delay: waiting {step.DelayMs}ms");
			await Task.Delay(step.DelayMs);
		}

		var assistant = step.Assistant ?? new LLMMessage { Role = "assistant", Content = "" };

		if (assistant.ToolCalls != null && assistant.ToolCalls.Count > 0) {
			GD.Print($"[MockLLMClient] LLM wants to perform {assistant.ToolCalls.Count} tool calls");
			foreach (var toolCall in assistant.ToolCalls) {
				GD.Print($"[MockLLMClient] Tool call: {toolCall.Function.Name} with args: {toolCall.Function.RawArguments}");
			}
			onToolCalls?.Invoke(assistant.ToolCalls, assistant);
			GD.Print($"[MockLLMClient] SendWithIndefiniteRetry completed with tool calls in {stopwatch.ElapsedMilliseconds}ms");
			return;
		}

		GD.Print("[MockLLMClient] LLM has finished responding");
		GD.Print($"[MockLLMClient] Assistant content: {assistant.Content ?? ""}");
		onComplete?.Invoke(assistant);
		GD.Print($"[MockLLMClient] SendWithIndefiniteRetry completed in {stopwatch.ElapsedMilliseconds}ms");
		return;
	}

	// Convenience builder: create a single tool call step
	public static Step MakeToolCall(string name, JsonNode args, string assistantMsg = "", string reasoning = null) {
		GD.Print($"[MockLLMClient] Creating tool call step for function: {name}");
		var toolCallId = Guid.NewGuid().ToString();
		var argsString = args?.ToJsonString() ?? "{}";
		GD.Print($"[MockLLMClient] Tool call ID: {toolCallId}, Args: {argsString}");

		return new Step {
			Assistant = new LLMMessage {
				Role = "assistant",
				Content = assistantMsg,
				Reasoning = reasoning,
				ToolCalls = new List<ToolCall> {
					new ToolCall {
						Index = 0,
						Id = toolCallId,
						Type = "function",
						Function = new ToolFunction {
							Name = name,
							RawArguments = argsString
						}
					}
				}
			}
		};
	}

	public static Step MakeAssistant(string message, string reasoning = null) {
		GD.Print($"[MockLLMClient] Creating assistant message step: {message}");
		return new Step { Assistant = new LLMMessage { Role = "assistant", Content = message, Reasoning = reasoning } };
	}

		// Convenience builder: pure delay step
		public static Step Delay(int milliseconds) {
			int ms = Math.Max(0, milliseconds);
			GD.Print($"[MockLLMClient] Creating delay step: {ms}ms");
			return new Step { Assistant = null, DelayMs = ms };
		}

}
