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
        string? sessionAccountId = null;

        if (string.IsNullOrEmpty(accessToken))
        {
            var sessionResp = await _web.FetchAsync(Origin, "/api/auth/session");
            if (sessionResp.Status != 200)
                throw ProviderException.SessionExpired($"HTTP {sessionResp.Status}");

            var session = ParseSessionInfo(sessionResp.Body);
            (accessToken, accountEmail, sessionAccountId) = (session.AccessToken, session.Email, session.AccountId);
            if (string.IsNullOrEmpty(accessToken))
                throw ProviderException.NotAuthenticated();
        }

        // 2. Workspace (Business / Enterprise / Edu) accounts: without ChatGPT-Account-Id the server answers
        //    for the personal account, which has no Codex limits. Use the workspace selected in chatgpt.com.
        var cookies = await _web.GetCookiesAsync("chatgpt.com");
        var accountId = ResolveAccountId(
            cookies.FirstOrDefault(c => c.Name == "_account")?.Value,
            sessionAccountId,
            accessToken);

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {accessToken}",
            ["Accept"] = "application/json",
        };
        if (accountId is not null) headers["ChatGPT-Account-Id"] = accountId;

        // 3. Codex usage endpoint inside the same browser context
        var usageResp = await _web.FetchAsync(Origin, "/backend-api/wham/usage", headers: headers);

        ProviderException.ThrowIfFailed(usageResp.Status);
        if (string.IsNullOrEmpty(usageResp.Body)) throw ProviderException.Parsing("빈 응답");

        return ParseWhamUsage(usageResp.Body, accountEmail, DateTimeOffset.Now);
    }

    /// <summary>Extracts <c>accessToken</c> and the user's email/name from <c>/api/auth/session</c>.</summary>
    public static (string? AccessToken, string? Email) ParseSession(string json)
    {
        var info = ParseSessionInfo(json);
        return (info.AccessToken, info.Email);
    }

    public sealed record SessionInfo(string? AccessToken, string? Email, string? AccountId);

    /// <summary>Parses <c>/api/auth/session</c>, including the selected workspace (<c>account.id</c>).</summary>
    public static SessionInfo ParseSessionInfo(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return new(null, null, null);
        }
        if (root is null) return new(null, null, null);

        var token = Json.Str(root["accessToken"]);
        var user = Json.Obj(root["user"]);
        var email = Json.Str(user?["email"]) ?? Json.Str(user?["name"]);
        var account = Json.Obj(root["account"]);
        var accountId = Json.Str(account?["id"]) ?? Json.Str(account?["account_id"]);
        return new(string.IsNullOrEmpty(token) ? null : token, email, NullIfEmpty(accountId));
    }

    /// <summary>
    /// Workspace account id: chatgpt.com's <c>_account</c> cookie (the workspace picked in the web UI),
    /// then the session's <c>account.id</c>, then the access token's <c>chatgpt_account_id</c> claim.
    /// </summary>
    public static string? ResolveAccountId(string? accountCookie, string? sessionAccountId, string? accessToken)
    {
        var cookie = NullIfEmpty(Uri.UnescapeDataString(accountCookie ?? "").Trim('"'));
        if (cookie is not null && !cookie.Equals("personal", StringComparison.OrdinalIgnoreCase)) return cookie;
        return NullIfEmpty(sessionAccountId) ?? AccountIdFromJwt(accessToken);
    }

    internal static string? AccountIdFromJwt(string? jwt)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: >= 2 }) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var claims = JsonNode.Parse(Convert.FromBase64String(payload)) as JsonObject;
            var auth = Json.Obj(claims?["https://api.openai.com/auth"]);
            return NullIfEmpty(Json.Str(auth?["chatgpt_account_id"]));
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // MARK: - Parsing

    /// <summary>
    /// Parses <c>/backend-api/wham/usage</c>. Besides <c>rate_limit.primary/secondary_window</c>, any other
    /// <c>*_window</c> objects (e.g. code review limits) are added after them. Workspace plans without fixed
    /// windows get a <see cref="ProviderUsageSnapshot.Notice"/> describing credits instead.
    /// </summary>
    public static ProviderUsageSnapshot ParseWhamUsage(string json, string? accountEmail, DateTimeOffset now)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw ProviderException.Parsing("빈 응답");
        }
        catch (JsonException e)
        {
            throw ProviderException.Parsing(e.Message);
        }

        var windows = new List<UsageWindow>();
        void Add(JsonObject? w, string id, string fallback)
        {
            if (w is null || Json.Num(w["used_percent"]) is not { } used) return;
            var seconds = (long?)Json.Num(w["limit_window_seconds"]);
            windows.Add(new UsageWindow(
                id,
                WindowLabel(seconds, fallback),
                Math.Clamp(1.0 - used / 100.0, 0.0, 1.0),
                ResolveResetDate(Json.Num(w["reset_at"]), (long?)Json.Num(w["reset_after_seconds"]), now)));
        }

        var rateLimit = Json.Obj(root["rate_limit"]);
        Add(Json.Obj(rateLimit?["primary_window"]), "primary", "5시간 세션");
        Add(Json.Obj(rateLimit?["secondary_window"]), "secondary", "주간 한도");

        // Other limit groups (e.g. "code_review_rate_limit") with the same window shape.
        foreach (var (key, node) in root)
        {
            if (key == "rate_limit" || node is not JsonObject group) continue;
            var prefix = GroupLabel(key);
            Add(Json.Obj(group["primary_window"]), $"{key}.primary", $"{prefix} 5시간");
            Add(Json.Obj(group["secondary_window"]), $"{key}.secondary", $"{prefix} 주간");
        }
        // Prefix labels of extra groups so they are distinguishable from the main Codex windows.
        for (var i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            var dot = w.Id.IndexOf('.');
            if (dot > 0 && !w.Label.StartsWith(GroupLabel(w.Id[..dot]), StringComparison.Ordinal))
                windows[i] = w with { Label = $"{GroupLabel(w.Id[..dot])} · {w.Label}" };
        }

        var planType = Json.Str(root["plan_type"]);
        var planDisplay = planType is { Length: > 0 } plan
            ? $"ChatGPT {char.ToUpperInvariant(plan[0])}{plan[1..].ToLowerInvariant()}"
            : null;

        return new ProviderUsageSnapshot(
            ProviderType.Codex,
            AuthStatus.Authenticated(accountEmail ?? planDisplay),
            planDisplay,
            windows,
            now,
            null,
            false)
        {
            Notice = windows.Count == 0 ? EmptyNotice(planDisplay, rateLimit, Json.Obj(root["credits"])) : null,
        };
    }

    private static string GroupLabel(string key) => key switch
    {
        "code_review_rate_limit" => "코드 리뷰",
        _ => key.Replace("_rate_limit", "").Replace('_', ' '),
    };

    private static string EmptyNotice(string? plan, JsonObject? rateLimit, JsonObject? credits)
    {
        var who = plan ?? "이 계정";
        if (credits is not null)
        {
            if (credits["unlimited"]?.GetValueKind() == JsonValueKind.True)
                return $"{who}: 고정 사용량 한도 없음 (무제한 크레딧).";
            if (Json.Num(credits["balance"]) is { } balance)
                return $"{who}: 고정 사용량 한도 없음 (크레딧 잔액 {balance:0.##}).";
        }
        if (rateLimit?["allowed"]?.GetValueKind() == JsonValueKind.False)
            return $"{who}: Codex 사용 권한이 없습니다. 워크스페이스 관리자에게 Codex 활성화를 요청하세요.";
        return $"{who}: 고정 사용량 한도(5시간/주간)가 보고되지 않았습니다. " +
               "Business/Enterprise 워크스페이스라면 chatgpt.com에서 해당 워크스페이스를 선택한 뒤 다시 로그인해 보세요.";
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
