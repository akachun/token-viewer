using TokenViewer.Core.Models;
using TokenViewer.Core.Providers;
using TokenViewer.Core.Services;

namespace TokenViewer.Core.Tests;

public class UsageWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(3 * 86400 + 2 * 3600, "3일 2시간", "3d")]
    [InlineData(2 * 3600 + 14 * 60, "2시간 14분", "2h")]
    [InlineData(5 * 60, "5분", "5m")]
    [InlineData(30, "1분 미만", "<1m")]
    [InlineData(-10, "곧 리셋", "0m")]
    public void FormatTimeUntilReset(int seconds, string full, string compact)
    {
        var w = new UsageWindow("w", "W", 0.5, Now.AddSeconds(seconds));
        Assert.Equal(full, w.FormatTimeUntilReset(Now));
        Assert.Equal(compact, w.FormatTimeUntilReset(Now, compact: true));
    }

    [Fact]
    public void PercentsAreClampedAndComplementary()
    {
        var w = new UsageWindow("w", "W", 1.4, null);
        Assert.Equal(100, w.RemainingPercent);
        Assert.Equal(0, w.UsedPercent);
        Assert.Null(w.FormatTimeUntilReset(Now));
    }

    [Fact]
    public void MostConstrainedStrategy_PicksLowestRemaining()
    {
        var snap = DemoData.Snapshots(Now)[ProviderType.Codex];
        Assert.Equal("primary", snap.PrimaryDisplayWindow(WindowSelectionStrategy.Primary)!.Id);
        Assert.Equal("secondary", snap.PrimaryDisplayWindow(WindowSelectionStrategy.MostConstrained)!.Id);
    }

    [Theory]
    [InlineData(0.05, HealthLevel.Critical)]
    [InlineData(0.10, HealthLevel.Critical)]
    [InlineData(0.15, HealthLevel.Warning)]
    [InlineData(0.20, HealthLevel.Warning)]
    [InlineData(0.21, HealthLevel.Healthy)]
    public void HealthLevel_UsesThreshold(double remaining, HealthLevel expected) =>
        Assert.Equal(expected, HealthColors.Level(new UsageWindow("w", "W", remaining, null), 20));
}

public class ThresholdAlertTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static ProviderUsageSnapshot Claude(double remaining) =>
        new(ProviderType.Claude, AuthStatus.Authenticated("me"), null,
            [new UsageWindow("five_hour", "5시간 세션", remaining, Now.AddHours(1))], Now, null, false);

    [Fact]
    public void AlertsOnceUntilRecovered()
    {
        var settings = new AppSettings();
        var eval = new ThresholdAlertEvaluator();

        var first = Assert.Single(eval.Evaluate(Claude(0.18), settings, Now));
        Assert.Equal("Claude 잔여량 부족 (18%)", first.Title);
        Assert.Contains("리셋까지 1시간 0분", first.Body);

        Assert.Empty(eval.Evaluate(Claude(0.15), settings, Now));   // deduplicated
        Assert.Empty(eval.Evaluate(Claude(0.30), settings, Now));   // recovered → key cleared
        Assert.Single(eval.Evaluate(Claude(0.10), settings, Now));  // alerts again
    }

    [Fact]
    public void RespectsDisabledAndUnnotifiedProviders()
    {
        var eval = new ThresholdAlertEvaluator();
        var settings = new AppSettings { NotificationsEnabled = false };
        Assert.Empty(eval.Evaluate(Claude(0.05), settings, Now));

        settings.NotificationsEnabled = true;
        settings.SetNotified(ProviderType.Claude, false);
        Assert.Empty(eval.Evaluate(Claude(0.05), settings, Now));
    }
}

public class SettingsTests
{
    [Fact]
    public void RoundTripsThroughDisk_AndRepairsBadValues()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tv-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var s = store.Load();
            s.MoveProviderDown(ProviderType.Codex);   // saved on change
            s.SetVisible(ProviderType.Gemini, false);
            s.DisplayStyle = TrayDisplayStyle.StatusDot;

