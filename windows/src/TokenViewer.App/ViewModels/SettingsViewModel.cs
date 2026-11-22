using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TokenViewer.App.Tray;
using TokenViewer.Core.Models;
using TokenViewer.Core.Services;

namespace TokenViewer.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly UsageMonitorViewModel _monitor;
    private readonly Action<AlertMessage> _notify;

    public SettingsViewModel(UsageMonitorViewModel monitor, Action<AlertMessage> notify)
    {
        _monitor = monitor;
        _notify = notify;
        Settings = monitor.Settings;

        Settings.PropertyChanged += OnSettingsChanged;
        monitor.SnapshotsChanged += OnSnapshotsChanged;
        RebuildRows();
        RenderPreviews();
    }

    public AppSettings Settings { get; }

    public IReadOnlyList<Option<TrayDisplayStyle>> DisplayStyles => SettingOptions.DisplayStyles;
    public IReadOnlyList<Option<QuotaMetricMode>> MetricModes => SettingOptions.MetricModes;
    public IReadOnlyList<Option<WindowSelectionStrategy>> WindowStrategies => SettingOptions.WindowStrategies;
    public IReadOnlyList<Option<ColorMode>> ColorModes => SettingOptions.ColorModes;
    public IReadOnlyList<Option<int>> RefreshIntervals => SettingOptions.RefreshIntervals;

    public ObservableCollection<ProviderSettingsRow> ProviderRows { get; } = [];
    public ObservableCollection<BitmapSource> PreviewDark { get; } = [];
    public ObservableCollection<BitmapSource> PreviewLight { get; } = [];

    public string ThresholdText => $"{Settings.WarningThresholdPercent}% 이하";

    public string DisplayStyleDescription =>
        DisplayStyles.FirstOrDefault(o => o.Value == Settings.DisplayStyle)?.Description ?? "";

    [RelayCommand]
    private void SendTestNotification() => _notify(ThresholdAlertEvaluator.TestMessage);

    [RelayCommand]
    private void ResetAlertHistory()
    {
        _monitor.ResetAlertHistory();
        MessageBox.Show("알림 중복 기록을 초기화했습니다.", "TokenViewer", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Call when the window closes so this instance stops listening.</summary>
    public void Detach()
    {
        Settings.PropertyChanged -= OnSettingsChanged;
        _monitor.SnapshotsChanged -= OnSnapshotsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.ProviderOrder) or nameof(AppSettings.VisibleProviders) or nameof(AppSettings.NotifiedProviders))
            RebuildRows();
        if (e.PropertyName == nameof(AppSettings.WarningThresholdPercent)) OnPropertyChanged(nameof(ThresholdText));
        if (e.PropertyName == nameof(AppSettings.DisplayStyle)) OnPropertyChanged(nameof(DisplayStyleDescription));
        RenderPreviews();
    }

    private void OnSnapshotsChanged(object? sender, EventArgs e)
    {
        foreach (var row in ProviderRows) row.Refresh();
        RenderPreviews();
    }

    private void RebuildRows()
    {
        ProviderRows.Clear();
        var order = Settings.ProviderOrder;
        for (var i = 0; i < order.Count; i++)
            ProviderRows.Add(new ProviderSettingsRow(order[i], _monitor, isFirst: i == 0, isLast: i == order.Count - 1));
    }

    /// <summary>Live preview on dark and light taskbars, using current snapshots (or demo data when signed out).</summary>
    private void RenderPreviews()
    {
        var demo = DemoData.Snapshots(DateTimeOffset.Now);
        PreviewDark.Clear();
        PreviewLight.Clear();
        foreach (var p in Settings.OrderedVisibleProviders)
        {
            var snap = _monitor.Snapshot(p);
            if (!snap.AuthStatus.IsAuthenticated) snap = demo[p];
            PreviewDark.Add(TrayIconRenderer.RenderBitmap(snap, Settings, lightTaskbar: false));
            PreviewLight.Add(TrayIconRenderer.RenderBitmap(snap, Settings, lightTaskbar: true));
        }
    }
}

public sealed partial class ProviderSettingsRow : ObservableObject
{
    private readonly UsageMonitorViewModel _monitor;

    public ProviderSettingsRow(ProviderType provider, UsageMonitorViewModel monitor, bool isFirst, bool isLast)
    {
        Provider = provider;
        _monitor = monitor;
        CanMoveUp = !isFirst;
        CanMoveDown = !isLast;
    }

    public ProviderType Provider { get; }
    public string DisplayName => Provider.DisplayName();
    public string CompanyName => $"({Provider.CompanyName()})";
    public bool CanMoveUp { get; }
    public bool CanMoveDown { get; }

    public string StatusText => _monitor.Snapshot(Provider).AuthStatus.StatusText;
    public bool IsAuthenticated => _monitor.Snapshot(Provider).AuthStatus.IsAuthenticated;
    public string LoginButtonText => IsAuthenticated ? "재인증" : "로그인";

    public bool SupportsManualToken => Provider is ProviderType.Codex or ProviderType.Claude;
    public Visibility ManualTokenVisibility => SupportsManualToken ? Visibility.Visible : Visibility.Collapsed;
    public string ManualTokenLabel => Provider == ProviderType.Claude
        ? "claude.ai sessionKey 쿠키 값"
        : "ChatGPT Access Token (/api/auth/session의 accessToken)";

    public bool IsVisible
    {
        get => _monitor.Settings.IsVisible(Provider);
        set => _monitor.Settings.SetVisible(Provider, value);
    }

    public bool IsNotified
    {
        get => _monitor.Settings.IsNotified(Provider);
        set => _monitor.Settings.SetNotified(Provider, value);
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsAuthenticated));
        OnPropertyChanged(nameof(LoginButtonText));
    }

    [RelayCommand]
    private void MoveUp() => _monitor.Settings.MoveProviderUp(Provider);

    [RelayCommand]
    private void MoveDown() => _monitor.Settings.MoveProviderDown(Provider);

    [RelayCommand]
    private void Login() => _monitor.StartLogin(Provider);

    [RelayCommand]
    private Task SignOut() => _monitor.SignOutAsync(Provider);

    /// <summary>Receives the PasswordBox (passwords can't be data-bound).</summary>
    [RelayCommand]
    private async Task SaveManualToken(object? parameter)
    {
        if (parameter is not PasswordBox box) return;
        var value = box.Password;
        switch (Provider)
        {
            case ProviderType.Codex:
                _monitor.Codex.SaveManualToken(value);
                break;
            case ProviderType.Claude:
                await _monitor.Claude.SaveManualSessionKeyAsync(value);
                break;
        }
        box.Clear();
        await _monitor.RefreshAsync(Provider);
    }
}
