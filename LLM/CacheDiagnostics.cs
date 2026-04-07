using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Godot;

/// <summary>
/// Tracks prompt prefix stability across consecutive LLM calls for a single entity.
/// Only analyzes messages up to and including the last <see cref="CacheControl"/>
/// breakpoint — everything after that is ephemeral by definition and ignored.
///
/// Usage: call <see cref="Analyze"/> with the fully-assembled context before each
/// LLM send. The report tells the consumer whether their cached prefix is stable.
/// </summary>
public class CacheDiagnostics {
	private const int PreviewLength = 120;

	private readonly string _label;
	private List<string> _previousHashes;
	private List<string> _previousPreviews;
	private int _previousBreakpointEnd;
	private int _callNumber;

	/// <summary>The most recent diagnostics report. Null before the second call.</summary>
	public CacheDiagnosticsReport LastReport { get; private set; }

	/// <summary>Fires after each <see cref="Analyze"/> call with the new report.</summary>
	public event Action<CacheDiagnosticsReport> ReportGenerated;

	public CacheDiagnostics(string entityLabel = null) {
		_label = entityLabel ?? "?";
	}

	/// <summary>
	/// Analyze a message list for prefix stability compared to the previous call.
	/// Only considers messages up to the last cache breakpoint.
	/// </summary>
	public CacheDiagnosticsReport Analyze(List<LLMMessage> messages) {
		_callNumber++;
		int breakpointEnd = FindLastBreakpointIndex(messages) + 1; // exclusive end
		var currentHashes = HashMessages(messages, breakpointEnd);
		var currentPreviews = BuildPreviews(messages, breakpointEnd);
		var report = BuildReport(
			messages, currentHashes, _previousHashes,
			_previousPreviews, currentPreviews,
			breakpointEnd, _previousBreakpointEnd,
			messages?.Count ?? 0, _callNumber);
		_previousHashes = currentHashes;
		_previousPreviews = currentPreviews;
		_previousBreakpointEnd = breakpointEnd;
		LastReport = report;

		LogReport(report);
		ReportGenerated?.Invoke(report);
		return report;
	}

	// ------------------------------------------------------------------
	//  Breakpoint detection
	// ------------------------------------------------------------------

	/// <summary>
	/// Find the index of the last message that has a cache_control marker.
	/// Returns -1 if no breakpoints found.
	/// </summary>
	private static int FindLastBreakpointIndex(List<LLMMessage> messages) {
		if (messages == null) return -1;
		for (int i = messages.Count - 1; i >= 0; i--) {
			if (HasCacheControl(messages[i])) return i;
		}
		return -1;
	}

	private static bool HasCacheControl(LLMMessage msg) {
		if (msg == null) return false;
		if (msg.Content is List<ContentPart> parts) {
			foreach (var part in parts) {
				if (part?.CacheControl != null) return true;
			}
		}
		return false;
	}

	// ------------------------------------------------------------------
	//  Hashing — one hash per message, based on cache-relevant fields
	// ------------------------------------------------------------------

	private static List<string> HashMessages(List<LLMMessage> messages, int count) {
		if (messages == null) return new List<string>();
		var hashes = new List<string>(count);
		using var sha = SHA256.Create();
		for (int i = 0; i < count && i < messages.Count; i++) {
			var msg = messages[i];
			if (msg == null) {
				hashes.Add("");
				continue;
			}
			var canonical = BuildCanonicalForm(msg);
			var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
			hashes.Add(Convert.ToHexString(bytes, 0, 8));
		}
		return hashes;
	}