            var reloaded = new SettingsStore(path).Load();
            Assert.Equal([ProviderType.Claude, ProviderType.Codex, ProviderType.Gemini], reloaded.ProviderOrder);
            Assert.False(reloaded.IsVisible(ProviderType.Gemini));
            Assert.Equal(TrayDisplayStyle.StatusDot, reloaded.DisplayStyle);
            Assert.Contains("\"statusDot\"", File.ReadAllText(path));

            File.WriteAllText(path, """{"providerOrder":["gemini"],"refreshIntervalSeconds":7,"warningThresholdPercent":0}""");
            var repaired = new SettingsStore(path).Load();
            Assert.Equal([ProviderType.Gemini, ProviderType.Codex, ProviderType.Claude], repaired.ProviderOrder);
            Assert.Equal(300, repaired.RefreshIntervalSeconds);
            Assert.Equal(20, repaired.WarningThresholdPercent);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WidgetSettings_DefaultOn_PersistPosition_AndRepairOpacity()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tv-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        try
        {
            // Settings written by v1.0.x have no widget keys: the widget starts enabled at the default spot.
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, """{"displayStyle":"ring"}""");
            var s = new SettingsStore(path).Load();
            Assert.True(s.WidgetEnabled);
            Assert.Null(s.WidgetLeft);

            s.WidgetLeft = 1200;
            s.WidgetTop = 40;
            s.WidgetEnabled = false;
            var reloaded = new SettingsStore(path).Load();
            Assert.Equal(1200, reloaded.WidgetLeft);
            Assert.Equal(40, reloaded.WidgetTop);
            Assert.False(reloaded.WidgetEnabled);

            File.WriteAllText(path, """{"widgetOpacity":0.05}""");
            Assert.Equal(0.9, new SettingsStore(path).Load().WidgetOpacity);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void KeyValueStore_PersistsTypedValues()
    {
        var path = Path.Combine(Path.GetTempPath(), "tv-state-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new JsonKeyValueStore(path);
            store.SetBool("flag", true);
            store.Set("cache", new AntigravityQuotaResult("me", "Pro",
                [new UsageWindow("ag-gemini-pro", "Gemini Pro", 0.5, null)], DateTimeOffset.UnixEpoch));

            var reopened = new JsonKeyValueStore(path);
            Assert.True(reopened.GetBool("flag"));
            var cache = reopened.Get<AntigravityQuotaResult>("cache");
            Assert.Equal(50, cache!.Windows[0].RemainingPercent);

            reopened.Remove("flag");
            Assert.False(new JsonKeyValueStore(path).GetBool("flag"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class TrayTextTests
{
    [Fact]
    public void Tooltip_IsCappedAt127Chars()
    {
        var now = DateTimeOffset.Now;
        var longLabel = new string('x', 80);
        var snap = new ProviderUsageSnapshot(ProviderType.Gemini, AuthStatus.Authenticated("me"), "Plan",
            [new(longLabel, longLabel, 0.5, null), new("b", longLabel, 0.4, null)], now, null, false);

        var tip = TrayText.Tooltip(snap, new AppSettings(), now);
        Assert.Equal(TrayText.MaxTooltipLength, tip.Length);
        Assert.EndsWith("…", tip);
    }

    [Fact]
    public void IconValue_ShowsDashWhenSignedOut()
    {
        Assert.Equal("–", TrayText.IconValue(ProviderUsageSnapshot.Empty(ProviderType.Codex), new AppSettings()));
        var demo = DemoData.Snapshots(DateTimeOffset.Now)[ProviderType.Claude];
        Assert.Equal("18", TrayText.IconValue(demo, new AppSettings()));
        Assert.Equal("82", TrayText.IconValue(demo, new AppSettings { MetricMode = QuotaMetricMode.Used }));
    }
}

public class OAuthQueryTests
{
    [Fact]
    public void ParseQuery_DecodesValues()
    {
        var q = GoogleOAuthService.ParseQuery("/oauth2callback?code=4%2F0Ab&state=xyz&scope=a+b");
        Assert.Equal("4/0Ab", q["code"]);
        Assert.Equal("xyz", q["state"]);
        Assert.Equal("a b", q["scope"]);
    }
}
