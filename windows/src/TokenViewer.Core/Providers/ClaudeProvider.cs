using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TokenViewer.Core.Models;
using TokenViewer.Core.Services;

namespace TokenViewer.Core.Providers;

public sealed record ClaudeOrganization(
    [property: JsonPropertyName("uuid")] string Uuid,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("rate_limit_tier")] string? RateLimitTier,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string>? Capabilities);

/// <summary>
/// Monitors Anthropic Claude subscription quota via the in-app claude.ai web session,
/// or an optional manually saved <c>sessionKey</c> cookie.
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    public const string ManualSessionKeyAccount = "claude.manualSessionKey";
    public const string VerifiedFlagKey = "tv.webSessionVerified.claude";

    public static readonly Uri Origin = new("https://claude.ai");

    public static readonly IReadOnlyDictionary<string, string> ApiHeaders = new Dictionary<string, string>
    {
        ["Accept"] = "application/json",
        ["anthropic-client-platform"] = "web_claude_ai",
    };

    private readonly IWebSession _web;
    private readonly ICredentialStore _credentials;
    private readonly IKeyValueStore _state;

    public ClaudeProvider(IWebSession web, ICredentialStore credentials, IKeyValueStore state)
    {
        _web = web;
        _credentials = credentials;
        _state = state;
    }

    public ProviderType Type => ProviderType.Claude;

    public async Task SaveManualSessionKeyAsync(string key)
    {
        var trimmed = key.Trim();
        if (trimmed.Length == 0)
        {
            _credentials.Delete(ManualSessionKeyAccount);
            return;
        }
        _credentials.Save(ManualSessionKeyAccount, trimmed);
        await InjectSessionKeyCookieAsync(trimmed);
    }

    public string? LoadManualSessionKey() => _credentials.Load(ManualSessionKeyAccount);

    public async Task<bool> HasStoredSessionAsync()
    {
        if (!string.IsNullOrEmpty(LoadManualSessionKey())) return true;
        if (_state.GetBool(VerifiedFlagKey)) return true;
        var cookies = await _web.GetCookiesAsync("claude.ai");
        return cookies.Any(c => c.Name == "sessionKey" && c.Value.Length > 0);
    }

    public async Task SignOutAsync()
    {
        _state.Remove(VerifiedFlagKey);
        _credentials.Delete(ManualSessionKeyAccount);
        await _web.ClearSessionAsync(["claude.ai", "anthropic.com"]);
    }

    public async Task<ProviderUsageSnapshot> FetchUsageAsync()
    {
        if (LoadManualSessionKey() is { Length: > 0 } manualKey)
            await InjectSessionKeyCookieAsync(manualKey);

        // 1. Organizations → org UUID and plan metadata
        var orgResp = await _web.FetchAsync(Origin, "/api/organizations", headers: ApiHeaders);
        ProviderException.ThrowIfFailed(orgResp.Status);

        var orgs = ParseOrganizations(orgResp.Body);
        if (orgs.Count == 0) throw ProviderException.NotAuthenticated();

        // Team / Enterprise members usually belong to several organizations (personal + company).
        // Prefer the one selected in claude.ai (lastActiveOrg cookie), then try the rest until one
        // reports usage windows.
        var cookies = await _web.GetCookiesAsync("claude.ai");
        var lastActive = cookies.FirstOrDefault(c => c.Name == "lastActiveOrg")?.Value;
        var candidates = OrderOrganizations(orgs, lastActive).Take(MaxOrganizationsToProbe).ToList();

        // 2. Usage windows: first organization that reports any
        ProviderUsageSnapshot? fallback = null;
        ProviderException? firstError = null;
        foreach (var org in candidates)
        {
            try
            {
                var usageResp = await _web.FetchAsync(Origin, $"/api/organizations/{org.Uuid}/usage", headers: ApiHeaders);
                ProviderException.ThrowIfFailed(usageResp.Status);
                var snap = ParseUsage(usageResp.Body, org, DateTimeOffset.Now);
                if (snap.Windows.Count > 0) return snap;
                fallback ??= snap;
            }
            catch (ProviderException e) when (e.Kind != ProviderErrorKind.RateLimited)
            {
                // A company org may refuse /usage (403/404) for members; keep trying other orgs.
                firstError ??= e;
            }
        }

        if (fallback is not null)
            return fallback with { Notice = EmptyNotice(fallback.PlanName, candidates) };
        throw firstError ?? ProviderException.NotAuthenticated();
    }

    private const int MaxOrganizationsToProbe = 5;

    /// <summary>lastActiveOrg first, then chat-capable orgs (paid tiers before free), then the rest.</summary>
    public static IReadOnlyList<ClaudeOrganization> OrderOrganizations(IReadOnlyList<ClaudeOrganization> orgs, string? lastActiveOrg) =>
        orgs
            .Select((o, i) => (o, i))
            .OrderBy(x => string.Equals(x.o.Uuid, lastActiveOrg, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.o.Capabilities?.Contains("chat") == true ? 0 : 1)
            .ThenBy(x => IsFreeTier(x.o.RateLimitTier) ? 1 : 0)
            .ThenBy(x => x.i)
            .Select(x => x.o)
            .ToList();

    private static bool IsFreeTier(string? tier) =>
        string.IsNullOrEmpty(tier) || tier.Contains("free", StringComparison.OrdinalIgnoreCase);

    private static string EmptyNotice(string? plan, IReadOnlyList<ClaudeOrganization> tried)
    {
        var names = string.Join(", ", tried.Select(o => o.Name ?? FormatTierName(o.RateLimitTier) ?? o.Uuid[..Math.Min(8, o.Uuid.Length)]));
        return $"{plan ?? "이 조직"}은(는) 고정 사용량 한도(5시간/주간)를 제공하지 않습니다. " +
               $"Enterprise·사용량 기반 플랜이면 관리자 콘솔에서 확인하세요. (확인한 조직: {names})";
    }

    private Task InjectSessionKeyCookieAsync(string value) =>
        _web.SetCookieAsync(new WebCookie("sessionKey", value, ".claude.ai", "/", true, DateTimeOffset.Now.AddDays(30)));

    // MARK: - Parsing

    public static IReadOnlyList<ClaudeOrganization> ParseOrganizations(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ClaudeOrganization>>(json) ?? [];
        }
        catch (JsonException)
        {
            // An HTML login page or an error object means the session is not valid.
            throw ProviderException.NotAuthenticated();
        }
    }

    private static readonly (string Id, string Label)[] KnownBuckets =
    [
        ("five_hour", "5시간 세션"),
        ("seven_day", "7일 주간 한도"),
        ("seven_day_sonnet", "Sonnet 주간"),
        ("seven_day_opus", "Opus 주간"),
        ("seven_day_oauth_apps", "앱 연동 주간"),
        ("extra_usage", "추가 사용량 (월)"),
    ];

    /// <summary>
    /// Parses <c>/usage</c>. Every top-level object with a numeric <c>utilization</c> becomes a window, so
    /// buckets that only some plans return (Team/Enterprise extras) are shown too. Known buckets come first.
    /// </summary>
    public static ProviderUsageSnapshot ParseUsage(string json, ClaudeOrganization org, DateTimeOffset now)
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

        var buckets = new List<(string Id, string Label, double Util, string? ResetsAt)>();
        foreach (var (key, node) in root)
        {
            if (node is not JsonObject obj || Json.Num(obj["utilization"]) is not { } util) continue;
            var known = Array.FindIndex(KnownBuckets, b => b.Id == key);
            var label = known >= 0 ? KnownBuckets[known].Label : HumanizeBucket(key);
            buckets.Add((key, label, util, Json.Str(obj["resets_at"])));
        }
        buckets = buckets
            .OrderBy(b => Array.FindIndex(KnownBuckets, k => k.Id == b.Id) is var i and >= 0 ? i : int.MaxValue)
            .ToList();

        // If any bucket is > 1.0, the API returns 0...100 percentages. If all values are within 0...1 and at
        // least one is fractional, it's a 0...1 fraction. Otherwise (e.g. integers 0 or 1) assume 0...100.
        var allUtils = buckets.Select(b => b.Util).ToList();
        var isFractionScale = !allUtils.Any(u => u > 1.0) && allUtils.Any(u => u is > 0.0 and < 1.0);

        var windows = buckets
            .Select(b => new UsageWindow(
                b.Id,
                b.Label,
                Math.Clamp(1.0 - (isFractionScale ? b.Util : b.Util / 100.0), 0.0, 1.0),
                DateParser.ParseIso8601(b.ResetsAt)))
            .ToList();

        ApplyExtraUsageAmounts(root, windows);

        var planLabel = FormatTierName(org.RateLimitTier) ?? "Claude";

        return new ProviderUsageSnapshot(
            ProviderType.Claude,
            AuthStatus.Authenticated(org.Name ?? planLabel),
            planLabel,
            windows,
            now,
            null,
            false);
    }

    public sealed record MoneyAmounts(double Used, double Limit, string? Currency);

    /// <summary>
    /// Monthly extra-usage spend. Prefers <c>spend.used/limit</c> ({amount_minor, exponent}, self-describing),
    /// then <c>extra_usage.used_credits/monthly_limit</c> scaled by <c>decimal_places</c> (both are minor units).
    /// </summary>
    public static MoneyAmounts? ParseExtraUsageAmounts(JsonObject root)
    {
        if (Json.Obj(root["spend"]) is { } spend &&
            Minor(Json.Obj(spend["used"])) is { } su && Minor(Json.Obj(spend["limit"])) is { } sl)
            return new(su.Value, sl.Value, su.Currency ?? sl.Currency);

        if (Json.Obj(root["extra_usage"]) is { } extra &&
            Json.Num(extra["used_credits"]) is { } used && Json.Num(extra["monthly_limit"]) is { } limit)
        {
            var scale = Math.Pow(10, Json.Num(extra["decimal_places"]) ?? 2);
            return new(used / scale, limit / scale, Json.Str(extra["currency"]));
        }
        return null;

        static (double Value, string? Currency)? Minor(JsonObject? money)
        {
            if (money is null || Json.Num(money["amount_minor"]) is not { } minor) return null;
            return (minor / Math.Pow(10, Json.Num(money["exponent"]) ?? 2), Json.Str(money["currency"]));
        }
    }

    /// <summary>Adds "($640.92/$700)" to the extra-usage window, or creates it from <c>spend</c> alone.</summary>
    private static void ApplyExtraUsageAmounts(JsonObject root, List<UsageWindow> windows)
    {
        if (ParseExtraUsageAmounts(root) is not { } money) return;
        var extra = Json.Obj(root["extra_usage"]);
        var reached = extra?["spend_limit_reached"]?.GetValueKind() == JsonValueKind.True;
        var label = $"추가 사용량 (월) {FormatMoney(money.Used, money.Currency)}/{FormatMoney(money.Limit, money.Currency)}";

        var idx = windows.FindIndex(w => w.Id == "extra_usage");
        if (idx >= 0)
        {
            windows[idx] = windows[idx] with { Label = label, RemainingFraction = reached ? 0 : windows[idx].RemainingFraction };
        }
        else if (money.Limit > 0)
        {
            var remaining = reached ? 0 : Math.Clamp(1.0 - money.Used / money.Limit, 0.0, 1.0);
            windows.Add(new UsageWindow("extra_usage", label, remaining, null));
        }
    }

    public static string FormatMoney(double amount, string? currency)
    {
        var number = amount.ToString("#,0.##", System.Globalization.CultureInfo.InvariantCulture);
        return currency?.ToUpperInvariant() switch
        {
            null or "" => number,
            "USD" => $"${number}",
            "EUR" => $"€{number}",
            "KRW" => $"₩{number}",
            var c => $"{number} {c}",
        };
    }

    private static string HumanizeBucket(string key) =>
        key.Replace("seven_day", "주간").Replace("five_hour", "5시간").Replace('_', ' ').Trim();

    public static string? FormatTierName(string? tier)
    {
        if (string.IsNullOrEmpty(tier)) return null;
        var lower = tier.ToLowerInvariant();
        if (lower.Contains("max_20") || lower.Contains("20x")) return "Claude Max (20x)";
        if (lower.Contains("max_5") || lower.Contains("5x")) return "Claude Max (5x)";
        if (lower.Contains("max")) return "Claude Max";
        if (lower.Contains("pro")) return "Claude Pro";
        if (lower.Contains("enterprise")) return "Claude Enterprise";
        if (lower.Contains("team") || lower.Contains("raven")) return "Claude Team";
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(tier.Replace('_', ' ').ToLowerInvariant());
    }
}
