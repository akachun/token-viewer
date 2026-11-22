using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TokenViewer.App.Services;
using TokenViewer.Core.Models;
using TokenViewer.Core.Providers;
using TokenViewer.Core.Services;

namespace TokenViewer.App.Views;

/// <summary>
/// In-app WebView2 login for Codex (chatgpt.com) and Claude (claude.ai). Shares the background
/// session's WebView2 profile, so cookies set here are used by later quota fetches.
/// </summary>
public partial class WebLoginWindow : Window
{
    private readonly ProviderType _provider;
    private readonly WebView2SessionBridge _bridge;
    private readonly IKeyValueStore _state;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _completed;
    private bool _checking;

    public event Action? Completed;
    public event Action? Cancelled;

    public WebLoginWindow(ProviderType provider, WebView2SessionBridge bridge, IKeyValueStore state)
    {
        InitializeComponent();
        _provider = provider;
        _bridge = bridge;
        _state = state;

        Title = $"{provider.DisplayName()} 로그인 — TokenViewer";
        HeaderText.Text = $"{provider.DisplayName()} 웹 로그인";
        var c = provider.AccentColor();
        AccentDot.Fill = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));

        _poll.Tick += async (_, _) => await CheckLoginStatusAsync(manual: false);
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _poll.Stop();
            if (!_completed) Cancelled?.Invoke();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Browser.EnsureCoreWebView2Async(_bridge.Environment);
        }
        catch (Exception ex)
        {
            SetStatus($"WebView2를 시작할 수 없습니다: {ex.Message}", isError: true);
            VerifyButton.IsEnabled = false;
            return;
        }

        Browser.CoreWebView2.Navigate(_provider.LoginUrl().AbsoluteUri);

        // Give the login page a moment before polling, like the macOS app.
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (!_completed && IsLoaded) _poll.Start();
    }

    private async void OnVerifyClick(object sender, RoutedEventArgs e) => await CheckLoginStatusAsync(manual: true);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private async Task CheckLoginStatusAsync(bool manual)
    {
        if (_completed || _checking || Browser.CoreWebView2 is null) return;
        _checking = true;
        if (manual)
        {
            VerifyButton.IsEnabled = false;
            SetStatus("로그인 세션 확인 중...", isError: false);
        }

        try
        {
            var wv = Browser.CoreWebView2;
            Uri.TryCreate(wv.Source, UriKind.Absolute, out var current);
            var host = current?.Host.ToLowerInvariant() ?? "";

            switch (_provider)
            {
                case ProviderType.Codex:
                {
                    // Only query /api/auth/session on chatgpt.com (not auth.openai.com)
                    if (!host.Contains("chatgpt.com"))
                    {
                        if (manual) SetStatus("ChatGPT 메인 화면으로 이동된 후 '인증 확인'을 눌러주세요.", isError: true);
                        return;
                    }
                    var resp = await _bridge.FetchOnAsync(wv, "/api/auth/session");
                    if (resp.Status == 200 && CodexProvider.ParseSession(resp.Body).AccessToken is not null)
                    {
                        FinishSuccess();
                        return;
                    }
                    if (manual) SetStatus("아직 로그인 세션이 감지되지 않았습니다. 로그인 완료 후 다시 시도해 주세요.", isError: true);
                    break;
                }

                case ProviderType.Claude:
                {
                    if (!host.Contains("claude.ai"))
                    {
                        if (manual) SetStatus("claude.ai 로그인 완료 후 다시 시도해 주세요.", isError: true);
                        return;
                    }

                    var cookies = await _bridge.GetCookiesAsync("claude.ai");
                    var hasSessionCookie = cookies.Any(c => c.Name == "sessionKey" && c.Value.Length > 0);
                    var onLoginPath = current?.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase) == true;

                    // Automatic polling only probes the API once the session cookie exists.
                    if (!manual && !(hasSessionCookie && !onLoginPath)) return;

                    var resp = await _bridge.FetchOnAsync(wv, "/api/organizations", headers: ClaudeProvider.ApiHeaders);
                    if (resp.Status == 200 && resp.Body.TrimStart().StartsWith('['))
                    {
                        FinishSuccess();
                        return;
                    }
                    if (manual) SetStatus($"세션 미감지 (HTTP {resp.Status}). 로그인 완료 후 다시 눌러주세요.", isError: true);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is WebSessionException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            if (manual) SetStatus($"확인 실패: {ex.Message}", isError: true);
        }
        finally
        {
            _checking = false;
            if (manual) VerifyButton.IsEnabled = true;
        }
    }

    private void FinishSuccess()
    {
        _completed = true;
        _poll.Stop();
        // Mark the session verified so providers don't rely solely on cookie-name heuristics.
        _state.SetBool($"tv.webSessionVerified.{_provider.Key()}", true);
        Completed?.Invoke();
        Close();
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B))
            : TryFindResource("TextFillColorPrimaryBrush") as Brush ?? SystemColors.ControlTextBrush;
        StatusText.Opacity = isError ? 1.0 : 0.7;
    }
}
