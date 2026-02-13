using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Screen-space overlay that draws color-coded bounding boxes and labels for
/// TargetResolutionWaypoints visible to a camera. Designed to be added as a
/// child of a SubViewport used for LLM vision capture.
/// </summary>
public partial class TargetOverlayDisplay : Control {
	[Export] public FontFile LabelFont;
	[Export] public int FontSize = 14;
	[Export] public float LineWidth = 2.0f;
	[Export] public float BoxPadding = 8.0f;
	[Export] public float LabelPadding = 4.0f;
	[Export] public float FallbackBoxSize = 40.0f; // Screen-space size when no AABB available
	[Export] public float MaxVisibleDistance = 5000.0f; // Don't draw waypoints beyond this distance
	[Export] public bool ShowAddress = true;
	[Export] public bool ShowObjectType = false;
	[Export] public float ScreenEdgePadding = 8.0f;
	[Export] public TargetResolutionWaypoint SelfWaypoint;

	/// <summary>
	/// Optional: Restrict drawing to waypoints within this distance of a source node.
	/// If null, draws all visible waypoints.
	/// </summary>
	public Node3D SourceNode { get; set; }

	private Camera3D _camera;
	private readonly HashSet<ulong> _missingBoundsLogged = new();

	private enum OffscreenDirection {
		None,
		Left,
		Right,
		Up,
		Down
	}

	private readonly struct OverlayEntry {
		public readonly TargetResolutionWaypoint Waypoint;
		public readonly Rect2 BoxRect;
		public readonly Vector2 ScreenPos;
		public readonly TargetOverlayConfig.NamedColor NamedColor;
		public readonly string LabelText;
		public readonly Vector2 LabelSize;
		public readonly OffscreenDirection OffscreenDirection;

		public OverlayEntry(
			TargetResolutionWaypoint waypoint,
			Rect2 boxRect,
			Vector2 screenPos,
			TargetOverlayConfig.NamedColor namedColor,
			string labelText,
			Vector2 labelSize,
			OffscreenDirection offscreenDirection
		) {
			Waypoint = waypoint;
			BoxRect = boxRect;
			ScreenPos = screenPos;
			NamedColor = namedColor;
			LabelText = labelText;
			LabelSize = labelSize;
			OffscreenDirection = offscreenDirection;
		}
	}

	public override void _Ready() {
		SetAnchorsPreset(LayoutPreset.FullRect);

		if (LabelFont == null) {
			LabelFont = ThemeDB.FallbackFont as FontFile;
			if (LabelFont == null) {
				// Try loading a known font
				LabelFont = GD.Load<FontFile>("res://Fonts/ZedMonoNerdFont-Bold.ttf");
			}
		}

		SetProcess(true);
	}

	public override void _Process(double delta) {
		// Update camera reference - use the viewport's camera
		if (!GodotObject.IsInstanceValid(_camera)) {
			_camera = GetViewport().GetCamera3D();
		}
		QueueRedraw();
	}

