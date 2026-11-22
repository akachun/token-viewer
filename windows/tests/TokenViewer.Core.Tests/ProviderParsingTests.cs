using System.Text.Json.Nodes;
using TokenViewer.Core.Models;
using TokenViewer.Core.Providers;
using TokenViewer.Core.Services;

namespace TokenViewer.Core.Tests;

public class ClaudeParsingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly ClaudeOrganization Org = new("org-1", "Sechun's Org", "default_claude_max_20x", ["chat"]);

    [Fact]
    public void PercentScale_IsConvertedToRemainingFraction()
    {
        const string json = """
            {"five_hour":{"utilization":82.0,"resets_at":"2026-11-22T14:00:00.123456+00:00"},
             "seven_day":{"utilization":46,"resets_at":"2026-11-26T00:00:00Z"},
             "seven_day_opus":null}
            """;

        var snap = ClaudeProvider.ParseUsage(json, Org, Now);

        Assert.Equal(2, snap.Windows.Count);
        Assert.Equal("five_hour", snap.Windows[0].Id);
        Assert.Equal(18, snap.Windows[0].RemainingPercent);
        Assert.Equal(54, snap.Windows[1].RemainingPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 14, 0, 0, TimeSpan.Zero), snap.Windows[0].ResetsAt!.Value.AddTicks(-1234560));
        Assert.Equal("Claude Max (20x)", snap.PlanName);
        Assert.Equal("Sechun's Org", snap.AuthStatus.Detail);
    }

    [Fact]
    public void FractionScale_IsDetected()
    {
        const string json = """{"five_hour":{"utilization":0.25},"seven_day":{"utilization":0.5}}""";
        var snap = ClaudeProvider.ParseUsage(json, Org, Now);
        Assert.Equal(75, snap.Windows[0].RemainingPercent);
        Assert.Equal(50, snap.Windows[1].RemainingPercent);
    }

    [Fact]
    public void IntegerZeroOrOne_DefaultsToPercentScale()
    {
        const string json = """{"five_hour":{"utilization":1},"seven_day":{"utilization":0}}""";
        var snap = ClaudeProvider.ParseUsage(json, Org, Now);
        Assert.Equal(99, snap.Windows[0].RemainingPercent);
        Assert.Equal(100, snap.Windows[1].RemainingPercent);
    }

    [Fact]
    public void HtmlOrganizationsResponse_MeansNotAuthenticated()
    {
        var e = Assert.Throws<ProviderException>(() => ClaudeProvider.ParseOrganizations("<html>login</html>"));
        Assert.Equal(ProviderErrorKind.NotAuthenticated, e.Kind);
    }

    [Theory]
    [InlineData("default_claude_max_5x", "Claude Max (5x)")]
    [InlineData("claude_pro", "Claude Pro")]
    [InlineData("claude_team_tier", "Claude Team")]
    [InlineData("enterprise_custom", "Enterprise Custom")]
    [InlineData(null, null)]
    public void FormatTierName(string? tier, string? expected) =>
        Assert.Equal(expected, ClaudeProvider.FormatTierName(tier));
}

public class CodexParsingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParsesPrimaryAndSecondaryWindows()
    {
        const string json = """
            {"plan_type":"plus","rate_limit":{"allowed":true,"limit_reached":false,
              "primary_window":{"used_percent":22,"limit_window_seconds":18000,"reset_after_seconds":3600},
              "secondary_window":{"used_percent":39,"limit_window_seconds":604800,"reset_at":1795000000}}}
            """;

        var snap = CodexProvider.ParseWhamUsage(json, "me@example.com", Now);

        Assert.Equal("ChatGPT Plus", snap.PlanName);
        Assert.Equal("me@example.com", snap.AuthStatus.Detail);
        Assert.Equal("5시간 윈도우", snap.Windows[0].Label);
        Assert.Equal(78, snap.Windows[0].RemainingPercent);
        Assert.Equal(Now.AddHours(1), snap.Windows[0].ResetsAt);
        Assert.Equal("7일 주간 한도", snap.Windows[1].Label);
        Assert.Equal(61, snap.Windows[1].RemainingPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795000000), snap.Windows[1].ResetsAt);
    }

    [Fact]
    public void ParseSession_ExtractsTokenAndEmail()
    {
        var (token, email) = CodexProvider.ParseSession("""{"accessToken":"abc","user":{"email":"a@b.c"}}""");
        Assert.Equal("abc", token);
        Assert.Equal("a@b.c", email);

        Assert.Equal((null, null), CodexProvider.ParseSession("{}"));
        Assert.Equal((null, null), CodexProvider.ParseSession("not json"));
    }
}

