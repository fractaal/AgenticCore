using Godot;
using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Threading;
using NetHttpClient = System.Net.Http.HttpClient;

/// <summary>
/// HTTP-based telemetry firehose. Posts JSON envelopes to a local collector.
/// </summary>
public partial class TelemetryClient : Node {
	private const int MaxQueuedEvents = 512;
	private const int MaxInflight = 8;
	private static TelemetryClient _instance;
	private static readonly NetHttpClient Http = new NetHttpClient { Timeout = TimeSpan.FromSeconds(2) };

	private readonly ConcurrentQueue<string> _outbound = new();

	private readonly JsonSerializerOptions _jsonOptions = new()
		{ DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

	private bool _enabled;
	private string _endpoint;
	private int _maxSendsPerFrame = 4;
	private int _inflight;

	public static TelemetryClient Instance => _instance;

	/// <summary>
	/// Resolve the singleton TelemetryClient instance created via Godot autoload.
	/// This method never creates a new node; it only looks up the existing autoload.
	/// </summary>
	public static TelemetryClient Get() {
		if (_instance != null) return _instance;
		var tree = Engine.GetMainLoop() as SceneTree;
		if (tree == null) {
			GD.PrintErr("[TelemetryHTTP] TelemetryClient.Get() called but SceneTree is not available.");
			return null;
		}

		var root = tree.Root;
		if (!GodotObject.IsInstanceValid(root)) {
			GD.PrintErr("[TelemetryHTTP] TelemetryClient.Get() called but SceneTree.Root is not valid.");
			return null;
		}

		// Prefer the autoload singleton at /root/TelemetryClient.
		var autoload = root.GetNodeOrNull<TelemetryClient>("TelemetryClient");
		if (GodotObject.IsInstanceValid(autoload)) {
			_instance = autoload;
			return _instance;
		}

		GD.PrintErr("[TelemetryHTTP] TelemetryClient.Get() called but no TelemetryClient autoload node was found. " +
		            "Ensure TelemetryClient is configured as an autoload in project.godot.");
		return null;
	}

	public override void _Ready() {
		base._Ready();
		if (_instance != null && _instance != this) {
			GD.PrintErr("[TelemetryHTTP] Multiple TelemetryClient instances detected. Using the first one.");
		} else {
			_instance = this;
		}

		_enabled = IsEnabled();
		_endpoint = ResolveEndpoint();
		_maxSendsPerFrame = AgenticConfig.GetValue("TELEMETRY_MAX_PER_FRAME", 4);
		GD.Print($"[TelemetryHTTP] enabled={_enabled} endpoint={_endpoint} maxPerFrame={_maxSendsPerFrame}");
		SetProcess(_enabled);
	}

	public override void _Process(double delta) {
		if (!_enabled) return;
		int sent = 0;
		while (sent < _maxSendsPerFrame && _outbound.TryDequeue(out var json)) {
			if (Interlocked.CompareExchange(ref _inflight, 0, 0) >= MaxInflight) {
				_outbound.Enqueue(json); // push it back and wait for inflight to drain
				break;
			}

			Interlocked.Increment(ref _inflight);
			_ = SendAsync(json);
			sent++;
		}
	}

	public void Enqueue(string type, string agent, object payload, string topic = null) {
		if (!_enabled) return;
		if (_outbound.Count >= MaxQueuedEvents) _outbound.TryDequeue(out _); // drop oldest

		var envelope = new TelemetryEnvelope {
			Type = type,
			Agent = agent,
			Topic = topic ?? type,
			TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			Payload = payload
		};

		var json = JsonSerializer.Serialize(envelope, _jsonOptions);
		_outbound.Enqueue(json);
	}

	private async Task SendAsync(string json) {
		try {
			var content = new StringContent(json, Encoding.UTF8, "application/json");
			await Http.PostAsync(_endpoint, content).ConfigureAwait(false);
		}
		catch (Exception e) {
			GD.PrintErr($"[TelemetryHTTP] POST failed: {e.Message}");
		}
		finally {
			Interlocked.Decrement(ref _inflight);
		}
	}

	private bool IsEnabled() {
		var env = System.Environment.GetEnvironmentVariable("TELEMETRY_ENABLED");
		if (!string.IsNullOrEmpty(env) &&
		    (env.Equals("true", StringComparison.OrdinalIgnoreCase) || env == "1")) return true;
		var cfg = AgenticConfig.GetValue("TELEMETRY_ENABLED", "false");
		return cfg.Equals("true", StringComparison.OrdinalIgnoreCase);
	}

	private string ResolveEndpoint() {
		var http = System.Environment.GetEnvironmentVariable("TELEMETRY_HTTP_URL") ??
		           AgenticConfig.GetValue("TELEMETRY_HTTP_URL", null);
		if (!string.IsNullOrWhiteSpace(http)) return http;

		var legacyWs = System.Environment.GetEnvironmentVariable("TELEMETRY_WS_URL") ??
		               AgenticConfig.GetValue("TELEMETRY_WS_URL", null);
		if (!string.IsNullOrWhiteSpace(legacyWs)) {
			// Allow legacy ws://host:port/ingest by converting scheme
			if (legacyWs.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) {
				return "http://" + legacyWs.Substring("ws://".Length);
			}

			if (legacyWs.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) {
				return "https://" + legacyWs.Substring("wss://".Length);
			}

			return legacyWs;
		}

		return "http://127.0.0.1:7777/ingest";
	}

	private sealed class TelemetryEnvelope {
		public string Type { get; set; }
		public string Agent { get; set; }
		public string Topic { get; set; }
		public long TimestampMs { get; set; }
		public object Payload { get; set; }
	}
}
