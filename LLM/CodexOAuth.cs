using Godot;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using HttpClient = System.Net.Http.HttpClient;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

public sealed class CodexAuthState {
	public string AccessToken { get; init; }
	public string RefreshToken { get; init; }
	public long ExpiresAtUnixSeconds { get; init; }
	public string AccountId { get; init; }

	public bool IsExpired(long nowUnixSeconds, long refreshBufferSeconds = 30) {
		if (ExpiresAtUnixSeconds <= 0) return true;
		return nowUnixSeconds >= (ExpiresAtUnixSeconds - refreshBufferSeconds);
	}
}

public sealed class CodexOAuth {
	private const string AuthFilePath = "user://CodexAuth.json";
	private const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
	private const string TokenUrl = "https://auth.openai.com/oauth/token";
	private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
	private const string Scope = "openid profile email offline_access";
	private const string RedirectUri = "http://localhost:1455/auth/callback";
	private const string AuthorizeExtraParams = "id_token_add_organizations=true&codex_cli_simplified_flow=true&originator=codex_cli_rs";
	private static readonly TimeSpan AuthTimeout = TimeSpan.FromMinutes(5);

	// Single-flight guard: ensure only one OAuth browser login is triggered at a time.
	private static readonly System.Threading.SemaphoreSlim AuthGate = new(1, 1);
	private static Task<CodexAuthState> InFlightLogin;

	private readonly HttpClient httpClient;

	public CodexOAuth(HttpClient client = null) {
		httpClient = client ?? new HttpClient();
	}

	public async Task<CodexAuthState> EnsureValidAuthAsync(bool forceRefresh = false) {
		var existing = LoadAuthState();
		long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

		// Fast path: existing token still valid
		if (!forceRefresh && existing != null && !existing.IsExpired(now)) {
			return existing;
		}

		// Try refresh (no browser)
		if (existing != null && !string.IsNullOrWhiteSpace(existing.RefreshToken)) {
			var refreshed = await RefreshAsync(existing.RefreshToken, existing.AccountId).ConfigureAwait(false);
			if (refreshed != null) {
				SaveAuthState(refreshed);
				return refreshed;
			}
		}

		// Browser login: single-flight. Only one caller opens a tab; others await the same task.
		await AuthGate.WaitAsync().ConfigureAwait(false);
		try {
			// Re-check after acquiring the gate (another request may have already logged in)
			existing = LoadAuthState();
			now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			if (!forceRefresh && existing != null && !existing.IsExpired(now)) {
				return existing;
			}

			if (InFlightLogin == null || InFlightLogin.IsCompleted) {
				InFlightLogin = LoginAsync(existing?.AccountId);
			}
		} finally {
			AuthGate.Release();
		}

		var loggedIn = await InFlightLogin.ConfigureAwait(false);
		SaveAuthState(loggedIn);
		return loggedIn;
	}

	public async Task<CodexAuthState> ForceRefreshAsync() {
		return await EnsureValidAuthAsync(forceRefresh: true).ConfigureAwait(false);
	}

	public async Task<CodexAuthState> LoginAsync(string fallbackAccountId = null) {
		string codeVerifier = CreateCodeVerifier();
		string codeChallenge = CreateCodeChallenge(codeVerifier);
		string state = CreateState();
		string authorizeUrl = BuildAuthorizeUrl(codeChallenge, state);

		GD.Print("[CodexOAuth] Opening browser for OpenAI login...");
		GD.Print($"[CodexOAuth] If the browser does not open, visit: {authorizeUrl}");
		try {
			OS.ShellOpen(authorizeUrl);
		} catch (Exception e) {
			GD.PrintErr($"[CodexOAuth] Failed to open browser: {e.Message}");
		}

		var authResult = await ListenForAuthorizationCodeAsync(state, AuthTimeout).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(authResult.Code)) {
			throw new InvalidOperationException("Authorization code missing from OAuth callback.");
		}

		var token = await ExchangeCodeAsync(authResult.Code, codeVerifier).ConfigureAwait(false);
		if (token == null || string.IsNullOrWhiteSpace(token.AccessToken)) {
			throw new InvalidOperationException("OAuth token exchange failed.");
		}

		long expiresAt = ComputeExpiresAt(token.ExpiresIn);
		string accountId = ExtractAccountIdFromJwt(token.AccessToken, fallbackAccountId);

