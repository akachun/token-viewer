using System.Text.Json;
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
        var primary = orgs.FirstOrDefault(o => o.Capabilities?.Contains("chat") == true) ?? orgs.FirstOrDefault()
            ?? throw ProviderException.NotAuthenticated();

        // 2. Usage windows for the active organization
        var usageResp = await _web.FetchAsync(Origin, $"/api/organizations/{primary.Uuid}/usage", headers: ApiHeaders);
        ProviderException.ThrowIfFailed(usageResp.Status);

        return ParseUsage(usageResp.Body, primary, DateTimeOffset.Now);
    }

    private Task InjectSessionKeyCookieAsync(string value) =>
        _web.SetCookieAsync(new WebCookie("sessionKey", value, ".claude.ai", "/", true, DateTimeOffset.Now.AddDays(30)));

    // MARK: - Parsing

    private sealed record UsageResponse(
        [property: JsonPropertyName("five_hour")] Bucket? FiveHour,
        [property: JsonPropertyName("seven_day")] Bucket? SevenDay,
        [property: JsonPropertyName("seven_day_sonnet")] Bucket? SevenDaySonnet,
        [property: JsonPropertyName("seven_day_opus")] Bucket? SevenDayOpus);

    private sealed record Bucket(
        [property: JsonPropertyName("utilization")] double? Utilization,
        [property: JsonPropertyName("resets_at")] string? ResetsAt);

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

    public static ProviderUsageSnapshot ParseUsage(string json, ClaudeOrganization org, DateTimeOffset now)
    {
        UsageResponse decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<UsageResponse>(json) ?? throw ProviderException.Parsing("빈 응답");
        }
        catch (JsonException e)
        {
            throw ProviderException.Parsing(e.Message);
        }

        var allUtils = new[] { decoded.FiveHour, decoded.SevenDay, decoded.SevenDaySonnet, decoded.SevenDayOpus }
            .Select(b => b?.Utilization)
            .OfType<double>()
            .ToList();

        // If any bucket is > 1.0, the API returns 0...100 percentages. If all values are within 0...1 and at
        // least one is fractional, it's a 0...1 fraction. Otherwise (e.g. integers 0 or 1) assume 0...100.
        var isFractionScale = !allUtils.Any(u => u > 1.0) && allUtils.Any(u => u is > 0.0 and < 1.0);

        var windows = new List<UsageWindow>();
        void Add(Bucket? b, string id, string label)
        {
            if (b?.Utilization is not { } util) return;
            var used = isFractionScale ? util : util / 100.0;
            windows.Add(new UsageWindow(id, label, Math.Clamp(1.0 - used, 0.0, 1.0), DateParser.ParseIso8601(b.ResetsAt)));
        }

        Add(decoded.FiveHour, "five_hour", "5시간 세션");
        Add(decoded.SevenDay, "seven_day", "7일 주간 한도");
        Add(decoded.SevenDaySonnet, "seven_day_sonnet", "Sonnet 주간");
        Add(decoded.SevenDayOpus, "seven_day_opus", "Opus 주간");

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

    public static string? FormatTierName(string? tier)
    {
        if (string.IsNullOrEmpty(tier)) return null;
        var lower = tier.ToLowerInvariant();
        if (lower.Contains("max_20") || lower.Contains("20x")) return "Claude Max (20x)";
        if (lower.Contains("max_5") || lower.Contains("5x")) return "Claude Max (5x)";
        if (lower.Contains("max")) return "Claude Max";
        if (lower.Contains("pro")) return "Claude Pro";
        if (lower.Contains("team")) return "Claude Team";
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(tier.Replace('_', ' ').ToLowerInvariant());
    }
}
