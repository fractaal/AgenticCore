using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// Response envelope from GET https://openrouter.ai/api/v1/models
/// </summary>
public class OpenRouterModelsResponse {
	[JsonPropertyName("data")] public List<OpenRouterModel> Data { get; set; }
}

/// <summary>
/// A single model entry from the OpenRouter models listing.
/// </summary>
public class OpenRouterModel {
	[JsonPropertyName("id")] public string Id { get; set; }
	[JsonPropertyName("name")] public string Name { get; set; }
	[JsonPropertyName("description")] public string Description { get; set; }
	[JsonPropertyName("context_length")] public int? ContextLength { get; set; }
	[JsonPropertyName("pricing")] public OpenRouterModelPricing Pricing { get; set; }
	[JsonPropertyName("architecture")] public OpenRouterModelArchitecture Architecture { get; set; }
	[JsonPropertyName("top_provider")] public OpenRouterTopProvider TopProvider { get; set; }
	[JsonPropertyName("supported_parameters")] public List<string> SupportedParameters { get; set; }
}

public class OpenRouterModelPricing {
	/// <summary>Cost per prompt token as a decimal string (e.g. "0.000003").</summary>
	[JsonPropertyName("prompt")] public string Prompt { get; set; }
	/// <summary>Cost per completion token as a decimal string.</summary>
	[JsonPropertyName("completion")] public string Completion { get; set; }
}

public class OpenRouterModelArchitecture {
	[JsonPropertyName("tokenizer")] public string Tokenizer { get; set; }
	[JsonPropertyName("instruct_type")] public string InstructType { get; set; }
	[JsonPropertyName("input_modalities")] public List<string> InputModalities { get; set; }
	[JsonPropertyName("output_modalities")] public List<string> OutputModalities { get; set; }
}

public class OpenRouterTopProvider {
	[JsonPropertyName("context_length")] public int? ContextLength { get; set; }
	[JsonPropertyName("max_completion_tokens")] public int? MaxCompletionTokens { get; set; }
	[JsonPropertyName("is_moderated")] public bool? IsModerated { get; set; }
}
