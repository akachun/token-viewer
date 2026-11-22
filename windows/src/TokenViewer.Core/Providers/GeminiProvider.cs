using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TokenViewer.Core.Models;
using TokenViewer.Core.Services;

namespace TokenViewer.Core.Providers;

/// <summary>Authentication/data-source mode for Gemini quota monitoring.</summary>
public enum GeminiSourceMode
{
    AntigravityLocal,
    OAuth,
}

/// <summary>
/// Monitors Google Gemini quota via
/// 1) local Antigravity <c>language_server</c> auto-sync with last-snapshot caching (recommended), or
/// 2) Google OAuth 2.0 / manual access token against <c>cloudcode-pa.googleapis.com</c>.
/// </summary>
public sealed class GeminiProvider : IUsageProvider
{
    public const string CredentialsAccount = "gemini.googleOAuthCredentials";
    private const string ModeKey = "tv.gemini.sourceMode";
    private const string AntigravityEnabledKey = "tv.gemini.antigravityEnabled";
    private const string AntigravityCacheKey = "tv.gemini.antigravityCache";
    private const string CloudCodeBase = "https://cloudcode-pa.googleapis.com";

    private readonly ICredentialStore _credentials;
    private readonly IKeyValueStore _state;
    private readonly HttpClient _http;
    private readonly GoogleOAuthService _oauth;
    private readonly AntigravityQuotaService _antigravity;

    public GeminiProvider(
        ICredentialStore credentials,
        IKeyValueStore state,
        HttpClient http,
        GoogleOAuthService oauth,
        AntigravityQuotaService antigravity)
    {
        _credentials = credentials;
        _state = state;
        _http = http;
        _oauth = oauth;
        _antigravity = antigravity;
    }

    public ProviderType Type => ProviderType.Gemini;

    public GoogleOAuthService OAuth => _oauth;
    public AntigravityQuotaService Antigravity => _antigravity;

    public GeminiSourceMode SourceMode
    {
        get => Enum.TryParse<GeminiSourceMode>(_state.GetString(ModeKey), out var m) ? m : GeminiSourceMode.AntigravityLocal;
        set => _state.SetString(ModeKey, value.ToString());
    }

    public bool IsAntigravityEnabled
    {
        get => _state.GetBool(AntigravityEnabledKey);
        set => _state.SetBool(AntigravityEnabledKey, value);
    }

    public void EnableAntigravityMode()
    {
        SourceMode = GeminiSourceMode.AntigravityLocal;
        IsAntigravityEnabled = true;
    }

    public GoogleOAuthCredentials? LoadCredentials() => _credentials.LoadJson<GoogleOAuthCredentials>(CredentialsAccount);

    public void SaveCredentials(GoogleOAuthCredentials creds)
    {
        SourceMode = GeminiSourceMode.OAuth;
        _credentials.SaveJson(CredentialsAccount, creds);
    }

    public void SaveManualAccessToken(string token, string? email = null)
    {
        var trimmed = token.Trim();
        if (trimmed.Length == 0)
        {
            _credentials.Delete(CredentialsAccount);
            return;
        }
        SaveCredentials(new GoogleOAuthCredentials
        {
            AccessToken = trimmed,
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
        });
    }

    public async Task<bool> HasStoredSessionAsync()
    {
        if (SourceMode == GeminiSourceMode.AntigravityLocal)
        {
            if (IsAntigravityEnabled) return true;
            // Also auto-detect if the Antigravity IDE is currently running
            return await _antigravity.IsRunningAsync();
        }
        var creds = LoadCredentials();
        return creds is not null && (creds.AccessToken.Length > 0 || !string.IsNullOrEmpty(creds.RefreshToken));
    }

    public Task SignOutAsync()
    {
        IsAntigravityEnabled = false;
        _state.Remove(AntigravityCacheKey);
        _credentials.Delete(CredentialsAccount);
        return Task.CompletedTask;
    }

    public Task<ProviderUsageSnapshot> FetchUsageAsync() =>
        SourceMode == GeminiSourceMode.AntigravityLocal ? FetchFromAntigravityAsync() : FetchFromOAuthAsync();

