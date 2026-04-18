using Godot;
using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Process-wide rate limiter for LLM API requests. All concrete LLMClient
/// implementations that hit a real network endpoint call AcquireAsync() at
/// the top of SendWithIndefiniteRetry; this gates the request's admission
/// to keep the global request rate at or below the configured ceiling.
///
/// Backed by a "next admission timestamp" gate, not a token bucket — the goal
/// is steady spacing between requests under sustained load (and across
/// multiple agents in the same process), not allowing bursts above the
/// configured rate.
///
/// Configuration: AgenticConfig key "LLM_MAX_REQUESTS_PER_SECOND".
/// - 0 or negative (default): limiter disabled, AcquireAsync returns instantly
/// - positive value N: requests are spaced at least 1/N seconds apart
/// Live-reloadable via AgenticConfig.ConfigChanged.
/// </summary>
public static class GlobalLLMRateLimiter {
	private const string ConfigKey = "LLM_MAX_REQUESTS_PER_SECOND";
	private const float DefaultRps = 0f;

	// Serializes admission scheduling so concurrent acquirers each get a
	// distinct slot. Held only for the brief slot-calculation window — the
	// actual wait happens outside the lock so other acquirers can queue.
	private static readonly SemaphoreSlim _scheduleGate = new(1, 1);
	private static volatile float _maxRps = DefaultRps;
	private static long _nextAdmissionAtTicks = 0;

	static GlobalLLMRateLimiter() {
		ReloadFromConfig();
		AgenticConfig.ConfigChanged += OnConfigChanged;
	}

	private static void OnConfigChanged(string key, string value) {
		if (string.Equals(key, ConfigKey, StringComparison.OrdinalIgnoreCase)) {
			ReloadFromConfig();
		}
	}

	private static void ReloadFromConfig() {
		_maxRps = Mathf.Max(0f, AgenticConfig.GetValue(ConfigKey, DefaultRps));
		if (_maxRps > 0f) {
			GD.Print($"[GlobalLLMRateLimiter] Active at {_maxRps:0.##} req/sec (min interval {1000f / _maxRps:0.#} ms).");
		} else {
			GD.Print("[GlobalLLMRateLimiter] Disabled (max RPS = 0).");
		}
	}

	/// <summary>
	/// Wait until the next request is admissible under the configured rate
	/// limit. Returns immediately if the limiter is disabled (max RPS = 0).
	/// Safe to call from multiple agents concurrently.
	/// </summary>
	public static async Task AcquireAsync() {
		float rps = _maxRps;
		if (rps <= 0f) return;

		long minIntervalTicks = (long)(TimeSpan.TicksPerSecond / rps);
		long waitTicks;

		await _scheduleGate.WaitAsync();
		try {
			long nowTicks = DateTime.UtcNow.Ticks;
			long admitAtTicks = Math.Max(nowTicks, _nextAdmissionAtTicks);
			waitTicks = admitAtTicks - nowTicks;
			_nextAdmissionAtTicks = admitAtTicks + minIntervalTicks;
		} finally {
			_scheduleGate.Release();
		}

		if (waitTicks > 0) {
			await Task.Delay(TimeSpan.FromTicks(waitTicks));
		}
	}
}