	public override void _Draw() {
		if (!GodotObject.IsInstanceValid(_camera)) return;
		var viewportRect = GetLocalViewportRect();
		if (viewportRect.Size.X <= 1f || viewportRect.Size.Y <= 1f) return;

		var waypoints = GetVisibleWaypoints();

		// First pass: compute all bounding boxes and label data
		var overlayData = new List<OverlayEntry>();

		foreach (var waypoint in waypoints) {
			var namedColor = TargetOverlayConfig.GetColorForWaypoint(waypoint);
			var screenRect = ComputeScreenBoundingBox(waypoint);
			var screenPos = _camera.UnprojectPosition(waypoint.GetPosition());
			var offscreenDirection = GetOffscreenDirection(screenPos, viewportRect);
			bool isSelf = GodotObject.IsInstanceValid(SelfWaypoint) && waypoint == SelfWaypoint;
			var labelText = BuildLabelText(waypoint, namedColor.Name, isSelf, offscreenDirection);
			var labelSize = MeasureLabelText(labelText);
			overlayData.Add(new OverlayEntry(
				waypoint,
				screenRect,
				screenPos,
				namedColor,
				labelText,
				labelSize,
				offscreenDirection
			));
		}

		// Second pass: compute label positions with collision avoidance
		var placedLabelRects = new List<Rect2>();
		var labelPositions = new List<Vector2>();

		foreach (var entry in overlayData) {
			// Initial position: above the box when on-screen, or pinned to an edge when off-screen
			var labelPos = ComputeLabelPosition(entry.BoxRect, entry.LabelSize, entry.ScreenPos, viewportRect, entry.OffscreenDirection);
			labelPos = ClampLabelPositionToViewport(labelPos, entry.LabelSize, viewportRect);
			var labelRect = BuildLabelRect(labelPos, entry.LabelSize);

			// Nudge down until no collision with already-placed labels
			int maxIterations = 50;
			int iteration = 0;
			while (iteration < maxIterations && HasCollision(labelRect, placedLabelRects)) {
				// Move label down by its height + small gap
				float nudgeAmount = entry.LabelSize.Y + LabelPadding * 2 + 4f;
				labelPos.Y += nudgeAmount;
				labelPos = ClampLabelPositionToViewport(labelPos, entry.LabelSize, viewportRect);
				labelRect = BuildLabelRect(labelPos, entry.LabelSize);
				iteration++;
			}

			placedLabelRects.Add(labelRect);
			labelPositions.Add(labelPos);
		}

		// Third pass: draw everything
		for (int i = 0; i < overlayData.Count; i++) {
			var entry = overlayData[i];
			var labelPos = labelPositions[i];

			// Draw bounding box only if any portion is visible
			if (entry.BoxRect.Intersects(viewportRect)) {
				DrawBoundingBox(entry.BoxRect, entry.NamedColor.Color);
			}

			// Draw label at adjusted position
			DrawWaypointLabelAt(entry.LabelText, labelPos, entry.LabelSize, entry.NamedColor.Color);
		}
	}

	private bool HasCollision(Rect2 rect, List<Rect2> placedRects) {
		foreach (var placed in placedRects) {
			if (rect.Intersects(placed)) return true;
		}
		return false;
	}

	private string BuildLabelText(TargetResolutionWaypoint waypoint, string colorName, bool isSelf, OffscreenDirection offscreenDirection) {
		string nameLine = waypoint.ObjectName ?? "Unknown";
		if (ShowObjectType && !string.IsNullOrEmpty(waypoint.ObjectType)) {
			nameLine = $"[{waypoint.ObjectType}] {nameLine}";
		}
		if (isSelf) {
			nameLine += " (YOU)";
		}
		nameLine = ApplyOffscreenIndicator(nameLine, offscreenDirection);

		var lines = new List<string> { nameLine };
		if (ShowAddress && !string.IsNullOrEmpty(waypoint.Address)) {
			lines.Add(waypoint.Address);
		}
		lines.Add($"({colorName})");
		return string.Join("\n", lines);
	}

	private string ApplyOffscreenIndicator(string labelLine, OffscreenDirection offscreenDirection) {
		switch (offscreenDirection) {
			case OffscreenDirection.Left:
				return $"<< {labelLine}";
			case OffscreenDirection.Right:
				return $"{labelLine} >>";
			case OffscreenDirection.Up:
				return $"^^ {labelLine}";
			case OffscreenDirection.Down:
				return $"{labelLine} vv";
			default:
				return labelLine;
		}
	}

	private Vector2 MeasureLabelText(string labelText) {
		var font = LabelFont ?? ThemeDB.FallbackFont;
		return font.GetMultilineStringSize(labelText, HorizontalAlignment.Left, -1, FontSize);
	}

