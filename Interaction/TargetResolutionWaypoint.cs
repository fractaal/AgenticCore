using Godot;
using System;
using System.Collections.Generic;

public partial class TargetResolutionWaypoint : Node {
	[Export]
	private Node3D target;

	/// <summary>
	/// Optional: explicit visual to use for overlay bounding boxes.
	/// When set, TargetOverlayDisplay will use this visual's AABB.
	/// </summary>
	[Export]
	public VisualInstance3D BoundsVisual;

	[Export]
	public string ObjectType;
	[Export]
	public string ObjectName;
	[Export]
	public string Address;

	/// <summary>
	/// If true, disables the legacy Label3D display. Use TargetOverlayDisplay for
	/// screen-space rendering instead (recommended for LLM vision).
	/// </summary>
	[Export]
	public bool DisableLegacyLabel3D = true;

	[Export]
	private Label3D _targetResolutionWaypointDisplay;

	public override void _Ready() {
		base._Ready();

		AddToGroup("TargetResolutionWaypoint");
		// Register with TargetResolution for O(1) lookups (avoids tree scans per tool call)
		TargetResolution.Instance?.RegisterWaypoint(this);

		// Default address if missing
		if (string.IsNullOrEmpty(Address)) {
			var HyphenatedName = ObjectName?.ToLower().Replace(" ", "-").Replace("_", "-");
			// Remove special characters except hyphens
			HyphenatedName = System.Text.RegularExpressions.Regex.Replace(HyphenatedName ?? "", @"[^a-z0-9\-]", "");
			Address = $"{HyphenatedName}-{Guid.NewGuid().ToString().Split('-')[0]}".ToLower();
		}

		// Resolve target priority: [Export] target (if set) > GetEntityComponent<Node3D>()
		if (target == null) {
			var fallback = this.GetEntityComponent<Node3D>();
			if (IsInstanceValid(fallback)) {
				target = fallback;
				GD.PrintErr("[TargetResolutionWaypoint] FELL BACK to GetEntityComponent<Node3D>(). Ensure the scene root is in group 'Entity' and target is set via [Export] when appropriate.");
			} else {
				GD.PrintErr("[TargetResolutionWaypoint] FAILED to resolve target. Please set the [Export] target or mark the entity root with group 'Entity'.");
			}
		}

		// Legacy Label3D system - can be disabled when using TargetOverlayDisplay
		if (DisableLegacyLabel3D) {
			// Hide existing Label3D if assigned
			if (IsInstanceValid(_targetResolutionWaypointDisplay)) {
				_targetResolutionWaypointDisplay.Visible = false;
			}
			return;
		}

		if (_targetResolutionWaypointDisplay == null) {
			var label = new Label3D {
				Name = "WaypointLabel",
				PixelSize = 0.01f,
				Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
				Text = ""
			};
			label.Position = new Vector3(0, 1.2f, 0);
			target.CallDeferred(nameof(_AddTargetResolutionWaypointLabelDeferred));
			_targetResolutionWaypointDisplay = label;
		}

		_targetResolutionWaypointDisplay.Text = $"{ObjectType} ({ObjectName}) - ID:{Address}";
	}

	private void _AddTargetResolutionWaypointLabelDeferred() {
		if (!DisableLegacyLabel3D && IsInstanceValid(target)) {
			target.AddChild(_targetResolutionWaypointDisplay);
		}
	}

	public Vector3 GetPosition() {
		if (target != null) return target.GlobalPosition;
		var parent3D = GetParent() as Node3D;
		return parent3D != null ? parent3D.GlobalPosition : Vector3.Zero;
	}

	public override void _ExitTree() {
		base._ExitTree();
		TargetResolution.Instance?.UnregisterWaypoint(this);
	}

}
