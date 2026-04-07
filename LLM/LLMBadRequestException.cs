using System;

/// <summary>
/// Thrown when the LLM API returns HTTP 400 Bad Request, indicating the request payload
/// (typically the conversation context) is malformed or invalid. This is NOT a transient error —
/// retrying with the same payload will fail identically. The caller should reset context and retry.
/// <para>
/// Distinct from 429 (rate limit) and 5xx (server errors) which ARE transient and safe to retry.
/// </para>
/// </summary>
public class LLMBadRequestException : Exception {
	public int StatusCode { get; }
	public string ResponseBody { get; }

	public LLMBadRequestException(int statusCode, string responseBody, string message)
		: base(message) {
		StatusCode = statusCode;
		ResponseBody = responseBody;
	}
}
