using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TokenViewer.App.ViewModels;
using TokenViewer.Core.Models;
using TokenViewer.Core.Services;

namespace TokenViewer.App.Views;

public sealed record WidgetRow(string Name, string Value, string? Reset, Brush DotBrush, Brush BarBrush, double BarWidth, string Tooltip);

/// <summary>
/// Small always-on-top card with each visible provider's key quota. Drag to move (position is saved),
/// double-click for the detailed popup, right-click for options.
/// </summary>
public partial class FloatingWidget : Window
{
    private const double BarMaxWidth = 54;
    private const double ScreenMargin = 12;

    private readonly UsageMonitorViewModel _monitor;
    private readonly AppSettings _settings;
    private readonly Action _openPopup;
    private readonly Action _openSettings;
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _placed;

    public FloatingWidget(UsageMonitorViewModel monitor, Action openPopup, Action openSettings)
    {
        InitializeComponent();
        _monitor = monitor;
        _settings = monitor.Settings;
        _openPopup = openPopup;
        _openSettings = openSettings;

        Opacity = _settings.WidgetOpacity;
        _monitor.SnapshotsChanged += (_, _) => Render();
        _settings.PropertyChanged += OnSettingsChanged;
        _countdownTimer.Tick += (_, _) => Render();

        SourceInitialized += (_, _) => HideFromAltTab();
        ContentRendered += (_, _) => PlaceWindow();
        SizeChanged += (_, _) => { if (_placed && _settings.WidgetLeft is null) PlaceDefault(); };

        Render();
    }

    /// <summary>Shows or hides the widget to match <see cref="AppSettings.WidgetEnabled"/>.</summary>
    public void SyncVisibility()
    {
        if (_settings.WidgetEnabled)
        {
            if (!IsVisible) Show();
            _countdownTimer.Start();
            Render();
        }
        else
        {
            _countdownTimer.Stop();
            Hide();
        }
    }

    private void Render()
    {
        var providers = _settings.OrderedVisibleProviders;
        RowsList.ItemsSource = providers.Select(p => BuildRow(_monitor.Snapshot(p))).ToList();
        EmptyText.Visibility = providers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private WidgetRow BuildRow(ProviderUsageSnapshot snapshot)
    {
        var p = snapshot.Provider;
        var window = snapshot.PrimaryDisplayWindow(_settings.WindowStrategy);
        var tooltip = TrayText.Tooltip(snapshot, _settings);

        string value;
        if (snapshot.AuthStatus.State == AuthState.SignedOut) value = "로그인";
        else if (snapshot.AuthStatus.State == AuthState.Expired) value = "만료";
        else if (window is null) value = snapshot.IsLoading ? "…" : "–";
        else value = $"{window.DisplayPercent(_settings.MetricMode)}%";

        var color = _settings.ColorMode switch
        {
            ColorMode.BrandColor => p.AccentColor(),
            ColorMode.Monochrome => new RgbColor(0xE6, 0xE6, 0xE6),
            _ => HealthColors.For(window, _settings.WarningThresholdPercent),
        };
        var brush = Brush(color);
        var dot = _settings.ColorMode == ColorMode.HealthColor ? Brush(p.AccentColor()) : brush;
        var barWidth = window is null ? 0 : BarMaxWidth * window.DisplayFraction(_settings.MetricMode);

        return new WidgetRow(p.DisplayName(), value, window?.FormatTimeUntilReset(compact: true), dot, brush, barWidth, tooltip);
    }

    private static SolidColorBrush Brush(RgbColor c)
    {
        var b = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    // MARK: - Position

    private void PlaceWindow()
    {
        if (_settings.WidgetLeft is { } left && _settings.WidgetTop is { } top && IsOnScreen(left, top))
        {
            Left = left;
            Top = top;
        }
        else
        {
            PlaceDefault();
        }
        _placed = true;
    }

    /// <summary>Bottom-right (or nearest corner to the taskbar) of the primary work area.</summary>
    private void PlaceDefault()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Left > 0 ? work.Left + ScreenMargin : work.Right - ActualWidth - ScreenMargin;
        Top = work.Top > 0 ? work.Top + ScreenMargin : work.Bottom - ActualHeight - ScreenMargin;
    }

    /// <summary>False when the saved position is on a monitor that is no longer connected.</summary>
    private bool IsOnScreen(double left, double top)
    {
        var screen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var width = Math.Max(ActualWidth, 40);
        var height = Math.Max(ActualHeight, 20);
        return screen.IntersectsWith(new Rect(left, top, width, height))
               && screen.Contains(new Point(left + width / 2, top + Math.Min(height, 20) / 2));
    }

    private void OnCardMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _openPopup();
            return;
        }
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return; // mouse already released
        }
        _settings.WidgetLeft = Math.Round(Left);
        _settings.WidgetTop = Math.Round(Top);
    }

    // MARK: - Context menu

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await _monitor.RefreshAllAsync();

    private void OnOpenPopupClick(object sender, RoutedEventArgs e) => _openPopup();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _openSettings();

    private void OnOpacityClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } &&
            double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            _settings.WidgetOpacity = v;
    }

    private void OnResetPositionClick(object sender, RoutedEventArgs e)
    {
        _settings.WidgetLeft = null;
        _settings.WidgetTop = null;
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => _settings.WidgetEnabled = false;

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.WidgetEnabled):
                SyncVisibility();
                break;
            case nameof(AppSettings.WidgetOpacity):
                Opacity = _settings.WidgetOpacity;
                break;
            case nameof(AppSettings.WidgetLeft) or nameof(AppSettings.WidgetTop):
                if (_settings.WidgetLeft is null || _settings.WidgetTop is null) PlaceDefault();
                break;
            default:
                Render();
                break;
        }
    }

    private bool _allowClose;

    /// <summary>Closes the window for real (app exit) without touching the saved setting.</summary>
    public void CloseForExit()
    {
        _allowClose = true;
        _countdownTimer.Stop();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose) return;
        // Alt+F4 on the widget hides it (and turns the setting off) instead of destroying the window.
        e.Cancel = true;
        _settings.WidgetEnabled = false;
    }

    // MARK: - Win32

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>Tool-window style keeps the widget out of Alt+Tab.</summary>
    private void HideFromAltTab()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_TOOLWINDOW));
    }
}
