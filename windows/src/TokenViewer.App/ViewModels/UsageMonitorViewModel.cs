using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TokenViewer.Core.Models;
using TokenViewer.Core.Providers;
using TokenViewer.Core.Services;

namespace TokenViewer.App.ViewModels;

/// <summary>
/// Coordinates authentication state, periodic polling, quota snapshots, and threshold alerts
/// for all providers. Lives on the UI thread.
/// </summary>
public sealed partial class UsageMonitorViewModel : ObservableObject
{
    private readonly Dictionary<ProviderType, ProviderUsageSnapshot> _snapshots =
        ProviderTypeExtensions.All.ToDictionary(p => p, ProviderUsageSnapshot.Empty);

    private readonly Dictionary<ProviderType, IUsageProvider> _providers;
    private readonly ThresholdAlertEvaluator _alerts = new();
    private readonly DispatcherTimer _pollTimer = new();

    [ObservableProperty] private bool isRefreshingAll;

    public UsageMonitorViewModel(
        AppSettings settings,
        CodexProvider codex,
        ClaudeProvider claude,
        GeminiProvider gemini)
    {
        Settings = settings;
        Codex = codex;
        Claude = claude;
        Gemini = gemini;
        _providers = new()
        {
            [ProviderType.Codex] = codex,
            [ProviderType.Claude] = claude,
            [ProviderType.Gemini] = gemini,
        };

        _pollTimer.Tick += async (_, _) => await RefreshAllAsync();
        Settings.PropertyChanged += OnSettingsChanged;
        RebuildCards();
    }

    public AppSettings Settings { get; }
    public CodexProvider Codex { get; }
    public ClaudeProvider Claude { get; }
    public GeminiProvider Gemini { get; }

    /// <summary>Cards for the popup, in the user's provider order.</summary>
    public ObservableCollection<ProviderCardViewModel> Cards { get; } = [];

    /// <summary>Raised whenever any snapshot changes (tray icons re-render on this).</summary>
    public event EventHandler? SnapshotsChanged;

    /// <summary>Invoked to deliver a threshold alert (the tray shows it as a Windows notification).</summary>
    public Action<AlertMessage>? Notify { get; set; }

    /// <summary>Invoked to open the login UI for a provider.</summary>
    public Action<ProviderType>? RequestLogin { get; set; }

    public Action? RequestSettings { get; set; }

    public ProviderUsageSnapshot Snapshot(ProviderType p) => _snapshots[p];

    public async void Start()
    {
        RestartPolling();
        if (Settings.DemoModeEnabled) ApplyDemoSnapshots();
        else await RefreshAllAsync();
    }

    public async Task RefreshAllAsync()
    {
        if (Settings.DemoModeEnabled)
        {
            ApplyDemoSnapshots();
            return;
        }
        if (IsRefreshingAll) return;
        IsRefreshingAll = true;
        try
        {
            await Task.WhenAll(ProviderTypeExtensions.All.Select(RefreshAsync));
        }
        finally
        {
            IsRefreshingAll = false;
        }
    }

    public async Task RefreshAsync(ProviderType type)
    {
        if (Settings.DemoModeEnabled)
        {
            ApplyDemoSnapshots();
            return;
        }

        var provider = _providers[type];
        SetSnapshot(Snapshot(type) with { IsLoading = true, ErrorMessage = null });

        try
        {
            if (!await provider.HasStoredSessionAsync())
            {
                SetSnapshot(SignedOut(type));
                return;
            }

            var updated = await provider.FetchUsageAsync();
            SetSnapshot(updated);
            foreach (var alert in _alerts.Evaluate(updated, Settings))
                Notify?.Invoke(alert);
        }
        catch (ProviderException e) when (e.Kind == ProviderErrorKind.NotAuthenticated)
        {
            SetSnapshot(SignedOut(type));
        }
        catch (ProviderException e) when (e.Kind == ProviderErrorKind.SessionExpired)
        {
            SetSnapshot(Snapshot(type) with
            {
                AuthStatus = AuthStatus.Expired(e.Detail ?? ""),
                IsLoading = false,
                ErrorMessage = "세션이 만료되었습니다. 다시 로그인해 주세요.",
            });
        }
        catch (Exception e)
        {
            var current = Snapshot(type);
            SetSnapshot(current with
            {
                AuthStatus = current.AuthStatus.State == AuthState.Authenticating ? AuthStatus.Authenticated(null) : current.AuthStatus,
                IsLoading = false,
                ErrorMessage = e.Message,
            });
        }
    }

    public void StartLogin(ProviderType type)
    {
        SetSnapshot(Snapshot(type) with { AuthStatus = AuthStatus.Authenticating, ErrorMessage = null });
        RequestLogin?.Invoke(type);
    }

    public async void HandleLoginCompleted(ProviderType type) => await RefreshAsync(type);

    public async void HandleLoginCancelled(ProviderType type)
    {
        if (await _providers[type].HasStoredSessionAsync()) return;
        var current = Snapshot(type);
        if (current.AuthStatus.State == AuthState.Authenticating)
            SetSnapshot(current with { AuthStatus = AuthStatus.SignedOut });
    }

    public async Task SignOutAsync(ProviderType type)
    {
        await _providers[type].SignOutAsync();
        SetSnapshot(ProviderUsageSnapshot.Empty(type));
    }

    public void ResetAlertHistory() => _alerts.Reset();

    public void ApplyDemoSnapshots()
    {
        foreach (var (type, snap) in DemoData.Snapshots(DateTimeOffset.Now))
            _snapshots[type] = snap;
        RebuildCards();
        SnapshotsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static ProviderUsageSnapshot SignedOut(ProviderType type) =>
        ProviderUsageSnapshot.Empty(type) with { LastUpdated = DateTimeOffset.Now };

    private void SetSnapshot(ProviderUsageSnapshot snapshot)
    {
        _snapshots[snapshot.Provider] = snapshot;
        RebuildCards();
        SnapshotsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildCards()
    {
        var order = Settings.OrderedVisibleProviders;
        if (order.Count == 0) order = Settings.ProviderOrder;

        Cards.Clear();
        foreach (var p in order)
            Cards.Add(new ProviderCardViewModel(_snapshots[p], Settings, this));
    }

    private async void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.RefreshIntervalSeconds):
                RestartPolling();
                break;
            case nameof(AppSettings.DemoModeEnabled):
                if (Settings.DemoModeEnabled) ApplyDemoSnapshots();
                else await RefreshAllAsync();
                break;
            case nameof(AppSettings.ProviderOrder):
            case nameof(AppSettings.VisibleProviders):
            case nameof(AppSettings.MetricMode):
            case nameof(AppSettings.WarningThresholdPercent):
            case nameof(AppSettings.ColorMode):
                RebuildCards();
                break;
        }
    }

    private void RestartPolling()
    {
        _pollTimer.Stop();
        _pollTimer.Interval = TimeSpan.FromSeconds(Settings.RefreshIntervalSeconds);
        _pollTimer.Start();
    }
}
