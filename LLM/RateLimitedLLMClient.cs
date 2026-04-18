using System;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// Decorator that gates an inner LLMClient through GlobalLLMRateLimiter.
///
/// Applied automatically by AgenticEntity.CreateConfiguredLLMClient to every
/// network-backed client (OpenRouter, Chutes, Codex). MockLLMClient is NOT
/// wrapped — callers construct it directly and pass it in via the
/// AgenticEntity ctor, so it bypasses this decorator entirely. That means
/// "mock is not throttled" is a structural property of the wiring rather
/// than a convention each new client has to remember.
///
/// Rate limit policy is fully owned by GlobalLLMRateLimiter (single config
/// key, single admission gate). This decorator is just the placement.
/// </summary>
public class RateLimitedLLMClient : LLMClient {
	private readonly LLMClient _inner;

	public RateLimitedLLMClient(LLMClient inner) {
		_inner = inner ?? throw new ArgumentNullException(nameof(inner));
	}

	public async Task SendWithIndefiniteRetry(
		List<LLMMessage> messages,
		List<Tool> tools,
		Action<LLMMessage> onComplete,
		Action<List<ToolCall>, LLMMessage> onToolCalls
	) {
		// Acquired once per logical send so retries within the inner client
		// share the admission. Tight retry loops are throttled at the NEXT
		// logical send, not within the current one.
		await GlobalLLMRateLimiter.AcquireAsync();
		await _inner.SendWithIndefiniteRetry(messages, tools, onComplete, onToolCalls);
	}
}