	/// <summary>
	/// Builds a deterministic string representation of the message fields that
	/// affect cache identity. Excludes cache_control markers and reasoning
	/// (both are LLM/provider concerns, not authored prefix content).
	/// String content and a single-text ContentPart produce the same canonical
	/// form, so <see cref="PromptCaching.WithCacheBreakpoint"/> (which converts
	/// string → ContentPart) won't cause false invalidation.
	/// </summary>
	private static string BuildCanonicalForm(LLMMessage msg) {
		var sb = new StringBuilder(256);
		sb.Append(msg.Role ?? "");
		sb.Append('|');

		switch (msg.Content) {
			case string text:
				sb.Append("text:");
				sb.Append(text);
				sb.Append(":;");
				break;
			case List<ContentPart> parts:
				foreach (var part in parts) {
					if (part == null) continue;
					sb.Append(part.Type ?? "");
					sb.Append(':');
					sb.Append(part.Text ?? "");
					sb.Append(':');
					sb.Append(part.ImageUrl?.Url ?? "");
					sb.Append(';');
				}
				break;
		}

		sb.Append('|');
		sb.Append(msg.ToolCallId ?? "");
		sb.Append('|');
		sb.Append(msg.ToolName ?? "");

		if (msg.ToolCalls != null) {
			foreach (var tc in msg.ToolCalls) {
				if (tc == null) continue;
				sb.Append('|');
				sb.Append(tc.Id ?? "");
				sb.Append(':');
				sb.Append(tc.Function?.Name ?? "");
				sb.Append(':');
				sb.Append(tc.Function?.RawArguments ?? "");
			}
		}

		return sb.ToString();
	}

	// ------------------------------------------------------------------
	//  Previews — short content summary per message for diff output
	// ------------------------------------------------------------------

	private static List<string> BuildPreviews(List<LLMMessage> messages, int count) {
		if (messages == null) return new List<string>();
		var previews = new List<string>(count);
		for (int i = 0; i < count && i < messages.Count; i++) {
			var msg = messages[i];
			previews.Add(msg != null ? BuildPreview(msg) : "");
		}
		return previews;
	}

	private static string BuildPreview(LLMMessage msg) {
		var text = ExtractPreviewText(msg);
		if (string.IsNullOrWhiteSpace(text)) return $"[{msg.Role ?? "?"}] (empty)";
		text = text.Replace('\n', ' ').Replace('\r', ' ');
		if (text.Length > PreviewLength) text = text.Substring(0, PreviewLength) + "...";
		return $"[{msg.Role ?? "?"}] {text}";
	}

	private static string ExtractPreviewText(LLMMessage msg) {
		if (msg.Content is string text) return text;
		if (msg.Content is List<ContentPart> parts) {
			foreach (var part in parts) {
				if (part == null) continue;
				if (!string.IsNullOrWhiteSpace(part.Text)) return part.Text;
				if (part.ImageUrl != null) return "(image)";
			}
		}
		if (msg.ToolCalls != null && msg.ToolCalls.Count > 0) {
			var first = msg.ToolCalls[0];
			return $"tool_call:{first.Function?.Name ?? "?"}({first.Function?.RawArguments ?? ""})";
		}
		if (!string.IsNullOrWhiteSpace(msg.ToolName)) return $"tool_result:{msg.ToolName}";
		return "";
	}

	// ------------------------------------------------------------------
	//  Report building
	// ------------------------------------------------------------------

	private static CacheDiagnosticsReport BuildReport(
		List<LLMMessage> messages,
		List<string> currentHashes,
		List<string> previousHashes,
		List<string> previousPreviews,
		List<string> currentPreviews,
		int currentBreakpointEnd,
		int previousBreakpointEnd,
		int totalMessages,
		int callNumber
	) {
		var report = new CacheDiagnosticsReport {
			CallNumber = callNumber,
			CachedMessageCount = currentBreakpointEnd,
			TotalMessages = totalMessages,
			FirstMismatchIndex = -1
		};

		if (currentBreakpointEnd == 0) {
			report.Stability = PrefixStability.NoBreakpoints;
			GD.PushWarning($"[CacheDiagnostics] No cache breakpoints found in context — " +
				"use .WithCacheBreakpoint() in BuildEphemeralContext to mark stable messages.");
			return report;
		}

		if (previousHashes == null) {
			report.Stability = PrefixStability.Initial;
			return report;
		}

		// Compare only the cached region (up to the smaller of the two breakpoint scopes)
		int compareEnd = Math.Min(currentBreakpointEnd, previousBreakpointEnd);
		int stableCount = 0;
		bool mismatchSeen = false;

		for (int i = 0; i < compareEnd; i++) {
			if (i < currentHashes.Count && i < previousHashes.Count
			    && currentHashes[i] == previousHashes[i]) {
				if (!mismatchSeen) stableCount++;
			} else {
				if (!mismatchSeen) {
					mismatchSeen = true;
					report.FirstMismatchIndex = i;
					report.FirstMismatchRole = messages != null && i < messages.Count
						? messages[i]?.Role ?? "?" : "?";
					report.PreviousPreview = previousPreviews != null && i < previousPreviews.Count
						? previousPreviews[i] : null;
					report.CurrentPreview = currentPreviews != null && i < currentPreviews.Count
						? currentPreviews[i] : null;
				}
			}
		}

		report.StableMessageCount = stableCount;

		if (mismatchSeen) {
			report.Stability = PrefixStability.Invalidated;
		} else if (currentBreakpointEnd > previousBreakpointEnd) {
			report.Stability = PrefixStability.Extended;
		} else if (currentBreakpointEnd == previousBreakpointEnd) {
			report.Stability = PrefixStability.Stable;
		} else {
			// Breakpoint scope shrank (e.g., compaction cleared history)
			report.Stability = PrefixStability.Stable;
		}

		return report;
	}

