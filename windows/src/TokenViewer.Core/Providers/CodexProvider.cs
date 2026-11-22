using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TokenViewer.Core.Models;
using TokenViewer.Core.Services;

namespace TokenViewer.Core.Providers;

/// <summary>
/// Monitors OpenAI / ChatGPT Codex subscription quota via the in-app chatgpt.com web session,
/// or an optional manually saved access token.
/// </summary>
public sealed class CodexProvider : IUsageProvider
{
    public const string ManualTokenAccount = "codex.manualAccessToken";
    public const string VerifiedFlagKey = "tv.webSessionVerified.codex";

    public static readonly Uri Origin = new("https://chatgpt.com");

    private readonly IWebSession _web;
    private readonly ICredentialStore _credentials;
    private readonly IKeyValueStore _state;

    public CodexProvider(IWebSession web, ICredentialStore credentials, IKeyValueStore state)
    {
        _web = web;
        _credentials = credentials;
        _state = state;
    }

    public ProviderType Type => ProviderType.Codex;

    public void SaveManualToken(string token)
    {
        var trimmed = token.Trim();
        if (trimmed.Length == 0) _credentials.Delete(ManualTokenAccount);
        else _credentials.Save(ManualTokenAccount, trimmed);
    }

    public string? LoadManualToken() => _credentials.Load(ManualTokenAccount);

    public async Task<bool> HasStoredSessionAsync()
    {
        if (!string.IsNullOrEmpty(LoadManualToken())) return true;
        if (_state.GetBool(VerifiedFlagKey)) return true;
        var cookies = await _web.GetCookiesAsync("chatgpt.com");
        return cookies.Any(c => c.Name.Contains("session-token"));
    }

    public async Task SignOutAsync()
    {
        _state.Remove(VerifiedFlagKey);
        _credentials.Delete(ManualTokenAccount);
        await _web.ClearSessionAsync(["chatgpt.com", "openai.com", "auth.openai.com"]);
    }

    public async Task<ProviderUsageSnapshot> FetchUsageAsync()
    {
        // 1. Access token from the manual store or the ChatGPT web session
        var accessToken = LoadManualToken();
        string? accountEmail = null;

        if (string.IsNullOrEmpty(accessToken))
        {
            var sessionResp = await _web.FetchAsync(Origin, "/api/auth/session");
            if (sessionResp.Status != 200)
                throw ProviderException.SessionExpired($"HTTP {sessionResp.Status}");

            (accessToken, accountEmail) = ParseSession(sessionResp.Body);
            if (string.IsNullOrEmpty(accessToken))
                throw ProviderException.NotAuthenticated();
        }

        // 2. Codex usage endpoint inside the same browser context
        var usageResp = await _web.FetchAsync(
            Origin,
            "/backend-api/wham/usage",
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {accessToken}",
                ["Accept"] = "application/json",
            });

        ProviderException.ThrowIfFailed(usageResp.Status);
        if (string.IsNullOrEmpty(usageResp.Body)) throw ProviderException.Parsing("빈 응답");

        return ParseWhamUsage(usageResp.Body, accountEmail, DateTimeOffset.Now);
    }

    /// <summary>Extracts <c>accessToken</c> and the user's email/name from <c>/api/auth/session</c>.</summary>
    public static (string? AccessToken, string? Email) ParseSession(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return (null, null);
        }
        if (root is null) return (null, null);

        var token = Json.Str(root["accessToken"]);
        var user = Json.Obj(root["user"]);
        var email = Json.Str(user?["email"]) ?? Json.Str(user?["name"]);
        return (string.IsNullOrEmpty(token) ? null : token, email);
    }

    // MARK: - Parsing

    private sealed record WhamUsageResponse(
        [property: JsonPropertyName("plan_type")] string? PlanType,
        [property: JsonPropertyName("rate_limit")] RateLimit? RateLimit);

    private sealed record RateLimit(
        [property: JsonPropertyName("allowed")] bool? Allowed,
        [property: JsonPropertyName("limit_reached")] bool? LimitReached,
        [property: JsonPropertyName("primary_window")] WhamWindow? PrimaryWindow,
        [property: JsonPropertyName("secondary_window")] WhamWindow? SecondaryWindow);

    private sealed record WhamWindow(
        [property: JsonPropertyName("used_percent")] double UsedPercent,
        [property: JsonPropertyName("limit_window_seconds")] long? LimitWindowSeconds,
        [property: JsonPropertyName("reset_after_seconds")] long? ResetAfterSeconds,
        [property: JsonPropertyName("reset_at")] double? ResetAt);

    public static ProviderUsageSnapshot ParseWhamUsage(string json, string? accountEmail, DateTimeOffset now)
    {
        WhamUsageResponse decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<WhamUsageResponse>(json) ?? throw ProviderException.Parsing("빈 응답");
        }
        catch (JsonException e)
        {
            throw ProviderException.Parsing(e.Message);
        }

        var windows = new List<UsageWindow>();
        void Add(WhamWindow? w, string id, string fallback)
        {
            if (w is null) return;
            windows.Add(new UsageWindow(
                id,
                WindowLabel(w.LimitWindowSeconds, fallback),
                Math.Clamp(1.0 - w.UsedPercent / 100.0, 0.0, 1.0),
                ResolveResetDate(w.ResetAt, w.ResetAfterSeconds, now)));
        }

        Add(decoded.RateLimit?.PrimaryWindow, "primary", "5시간 세션");
        Add(decoded.RateLimit?.SecondaryWindow, "secondary", "주간 한도");

        var planDisplay = decoded.PlanType is { Length: > 0 } plan
            ? $"ChatGPT {char.ToUpperInvariant(plan[0])}{plan[1..].ToLowerInvariant()}"
            : null;

        return new ProviderUsageSnapshot(
            ProviderType.Codex,
            AuthStatus.Authenticated(accountEmail ?? planDisplay),
            planDisplay,
            windows,
            now,
            null,
            false);
    }

    internal static string WindowLabel(long? seconds, string fallback)
    {
        if (seconds is not > 0) return fallback;
        if (seconds >= 86400 * 6) return $"{seconds / 86400}일 주간 한도";
        if (seconds >= 3600) return $"{seconds / 3600}시간 윈도우";
        return fallback;
    }

    internal static DateTimeOffset? ResolveResetDate(double? resetAt, long? resetAfter, DateTimeOffset now)
    {
        if (resetAt is > 0) return DateTimeOffset.FromUnixTimeMilliseconds((long)(resetAt.Value * 1000));
        if (resetAfter is > 0) return now.AddSeconds(resetAfter.Value);
        return null;
    }
}
