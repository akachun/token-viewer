using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using TokenViewer.Core.Models;
using TokenViewer.Core.Services;

namespace TokenViewer.App.ViewModels;

public sealed record WindowRowViewModel(string Label, string PercentText, double BarValue, Brush BarBrush, string? ResetText);

/// <summary>Display model for one provider card in the popup (rebuilt on every snapshot change).</summary>
public sealed class ProviderCardViewModel
{
    public ProviderCardViewModel(ProviderUsageSnapshot snapshot, AppSettings settings, UsageMonitorViewModel monitor)
    {
        var p = snapshot.Provider;
        Provider = p;
        DisplayName = p.DisplayName();
        CompanyName = $"({p.CompanyName()})";
        AccentBrush = Brush(p.AccentColor());
        StatusText = snapshot.AuthStatus.StatusText;
        PlanName = snapshot.PlanName;
        IsAuthenticated = snapshot.AuthStatus.IsAuthenticated;
        NeedsLogin = snapshot.AuthStatus.State is AuthState.SignedOut or AuthState.Expired;
        IsLoading = snapshot.IsLoading;
        ErrorMessage = snapshot.ErrorMessage;
        LastUpdatedText = snapshot.LastUpdated is { } t ? $"업데이트: {t.ToLocalTime():HH:mm}" : null;

        var metric = settings.MetricMode;
        Windows = snapshot.Windows.Select(w => new WindowRowViewModel(
            w.Label,
            $"{w.DisplayPercent(metric)}% {metric.ShortTitle()}",
            w.DisplayFraction(metric) * 100,
            Brush(settings.ColorMode == ColorMode.BrandColor ? p.AccentColor() : HealthColors.For(w, settings.WarningThresholdPercent)),
            w.FormatTimeUntilReset() is { } r ? $"리셋 {r}" : null)).ToList();
        HasNoWindows = IsAuthenticated && Windows.Count == 0 && ErrorMessage is null;
        NoWindowsText = snapshot.Notice ?? "표시할 쿼터 윈도우가 없습니다.";

        LoginCommand = new RelayCommand(() => monitor.StartLogin(p));
        RefreshCommand = new AsyncRelayCommand(() => monitor.RefreshAsync(p));
        SignOutCommand = new AsyncRelayCommand(() => monitor.SignOutAsync(p));
        OpenDashboardCommand = new RelayCommand(() =>
            Process.Start(new ProcessStartInfo(p.UsageDashboardUrl().AbsoluteUri) { UseShellExecute = true }));
    }

    public ProviderType Provider { get; }
    public string DisplayName { get; }
    public string CompanyName { get; }
    public Brush AccentBrush { get; }
    public string StatusText { get; }
    public string? PlanName { get; }
    public bool IsAuthenticated { get; }
    public bool NeedsLogin { get; }
    public bool IsLoading { get; }
    public string? ErrorMessage { get; }
    public string? LastUpdatedText { get; }
    public IReadOnlyList<WindowRowViewModel> Windows { get; }
    public bool HasNoWindows { get; }
    public string NoWindowsText { get; }

    public Visibility LoginVisibility => NeedsLogin ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SignedInVisibility => IsAuthenticated ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility => string.IsNullOrEmpty(ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PlanVisibility => string.IsNullOrEmpty(PlanName) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoWindowsVisibility => HasNoWindows ? Visibility.Visible : Visibility.Collapsed;

    public IRelayCommand LoginCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand SignOutCommand { get; }
    public IRelayCommand OpenDashboardCommand { get; }

    private static SolidColorBrush Brush(RgbColor c)
    {
        var b = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        b.Freeze();
        return b;
    }
}