	// ------------------------------------------------------------------
	//  Logging
	// ------------------------------------------------------------------

	private void LogReport(CacheDiagnosticsReport report) {
		var sb = new StringBuilder();
		sb.Append($"[CacheDiagnostics:{_label}] Call #{report.CallNumber}: ");

		switch (report.Stability) {
			case PrefixStability.NoBreakpoints:
				sb.Append($"NO BREAKPOINTS — {report.TotalMessages} messages, nothing marked for caching");
				break;
			case PrefixStability.Initial:
				sb.Append($"BASELINE — {report.CachedMessageCount} cached / {report.TotalMessages} total");
				break;
			case PrefixStability.Stable:
				sb.Append($"STABLE — {report.CachedMessageCount} cached / {report.TotalMessages} total");
				break;
			case PrefixStability.Extended:
				sb.Append($"EXTENDED — {report.StableMessageCount} retained + {report.CachedMessageCount - report.StableMessageCount} new cached / {report.TotalMessages} total");
				break;
			case PrefixStability.Invalidated:
				sb.Append($"INVALIDATED at [{report.FirstMismatchIndex}] ({report.FirstMismatchRole})");
				sb.Append($" — {report.StableMessageCount}/{report.CachedMessageCount} cached stable, {report.TotalMessages} total");
				if (report.PreviousPreview != null || report.CurrentPreview != null) {
					sb.Append($"\n[CacheDiagnostics]   was: {report.PreviousPreview ?? "(nothing)"}");
					sb.Append($"\n[CacheDiagnostics]   now: {report.CurrentPreview ?? "(nothing)"}");
				}
				break;
		}

		GD.Print(sb.ToString());
	}
}

// ======================================================================
//  Public types
// ======================================================================

public enum PrefixStability {
	/// <summary>No cache breakpoints found in the context.</summary>
	NoBreakpoints,
	/// <summary>First call for this entity — baseline recorded, no comparison yet.</summary>
	Initial,
	/// <summary>All cached messages are byte-identical to the previous call.</summary>
	Stable,
	/// <summary>Previous cached messages retained, breakpoint scope grew (more messages cached).</summary>
	Extended,
	/// <summary>A cached message changed — prefix invalidated.</summary>
	Invalidated
}

public enum MessageStatus {
	Initial,
	Retained,
	Changed,
	New
}

public class MessageStabilityEntry {
	public int Index;
	public string Role;
	public string Hash;
	public MessageStatus Status;
}

public class CacheDiagnosticsReport {
	public int CallNumber;
	/// <summary>Number of messages in the cached region (up to last breakpoint).</summary>
	public int CachedMessageCount;
	/// <summary>Total messages in the full context.</summary>
	public int TotalMessages;
	/// <summary>Number of leading cached messages that are byte-identical to previous call.</summary>
	public int StableMessageCount;
	public int FirstMismatchIndex;
	public string FirstMismatchRole;
	public string PreviousPreview;
	public string CurrentPreview;
	public PrefixStability Stability;
	public List<MessageStabilityEntry> Messages;
}
