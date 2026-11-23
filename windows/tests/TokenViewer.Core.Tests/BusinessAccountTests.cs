using System.Text;
using TokenViewer.Core.Providers;
using TokenViewer.Core.Services;

namespace TokenViewer.Core.Tests;

/// <summary>Scripted IWebSession: responses keyed by path, records request headers.</summary>
internal sealed class FakeWebSession : IWebSession
{
    public Dictionary<string, (int Status, string Body)> Responses { get; } = new();
    public List<WebCookie> Cookies { get; } = [];
    public List<(string Path, IReadOnlyDictionary<string, string>? Headers)> Requests { get; } = [];

    public Task<WebFetchResponse> FetchAsync(Uri origin, string pathOrUrl, string method = "GET",
        IReadOnlyDictionary<string, string>? headers = null, string? body = null)
    {
        Requests.Add((pathOrUrl, headers));
        return Task.FromResult(Responses.TryGetValue(pathOrUrl, out var r)
            ? new WebFetchResponse(r.Status, r.Body)
            : new WebFetchResponse(404, ""));
    }

    public Task<IReadOnlyList<WebCookie>> GetCookiesAsync(string domainSubstring) =>
        Task.FromResult<IReadOnlyList<WebCookie>>(Cookies.Where(c => c.Domain.Contains(domainSubstring)).ToList());

    public Task SetCookieAsync(WebCookie cookie) { Cookies.Add(cookie); return Task.CompletedTask; }
    public Task ClearSessionAsync(IEnumerable<string> domains) => Task.CompletedTask;
}

public class ClaudeBusinessTests
{
    private static ClaudeProvider Provider(FakeWebSession web) =>
        new(web, new InMemoryCredentialStore(), new JsonKeyValueStore(Path.GetTempFileName()));

    private const string Orgs = """
        [{"uuid":"personal","name":"me@corp.com's Organization","rate_limit_tier":"default_claude_ai","capabilities":["chat"]},
         {"uuid":"team-1","name":"Corp","rate_limit_tier":"default_raven","capabilities":["chat","claude_pro"]}]
        """;

    private const string EmptyUsage = """{"five_hour":null,"seven_day":null,"seven_day_opus":null}""";
    private const string TeamUsage = """{"five_hour":{"utilization":30,"resets_at":"2026-11-23T10:00:00Z"},"seven_day":{"utilization":10}}""";

    [Fact]
    public async Task SkipsPersonalOrgWithoutWindows_AndUsesTeamOrg()
    {
        var web = new FakeWebSession();
        web.Responses["/api/organizations"] = (200, Orgs);
        web.Responses["/api/organizations/personal/usage"] = (200, EmptyUsage);
        web.Responses["/api/organizations/team-1/usage"] = (200, TeamUsage);

        var snap = await Provider(web).FetchUsageAsync();

        Assert.Equal("Corp", snap.AuthStatus.Detail);
        Assert.Equal("Claude Team", snap.PlanName);
        Assert.Equal(["five_hour", "seven_day"], snap.Windows.Select(w => w.Id));
    }

    [Fact]
    public async Task LastActiveOrgCookie_IsTriedFirst()
    {
        var web = new FakeWebSession();
        web.Cookies.Add(new WebCookie("lastActiveOrg", "team-1", ".claude.ai"));
        web.Responses["/api/organizations"] = (200, Orgs);
        web.Responses["/api/organizations/team-1/usage"] = (200, TeamUsage);

        await Provider(web).FetchUsageAsync();

        Assert.Equal("/api/organizations/team-1/usage", web.Requests[1].Path);
    }

    [Fact]
    public async Task ForbiddenOrg_FallsThroughToNext()
    {
        var web = new FakeWebSession();
        web.Cookies.Add(new WebCookie("lastActiveOrg", "team-1", ".claude.ai"));
        web.Responses["/api/organizations"] = (200, Orgs);
        web.Responses["/api/organizations/team-1/usage"] = (403, "{}");
        web.Responses["/api/organizations/personal/usage"] = (200, TeamUsage);

        var snap = await Provider(web).FetchUsageAsync();
        Assert.Equal(2, snap.Windows.Count);
    }

    [Fact]
    public async Task NoWindowsAnywhere_ReturnsNotice()
    {
        var web = new FakeWebSession();
        web.Responses["/api/organizations"] = (200,
            """[{"uuid":"ent","name":"Corp","rate_limit_tier":"default_claude_enterprise","capabilities":["chat"]}]""");
        web.Responses["/api/organizations/ent/usage"] = (200, EmptyUsage);

        var snap = await Provider(web).FetchUsageAsync();

        Assert.Empty(snap.Windows);
        Assert.Equal("Claude Enterprise", snap.PlanName);
        Assert.Contains("Corp", snap.Notice);
    }

    [Fact]
    public async Task AllOrgsForbidden_Throws()
    {
        var web = new FakeWebSession();
        web.Responses["/api/organizations"] = (200, Orgs);
        web.Responses["/api/organizations/personal/usage"] = (403, "");
        web.Responses["/api/organizations/team-1/usage"] = (403, "");

        var e = await Assert.ThrowsAsync<ProviderException>(() => Provider(web).FetchUsageAsync());
        Assert.Equal(ProviderErrorKind.SessionExpired, e.Kind);
    }

