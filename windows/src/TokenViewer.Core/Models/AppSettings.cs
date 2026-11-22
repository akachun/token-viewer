using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TokenViewer.Core.Models;

/// <summary>
/// Tray icon look. Windows tray icons are small squares, so only the macOS styles
/// that fit a square (ring, number, status dot) are supported.
/// </summary>
public enum TrayDisplayStyle
{
    Ring,
    Number,
    StatusDot,
}

public enum QuotaMetricMode
{
    Remaining,
    Used,
}

public enum WindowSelectionStrategy
{
    Primary,
    MostConstrained,
}

public enum ColorMode
{
    HealthColor,
    BrandColor,
    Monochrome,
}

public sealed record Option<T>(T Value, string Title, string? Description = null);

public static class SettingOptions
{
    public static readonly IReadOnlyList<Option<TrayDisplayStyle>> DisplayStyles =
    [
        new(TrayDisplayStyle.Ring, "원형 게이지 링", "원형 진행 링으로 잔여량을 표시하고 가운데에 수치나 약자를 표시합니다"),
        new(TrayDisplayStyle.Number, "큰 숫자", "퍼센트 숫자만 크게 표시합니다"),
        new(TrayDisplayStyle.StatusDot, "상태 도트 + 약자", "AI 약자와 잔여량 상태 색상 도트를 표시합니다"),
    ];

    public static readonly IReadOnlyList<Option<QuotaMetricMode>> MetricModes =
    [
        new(QuotaMetricMode.Remaining, "잔여량 (Remaining %)"),
        new(QuotaMetricMode.Used, "사용량 (Used %)"),
    ];

    public static readonly IReadOnlyList<Option<WindowSelectionStrategy>> WindowStrategies =
    [
        new(WindowSelectionStrategy.Primary, "기본 윈도우 (5시간 / 주력 모델)"),
        new(WindowSelectionStrategy.MostConstrained, "가장 부족한 윈도우 우선"),
    ];

    public static readonly IReadOnlyList<Option<ColorMode>> ColorModes =
    [
        new(ColorMode.HealthColor, "상태 색상 (초록/노랑/빨강)"),
        new(ColorMode.BrandColor, "AI 브랜드 색상"),
        new(ColorMode.Monochrome, "모노크롬 (작업 표시줄 테마에 맞춤)"),
    ];

    public static readonly IReadOnlyList<Option<int>> RefreshIntervals =
    [
        new(60, "1분마다"),
        new(180, "3분마다"),
        new(300, "5분마다 (권장)"),
        new(600, "10분마다"),
        new(1800, "30분마다"),
    ];

    public static string ShortTitle(this QuotaMetricMode m) => m == QuotaMetricMode.Remaining ? "잔여" : "사용";
}

/// <summary>Persistent user preferences. Serialized to settings.json by <see cref="Services.SettingsStore"/>.</summary>
public sealed partial class AppSettings : ObservableObject
{
    [ObservableProperty] private List<ProviderType> providerOrder = [.. ProviderTypeExtensions.All];
    [ObservableProperty] private List<ProviderType> visibleProviders = [.. ProviderTypeExtensions.All];
    [ObservableProperty] private List<ProviderType> notifiedProviders = [.. ProviderTypeExtensions.All];

    [ObservableProperty] private TrayDisplayStyle displayStyle = TrayDisplayStyle.Ring;
    [ObservableProperty] private QuotaMetricMode metricMode = QuotaMetricMode.Remaining;
    [ObservableProperty] private WindowSelectionStrategy windowStrategy = WindowSelectionStrategy.Primary;
    [ObservableProperty] private ColorMode colorMode = ColorMode.HealthColor;

    /// <summary>Ring style: draw the percentage inside the ring instead of the provider letter.</summary>
    [ObservableProperty] private bool showPercentInIcon = true;

    [ObservableProperty] private int refreshIntervalSeconds = 300;
    [ObservableProperty] private bool notificationsEnabled = true;
    [ObservableProperty] private int warningThresholdPercent = 20;
    [ObservableProperty] private bool launchAtLogin;
    [ObservableProperty] private bool demoModeEnabled;

    [JsonIgnore]
    public IReadOnlyList<ProviderType> OrderedVisibleProviders =>
        ProviderOrder.Where(VisibleProviders.Contains).ToList();

    public bool IsVisible(ProviderType p) => VisibleProviders.Contains(p);

    public void SetVisible(ProviderType p, bool visible) =>
        VisibleProviders = Toggle(VisibleProviders, p, visible);

    public bool IsNotified(ProviderType p) => NotifiedProviders.Contains(p);

    public void SetNotified(ProviderType p, bool notified) =>
        NotifiedProviders = Toggle(NotifiedProviders, p, notified);

    public void MoveProviderUp(ProviderType p)
    {
        var idx = ProviderOrder.IndexOf(p);
        if (idx <= 0) return;
        var copy = new List<ProviderType>(ProviderOrder);
        (copy[idx], copy[idx - 1]) = (copy[idx - 1], copy[idx]);
        ProviderOrder = copy;
    }

    public void MoveProviderDown(ProviderType p)
    {
        var idx = ProviderOrder.IndexOf(p);
        if (idx < 0 || idx >= ProviderOrder.Count - 1) return;
        var copy = new List<ProviderType>(ProviderOrder);
        (copy[idx], copy[idx + 1]) = (copy[idx + 1], copy[idx]);
        ProviderOrder = copy;
    }

    /// <summary>Repairs values loaded from disk (unknown/missing providers, out-of-range numbers).</summary>
    public void Normalize()
    {
        var order = ProviderOrder.Distinct().Where(ProviderTypeExtensions.All.Contains).ToList();
        order.AddRange(ProviderTypeExtensions.All.Where(p => !order.Contains(p)));
        ProviderOrder = order;
        VisibleProviders = VisibleProviders.Distinct().ToList();
        NotifiedProviders = NotifiedProviders.Distinct().ToList();

        if (SettingOptions.RefreshIntervals.All(o => o.Value != RefreshIntervalSeconds))
            RefreshIntervalSeconds = 300;
        if (WarningThresholdPercent is <= 0 or > 100)
            WarningThresholdPercent = 20;
    }

    private static List<ProviderType> Toggle(List<ProviderType> source, ProviderType p, bool on)
    {
        var copy = new List<ProviderType>(source);
        if (on && !copy.Contains(p)) copy.Add(p);
        if (!on) copy.Remove(p);
        return copy;
    }
}
