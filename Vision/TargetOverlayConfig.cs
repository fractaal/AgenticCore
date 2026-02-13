using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Configuration for target overlay colors. Provides deterministic color assignment
/// based on waypoint address hash, with human-readable color names for LLM text prompts.
/// </summary>
public static class TargetOverlayConfig {
	/// <summary>
	/// Named color entry with both the color value and human-readable name.
	/// </summary>
	public readonly struct NamedColor {
		public readonly Color Color;
		public readonly string Name;

		public NamedColor(Color color, string name) {
			Color = color;
			Name = name;
		}
	}

	/// <summary>
	/// Palette of distinct, named colors for waypoint overlays.
	/// Chosen for good contrast against typical game backgrounds.
	/// </summary>
	public static readonly NamedColor[] Palette = {
		new(new Color(0.2f, 0.6f, 1.0f), "BLUE"),       // Bright blue
		new(new Color(1.0f, 0.3f, 0.3f), "RED"),        // Bright red
		new(new Color(0.3f, 1.0f, 0.3f), "GREEN"),      // Bright green
		new(new Color(1.0f, 1.0f, 0.2f), "YELLOW"),     // Bright yellow
		new(new Color(0.0f, 1.0f, 1.0f), "CYAN"),       // Cyan
		new(new Color(1.0f, 0.4f, 1.0f), "MAGENTA"),    // Magenta/pink
		new(new Color(1.0f, 0.6f, 0.2f), "ORANGE"),     // Orange
		new(new Color(0.7f, 0.4f, 1.0f), "PURPLE"),     // Purple
		new(new Color(0.6f, 1.0f, 0.6f), "LIME"),       // Lime green
		new(new Color(0.4f, 0.8f, 0.8f), "TEAL"),       // Teal
		new(new Color(1.0f, 0.7f, 0.8f), "PINK"),       // Light pink
		new(new Color(1.0f, 1.0f, 0.6f), "CREAM"),      // Cream/light yellow
	};

	// Cache for consistent color assignment within a session
	private static readonly Dictionary<string, int> _addressToColorIndex = new();
	private static int _nextColorIndex = 0;

	/// <summary>
	/// Get the named color for a waypoint address. Deterministic within a session -
	/// same address always gets the same color.
	/// </summary>
	public static NamedColor GetColorForAddress(string address) {
		if (string.IsNullOrEmpty(address)) {
			return Palette[0];
		}

		if (!_addressToColorIndex.TryGetValue(address, out int index)) {
			// Assign next color in rotation
			index = _nextColorIndex % Palette.Length;
			_addressToColorIndex[address] = index;
			_nextColorIndex++;
		}

		return Palette[index];
	}

	/// <summary>
	/// Get the named color for a waypoint.
	/// </summary>
	public static NamedColor GetColorForWaypoint(TargetResolutionWaypoint waypoint) {
		if (!GodotObject.IsInstanceValid(waypoint)) {
			return Palette[0];
		}
		return GetColorForAddress(waypoint.Address);
	}

	/// <summary>
	/// Format a waypoint reference for LLM text prompts with color annotation.
	/// Example: "CheckoutCounter checkout-abc123 (in BLUE)"
	/// </summary>
	public static string FormatWaypointWithColor(TargetResolutionWaypoint waypoint) {
		if (!GodotObject.IsInstanceValid(waypoint)) {
			return "(invalid waypoint)";
		}

		var namedColor = GetColorForWaypoint(waypoint);
		return $"{waypoint.ObjectName} {waypoint.Address} (in {namedColor.Name})";
	}

	/// <summary>
	/// Reset color assignments. Useful for testing or when scene changes significantly.
	/// </summary>
	public static void ResetColorAssignments() {
		_addressToColorIndex.Clear();
		_nextColorIndex = 0;
	}
}

