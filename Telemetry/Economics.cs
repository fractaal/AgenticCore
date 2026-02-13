using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

public partial class Economics : Node {
	private const string SavePath = "user://Economics.json";
	private const double UiUpdateEpsilon = 0.00001;

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

	private CanvasLayer _layer;
	private Control _root;
	private Label _label;
	private double _lastRenderedSessionCost = double.NaN;
	private double _lastRenderedAllTimeCost = double.NaN;

	public override void _Ready() {
		base._Ready();
		if (_instance != null && _instance != this) {
			GD.PrintErr("[Economics] Multiple instances detected. Using the first one.");
			return;
		}
		_instance = this;
		Load();
		_allTimeAtStart = _allTime.Clone();
		BuildOverlay();
		RefreshDisplay();
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
		RefreshDisplay();
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

	private void BuildOverlay() {
		_layer = new CanvasLayer {
			Name = "EconomicsOverlay",
			Layer = 110
		};
		_root = new Control {
			Name = "EconomicsRoot",
			MouseFilter = Control.MouseFilterEnum.Pass
		};
		_root.AnchorLeft = 0;
		_root.AnchorRight = 1;
		_root.AnchorTop = 0;
		_root.AnchorBottom = 1;

		_label = new Label {
			Name = "EconomicsLabel",
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Top,
			AutowrapMode = TextServer.AutowrapMode.Off,
			MouseFilter = Control.MouseFilterEnum.Pass
		};
		_label.AnchorLeft = 1;
		_label.AnchorRight = 1;
		_label.AnchorTop = 0;
		_label.AnchorBottom = 0;
		_label.OffsetLeft = -520;
		_label.OffsetRight = -16;
		_label.OffsetTop = 8;
		_label.OffsetBottom = 72;

		_root.AddChild(_label);
		_layer.AddChild(_root);
		CallDeferred(Node.MethodName.AddChild, _layer);
	}

	private void RefreshDisplay() {
		if (!GodotObject.IsInstanceValid(_label)) return;
		if (Math.Abs(_session.Cost - _lastRenderedSessionCost) < UiUpdateEpsilon &&
		    Math.Abs(_allTime.Cost - _lastRenderedAllTimeCost) < UiUpdateEpsilon) return;

		_lastRenderedSessionCost = _session.Cost;
		_lastRenderedAllTimeCost = _allTime.Cost;

		double allTimeBefore = _allTimeAtStart.Cost;
		double allTimeIncluding = _allTime.Cost;
		double sessionCost = _session.Cost;
		double sessionDiscount = _session.CacheDiscount;
		double sessionWouldHave = sessionCost + sessionDiscount;
		string sessionDeltaLabel = sessionDiscount >= 0.0
			? $"saved {FormatMoney(sessionDiscount)}"
			: $"lost {FormatMoney(Math.Abs(sessionDiscount))}";

		_label.Text =
			$"All Time {FormatMoney(allTimeBefore)} - {FormatMoney(allTimeIncluding)} including this session\n" +
			$"This Session {FormatMoney(sessionCost)} (would have been {FormatMoney(sessionWouldHave)} - {sessionDeltaLabel})";
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

	private static string FormatMoney(double amount) {
		return "$" + amount.ToString("0.00", CultureInfo.InvariantCulture);
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
