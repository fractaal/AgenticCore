using Godot;

public static class ArtifactPublisher {
	public static void Emit(Node source, string artifactId, string content, string language = "text", object meta = null) {
		if (!GodotObject.IsInstanceValid(source)) return;
		if (string.IsNullOrWhiteSpace(artifactId)) return;
		if (string.IsNullOrWhiteSpace(content)) return;
		var telemetry = TelemetryClient.Get();
		if (telemetry == null) return;
		var agentLabel = ResolveAgentLabel(source);
		var payload = new {
			id = artifactId,
			language = string.IsNullOrWhiteSpace(language) ? "text" : language,
			content = content,
			meta
		};
		telemetry.Enqueue("artifact_emit", agentLabel, payload, topic: "artifacts");
	}

	public static string BuildArtifactId(Node agentNode, string artifactKey) {
		var agentLabel = ResolveAgentLabel(agentNode);
		if (string.IsNullOrWhiteSpace(artifactKey)) return agentLabel;
		return $"{agentLabel}::{artifactKey}";
	}

	private static string ResolveAgentLabel(Node source) {
		if (!GodotObject.IsInstanceValid(source)) return "unknown_agent";
		return source.GetPath().ToString();
	}
}
