using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public sealed class OllamaLLMClient : LLMClient {
	private readonly OpenAICompatibleLLMClient inner;

	public OllamaLLMClient() {
		GD.Print("[OllamaLLMClient] Initialize");
		var authMode = ParseAuthMode(AgenticConfig.GetValue("OLLAMA_AUTH_MODE", ""));
		var apiKey = AgenticConfig.GetValue("OLLAMA_API_KEY", "");
		if (authMode != OpenAICompatibleAuthMode.None && string.IsNullOrWhiteSpace(apiKey)) {
			throw new InvalidOperationException(
				"[OllamaLLMClient] OLLAMA_AUTH_MODE requires OLLAMA_API_KEY. Set both values in user://AgenticConfig.txt, or set OLLAMA_AUTH_MODE=none for local Ollama.");
		}

		inner = new OpenAICompatibleLLMClient(new OpenAICompatibleLLMClientOptions {
			ClientName = "OllamaLLMClient",
			BaseUrl = AgenticConfig.GetValue("OLLAMA_BASE_URL", "http://localhost:11434"),
			BaseUrlConfigKey = "OLLAMA_BASE_URL",
			Model = AgenticConfig.GetValue("OLLAMA_MODEL", "llama3.1"),
			Temperature = AgenticConfig.GetValue("TEMPERATURE", 1.0f),
			MaxTokens = AgenticConfig.GetValue("OLLAMA_MAX_TOKENS", 10000),
			Think = ParseOptionalBool(AgenticConfig.GetValue("OLLAMA_THINK", "")),
			ApiKey = apiKey,
			AuthMode = authMode
		});
	}

	public Task SendWithIndefiniteRetry(List<LLMMessage> messages, List<Tool> tools, Action<LLMMessage> onComplete,
		Action<List<ToolCall>, LLMMessage> onToolCalls) {
		return inner.SendWithIndefiniteRetry(messages, tools, onComplete, onToolCalls);
	}

	private static bool? ParseOptionalBool(string raw) {
		if (string.IsNullOrWhiteSpace(raw)) return null;

		string normalized = raw.Trim().ToLowerInvariant();
		switch (normalized) {
			case "true":
			case "1":
			case "yes":
			case "on":
				return true;
			case "false":
			case "0":
			case "no":
			case "off":
				return false;
			default:
				GD.PushWarning(
					$"[OllamaLLMClient] Invalid OLLAMA_THINK '{raw}'. Use true, false, 1, 0, yes, no, on, off, or leave empty for the model default.");
				return null;
		}
	}

	private static OpenAICompatibleAuthMode ParseAuthMode(string rawMode) {
		if (string.IsNullOrWhiteSpace(rawMode)) return OpenAICompatibleAuthMode.None;

		string normalized = rawMode.Trim().ToLowerInvariant();
		switch (normalized) {
			case "none":
			case "no-auth":
			case "no_auth":
			case "off":
				return OpenAICompatibleAuthMode.None;
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
					$"[OllamaLLMClient] Invalid OLLAMA_AUTH_MODE '{rawMode}'. Using default 'none'.");
				return OpenAICompatibleAuthMode.None;
		}
	}
}