	private void DrawWaypointLabelAt(string labelText, Vector2 labelPos, Vector2 labelSize, Color color) {
		var font = LabelFont ?? ThemeDB.FallbackFont;

		// Draw background pill for readability
		var bgRect = new Rect2(
			labelPos.X - LabelPadding,
			labelPos.Y - LabelPadding,
			labelSize.X + LabelPadding * 2,
			labelSize.Y + LabelPadding * 2
		);
		DrawRect(bgRect, new Color(0, 0, 0, 0.7f));

		// Draw text
		DrawMultilineString(
			font,
			labelPos,
			labelText,
			HorizontalAlignment.Left,
			-1,
			FontSize,
			-1,
			color
		);
	}

	private List<TargetResolutionWaypoint> GetVisibleWaypoints() {
		var result = new List<TargetResolutionWaypoint>();
		var allWaypoints = GetTree().GetNodesInGroup("TargetResolutionWaypoint");

		Vector3 sourcePos = GodotObject.IsInstanceValid(SourceNode)
			? SourceNode.GlobalPosition
			: _camera.GlobalPosition;

		foreach (var node in allWaypoints) {
			if (node is not TargetResolutionWaypoint waypoint) continue;
			if (!GodotObject.IsInstanceValid(waypoint)) continue;

			var pos = waypoint.GetPosition();
			if (_camera.IsPositionBehind(pos)) continue;

			float distance = sourcePos.DistanceTo(pos);
			if (distance > MaxVisibleDistance) continue;

			result.Add(waypoint);
		}

		return result;
	}

	private Rect2 ComputeScreenBoundingBox(TargetResolutionWaypoint waypoint) {
		Vector3 worldPos = waypoint.GetPosition();

		// Require explicit BoundsVisual for OBB projection
		if (GodotObject.IsInstanceValid(waypoint.BoundsVisual)) {
			var boundsVisual = waypoint.BoundsVisual;
			var localAabb = boundsVisual.GetAabb();
			if (localAabb.Size.LengthSquared() > 0.001f) {
				return ProjectOrientedBoundsToScreen(localAabb, boundsVisual.GlobalTransform);
			}
			LogMissingBoundsVisual(waypoint, "BoundsVisual AABB is empty");
		} else {
			LogMissingBoundsVisual(waypoint, "BoundsVisual not set");
		}

		// Fallback: fixed-size box around center point
		Vector2 screenCenter = _camera.UnprojectPosition(worldPos);
		float halfSize = FallbackBoxSize / 2f;
		return new Rect2(
			screenCenter.X - halfSize,
			screenCenter.Y - halfSize,
			FallbackBoxSize,
			FallbackBoxSize
		);
	}

	private Rect2 ProjectOrientedBoundsToScreen(Aabb localAabb, Transform3D globalTransform) {
		// Project all 8 corners of the local AABB using the full transform (OBB)
		Vector3[] corners = {
			localAabb.Position,
			localAabb.Position + new Vector3(localAabb.Size.X, 0, 0),
			localAabb.Position + new Vector3(0, localAabb.Size.Y, 0),
			localAabb.Position + new Vector3(0, 0, localAabb.Size.Z),
			localAabb.Position + new Vector3(localAabb.Size.X, localAabb.Size.Y, 0),
			localAabb.Position + new Vector3(localAabb.Size.X, 0, localAabb.Size.Z),
			localAabb.Position + new Vector3(0, localAabb.Size.Y, localAabb.Size.Z),
			localAabb.End
		};

		float minX = float.MaxValue, minY = float.MaxValue;
		float maxX = float.MinValue, maxY = float.MinValue;
		int validCorners = 0;

		foreach (var corner in corners) {
			var worldCorner = globalTransform * corner;
			if (_camera.IsPositionBehind(worldCorner)) continue;
			var screenPos = _camera.UnprojectPosition(worldCorner);
			minX = Mathf.Min(minX, screenPos.X);
			minY = Mathf.Min(minY, screenPos.Y);
			maxX = Mathf.Max(maxX, screenPos.X);
			maxY = Mathf.Max(maxY, screenPos.Y);
			validCorners++;
		}

		if (validCorners < 2) {
			// Not enough visible corners, use center as fallback
			var center = globalTransform * localAabb.GetCenter();
			var screenCenter = _camera.UnprojectPosition(center);
			float halfSize = FallbackBoxSize / 2f;
			return new Rect2(screenCenter.X - halfSize, screenCenter.Y - halfSize, FallbackBoxSize, FallbackBoxSize);
		}

		// Add padding
		return new Rect2(
			minX - BoxPadding,
			minY - BoxPadding,
			(maxX - minX) + BoxPadding * 2,
			(maxY - minY) + BoxPadding * 2
		);
	}

