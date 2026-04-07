using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Consumer-facing utilities for marking LLM messages with cache breakpoints.
/// Use these in <c>BuildEphemeralContext</c> to tell the LLM provider which parts
/// of your prompt prefix are stable and should be cached.
///
/// The LLM client itself is a dumb pipe — it sends whatever cache markers it
/// receives. The caching decision belongs here, at the consumer level, because
/// only the consumer knows which messages are static vs dynamic.
///
/// <example>
/// <code>
/// public List&lt;LLMMessage&gt; BuildEphemeralContext(List&lt;LLMMessage&gt; persistent) {
///     var ctx = new List&lt;LLMMessage&gt;();
///
///     // Static system prompt — cache this
///     ctx.Add(LLMMessage.FromText("system", systemPrompt).WithCacheBreakpoint());
///
///     // Static reference material — cache this too
///     ctx.Add(LLMMessage.FromText("user", flightManual).WithCacheBreakpoint());
///
///     // Dynamic sensor state — do NOT cache, changes every cycle
///     ctx.Add(LLMMessage.FromText("user", BuildSensorReadings()));
///
///     // Conversation history
///     ctx.AddRange(persistent);
///     return ctx;
/// }
/// </code>
/// </example>
/// </summary>
public static class PromptCaching {
	private static bool _enabled = true;
	private static string _defaultTtl;
	private static bool _initialized;

	/// <summary>
	/// Whether prompt caching is globally enabled. When false,
	/// <see cref="WithCacheBreakpoint"/> is a no-op.
	/// Config key: <c>PROMPT_CACHE_ENABLED</c> (default: true).
	/// </summary>
	public static bool Enabled {
		get {
			EnsureInitialized();
			return _enabled;
		}
	}

	/// <summary>
	/// Default cache TTL from config. Null means provider default (5 min for Anthropic).
	/// Config key: <c>PROMPT_CACHE_TTL</c> (values: <c>null</c>, <c>"1h"</c>).
	/// </summary>
	public static string DefaultTtl {
		get {
			EnsureInitialized();
			return _defaultTtl;
		}
	}

	/// <summary>
	/// Mark a message with a cache breakpoint. The LLM provider will cache
	/// everything up to and including this message's last content part.
	/// <para>
	/// Place breakpoints on <b>stable</b> content that doesn't change between calls
	/// (system prompts, reference documents, static instructions). Do NOT place
	/// breakpoints on dynamic content (sensor readings, timestamps, ephemeral state)
	/// — it wastes cache writes and never hits.
	/// </para>
	/// <para>
	/// No-op when <see cref="Enabled"/> is false or the message has no content.
	/// </para>
	/// </summary>
	/// <param name="message">The message to mark.</param>
	/// <param name="ttl">
	/// Cache TTL override. <c>null</c> uses <see cref="DefaultTtl"/>.
	/// Anthropic supports <c>"1h"</c> for 1-hour cache (2x write cost).
	/// </param>
	/// <returns>The same message (fluent).</returns>
	public static LLMMessage WithCacheBreakpoint(this LLMMessage message, string ttl = null) {
		if (!Enabled) return message;
		if (message == null) return message;

		var resolvedTtl = ttl ?? DefaultTtl;
		var cacheControl = new CacheControl {
			Type = "ephemeral",
			Ttl = resolvedTtl
		};

		ApplyCacheControlToLastPart(message, cacheControl);
		return message;
	}

	/// <summary>
	/// Apply cache_control to the last content part of a message.
	/// Cache breakpoints mark the END of a cacheable prefix — everything up to
	/// and including the marked part gets cached by the provider.
	/// </summary>
	private static void ApplyCacheControlToLastPart(LLMMessage message, CacheControl cacheControl) {
		if (message.Content is string text) {
			if (string.IsNullOrWhiteSpace(text)) return;
			message.Content = new List<ContentPart> {
				new ContentPart { Type = "text", Text = text, CacheControl = cacheControl }
			};
			return;
		}

		if (message.Content is List<ContentPart> parts) {
			for (int i = parts.Count - 1; i >= 0; i--) {
				var part = parts[i];
				if (part == null) continue;
				part.CacheControl = cacheControl;
				return;
			}
		}
	}

	private static void EnsureInitialized() {
		if (_initialized) return;
		_initialized = true;
		RefreshFromConfig();
		AgenticConfig.ConfigChanged += OnConfigChanged;
	}

	private static void OnConfigChanged(string key, string value) {
		if (string.Equals(key, "PROMPT_CACHE_ENABLED", StringComparison.OrdinalIgnoreCase) ||
		    string.Equals(key, "PROMPT_CACHE_TTL", StringComparison.OrdinalIgnoreCase)) {
			RefreshFromConfig();
		}
	}

	private static void RefreshFromConfig() {
		var rawEnabled = AgenticConfig.GetValue("PROMPT_CACHE_ENABLED", "true");
		if (bool.TryParse(rawEnabled, out bool parsed)) _enabled = parsed;
		else if (rawEnabled == "1") _enabled = true;
		else if (rawEnabled == "0") _enabled = false;
		else _enabled = true;

		var rawTtl = AgenticConfig.GetValue("PROMPT_CACHE_TTL", null);
		_defaultTtl = string.IsNullOrWhiteSpace(rawTtl) ? null : rawTtl.Trim();
	}
}
