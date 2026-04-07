using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

public partial class Economics : Node {
	private const string SavePath = "user://Economics.json";

	[Signal] public delegate void UsageUpdatedEventHandler();

	private static Economics _instance;
	public static Economics Instance => _instance;
	public static Economics Get() {
		if (GodotObject.IsInstanceValid(_instance)) return _instance;
		var tree = Engine.GetMainLoop() as SceneTree;
		var root = tree?.Root;
		if (!GodotObject.IsInstanceValid(root)) return null;
		var autoload = root.GetNodeOrNull<Economics>("Economics");
		if (GodotObject.IsInstanceValid(autoload)) {
			_instance = autoload;
			return _instance;
		}
		return null;
	}

	private readonly JsonSerializerOptions _jsonOptions = new() {
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = true
	};

	private EconomicsTotals _allTime = new();
	private EconomicsTotals _session = new();
	private EconomicsTotals _allTimeAtStart = new();

	public double SessionCost => _session.Cost;
	public double SessionCacheDiscount => _session.CacheDiscount;
	public long SessionRequests => _session.Requests;
	public long SessionPromptTokens => _session.PromptTokens;
	public long SessionCompletionTokens => _session.CompletionTokens;
	public long SessionCachedTokens => _session.CachedTokens;
	public long SessionCacheWriteTokens => _session.CacheWriteTokens;
	public double AllTimeCostBefore => _allTimeAtStart.Cost;
	public double AllTimeCostCurrent => _allTime.Cost;

	public override void _Ready() {
		base._Ready();
		if (_instance != null && _instance != this) {
			GD.PrintErr("[Economics] Multiple instances detected. Using the first one.");
			return;
		}
		_instance = this;
		Load();
		_allTimeAtStart = _allTime.Clone();
	}

	public override void _ExitTree() {
		Save();
		base._ExitTree();
	}

	public void RecordUsage(OpenRouterUsage usage, double? cacheDiscount) {
		if (usage == null || usage.Cost == null) return;
		double cost = usage.Cost.Value;
		double discount = cacheDiscount ?? 0.0;
		ApplyUsage(_session, usage, cost, discount);
		ApplyUsage(_allTime, usage, cost, discount);
		Save();
		EmitSignal(SignalName.UsageUpdated);
	}

	private static void ApplyUsage(EconomicsTotals totals, OpenRouterUsage usage, double cost, double discount) {
		totals.Cost += cost;
		totals.CacheDiscount += discount;
		totals.Requests += 1;
		totals.PromptTokens += usage.PromptTokens ?? 0;
		totals.CompletionTokens += usage.CompletionTokens ?? 0;
		totals.CachedTokens += usage.PromptTokensDetails?.CachedTokens ?? 0;
		totals.CacheWriteTokens += usage.PromptTokensDetails?.CacheWriteTokens ?? 0;
	}

	private void Load() {
		if (!FileAccess.FileExists(SavePath)) return;
		try {
			using var file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Read);
			var json = file.GetAsText();
			if (string.IsNullOrWhiteSpace(json)) return;
			var loaded = JsonSerializer.Deserialize<EconomicsTotals>(json, _jsonOptions);
			if (loaded != null) _allTime = loaded;
		} catch (Exception e) {
			GD.PrintErr($"[Economics] Failed to load economics data: {e.Message}");
		}
	}

	private void Save() {
		try {
			using var file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Write);
			var json = JsonSerializer.Serialize(_allTime, _jsonOptions);
			file.StoreString(json);
		} catch (Exception e) {
			GD.PrintErr($"[Economics] Failed to save economics data: {e.Message}");
		}
	}

	public static string FormatMoney(double amount) {
		return "$" + amount.ToString("0.00", CultureInfo.InvariantCulture);
	}

	public static string FormatTokens(long tokens) {
		if (tokens >= 1_000_000) return (tokens / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
		if (tokens >= 1_000) return (tokens / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
		return tokens.ToString(CultureInfo.InvariantCulture);
	}
}

public class EconomicsTotals {
	[JsonPropertyName("cost")] public double Cost { get; set; }
	[JsonPropertyName("cache_discount")] public double CacheDiscount { get; set; }
	[JsonPropertyName("requests")] public long Requests { get; set; }
	[JsonPropertyName("prompt_tokens")] public long PromptTokens { get; set; }
	[JsonPropertyName("completion_tokens")] public long CompletionTokens { get; set; }
	[JsonPropertyName("cached_tokens")] public long CachedTokens { get; set; }
	[JsonPropertyName("cache_write_tokens")] public long CacheWriteTokens { get; set; }

	public EconomicsTotals Clone() {
		return new EconomicsTotals {
			Cost = Cost,
			CacheDiscount = CacheDiscount,
			Requests = Requests,
			PromptTokens = PromptTokens,
			CompletionTokens = CompletionTokens,
			CachedTokens = CachedTokens,
			CacheWriteTokens = CacheWriteTokens
		};
	}
}
