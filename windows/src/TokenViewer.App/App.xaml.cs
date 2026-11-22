using System.ComponentModel;
using System.Windows;
using TokenViewer.App.Services;
using TokenViewer.App.Tray;
using TokenViewer.App.ViewModels;
using TokenViewer.App.Views;
using TokenViewer.Core.Models;
using TokenViewer.Core.Providers;
using TokenViewer.Core.Services;

namespace TokenViewer.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\TokenViewer.SingleInstance";

    private Mutex? _singleInstance;
    private WebView2SessionBridge? _bridge;
    private JsonKeyValueStore? _state;
    private UsageMonitorViewModel? _monitor;
    private TrayIconManager? _tray;
    private PopupWindow? _popup;
    private SettingsWindow? _settingsWindow;
    private Window? _loginWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("TokenViewer가 이미 실행 중입니다. 작업 표시줄 트레이를 확인해 주세요.", "TokenViewer",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var settings = new SettingsStore(AppPaths.SettingsFile).Load();
        _state = new JsonKeyValueStore(AppPaths.StateFile);
        var credentials = new CredentialManagerStore();
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        _bridge = new WebView2SessionBridge(AppPaths.WebViewDataDir);
        await _bridge.InitializeAsync();
        if (_bridge.InitializationError is { } error)
        {
            MessageBox.Show(
                $"{error}\n\nCodex·Claude 조회에는 WebView2가 필요합니다. https://go.microsoft.com/fwlink/p/?LinkId=2124703 에서 Evergreen 런타임을 설치한 뒤 다시 실행해 주세요. Gemini는 계속 사용할 수 있습니다.",
                "TokenViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var antigravity = new AntigravityQuotaService(
            new WindowsLanguageServerProbe(),
            WindowsLanguageServerProbe.CreateLocalhostHttpClient());
        var oauth = new GoogleOAuthService(http, new ShellBrowserLauncher());

        _monitor = new UsageMonitorViewModel(
            settings,
            new CodexProvider(_bridge, credentials, _state),
            new ClaudeProvider(_bridge, credentials, _state),
            new GeminiProvider(credentials, _state, http, oauth, antigravity));

        _popup = new PopupWindow(_monitor, OpenSettings, Quit);
        _tray = new TrayIconManager(_monitor, () => _popup.Toggle(), OpenSettings, Quit);

        _monitor.Notify = msg => _tray.ShowNotification(msg);
        _monitor.RequestLogin = PresentLogin;
        _monitor.RequestSettings = OpenSettings;

        SyncLaunchAtLogin(settings);
        settings.PropertyChanged += OnSettingsChanged;

        _monitor.Start();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.LaunchAtLogin) && sender is AppSettings s)
            SyncLaunchAtLogin(s);
    }

    private static void SyncLaunchAtLogin(AppSettings settings)
    {
        try
        {
            if (StartupService.IsEnabled() != settings.LaunchAtLogin)
                StartupService.SetEnabled(settings.LaunchAtLogin);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Registry write blocked by policy; leave the setting as-is.
        }
    }

    private void OpenSettings()
    {
        if (_monitor is null || _tray is null) return;
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(new SettingsViewModel(_monitor, _tray.ShowNotification));
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void PresentLogin(ProviderType provider)
    {
        if (_monitor is null || _bridge is null || _state is null) return;
        _loginWindow?.Close();

        switch (provider)
        {
            case ProviderType.Codex:
            case ProviderType.Claude:
            {
                if (!_bridge.IsAvailable)
                {
                    MessageBox.Show(_bridge.InitializationError ?? "WebView2를 사용할 수 없습니다.", "TokenViewer",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    _monitor.HandleLoginCancelled(provider);
                    return;
                }
                var win = new WebLoginWindow(provider, _bridge, _state);
                win.Completed += () => _monitor.HandleLoginCompleted(provider);
                win.Cancelled += () => _monitor.HandleLoginCancelled(provider);
                ShowLoginWindow(win);
                break;
            }

            case ProviderType.Gemini:
            {
                var win = new GeminiAuthWindow(_monitor.Gemini);
                win.Completed += () => _monitor.HandleLoginCompleted(provider);
                win.Cancelled += () => _monitor.HandleLoginCancelled(provider);
                ShowLoginWindow(win);
                break;
            }
        }
    }

    private void ShowLoginWindow(Window win)
    {
        _loginWindow = win;
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_loginWindow, win)) _loginWindow = null;
        };
        win.Show();
        win.Activate();
    }

    private void Quit()
    {
        _tray?.Dispose();
        _bridge?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