    // MARK: - Mode 1: Antigravity local server (with last-snapshot cache)

    private async Task<ProviderUsageSnapshot> FetchFromAntigravityAsync()
    {
        try
        {
            var result = await _antigravity.FetchQuotaAsync();
            IsAntigravityEnabled = true;
            _state.Set(AntigravityCacheKey, result);
            return Snapshot(result.AccountLabel ?? "Antigravity 연결됨", result.PlanName, result.Windows, result.FetchedAt, null);
        }
        catch (AntigravityException e) when (e.Kind == AntigravityErrorKind.NotRunning)
        {
            // Serve the last cached snapshot while the IDE is closed
            if (_state.Get<AntigravityQuotaResult>(AntigravityCacheKey) is { } cached)
            {
                var label = cached.AccountLabel ?? "Antigravity";
                return Snapshot($"{label} · IDE 대기 중", cached.PlanName, cached.Windows, cached.FetchedAt, null);
            }
            return Snapshot("Antigravity IDE 대기 중", null, [], DateTimeOffset.Now,
                "Antigravity IDE를 실행하면 자동으로 Gemini 잔여량을 동기화합니다.");
        }
        catch (Exception e) when (e is AntigravityException or HttpRequestException or TaskCanceledException or JsonException)
        {
            if (_state.Get<AntigravityQuotaResult>(AntigravityCacheKey) is { } cached)
                return Snapshot(cached.AccountLabel ?? "Antigravity", cached.PlanName, cached.Windows, cached.FetchedAt, e.Message);
            return Snapshot("Antigravity 연결 오류", null, [], DateTimeOffset.Now, e.Message);
        }

        static ProviderUsageSnapshot Snapshot(string label, string? plan, IReadOnlyList<UsageWindow> windows, DateTimeOffset at, string? error) =>
            new(ProviderType.Gemini, AuthStatus.Authenticated(label), plan ?? "Antigravity", windows, at, error, false);
    }

    // MARK: - Mode 2: Google OAuth 2.0

    private async Task<ProviderUsageSnapshot> FetchFromOAuthAsync()
    {
        var creds = LoadCredentials();
        if (creds is null || creds.AccessToken.Length == 0) throw ProviderException.NotAuthenticated();

        if (creds.IsAccessTokenExpired && creds.RefreshToken is not null)
            creds = await RefreshAsync(creds);

        var info = await LoadCodeAssistAsync(creds.AccessToken);

        IReadOnlyList<UsageWindow> windows;
        try
        {
            windows = BuildWindows(await RetrieveUserQuotaAsync(creds.AccessToken, info.ProjectId));
        }
        catch (ProviderException e) when (e.Kind == ProviderErrorKind.SessionExpired && creds.RefreshToken is not null)
        {
            creds = await RefreshAsync(creds);
            windows = BuildWindows(await RetrieveUserQuotaAsync(creds.AccessToken, info.ProjectId));
        }

        return new ProviderUsageSnapshot(
            ProviderType.Gemini,
            AuthStatus.Authenticated(creds.Email ?? info.TierName ?? "Google 계정"),
            info.TierName ?? "Gemini",
            windows,
            DateTimeOffset.Now,
            null,
            false);
    }

    private async Task<GoogleOAuthCredentials> RefreshAsync(GoogleOAuthCredentials creds)
    {
        try
        {
            var refreshed = await _oauth.RefreshAccessTokenAsync(creds);
            SaveCredentials(refreshed);
            return refreshed;
        }
        catch (Exception e) when (e is GoogleOAuthException or HttpRequestException)
        {
            throw ProviderException.SessionExpired(e.Message);
        }
    }

    private sealed record CodeAssistInfo(string? ProjectId, string? TierName);