public class GeminiParsingTests
{
    [Fact]
    public void BuildWindows_PicksMostConstrainedProAndFlash()
    {
        var buckets = GeminiProvider.ParseQuotaBuckets("""
            {"buckets":[
              {"modelId":"gemini-2.5-pro","remainingFraction":0.8,"resetTime":"2026-11-23T00:00:00Z"},
              {"modelId":"gemini-3-pro-preview","remainingFraction":0.4},
              {"modelId":"gemini-2.5-flash","remainingFraction":0.95},
              {"modelId":"gemini-embedding","remainingFraction":1.0},
              {"modelId":"no-fraction"}
            ]}
            """);

        var windows = GeminiProvider.BuildWindows(buckets);

        Assert.Equal(["gemini-pro", "gemini-flash", "gemini-embedding"], windows.Select(w => w.Id));
        Assert.Equal(40, windows[0].RemainingPercent);
        Assert.Equal(95, windows[1].RemainingPercent);
    }

    [Theory]
    [InlineData("g1-ultra-tier", "Google AI Ultra")]
    [InlineData("standard-tier", "Gemini Standard")]
    [InlineData("free-tier", "Gemini Free")]
    public void FormatTier(string raw, string expected) => Assert.Equal(expected, GeminiProvider.FormatTier(raw));

    [Fact]
    public void ExtractGoogleErrorMessage_PrefersStatusAndMessage()
    {
        Assert.Equal("[PERMISSION_DENIED] nope",
            GeminiProvider.ExtractGoogleErrorMessage("""{"error":{"status":"PERMISSION_DENIED","message":"nope"}}"""));
        Assert.Equal("plain", GeminiProvider.ExtractGoogleErrorMessage("plain"));
    }
}

public class AntigravityParsingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParsesModelConfigsIntoProAndFlash()
    {
        const string json = """
            {"userStatus":{"email":"me@gmail.com",
              "planStatus":{"planInfo":{"planName":"Google AI Pro"}},
              "cascadeModelConfigData":{"clientModelConfigs":[
                {"label":"Gemini 3 Pro (High)","quotaInfo":{"remainingFraction":0.6,"resetTime":"2026-11-22T17:00:00Z"}},
                {"label":"Gemini 3 Pro (Low)","quotaInfo":{"remainingFraction":0.7}},
                {"label":"Gemini 3 Flash","quotaInfo":{}},
                {"label":"Claude Sonnet 4.5","quotaInfo":{"remainingFraction":1.0}}
              ]}}}
            """;

        var result = AntigravityQuotaService.ParseUserStatus(json, Now);

        Assert.Equal("me@gmail.com", result.AccountLabel);
        Assert.Equal("Google AI Pro", result.PlanName);
        Assert.Equal(2, result.Windows.Count);
        Assert.Equal("Gemini Pro", result.Windows[0].Label);
        Assert.Equal(60, result.Windows[0].RemainingPercent);
        Assert.Equal("Gemini Flash", result.Windows[1].Label);
        Assert.Equal(0, result.Windows[1].RemainingPercent); // missing remainingFraction = exhausted
    }

    [Fact]
    public void FallsBackToPromptCredits_AcceptingStringNumbers()
    {
        const string json = """
            {"userStatus":{"planStatus":{"availablePromptCredits":"250","planInfo":{"monthlyPromptCredits":1000}}}}
            """;
        var result = AntigravityQuotaService.ParseUserStatus(json, Now);
        Assert.Equal("antigravity-credits", Assert.Single(result.Windows).Id);
        Assert.Equal(25, result.Windows[0].RemainingPercent);
    }

    [Fact]
    public void MissingUserStatus_Throws()
    {
        var e = Assert.Throws<AntigravityException>(() => AntigravityQuotaService.ParseUserStatus("{}", Now));
        Assert.Equal(AntigravityErrorKind.RpcFailed, e.Kind);
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Antigravity\language_server_windows_x64.exe --csrf_token abc-123 --port 0", "abc-123")]
    [InlineData(@"""C:\x\language_server_windows_x64.exe"" --csrf_token=""tok""", "tok")]
    [InlineData(@"C:\x\other.exe --csrf_token abc", null)]
    [InlineData(@"C:\x\language_server.exe --port 1", null)]
    public void ExtractCsrfToken(string commandLine, string? expected) =>
        Assert.Equal(expected, AntigravityQuotaService.ExtractCsrfToken(commandLine));
}