	private void LogMissingBoundsVisual(TargetResolutionWaypoint waypoint, string reason) {
		if (!GodotObject.IsInstanceValid(waypoint)) return;
		ulong id = waypoint.GetInstanceId();
		if (_missingBoundsLogged.Contains(id)) return;
		_missingBoundsLogged.Add(id);
		string name = !string.IsNullOrEmpty(waypoint.ObjectName) ? waypoint.ObjectName : waypoint.Name;
		string address = !string.IsNullOrEmpty(waypoint.Address) ? waypoint.Address : "no-address";
		GD.PrintErr($"[TargetOverlayDisplay] Waypoint '{name}' ({address}) missing BoundsVisual: {reason}.");
	}

	private Vector2 ComputeLabelPosition(Rect2 boxRect, Vector2 labelSize, Vector2 screenPos, Rect2 viewportRect, OffscreenDirection offscreenDirection) {
		switch (offscreenDirection) {
			case OffscreenDirection.Left:
				return new Vector2(viewportRect.Position.X + ScreenEdgePadding, screenPos.Y - labelSize.Y * 0.5f);
			case OffscreenDirection.Right:
				return new Vector2(viewportRect.End.X - labelSize.X - ScreenEdgePadding, screenPos.Y - labelSize.Y * 0.5f);
			case OffscreenDirection.Up:
				return new Vector2(screenPos.X - labelSize.X * 0.5f, viewportRect.Position.Y + ScreenEdgePadding);
			case OffscreenDirection.Down:
				return new Vector2(screenPos.X - labelSize.X * 0.5f, viewportRect.End.Y - labelSize.Y - ScreenEdgePadding);
			default:
				return new Vector2(boxRect.Position.X, boxRect.Position.Y - labelSize.Y - LabelPadding);
		}
	}

	private Vector2 ClampLabelPositionToViewport(Vector2 labelPos, Vector2 labelSize, Rect2 viewportRect) {
		float minX = viewportRect.Position.X + ScreenEdgePadding;
		float minY = viewportRect.Position.Y + ScreenEdgePadding;
		float maxX = viewportRect.End.X - labelSize.X - ScreenEdgePadding;
		float maxY = viewportRect.End.Y - labelSize.Y - ScreenEdgePadding;

		if (maxX < minX) maxX = minX;
		if (maxY < minY) maxY = minY;

		return new Vector2(
			Mathf.Clamp(labelPos.X, minX, maxX),
			Mathf.Clamp(labelPos.Y, minY, maxY)
		);
	}

	private Rect2 BuildLabelRect(Vector2 labelPos, Vector2 labelSize) {
		return new Rect2(
			labelPos.X - LabelPadding,
			labelPos.Y - LabelPadding,
			labelSize.X + LabelPadding * 2,
			labelSize.Y + LabelPadding * 2
		);
	}

	private Rect2 GetLocalViewportRect() {
		return new Rect2(Vector2.Zero, Size);
	}

	private OffscreenDirection GetOffscreenDirection(Vector2 screenPos, Rect2 viewportRect) {
		if (screenPos.X < viewportRect.Position.X) return OffscreenDirection.Left;
		if (screenPos.X > viewportRect.End.X) return OffscreenDirection.Right;
		if (screenPos.Y < viewportRect.Position.Y) return OffscreenDirection.Up;
		if (screenPos.Y > viewportRect.End.Y) return OffscreenDirection.Down;
		return OffscreenDirection.None;
	}

	private void DrawBoundingBox(Rect2 rect, Color color) {
		// Draw full rectangle outline
		DrawRect(rect, color, filled: false, width: LineWidth);
	}
}
