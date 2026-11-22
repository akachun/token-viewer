using TokenViewer.Core.Models;

namespace TokenViewer.Core.Services;

/// <summary>Sample snapshots for testing the UI and alerts without logging in.</summary>
public static class DemoData
{
    public static IReadOnlyDictionary<ProviderType, ProviderUsageSnapshot> Snapshots(DateTimeOffset now) =>
        new Dictionary<ProviderType, ProviderUsageSnapshot>
        {
            [ProviderType.Codex] = new(
                ProviderType.Codex,
                AuthStatus.Authenticated("demo@openai.com"),
                "ChatGPT Plus",
                [
                    new("primary", "5시간 윈도우", 0.78, now.AddSeconds(3600 * 2 + 900)),
                    new("secondary", "7일 주간 한도", 0.61, now.AddSeconds(86400 * 3 + 7200)),
                ],
                now, null, false),
            [ProviderType.Claude] = new(
                ProviderType.Claude,
                AuthStatus.Authenticated("demo@anthropic.com"),
                "Claude Max",
                [
                    new("five_hour", "5시간 세션", 0.18, now.AddSeconds(3600 + 1800)),
                    new("seven_day", "7일 주간 한도", 0.54, now.AddDays(4)),
                ],
                now, null, false),
            [ProviderType.Gemini] = new(
                ProviderType.Gemini,
                AuthStatus.Authenticated("demo@gmail.com"),
                "Google AI Pro",
                [
                    new("gemini-pro", "Gemini Pro", 0.92, now.AddHours(11)),
                    new("gemini-flash", "Gemini Flash", 0.99, now.AddHours(11)),
                ],
                now, null, false),
        };
}
