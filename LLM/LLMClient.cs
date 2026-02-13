using System;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// Interface for LLM backends. Implementations: OpenRouterLLMClient, MockLLMClient, etc.
/// </summary>
public interface LLMClient {
	Task SendWithIndefiniteRetry(
		List<LLMMessage> messages,
		List<Tool> tools,
		Action<LLMMessage> onComplete,
		Action<List<ToolCall>, LLMMessage> onToolCalls
	);
}

internal static class LLMClientPostprocessor {
	private const string UserMergeSeparator = "\n\n";

	public static List<LLMMessage> MergeConsecutiveUserMessages(List<LLMMessage> messages) {
		if (messages == null || messages.Count == 0) return messages;

		var merged = new List<LLMMessage>(messages.Count);
		foreach (var message in messages) {
			if (message == null) {
				merged.Add(null);
				continue;
			}

			if (IsUserMessage(message) && merged.Count > 0 && IsUserMessage(merged[^1])) {
				MergeMessageContent(merged[^1], message);
				continue;
			}

			merged.Add(new LLMMessage(message));
		}

		return SanitizeMessages(merged);
	}

	private static bool IsUserMessage(LLMMessage message) {
		return message != null && string.Equals(message.Role, "user", StringComparison.Ordinal);
	}

	private static void MergeMessageContent(LLMMessage target, LLMMessage next) {
		if (target == null || next == null) return;
		target.Content = MergeContent(target.Content, next.Content);
	}

	private static object MergeContent(object first, object second) {
		if (first == null) return CloneContent(second);
		if (second == null) return first;
		if (first is string firstText && second is string secondText) return MergeText(firstText, secondText);

		var parts = new List<ContentPart>();
		AppendContentParts(parts, first, false);
		AppendContentParts(parts, second, true);
		return parts;
	}

	private static string MergeText(string first, string second) {
		if (string.IsNullOrEmpty(first)) return second ?? string.Empty;
		if (string.IsNullOrEmpty(second)) return first;
		return $"{first}{UserMergeSeparator}{second}";
	}

	private static void AppendContentParts(List<ContentPart> target, object content, bool insertSeparator) {
		if (!HasContent(content)) return;
		if (insertSeparator && target.Count > 0) {
			var last = target[^1];
			if (last != null && IsTextPartWithContent(last)) {
				last.Text += UserMergeSeparator;
			} else {
				insertSeparator = false;
			}
		}

		if (content is string text) {
			target.Add(ContentPart.FromText(text));
			return;
		}
		if (content is List<ContentPart> parts) {
			bool prefixed = false;
			foreach (var part in parts) {
				if (part == null) continue;
				if (IsTextPartEmpty(part)) continue;
				var copy = new ContentPart(part);
				if (insertSeparator && !prefixed && IsTextPartWithContent(copy)) {
					copy.Text = UserMergeSeparator + copy.Text;
					prefixed = true;
				}
				target.Add(copy);
			}
			return;
		}

		target.Add(ContentPart.FromText(content.ToString()));
	}

	private static bool HasContent(object content) {
		if (content == null) return false;
		if (content is string text) return !string.IsNullOrEmpty(text);
		if (content is List<ContentPart> parts) return parts.Count > 0;
		return true;
	}

	private static List<LLMMessage> SanitizeMessages(List<LLMMessage> messages) {
		if (messages == null) return null;
		var sanitized = new List<LLMMessage>(messages.Count);
		foreach (var message in messages) {
			if (message == null) {
				sanitized.Add(null);
				continue;
			}
			var copy = new LLMMessage(message);
			copy.Content = SanitizeContent(copy.Content);
			sanitized.Add(copy);
		}
		return sanitized;
	}

	private static object SanitizeContent(object content) {
		if (content is List<ContentPart> parts) {
			var list = new List<ContentPart>(parts.Count);
			foreach (var part in parts) {
				if (part == null) continue;
				if (IsTextPartEmpty(part)) continue;
				list.Add(new ContentPart(part));
			}
			return list.Count > 0 ? list : string.Empty;
		}
		return content;
	}

	private static bool IsTextPartEmpty(ContentPart part) {
		if (part == null) return true;
		if (part.ImageUrl != null) return false;
		if (!string.IsNullOrWhiteSpace(part.Type) && !string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase)) return false;
		return string.IsNullOrWhiteSpace(part.Text);
	}

	private static bool IsTextPartWithContent(ContentPart part) {
		if (part == null) return false;
		if (!string.IsNullOrWhiteSpace(part.Type) && !string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase)) return false;
		return !string.IsNullOrWhiteSpace(part.Text);
	}

	private static object CloneContent(object content) {
		if (content == null) return null;
		if (content is string text) return text;
		if (content is List<ContentPart> parts) {
			var list = new List<ContentPart>(parts.Count);
			foreach (var part in parts) list.Add(new ContentPart(part));
			return list;
		}
		return content;
	}
}
