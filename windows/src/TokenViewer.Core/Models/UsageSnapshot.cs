using System.Text.Json.Serialization;

namespace TokenViewer.Core.Models;

/// <summary>A single rate-limit / quota window (e.g., 5-hour session, 7-day weekly, or model bucket).</summary>
public sealed record UsageWindow(string Id, string Label, double RemainingFraction, DateTimeOffset? ResetsAt)
{
    [JsonIgnore]
    public double ClampedRemainingFraction => Math.Clamp(RemainingFraction, 0.0, 1.0);

    [JsonIgnore]
    public int RemainingPercent => (int)Math.Round(ClampedRemainingFraction * 100.0, MidpointRounding.AwayFromZero);

    [JsonIgnore]
    public double UsedFraction => 1.0 - ClampedRemainingFraction;

    [JsonIgnore]
    public int UsedPercent => 100 - RemainingPercent;

    public int DisplayPercent(QuotaMetricMode metric) =>
        metric == QuotaMetricMode.Remaining ? RemainingPercent : UsedPercent;

    public double DisplayFraction(QuotaMetricMode metric) =>
        metric == QuotaMetricMode.Remaining ? ClampedRemainingFraction : UsedFraction;

    /// <summary>Short countdown string (e.g., "2시간 14분" or compact "2h").</summary>
    public string? FormatTimeUntilReset(DateTimeOffset? now = null, bool compact = false)
    {
        if (ResetsAt is not { } resetsAt) return null;
        var seconds = (long)(resetsAt - (now ?? DateTimeOffset.Now)).TotalSeconds;
        if (seconds <= 0) return compact ? "0m" : "곧 리셋";

        var days = seconds / 86400;
        var hours = seconds % 86400 / 3600;
        var minutes = seconds % 3600 / 60;

        if (days > 0) return compact ? $"{days}d" : $"{days}일 {hours}시간";
        if (hours > 0) return compact ? $"{hours}h" : $"{hours}시간 {minutes}분";
        if (minutes > 0) return compact ? $"{minutes}m" : $"{minutes}분";
        return compact ? "<1m" : "1분 미만";
    }
}

public enum AuthState
{
    SignedOut,
    Authenticating,
    Authenticated,
    Expired,
}

/// <summary>Authentication status. <see cref="Detail"/> is the account label or the expiry reason.</summary>
public sealed record AuthStatus(AuthState State, string? Detail = null)
{
    public static readonly AuthStatus SignedOut = new(AuthState.SignedOut);
    public static readonly AuthStatus Authenticating = new(AuthState.Authenticating);
    public static AuthStatus Authenticated(string? accountLabel) => new(AuthState.Authenticated, accountLabel);
    public static AuthStatus Expired(string reason) => new(AuthState.Expired, reason);

    public bool IsAuthenticated => State == AuthState.Authenticated;

    public string StatusText => State switch
    {
        AuthState.SignedOut => "미인증",
        AuthState.Authenticating => "인증 중...",
        AuthState.Authenticated => string.IsNullOrEmpty(Detail) ? "연결됨" : Detail!,
        AuthState.Expired => $"세션 만료 ({Detail})",
        _ => "",
    };
}

/// <summary>Complete snapshot of one AI provider's current authentication and quota state.</summary>
public sealed record ProviderUsageSnapshot(
    ProviderType Provider,
    AuthStatus AuthStatus,
    string? PlanName,
    IReadOnlyList<UsageWindow> Windows,
    DateTimeOffset? LastUpdated,
    string? ErrorMessage,
    bool IsLoading)
{
    public static ProviderUsageSnapshot Empty(ProviderType provider) =>
        new(provider, AuthStatus.SignedOut, null, [], null, null, false);

    /// <summary>
    /// Optional explanation shown instead of the generic "no quota windows" text, e.g. when a business /
    /// enterprise workspace has no fixed limits or the account belongs to several organizations.
    /// </summary>
    public string? Notice { get; init; }

    /// <summary>Selects the window to summarize in the tray icon.</summary>
    public UsageWindow? PrimaryDisplayWindow(WindowSelectionStrategy strategy)
    {
        if (Windows.Count == 0) return null;
        return strategy switch
        {
            WindowSelectionStrategy.MostConstrained => Windows.MinBy(w => w.ClampedRemainingFraction),
            _ => Windows[0],
        };
    }
}

public enum HealthLevel
{
    Unknown,
    Healthy,
    Warning,
    Critical,
}

public static class HealthColors
{
    public static readonly RgbColor Critical = RgbColor.FromUnit(0.94, 0.27, 0.27);
    public static readonly RgbColor Warning = RgbColor.FromUnit(0.96, 0.62, 0.04);
    public static readonly RgbColor Healthy = RgbColor.FromUnit(0.13, 0.77, 0.37);
    public static readonly RgbColor Neutral = new(0x8A, 0x8A, 0x8E);

    public static HealthLevel Level(UsageWindow? window, int warningThreshold)
    {
        if (window is null) return HealthLevel.Unknown;
        var rem = window.RemainingPercent;
        if (rem <= Math.Max(warningThreshold / 2, 10)) return HealthLevel.Critical;
        if (rem <= warningThreshold) return HealthLevel.Warning;
        return HealthLevel.Healthy;
    }

    public static RgbColor For(UsageWindow? window, int warningThreshold) => Level(window, warningThreshold) switch
    {
        HealthLevel.Critical => Critical,
        HealthLevel.Warning => Warning,
        HealthLevel.Healthy => Healthy,
        _ => Neutral,
    };
}