    private async Task<CodeAssistInfo> LoadCodeAssistAsync(string accessToken)
    {
        try
        {
            using var req = Post($"{CloudCodeBase}/v1internal:loadCodeAssist", accessToken,
                """{"metadata":{"ideName":"IDE_UNSPECIFIED","platform":"WINDOWS_AMD64","pluginType":"GEMINI"}}""");
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return new(null, null);

            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync()) as JsonObject;
            var tier = Json.Obj(json?["currentTier"]);
            var tierName = Json.Str(tier?["name"]) ?? Json.Str(tier?["id"]);
            return new(Json.Str(json?["cloudaicompanionProject"]), FormatTier(tierName));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(null, null);
        }
    }

    public sealed record QuotaBucket(string? ModelId, double? RemainingFraction, string? ResetTime);

    private async Task<IReadOnlyList<QuotaBucket>> RetrieveUserQuotaAsync(string accessToken, string? projectId)
    {
        var payload = new JsonObject();
        if (!string.IsNullOrEmpty(projectId)) payload["project"] = projectId;

        using var req = Post($"{CloudCodeBase}/v1internal:retrieveUserQuota", accessToken, payload.ToJsonString());
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw ProviderException.Network(e.Message);
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.StatusCode == HttpStatusCode.Unauthorized) throw ProviderException.SessionExpired("HTTP 401");
            if (resp.StatusCode == HttpStatusCode.TooManyRequests) throw ProviderException.RateLimited();
            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                var detail = ExtractGoogleErrorMessage(body) ?? $"HTTP {code}";
                throw ProviderException.Network(code == 403 ? $"Google 권한 거부 (403): {detail}" : $"HTTP {code}: {detail}");
            }
            return ParseQuotaBuckets(body);
        }
    }

    public static IReadOnlyList<QuotaBucket> ParseQuotaBuckets(string json)
    {
        try
        {
            var buckets = Json.Arr((JsonNode.Parse(json) as JsonObject)?["buckets"]);
            return buckets?.OfType<JsonObject>()
                .Select(b => new QuotaBucket(Json.Str(b["modelId"]), Json.Num(b["remainingFraction"]), Json.Str(b["resetTime"])))
                .ToList() ?? [];
        }
        catch (JsonException e)
        {
            throw ProviderException.Parsing(e.Message);
        }
    }

    private static HttpRequestMessage Post(string url, string accessToken, string jsonBody)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return req;
    }

    internal static string? ExtractGoogleErrorMessage(string body)
    {
        try
        {
            if ((JsonNode.Parse(body) as JsonObject)?["error"] is JsonObject err)
            {
                var status = Json.Str(err["status"]);
                var message = Json.Str(err["message"]);
                return status is not null && message is not null ? $"[{status}] {message}" : message ?? status;
            }
        }
        catch (JsonException)
        {
            // fall through
        }
        return body.Length > 160 ? body[..160] : body;
    }

    public static IReadOnlyList<UsageWindow> BuildWindows(IReadOnlyList<QuotaBucket> buckets)
    {
        QuotaBucket? proBest = null, flashBest = null;
        var others = new List<UsageWindow>();

        foreach (var b in buckets)
        {
            if (b.ModelId is not { } modelId || b.RemainingFraction is not { } rem) continue;
            var lower = modelId.ToLowerInvariant();
            if (lower.Contains("pro"))
            {
                if (proBest is null || rem < (proBest.RemainingFraction ?? 1.0)) proBest = b;
            }
            else if (lower.Contains("flash"))
            {
                if (flashBest is null || rem < (flashBest.RemainingFraction ?? 1.0)) flashBest = b;
            }
            else
            {
                others.Add(new UsageWindow(modelId, modelId, rem, DateParser.ParseIso8601(b.ResetTime)));
            }
        }

        var result = new List<UsageWindow>();
        if (proBest?.RemainingFraction is { } pr)
            result.Add(new UsageWindow("gemini-pro", "Gemini Pro", pr, DateParser.ParseIso8601(proBest.ResetTime)));
        if (flashBest?.RemainingFraction is { } fr)
            result.Add(new UsageWindow("gemini-flash", "Gemini Flash", fr, DateParser.ParseIso8601(flashBest.ResetTime)));
        result.AddRange(others.Take(2));
        return result;
    }

    public static string? FormatTier(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var lower = raw.ToLowerInvariant();
        if (lower.Contains("ultra")) return "Google AI Ultra";
        if (lower.Contains("pro")) return "Google AI Pro";
        if (lower.Contains("standard")) return "Gemini Standard";
        if (lower.Contains("free")) return "Gemini Free";
        return raw;
    }
}