    [Fact]
    public void UnknownBuckets_AreParsedAfterKnownOnes()
    {
        const string json = """
            {"extra_usage":{"is_enabled":true,"utilization":25.0},
             "seven_day":{"utilization":50},
             "seven_day_cowork":{"utilization":5},
             "iguana":null}
            """;
        var org = new ClaudeOrganization("o", "Corp", "default_claude_enterprise", ["chat"]);
        var snap = ClaudeProvider.ParseUsage(json, org, DateTimeOffset.Now);

        Assert.Equal(["seven_day", "extra_usage", "seven_day_cowork"], snap.Windows.Select(w => w.Id));
        Assert.Equal(0.75, snap.Windows[1].RemainingFraction, 3);
        Assert.Equal("주간 cowork", snap.Windows[2].Label);
    }
}

public class CodexBusinessTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static string Jwt(string accountId)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"none\"}")}.{B64($$$"""{"https://api.openai.com/auth":{"chatgpt_account_id":"{{{accountId}}}"}}""")}.sig";
    }

    [Fact]
    public void ParseSessionInfo_ReadsWorkspaceAccount()
    {
        var info = CodexProvider.ParseSessionInfo(
            """{"accessToken":"t","user":{"email":"a@corp.com"},"account":{"id":"ws-1","planType":"team","structure":"workspace"}}""");
        Assert.Equal(new CodexProvider.SessionInfo("t", "a@corp.com", "ws-1"), info);
    }

    [Theory]
    [InlineData("ws-cookie", "ws-session", "ws-cookie")]
    [InlineData("%22ws-cookie%22", null, "ws-cookie")]
    [InlineData(null, "ws-session", "ws-session")]
    [InlineData("personal", "ws-session", "ws-session")]
    [InlineData(null, null, "ws-jwt")]
    public void ResolveAccountId_Precedence(string? cookie, string? session, string expected) =>
        Assert.Equal(expected, CodexProvider.ResolveAccountId(cookie, session, Jwt("ws-jwt")));

    [Fact]
    public void ResolveAccountId_GarbageToken_ReturnsNull() =>
        Assert.Null(CodexProvider.ResolveAccountId(null, null, "not-a-jwt"));

    [Fact]
    public async Task FetchUsage_SendsChatGptAccountIdHeader()
    {
        var web = new FakeWebSession();
        web.Cookies.Add(new WebCookie("_account", "ws-1", ".chatgpt.com"));
        web.Responses["/api/auth/session"] = (200, """{"accessToken":"tok","user":{"email":"a@corp.com"}}""");
        web.Responses["/backend-api/wham/usage"] = (200,
            """{"plan_type":"business","rate_limit":{"primary_window":{"used_percent":20,"limit_window_seconds":18000}}}""");

        var provider = new CodexProvider(web, new InMemoryCredentialStore(), new JsonKeyValueStore(Path.GetTempFileName()));
        var snap = await provider.FetchUsageAsync();

        var usageReq = web.Requests.Single(r => r.Path == "/backend-api/wham/usage");
        Assert.Equal("ws-1", usageReq.Headers!["ChatGPT-Account-Id"]);
        Assert.Equal("ChatGPT Business", snap.PlanName);
        Assert.Single(snap.Windows);
    }

    [Fact]
    public void CreditsOnlyPlan_ProducesNotice()
    {
        var snap = CodexProvider.ParseWhamUsage(
            """{"plan_type":"enterprise","rate_limit":null,"credits":{"has_credits":true,"unlimited":false,"balance":"120.5"}}""",
            null, Now);
        Assert.Empty(snap.Windows);
        Assert.Contains("120.5", snap.Notice);
    }

    [Fact]
    public void NotAllowed_ProducesPermissionNotice()
    {
        var snap = CodexProvider.ParseWhamUsage(
            """{"plan_type":"business","rate_limit":{"allowed":false,"primary_window":null}}""", null, Now);
        Assert.Contains("관리자", snap.Notice);
    }

    [Fact]
    public void ExtraLimitGroups_AreAppendedWithPrefix()
    {
        var snap = CodexProvider.ParseWhamUsage("""
            {"plan_type":"business",
             "rate_limit":{"primary_window":{"used_percent":10,"limit_window_seconds":18000}},
             "code_review_rate_limit":{"primary_window":{"used_percent":50,"limit_window_seconds":604800}}}
            """, null, Now);
        Assert.Equal(["primary", "code_review_rate_limit.primary"], snap.Windows.Select(w => w.Id));
        Assert.Equal("코드 리뷰 · 7일 주간 한도", snap.Windows[1].Label);
        Assert.Null(snap.Notice);
    }
}

public class CodexSpendControlTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    // Shape reported from a ChatGPT Business workspace (no 5h/weekly windows, monthly credit budget).
    private const string BusinessJson = """
        {"plan_type":"business","rate_limit":null,
         "spend_control":{"reached":false,"individual_limit":{"unit":"credit","limit":"10000","used":"5923.143",
           "remaining":"4076.857","used_percent":59,"remaining_percent":41,"reset_after_seconds":689351,"reset_at":1796083200}}}
        """;

    [Fact]
    public void IndividualLimit_BecomesMonthlyCreditWindow()
    {
        var snap = CodexProvider.ParseWhamUsage(BusinessJson, null, Now);

        var w = Assert.Single(snap.Windows);
        Assert.Equal("spend_individual_limit", w.Id);
        Assert.Equal("월간 크레딧 한도 (5,923/10,000)", w.Label);
        Assert.Equal(41, w.RemainingPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1796083200), w.ResetsAt);
        Assert.Null(snap.Notice);
    }

    [Fact]
    public void PercentOnly_And_Reached_AreHandled()
    {
        var snap = CodexProvider.ParseWhamUsage(
            """{"spend_control":{"individual_limit":{"used_percent":30},"team_limit":{"used_percent":10,"reached":true}}}""",
            null, Now);
        Assert.Equal([70, 0], snap.Windows.Select(w => w.RemainingPercent));
    }
}