		return new CodexAuthState {
			AccessToken = token.AccessToken,
			RefreshToken = token.RefreshToken,
			ExpiresAtUnixSeconds = expiresAt,
			AccountId = accountId
		};
	}

	private async Task<CodexAuthState> RefreshAsync(string refreshToken, string fallbackAccountId) {
		try {
			var form = new Dictionary<string, string> {
				{"grant_type", "refresh_token"},
				{"client_id", ClientId},
				{"refresh_token", refreshToken}
			};
			var response = await httpClient.PostAsync(TokenUrl, new FormUrlEncodedContent(form)).ConfigureAwait(false);
			string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
			if (!response.IsSuccessStatusCode) {
				GD.PrintErr($"[CodexOAuth] Refresh failed: {(int)response.StatusCode} {response.StatusCode} {body}");
				return null;
			}
			var token = JsonSerializer.Deserialize<TokenResponse>(body);
			if (token == null || string.IsNullOrWhiteSpace(token.AccessToken)) {
				GD.PrintErr("[CodexOAuth] Refresh response missing access_token.");
				return null;
			}

			long expiresAt = ComputeExpiresAt(token.ExpiresIn);
			string accountId = ExtractAccountIdFromJwt(token.AccessToken, fallbackAccountId);
			return new CodexAuthState {
				AccessToken = token.AccessToken,
				RefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? refreshToken : token.RefreshToken,
				ExpiresAtUnixSeconds = expiresAt,
				AccountId = accountId
			};
		} catch (Exception e) {
			GD.PrintErr($"[CodexOAuth] Refresh exception: {e.Message}");
			return null;
		}
	}

	private static string BuildAuthorizeUrl(string codeChallenge, string state) {
		var sb = new StringBuilder();
		sb.Append(AuthorizeUrl);
		sb.Append("?response_type=code");
		sb.Append("&client_id=").Append(Uri.EscapeDataString(ClientId));
		sb.Append("&redirect_uri=").Append(Uri.EscapeDataString(RedirectUri));
		sb.Append("&scope=").Append(Uri.EscapeDataString(Scope));
		sb.Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge));
		sb.Append("&code_challenge_method=S256");
		sb.Append("&state=").Append(Uri.EscapeDataString(state));
		sb.Append("&").Append(AuthorizeExtraParams);
		return sb.ToString();
	}

	private async Task<AuthCodeResult> ListenForAuthorizationCodeAsync(string expectedState, TimeSpan timeout) {
		var listener = new HttpListener();
		listener.Prefixes.Add("http://localhost:1455/auth/callback/");
		listener.Start();
		GD.Print("[CodexOAuth] Listening for OAuth callback on http://localhost:1455/auth/callback/");

		try {
			var contextTask = listener.GetContextAsync();
			var completed = await Task.WhenAny(contextTask, Task.Delay(timeout)).ConfigureAwait(false);
			if (completed != contextTask) {
				throw new TimeoutException("Timed out waiting for OAuth callback.");
			}

			var context = contextTask.Result;
			string code = context.Request.QueryString["code"];
			string state = context.Request.QueryString["state"];

			string html = "<html><body><h2>Codex auth complete.</h2><p>You may close this window.</p></body></html>";
			byte[] buffer = Encoding.UTF8.GetBytes(html);
			context.Response.ContentType = "text/html";
			context.Response.ContentLength64 = buffer.Length;
			await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
			context.Response.OutputStream.Close();

			if (!string.IsNullOrEmpty(expectedState) && !string.Equals(expectedState, state, StringComparison.Ordinal)) {
				throw new InvalidOperationException("OAuth state mismatch.");
			}

			return new AuthCodeResult { Code = code, State = state };
		} finally {
			listener.Stop();
			listener.Close();
		}
	}

	private async Task<TokenResponse> ExchangeCodeAsync(string code, string codeVerifier) {
		var form = new Dictionary<string, string> {
			{"grant_type", "authorization_code"},
			{"client_id", ClientId},
			{"code", code},
			{"redirect_uri", RedirectUri},
			{"code_verifier", codeVerifier}
		};

		var response = await httpClient.PostAsync(TokenUrl, new FormUrlEncodedContent(form)).ConfigureAwait(false);
		string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
		if (!response.IsSuccessStatusCode) {
			GD.PrintErr($"[CodexOAuth] Token exchange failed: {(int)response.StatusCode} {response.StatusCode} {body}");
			return null;
		}

		return JsonSerializer.Deserialize<TokenResponse>(body);
	}

	private CodexAuthState LoadAuthState() {
		try {
			if (!FileAccess.FileExists(AuthFilePath)) return null;
			using var file = FileAccess.Open(AuthFilePath, FileAccess.ModeFlags.Read);
			string json = file.GetAsText();
			if (string.IsNullOrWhiteSpace(json)) return null;
			var data = JsonSerializer.Deserialize<AuthFileData>(json);
			if (data == null) return null;
			return new CodexAuthState {
				AccessToken = data.Access,
				RefreshToken = data.Refresh,
				ExpiresAtUnixSeconds = data.Expires,
				AccountId = data.AccountId
			};
		} catch (Exception e) {
			GD.PrintErr($"[CodexOAuth] Failed to load auth state: {e.Message}");
			return null;
		}
	}

	private void SaveAuthState(CodexAuthState state) {
		if (state == null) return;
		try {
			var data = new AuthFileData {
				Access = state.AccessToken,
				Refresh = state.RefreshToken,
				Expires = state.ExpiresAtUnixSeconds,
				AccountId = state.AccountId,
				Warning = "DO NOT SHARE THIS FILE. Contains OAuth tokens."
			};
			string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
			using var file = FileAccess.Open(AuthFilePath, FileAccess.ModeFlags.Write);
			file.StoreString(json);
			GD.Print("[CodexOAuth] Saved tokens to user://CodexAuth.json. Do not share this file.");
		} catch (Exception e) {
			GD.PrintErr($"[CodexOAuth] Failed to save auth state: {e.Message}");
		}
	}

	private static long ComputeExpiresAt(int? expiresInSeconds) {
		int ttl = expiresInSeconds.HasValue && expiresInSeconds.Value > 0 ? expiresInSeconds.Value : 3600;
		long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		return now + ttl;
	}

	private static string ExtractAccountIdFromJwt(string jwt, string fallbackAccountId) {
		if (string.IsNullOrWhiteSpace(jwt)) return fallbackAccountId;
		try {
			var parts = jwt.Split('.');
			if (parts.Length < 2) return fallbackAccountId;
			byte[] payloadBytes = Base64UrlDecode(parts[1]);
			string payloadJson = Encoding.UTF8.GetString(payloadBytes);
			using var doc = JsonDocument.Parse(payloadJson);
			if (doc.RootElement.TryGetProperty("https://api.openai.com/auth", out var authElement)
				&& authElement.ValueKind == JsonValueKind.Object
				&& authElement.TryGetProperty("chatgpt_account_id", out var accountIdElement)) {
				var accountId = accountIdElement.GetString();
				if (!string.IsNullOrWhiteSpace(accountId)) return accountId;
			}
		} catch (Exception e) {
			GD.PrintErr($"[CodexOAuth] Failed to decode account id from JWT: {e.Message}");
		}
		return fallbackAccountId;
	}

	private static string CreateCodeVerifier() {
		byte[] bytes = new byte[32];
		System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
		return Base64UrlEncode(bytes);
	}

	private static string CreateCodeChallenge(string verifier) {
		byte[] bytes = Encoding.ASCII.GetBytes(verifier);
		byte[] hash = SHA256.HashData(bytes);
		return Base64UrlEncode(hash);
	}

	private static string CreateState() {
		byte[] bytes = new byte[16];
		System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
		return Base64UrlEncode(bytes);
	}

	private static string Base64UrlEncode(byte[] input) {
		return Convert.ToBase64String(input)
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');
	}

	private static byte[] Base64UrlDecode(string input) {
		string padded = input.Replace('-', '+').Replace('_', '/');
		switch (padded.Length % 4) {
			case 2:
				padded += "==";
				break;
			case 3:
				padded += "=";
				break;
		}
		return Convert.FromBase64String(padded);
	}

	private sealed class AuthFileData {
		[JsonPropertyName("access")] public string Access { get; set; }
		[JsonPropertyName("refresh")] public string Refresh { get; set; }
		[JsonPropertyName("expires")] public long Expires { get; set; }
		[JsonPropertyName("account_id")] public string AccountId { get; set; }
		[JsonPropertyName("warning")] public string Warning { get; set; }
	}

	private sealed class TokenResponse {
		[JsonPropertyName("access_token")] public string AccessToken { get; set; }
		[JsonPropertyName("refresh_token")] public string RefreshToken { get; set; }
		[JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
	}

	private sealed class AuthCodeResult {
		public string Code { get; set; }
		public string State { get; set; }
	}
}
