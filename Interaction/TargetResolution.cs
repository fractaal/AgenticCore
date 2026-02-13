using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public partial class TargetResolution : Node {
	public override void _Ready() {
		base._Ready();
		Instance = this;
		// Seed cache for already-instanced waypoints
		SeedFromTree();
	}

	public static TargetResolution Instance { get; private set; }


		[Export]
		public bool VerboseLogging { get; set; } = true;

	// Legacy list kept for compatibility in a few methods that enumerate
	Godot.Collections.Array<Node> _waypoints = new();
	// Fast exact lookup (case-insensitive) to avoid scene-tree scans each call
	private readonly Dictionary<string, TargetResolutionWaypoint> _byAddress = new(StringComparer.OrdinalIgnoreCase);

	private void SeedFromTree() {
		_waypoints = GetTree().GetNodesInGroup("TargetResolutionWaypoint");
		_byAddress.Clear();
		for (int i = 0; i < _waypoints.Count; i++) {
			if (_waypoints[i] is TargetResolutionWaypoint wp && IsInstanceValid(wp) && !string.IsNullOrEmpty(wp.Address)) {
				_byAddress[wp.Address] = wp;
			}
		}
	}

	public void RegisterWaypoint(TargetResolutionWaypoint wp) {
		if (!IsInstanceValid(wp) || string.IsNullOrEmpty(wp.Address)) return;
		_byAddress[wp.Address] = wp;
	}

	public void UnregisterWaypoint(TargetResolutionWaypoint wp) {
		if (wp == null) return;
		if (!string.IsNullOrEmpty(wp.Address)) _byAddress.Remove(wp.Address);
	}

	// Deprecated: prefer cached registration. Left as a manual resync escape hatch.
	public void RefreshWaypoints() {
		SeedFromTree();
	}


	public Vector3? ResolveTarget(string targetId) {
		if (string.IsNullOrEmpty(targetId)) return null;
		if (_byAddress.TryGetValue(targetId, out var wp) && IsInstanceValid(wp)) {
			return wp.GetPosition();
		}
		if (VerboseLogging) GD.PrintErr($"[TargetResolution] No waypoint found for target ID '{targetId}'.");
		return null;
	}

	public (bool exactMatch, TargetResolutionWaypoint waypoint) ResolveTargetFuzzy(string targetId) {
		if (string.IsNullOrEmpty(targetId)) return (false, null);
		// Exact first
		if (_byAddress.TryGetValue(targetId, out var exact) && IsInstanceValid(exact)) {
			return (true, exact);
		}
		// Fuzzy: case-insensitive contains on address keys
		foreach (var kv in _byAddress) {
			if (kv.Key != null && kv.Key.IndexOf(targetId, StringComparison.OrdinalIgnoreCase) >= 0 && IsInstanceValid(kv.Value)) {
				return (false, kv.Value);
			}
		}
		return (false, null);
	}
	public string GetAllValidWaypoints(bool forceRefresh = true, Node sourceNode = null) {
		if (forceRefresh) SeedFromTree();
		string result = "\n";
		int i = 0;
		foreach (var kv in _byAddress) {
			var waypoint = kv.Value;
			if (!IsInstanceValid(waypoint)) continue;

			// Get color annotation for visual-text correlation
			var namedColor = TargetOverlayConfig.GetColorForWaypoint(waypoint);
			result += $"{waypoint.ObjectName} {waypoint.Address} (in {namedColor.Name}) | type: {waypoint.ObjectType}\n";

			var interactables = waypoint.GetEntityComponents<Interactable>();
			if (interactables == null || interactables.Count == 0) {
				result += "  Available tools: (none)\n";
				continue;
			}

			var seen = new HashSet<string>();
			var toolNames = new List<string>();
			for (int j = 0; j < interactables.Count; j++) {
				var interactable = interactables[j];
				if (!IsInstanceValid(interactable)) continue;
				var availableTools = interactable.GetAvailableTools(new ToolCallContext(sourceNode: sourceNode, targetWaypoint: waypoint, targetInteractable: interactable));
				if (availableTools == null) continue;
				for (int t = 0; t < availableTools.Count; t++) {
					var tool = availableTools[t];
					if (tool == null || tool.Function == null || string.IsNullOrEmpty(tool.Function.Name)) continue;
					if (seen.Add(tool.Function.Name)) toolNames.Add(tool.Function.Name);
				}
			}

			result += toolNames.Count == 0 ? "  Available tools: (none)\n" : $"  Available tools: {string.Join(", ", toolNames)}\n";
			i++;
		}
		return result;
	}

	/// <summary>
	/// Get all available tools from all interactable objects for a specific caller.
	/// </summary>
	public List<Tool> GetAllInteractableTools(Node sourceNode) {
		var tools = new List<Tool>();
		var seenToolNames = new HashSet<string>();
		foreach (var kv in _byAddress) {
			var waypoint = kv.Value;
			if (!IsInstanceValid(waypoint)) continue;
			var interactables = waypoint.GetEntityComponents<Interactable>();
			for (int j = 0; j < interactables.Count; j++) {
				var interactable = interactables[j];
				if (!IsInstanceValid(interactable)) continue;
				var ctx = new ToolCallContext(sourceNode, waypoint, interactable);
				var availableTools = interactable.GetAvailableTools(ctx);
				foreach (var tool in availableTools) {
					if (!seenToolNames.Contains(tool.Function.Name)) {
						seenToolNames.Add(tool.Function.Name);
						tools.Add(tool);
					} else {
						var existingTool = tools.Find(t => t.Function.Name == tool.Function.Name);
						if (existingTool != null && !AreToolSignaturesIdentical(existingTool, tool)) {
							GD.PrintErr($"[TargetResolution] ERROR: Tool '{existingTool.Function.Name}' has conflicting signatures. Skipping duplicate from interactable {interactable.Name} on entity {waypoint.Address}");
						}
					}
				}
			}
		}
		return tools;
	}

	private bool AreToolSignaturesIdentical(Tool tool1, Tool tool2) {
		if (tool1.Function.Name != tool2.Function.Name) return false;
		if (tool1.Function.Description != tool2.Function.Description) return false;

		// Compare parameters - this is a simplified comparison
		// In a more robust implementation, you might want to do deep parameter comparison
		var params1Json = tool1.Function.Parameters?.ToString() ?? "";
		var params2Json = tool2.Function.Parameters?.ToString() ?? "";

		return params1Json == params2Json;
	}

	/// <summary>
	/// Get the interactable object by target ID
	/// </summary>
	public Interactable GetInteractable(string targetId) {
		if (string.IsNullOrEmpty(targetId)) return null;
		if (_byAddress.TryGetValue(targetId, out var waypoint) && IsInstanceValid(waypoint)) {
			var interactables = waypoint.GetEntityComponents<Interactable>();
			return interactables.FirstOrDefault();
		}
		return null;
	}

	/// <summary>
	/// Execute a tool call on an interactable object with full context
	/// </summary>
	public async Task<ToolCallResult> ExecuteInteractableToolCallAsync(ToolCall toolCall, Node sourceNode) {
		if (VerboseLogging) {
			var argsStr = toolCall.Function.Arguments != null ? toolCall.Function.Arguments.ToString() : "{}";
			GD.Print($"[TargetResolution] Incoming ToolCall name='{toolCall.Function.Name}' args={argsStr}");
		}

		// Determine if this is an outbound (has targetId) or self-scoped (no targetId) tool call
		bool hasTarget = toolCall.Function.Arguments != null && toolCall.Function.Arguments.AsObject().ContainsKey("targetId");

		if (hasTarget) {
			string targetId = toolCall.Function.Arguments["targetId"].ToString();
			if (!_byAddress.TryGetValue(targetId, out var waypoint) || !IsInstanceValid(waypoint)) {
				var allWaypointAddresses = string.Join(", ", _byAddress.Keys);
				if (VerboseLogging) GD.PrintErr($"[TargetResolution] Resolve target FAILED for id='{targetId}'. Available: {allWaypointAddresses}");
				return Results.FailText($"No object found with ID: {targetId}.\nAvailable IDs: {allWaypointAddresses}", "target_not_found");
			}

			// Find the Interactable on this entity that supports this tool (caller-aware)
			var interactables = waypoint.GetEntityComponents<Interactable>();
			Interactable chosen = null;
			for (int i = 0; i < interactables.Count; i++) {
				var it = interactables[i];
				if (!IsInstanceValid(it)) continue;
				var listingCtx = new ToolCallContext(sourceNode, waypoint, it);
				// Ignore proximity for routing so a too-far call still resolves and returns a clear out-of-range error.
				var tools = ToolSchemaBuilder.BuildSchemas(it, listingCtx, ignoreProximity: true);
				for (int t = 0; t < tools.Count; t++) {
					if (tools[t].Function.Name == toolCall.Function.Name) { chosen = it; break; }
				}
				if (IsInstanceValid(chosen)) break;
			}
			if (!IsInstanceValid(chosen)) {
				var allInteractableNames = string.Join(", ", interactables.Select(i => i.Name));
				if (VerboseLogging) GD.PrintErr($"[TargetResolution] Tool '{toolCall.Function.Name}' not found on remote entity '{waypoint.GetBelongingEntity<Node>().Name}'. Available interactables: {allInteractableNames}");
				return Results.FailText($"No interactable on remote target {waypoint.GetBelongingEntity<Node>().Name} advertises public tool: '{toolCall.Function.Name}'.\nAvailable interactables: {allInteractableNames}", "unsupported");
			}
			if (VerboseLogging) GD.Print($"[TargetResolution] Executing '{toolCall.Function.Name}' on '{chosen.Name}' (entity='{waypoint.GetBelongingEntity<Node>().Name}', id='{waypoint.Address}')");
			var ctx = new ToolCallContext(sourceNode, waypoint, chosen);
			return await chosen.ExecuteToolCallAsync(toolCall, ctx);
		}
		else {
			// Self-scoped: find an interactable on the caller's own entity that advertises this tool without a target
			var sourceEntity = sourceNode?.GetBelongingEntity<Node>();
			if (!IsInstanceValid(sourceEntity)) return Results.FailText("Caller/source entity not found. Can't check tool availability without a source entity.", "no_source_entity");

			var interactables = sourceEntity.GetEntityComponents<Interactable>();
			Interactable chosen = null;
			for (int i = 0; i < interactables.Count; i++) {
				var it = interactables[i];
				if (!IsInstanceValid(it)) continue;
				var listingCtx = new ToolCallContext(sourceNode, targetWaypoint: null, targetInteractable: it);
				var tools = ToolSchemaBuilder.BuildSchemas(it, listingCtx, ignoreProximity: true);
				for (int t = 0; t < tools.Count; t++) {
					if (tools[t].Function.Name == toolCall.Function.Name) { chosen = it; break; }
				}
				if (IsInstanceValid(chosen)) break;
			}
			if (!IsInstanceValid(chosen)) {
				var allInteractableNames = string.Join(", ", interactables.Select(i => i.Name));
				if (VerboseLogging) GD.PrintErr($"[TargetResolution] Tool '{toolCall.Function.Name}' not found on self entity '{sourceEntity.Name}'. Available interactables: {allInteractableNames}");
				return Results.FailText($"No interactable on self entity {sourceEntity.Name} advertises private tool: '{toolCall.Function.Name}'.\nAvailable interactables: {allInteractableNames}", "unsupported");
			}
			if (VerboseLogging) GD.Print($"[TargetResolution] Executing '{toolCall.Function.Name}' on '{chosen.Name}' (self entity='{sourceEntity.Name}')");
			var ctx = new ToolCallContext(sourceNode, targetWaypoint: null, targetInteractable: chosen);
			return await chosen.ExecuteToolCallAsync(toolCall, ctx);
		}
	}
}
