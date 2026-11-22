using System.Diagnostics;
using System.Windows;
using TokenViewer.Core.Providers;
using TokenViewer.Core.Services;

namespace TokenViewer.App.Views;

public partial class GeminiAuthWindow : Window
{
    private readonly GeminiProvider _gemini;
    private bool _completed;

    public event Action? Completed;
    public event Action? Cancelled;

    public GeminiAuthWindow(GeminiProvider gemini)
    {
        InitializeComponent();
        _gemini = gemini;

        if (gemini.SourceMode == GeminiSourceMode.OAuth) Tabs.SelectedIndex = 1;
        if (gemini.LoadCredentials() is { ClientId.Length: > 0 } creds) ClientIdBox.Text = creds.ClientId;

        Loaded += async (_, _) => await RefreshAntigravityStatusAsync();
        Closed += (_, _) =>
        {
            if (!_completed) Cancelled?.Invoke();
        };
    }

    private async Task<bool> RefreshAntigravityStatusAsync()
    {
        AntigravityStatus.Text = "실행 상태 확인 중...";
        var running = await _gemini.Antigravity.IsRunningAsync();
        AntigravityStatus.Text = running
            ? "● Antigravity IDE 실행 중 — 연결할 수 있습니다."
            : "○ Antigravity IDE가 실행 중이 아닙니다. 연결해 두면 IDE를 실행할 때 자동으로 동기화됩니다.";
        return running;
    }

    private async void OnConnectAntigravity(object sender, RoutedEventArgs e)
    {
        ConnectAntigravityButton.IsEnabled = false;
        try
        {
            await RefreshAntigravityStatusAsync();
            // Enabling works even while the IDE is closed: the provider then waits for it.
            _gemini.EnableAntigravityMode();
            Finish();
        }
        finally
        {
            ConnectAntigravityButton.IsEnabled = true;
        }
    }

    private async void OnRecheckAntigravity(object sender, RoutedEventArgs e) => await RefreshAntigravityStatusAsync();

    private void OnDownloadAntigravity(object sender, RoutedEventArgs e) => Open("https://antigravity.google");

    private async void OnGoogleSignIn(object sender, RoutedEventArgs e)
    {
        GoogleSignInButton.IsEnabled = false;
        ShowResult("브라우저 로그인 대기 중...", isError: false);
        try
        {
            var creds = await _gemini.OAuth.SignInAsync(ClientIdBox.Text, ClientSecretBox.Password);
            _gemini.SaveCredentials(creds);
            Finish();
        }
        catch (Exception ex) when (ex is GoogleOAuthException or HttpRequestException)
        {
            ShowResult(ex.Message, isError: true);
        }
        finally
        {
            GoogleSignInButton.IsEnabled = true;
        }
    }

    private void OnSaveToken(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AccessTokenBox.Password))
        {
            ShowResult("Access Token을 입력해 주세요.", isError: true);
            return;
        }
        _gemini.SaveManualAccessToken(AccessTokenBox.Password, EmailBox.Text);
        Finish();
    }

    private void OnOpenConsentScreen(object sender, RoutedEventArgs e) => Open("https://console.cloud.google.com/apis/credentials/consent");
    private void OnOpenCredentials(object sender, RoutedEventArgs e) => Open("https://console.cloud.google.com/apis/credentials");
    private void OnOpenPlayground(object sender, RoutedEventArgs e) => Open("https://developers.google.com/oauthplayground/");
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void Finish()
    {
        _completed = true;
        Completed?.Invoke();
        Close();
    }

    private void ShowResult(string text, bool isError)
    {
        ResultText.Text = text;
        ResultText.Opacity = isError ? 1.0 : 0.7;
        ResultText.Visibility = Visibility.Visible;
    }

    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
