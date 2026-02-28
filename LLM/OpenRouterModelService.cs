using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Game-agnostic service for fetching available models from OpenRouter.
/// Results are cached in-memory with a configurable TTL keyed by API key.
/// </summary>
public static class OpenRouterModelService {
	private const string ModelsEndpoint = "https://openrouter.ai/api/v1/models";
	private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

	private static List<OpenRouterModel> _cachedModels;
	private static string _cachedForApiKey;
	private static DateTime _cacheExpiry = DateTime.MinValue;

	/// <summary>
	/// Fetch text-capable models from OpenRouter. Returns cached list if still valid.
	/// </summary>
	/// <param name="apiKey">Optional API key override. If null, reads OPEN_ROUTER_API_KEY from AgenticConfig.</param>
	/// <returns>Text-capable models sorted alphabetically by name.</returns>
	public static async Task<List<OpenRouterModel>> FetchModelsAsync(string apiKey = null) {
		var key = apiKey ?? AgenticConfig.GetValue("OPEN_ROUTER_API_KEY", "");
		if (string.IsNullOrWhiteSpace(key) || key == "your_api_key_here") {
			throw new InvalidOperationException("OpenRouter API key is not configured.");
		}

		if (_cachedModels != null
		    && string.Equals(_cachedForApiKey, key, StringComparison.Ordinal)
		    && DateTime.UtcNow < _cacheExpiry) {
			GD.Print($"[OpenRouterModelService] Returning {_cachedModels.Count} cached models.");
			return _cachedModels;
		}

		using var httpClient = new System.Net.Http.HttpClient();
		httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
		httpClient.DefaultRequestHeaders.Add("User-Agent", "Godot-LLM-Interface");
		httpClient.Timeout = TimeSpan.FromSeconds(30);

		GD.Print("[OpenRouterModelService] Fetching models from OpenRouter...");
		var response = await httpClient.GetAsync(ModelsEndpoint).ConfigureAwait(false);

		if (!response.IsSuccessStatusCode) {
			string body;
			try {
				body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
			} catch {
				body = "(could not read response body)";
			}
			throw new HttpRequestException(
				$"Failed to fetch models: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
		}

		var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
		var parsed = JsonSerializer.Deserialize<OpenRouterModelsResponse>(json);
		if (parsed?.Data == null) {
			throw new InvalidOperationException("Models response was empty or malformed.");
		}

		var filtered = parsed.Data
			.Where(IsVisionTextCapable)
			.OrderBy(m => m.Name ?? m.Id, StringComparer.OrdinalIgnoreCase)
			.ToList();

		_cachedModels = filtered;
		_cachedForApiKey = key;
		_cacheExpiry = DateTime.UtcNow + CacheTtl;

		GD.Print($"[OpenRouterModelService] Fetched {parsed.Data.Count} total models, {filtered.Count} text-capable.");
		return filtered;
	}

	/// <summary>
	/// Invalidate the cached model list (e.g. after API key change).
	/// </summary>
	public static void InvalidateCache() {
		_cachedModels = null;
		_cachedForApiKey = null;
		_cacheExpiry = DateTime.MinValue;
	}

	private static bool IsVisionTextCapable(OpenRouterModel model) {
		if (model?.Architecture == null) return false;
		var input = model.Architecture.InputModalities;
		var output = model.Architecture.OutputModalities;
		if (input == null || output == null) return false;
		return input.Contains("text") && input.Contains("image") && output.Contains("text");
	}
}
