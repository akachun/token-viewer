using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Win32;
using TokenViewer.App.Services;
using TokenViewer.App.ViewModels;
using TokenViewer.Core.Models;
using TokenViewer.Core.Services;

namespace TokenViewer.App.Tray;

/// <summary>
/// Owns one notification-area icon per visible provider (the macOS menu bar shows them side by
/// side; Windows tray icons are fixed-size squares, so each provider gets its own icon).
/// Windows decides the on-screen order; icons are created in the user's provider order.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly UsageMonitorViewModel _monitor;
    private readonly AppSettings _settings;
    private readonly Action _togglePopup;
    private readonly Action _openSettings;
    private readonly Action _quit;
    private readonly List<(ProviderType Provider, TaskbarIcon Icon)> _icons = [];
    private readonly DispatcherTimer _tooltipTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _lightTaskbar = TaskbarTheme.IsLight();

    public TrayIconManager(UsageMonitorViewModel monitor, Action togglePopup, Action openSettings, Action quit)
    {
        _monitor = monitor;
        _settings = monitor.Settings;
        _togglePopup = togglePopup;
        _openSettings = openSettings;
        _quit = quit;

        _monitor.SnapshotsChanged += (_, _) => UpdateAll();
        _settings.PropertyChanged += OnSettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _tooltipTimer.Tick += (_, _) => UpdateAll(); // keeps "resets in" countdowns fresh
        _tooltipTimer.Start();

        Rebuild();
    }

    public void ShowNotification(AlertMessage message)
    {
        if (_icons.Count == 0) return;
        _icons[0].Icon.ShowNotification(message.Title, message.Body, NotificationIcon.Warning);
    }

    private void Rebuild()
    {
        foreach (var (_, icon) in _icons) icon.Dispose();
        _icons.Clear();

        var providers = _settings.OrderedVisibleProviders;
        // Always keep at least one icon so the app stays reachable.
        if (providers.Count == 0) providers = [_settings.ProviderOrder[0]];

        foreach (var p in providers)
        {
            var icon = new TaskbarIcon
            {
                ContextMenu = BuildContextMenu(),
                NoLeftClickDelay = true,
            };
            icon.TrayLeftMouseUp += (_, _) => _togglePopup();
            icon.ForceCreate(enablesEfficiencyMode: false);
            _icons.Add((p, icon));
        }
        UpdateAll();
    }

    private void UpdateAll()
    {
        foreach (var (p, icon) in _icons)
        {
            var snapshot = _monitor.Snapshot(p);
            var old = icon.Icon;
            icon.Icon = TrayIconRenderer.RenderIcon(snapshot, _settings, _lightTaskbar);
            icon.ToolTipText = TrayText.Tooltip(snapshot, _settings);
            old?.Dispose();
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("지금 새로고침", async () => await _monitor.RefreshAllAsync()));
        menu.Items.Add(MenuItem("설정...", _openSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("TokenViewer 종료", _quit));
        return menu;

        static MenuItem MenuItem(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => onClick();
            return item;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.ProviderOrder) or nameof(AppSettings.VisibleProviders))
            Rebuild();
        else
            UpdateAll();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        // Theme changes arrive on a system thread.
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var light = TaskbarTheme.IsLight();
            if (light == _lightTaskbar) return;
            _lightTaskbar = light;
            UpdateAll();
        });
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _tooltipTimer.Stop();
        foreach (var (_, icon) in _icons) icon.Dispose();
        _icons.Clear();
    }
}
