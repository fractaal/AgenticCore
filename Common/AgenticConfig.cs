using Godot;
using System;
using System.Collections.Generic;

public static class AgenticConfig {
	private static readonly Dictionary<string, string> settings =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	private const string SettingsFilePath = "user://AgenticConfig.txt";

	/// <summary>
	/// Fired after any call to SetValue(). Subscribers receive the changed key and new value.
	/// </summary>
	public static event Action<string, string> ConfigChanged;

	static AgenticConfig() {
		LoadSettings();
	}

	private static void LoadSettings() {
		settings.Clear();

		if (!FileAccess.FileExists(SettingsFilePath)) {
			GD.PushWarning($"Settings file not found: {SettingsFilePath}");
			CreateDefaultSettingsFile();
			return;
		}

		using var file = FileAccess.Open(SettingsFilePath, FileAccess.ModeFlags.Read);
		while (!file.EofReached()) {
			var line = file.GetLine();
			if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) {
				continue;
			}

			var parts = line.Split('=', 2);
			if (parts.Length == 2) {
				settings[parts[0].Trim()] = parts[1].Trim();
			}
		}
	}

	private static void CreateDefaultSettingsFile() {
		GD.Print($"Creating default settings file at: {SettingsFilePath}");
		using var file = FileAccess.Open(SettingsFilePath, FileAccess.ModeFlags.Write);
		file.StoreLine("llm_backend=openrouter");
		file.StoreLine("LLM_BACKEND=openrouter");
		file.StoreLine("OPEN_ROUTER_API_KEY=your_api_key_here");
		file.StoreLine("MODEL=openai/gpt-4o-mini");
		file.StoreLine("CODEX_MODEL=gpt-4.1-mini");
		file.StoreLine("CODEX_REASONING_EFFORT=");
		file.StoreLine("CODEX_REASONING_SUMMARY=");
		file.StoreLine("CODEX_TEXT_VERBOSITY=");
		file.StoreLine("TEMPERATURE=1.0");
		file.StoreLine("CHUTES_API_KEY=your_api_key_here");
		file.StoreLine("CHUTES_BASE_URL=");
		file.StoreLine("CHUTES_USERNAME=");
		file.StoreLine("CHUTES_CHUTE_NAME=");
		file.StoreLine("CHUTES_MODEL=");
		file.StoreLine("CHUTES_AUTH_MODE=x-api-key");
		file.StoreLine("CHUTES_MAX_TOKENS=10000");
		file.StoreLine("OPEN_ROUTER_PROVIDER_ONLY=");
		file.StoreLine("OPEN_ROUTER_PROVIDER_ALLOW_FALLBACKS=");
		file.StoreLine("OPEN_ROUTER_PROMPT_CACHE_ENABLED=true");
		file.StoreLine("OPEN_ROUTER_PROMPT_CACHE_TTL=5m");
		file.StoreLine("STARSHIP_PROMPT_VISION_ENABLED=true");
		file.StoreLine("STARSHIP_COMMAND_AUTHORITY_LABEL=Command Authority");
		file.StoreLine("STARSHIP_UI_MONO_FONT=fantasque");

		// Populate initial settings from defaults
		settings["llm_backend"] = "openrouter";
		settings["LLM_BACKEND"] = "openrouter";
		settings["OPEN_ROUTER_API_KEY"] = "your_api_key_here";
		settings["MODEL"] = "openai/gpt-4o-mini";
		settings["CODEX_MODEL"] = "gpt-4.1-mini";
		settings["CODEX_REASONING_EFFORT"] = "";
		settings["CODEX_REASONING_SUMMARY"] = "";
		settings["CODEX_TEXT_VERBOSITY"] = "";
		settings["TEMPERATURE"] = "1.0";
		settings["CHUTES_API_KEY"] = "your_api_key_here";
		settings["CHUTES_BASE_URL"] = "";
		settings["CHUTES_USERNAME"] = "";
		settings["CHUTES_CHUTE_NAME"] = "";
		settings["CHUTES_MODEL"] = "";
		settings["CHUTES_AUTH_MODE"] = "x-api-key";
		settings["CHUTES_MAX_TOKENS"] = "10000";
		settings["OPEN_ROUTER_PROVIDER_ONLY"] = "";
		settings["OPEN_ROUTER_PROVIDER_ALLOW_FALLBACKS"] = "";
		settings["OPEN_ROUTER_PROMPT_CACHE_ENABLED"] = "true";
		settings["OPEN_ROUTER_PROMPT_CACHE_TTL"] = "5m";
		settings["STARSHIP_PROMPT_VISION_ENABLED"] = "true";
		settings["STARSHIP_COMMAND_AUTHORITY_LABEL"] = "Command Authority";
		settings["STARSHIP_UI_MONO_FONT"] = "fantasque";
	}

	public static string GetValue(string key, string defaultValue = null) {
		if (settings.TryGetValue(key, out var value)) {
			GD.Print($"[Config] {key} found, using value: {value}");
			return value;
		}

		GD.PushWarning($"[Config] {key} not found, using default value: {defaultValue}");
		return defaultValue;
	}

	public static float GetValue(string key, float defaultValue = 0.0f) {
		if (settings.TryGetValue(key, out var value) && float.TryParse(value, out float result)) {
			GD.Print($"[Config] {key} found, using value: {result}");
			return result;
		}

		GD.PushWarning($"[Config] {key} not found, using default value: {defaultValue}");
		return defaultValue;
	}

	public static int GetValue(string key, int defaultValue = 0) {

		if (settings.TryGetValue(key, out var value) && int.TryParse(value, out int result)) {
			GD.Print($"[Config] {key} found, using value: {result}");
			return result;
		}

		GD.PushWarning($"[Config] {key} not found, using default value: {defaultValue}");
		return defaultValue;
	}

	public static bool GetBoolValue(string key, bool defaultValue = false) {
		if (settings.TryGetValue(key, out var value) && TryParseBool(value, out bool result)) {
			GD.Print($"[Config] {key} found, using value: {result}");
			return result;
		}

		GD.PushWarning($"[Config] {key} not found, using default value: {defaultValue}");
		return defaultValue;
	}

	public static void SetValue(string key, string value, bool persist = true) {
		if (string.IsNullOrWhiteSpace(key)) {
			GD.PushError("[Config] Cannot set value: key is null or whitespace.");
			return;
		}

		var trimmedKey = key.Trim();
		settings[trimmedKey] = value?.Trim() ?? string.Empty;
		if (persist) SaveSettings();
		ConfigChanged?.Invoke(trimmedKey, settings[trimmedKey]);
	}

	private static void SaveSettings() {
		try {
			using var file = FileAccess.Open(SettingsFilePath, FileAccess.ModeFlags.Write);
			if (file == null) {
				GD.PushError($"[Config] Failed to open settings file for writing: {SettingsFilePath}");
				return;
			}
			var keys = new List<string>(settings.Keys);
			keys.Sort(StringComparer.OrdinalIgnoreCase);
			foreach (var key in keys) {
				var value = settings.TryGetValue(key, out var storedValue) ? storedValue : string.Empty;
				file.StoreLine($"{key}={value}");
			}
		} catch (Exception e) {
			GD.PushError($"[Config] Failed to save settings file: {e.Message}");
		}
	}

	private static bool TryParseBool(string value, out bool result) {
		result = false;
		if (string.IsNullOrWhiteSpace(value)) return false;
		var normalized = value.Trim().ToLowerInvariant();
		switch (normalized) {
			case "true":
			case "1":
			case "yes":
			case "on":
				result = true;
				return true;
			case "false":
			case "0":
			case "no":
			case "off":
				result = false;
				return true;
			default:
				return false;
		}
	}
}
