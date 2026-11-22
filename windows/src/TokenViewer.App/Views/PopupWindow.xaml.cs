using System.ComponentModel;
using System.Windows;
using TokenViewer.App.ViewModels;
using TokenViewer.Core.Models;

namespace TokenViewer.App.Views;

/// <summary>Flyout shown above the notification area when a tray icon is clicked.</summary>
public partial class PopupWindow : Window
{
    private readonly UsageMonitorViewModel _monitor;
    private readonly Action _openSettings;
    private readonly Action _quit;
    private DateTime _hiddenAt = DateTime.MinValue;

    public PopupWindow(UsageMonitorViewModel monitor, Action openSettings, Action quit)
    {
        InitializeComponent();
        _monitor = monitor;
        _openSettings = openSettings;
        _quit = quit;

        CardsList.ItemsSource = monitor.Cards;
        UpdateDemoBadge();
        monitor.Settings.PropertyChanged += OnSettingsChanged;
        monitor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UsageMonitorViewModel.IsRefreshingAll))
                RefreshAllButton.IsEnabled = !monitor.IsRefreshingAll;
        };

        Deactivated += (_, _) => HidePopup();
        SizeChanged += (_, _) => PositionNearTray();
    }

    public void Toggle()
    {
        // Clicking the tray icon deactivates the popup first; don't immediately reopen it.
        if (IsVisible || (DateTime.Now - _hiddenAt).TotalMilliseconds < 250)
        {
            HidePopup();
            return;
        }
        Show();
        PositionNearTray();
        Activate();
    }

    private void HidePopup()
    {
        if (!IsVisible) return;
        _hiddenAt = DateTime.Now;
        Hide();
    }

    /// <summary>Places the popup at the corner of the work area closest to the taskbar.</summary>
    private void PositionNearTray()
    {
        var work = SystemParameters.WorkArea;
        const double margin = 12;

        var taskbarAtTop = work.Top > 0;
        var taskbarAtLeft = work.Left > 0;

        Left = taskbarAtLeft ? work.Left + margin : work.Right - ActualWidth - margin;
        Top = taskbarAtTop ? work.Top + margin : work.Bottom - ActualHeight - margin;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.DemoModeEnabled)) UpdateDemoBadge();
    }

    private void UpdateDemoBadge() =>
        DemoBadge.Visibility = _monitor.Settings.DemoModeEnabled ? Visibility.Visible : Visibility.Collapsed;

    private async void OnRefreshAllClick(object sender, RoutedEventArgs e) => await _monitor.RefreshAllAsync();

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        HidePopup();
        _openSettings();
    }

    private void OnQuitClick(object sender, RoutedEventArgs e) => _quit();

    protected override void OnClosing(CancelEventArgs e)
    {
        // The popup is reused; closing just hides it (Alt+F4).
        e.Cancel = true;
        HidePopup();
    }
}
