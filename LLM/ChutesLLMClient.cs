using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public sealed class ChutesLLMClient : LLMClient {
	private readonly OpenAICompatibleLLMClient inner;

	public ChutesLLMClient() {
		GD.Print("[ChutesLLMClient] Initialize");
		var chutesApiKey = AgenticConfig.GetValue("CHUTES_API_KEY", "");
		if (string.IsNullOrWhiteSpace(chutesApiKey)) {
			throw new InvalidOperationException(
				"[ChutesLLMClient] Missing CHUTES_API_KEY. Set CHUTES_API_KEY in user://AgenticConfig.txt.");
		}

		var baseUrl = ResolveBaseUrl();
		var model = AgenticConfig.GetValue("CHUTES_MODEL", "");
		if (string.IsNullOrWhiteSpace(model)) {
			model = baseUrl;
		}

		inner = new OpenAICompatibleLLMClient(new OpenAICompatibleLLMClientOptions {
			ClientName = "ChutesLLMClient",
			BaseUrl = baseUrl,
			BaseUrlConfigKey = "CHUTES_BASE_URL",
			Model = model,
			Temperature = AgenticConfig.GetValue("TEMPERATURE", 1.0f),
			MaxTokens = AgenticConfig.GetValue("CHUTES_MAX_TOKENS", 10000),
			ReasoningEffort = NormalizeReasoningEffort(AgenticConfig.GetValue("CHUTES_REASONING_EFFORT", "")),
			ApiKey = chutesApiKey,
			AuthMode = ParseAuthMode(AgenticConfig.GetValue("CHUTES_AUTH_MODE", ""))
		});
	}

	public Task SendWithIndefiniteRetry(List<LLMMessage> messages, List<Tool> tools, Action<LLMMessage> onComplete,
		Action<List<ToolCall>, LLMMessage> onToolCalls) {
		return inner.SendWithIndefiniteRetry(messages, tools, onComplete, onToolCalls);
	}

	private static string NormalizeReasoningEffort(string raw) {
		if (string.IsNullOrWhiteSpace(raw)) return "";
		string normalized = raw.Trim().ToLowerInvariant();
		switch (normalized) {
			case "minimal":
			case "low":
			case "medium":
			case "high":
				return normalized;
			default:
				GD.PushWarning(
					$"[ChutesLLMClient] Invalid CHUTES_REASONING_EFFORT '{raw}'. " +
					"Expected one of: minimal, low, medium, high (OpenAI-compatible). " +
					"Note: not all Chutes-hosted models honor this hint. Falling back to model default.");
				return "";
		}
	}

	private static OpenAICompatibleAuthMode ParseAuthMode(string rawMode) {
		if (string.IsNullOrWhiteSpace(rawMode)) return OpenAICompatibleAuthMode.XApiKey;

		string normalized = rawMode.Trim().ToLowerInvariant();
		switch (normalized) {
			case "x-api-key":
			case "x_api_key":
			case "xapikey":
			case "api-key":
			case "apikey":
				return OpenAICompatibleAuthMode.XApiKey;
			case "bearer":
			case "authorization":
				return OpenAICompatibleAuthMode.Bearer;
			default:
				GD.PushWarning(
					$"[ChutesLLMClient] Invalid CHUTES_AUTH_MODE '{rawMode}'. Using default 'x-api-key'.");
				return OpenAICompatibleAuthMode.XApiKey;
		}
	}

	private static string ResolveBaseUrl() {
		var configuredBaseUrl = AgenticConfig.GetValue("CHUTES_BASE_URL", "");
		if (!string.IsNullOrWhiteSpace(configuredBaseUrl)) {
			return configuredBaseUrl;
		}

		var username = AgenticConfig.GetValue("CHUTES_USERNAME", "");
		var chuteName = AgenticConfig.GetValue("CHUTES_CHUTE_NAME", "");
		if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(chuteName)) {
			return $"https://api.chutes.ai/chutes/{username.Trim()}/{chuteName.Trim()}";
		}

		throw new InvalidOperationException(
			"[ChutesLLMClient] Missing Chutes endpoint configuration. Set CHUTES_BASE_URL or set both CHUTES_USERNAME and CHUTES_CHUTE_NAME in user://AgenticConfig.txt.");
	}
}
