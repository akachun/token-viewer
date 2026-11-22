using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using TokenViewer.Core.Services;

namespace TokenViewer.App.Services;

/// <summary>
/// Hosts hidden WebView2 controllers that share one persistent profile, so authenticated web API
/// calls (chatgpt.com, claude.ai) run with real browser cookies and a Chromium TLS fingerprint,
/// which avoids Cloudflare blocks. The visible login window uses the same environment, so cookies
/// set during login are immediately available here.
///
/// All members must be called on the WPF UI thread.
/// </summary>
public sealed class WebView2SessionBridge : IWebSession, IDisposable
{
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    private readonly string _userDataFolder;
    private readonly Dictionary<string, CoreWebView2Controller> _controllers = new(StringComparer.OrdinalIgnoreCase);
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _cookieController;
    private Window? _hostWindow;
    private IntPtr _hostHwnd;

    public WebView2SessionBridge(string userDataFolder) => _userDataFolder = userDataFolder;

    public bool IsAvailable => _environment is not null;

    /// <summary>Set when the WebView2 runtime could not be started (e.g. not installed).</summary>
    public string? InitializationError { get; private set; }

    public CoreWebView2Environment Environment =>
        _environment ?? throw new WebSessionException(InitializationError ?? "WebView2가 초기화되지 않았습니다.");

    public async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(_userDataFolder);
            _environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);

            // An invisible, never-shown window provides the parent HWND for background controllers.
            _hostWindow = new Window
            {
                Width = 0,
                Height = 0,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            _hostHwnd = new WindowInteropHelper(_hostWindow).EnsureHandle();

            // Always-available controller used for cookie access before any site is loaded.
            _cookieController = await CreateControllerAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _environment = null;
            InitializationError = "Microsoft Edge WebView2 런타임이 설치되어 있지 않습니다.";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _environment = null;
            InitializationError = $"WebView2 초기화 실패: {e.Message}";
        }
    }

    private CoreWebView2CookieManager CookieManager =>
        (_cookieController ?? throw new WebSessionException(InitializationError ?? "WebView2가 초기화되지 않았습니다."))
            .CoreWebView2.CookieManager;

    // MARK: - IWebSession

    public async Task<WebFetchResponse> FetchAsync(
        Uri origin,
        string pathOrUrl,
        string method = "GET",
        IReadOnlyDictionary<string, string>? headers = null,
        string? body = null)
    {
        var controller = await EnsureLoadedAsync(origin);
        return await FetchOnAsync(controller.CoreWebView2, pathOrUrl, method, headers, body);
    }

    public async Task<IReadOnlyList<WebCookie>> GetCookiesAsync(string domainSubstring)
    {
        if (!IsAvailable) return [];
        // An empty URI returns every cookie in the profile.
        var cookies = await CookieManager.GetCookiesAsync("");
        return cookies
            .Where(c => c.Domain.Contains(domainSubstring, StringComparison.OrdinalIgnoreCase))
            .Select(c => new WebCookie(c.Name, c.Value, c.Domain, c.Path, c.IsSecure,
                c.IsSession ? null : new DateTimeOffset(c.Expires)))
            .ToList();
    }

    public Task SetCookieAsync(WebCookie cookie)
    {
        var mgr = CookieManager;
        var c = mgr.CreateCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
        c.IsSecure = cookie.IsSecure;
        if (cookie.Expires is { } exp) c.Expires = exp.LocalDateTime;
        mgr.AddOrUpdateCookie(c);
        return Task.CompletedTask;
    }

    public async Task ClearSessionAsync(IEnumerable<string> domains)
    {
        var list = domains.ToList();
        if (IsAvailable)
        {
            var mgr = CookieManager;
            var cookies = await mgr.GetCookiesAsync("");
            foreach (var c in cookies.Where(c => list.Any(d => c.Domain.Contains(d, StringComparison.OrdinalIgnoreCase))))
                mgr.DeleteCookie(c);
        }

        foreach (var key in _controllers.Keys.Where(k => list.Any(d => k.Contains(d, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _controllers[key].Close();
            _controllers.Remove(key);
        }
    }

    // MARK: - Fetch inside a page

    /// <summary>
    /// Runs <c>fetch(..., {credentials: 'include'})</c> inside <paramref name="webView"/>'s page.
    /// <c>ExecuteScriptAsync</c> does not await promises, so the result comes back through
    /// <c>chrome.webview.postMessage</c>, matched by a per-request id.
    /// </summary>
    public async Task<WebFetchResponse> FetchOnAsync(
        CoreWebView2 webView,
        string pathOrUrl,
        string method = "GET",
        IReadOnlyDictionary<string, string>? headers = null,
        string? body = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<WebFetchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw;
            try
            {
                raw = e.TryGetWebMessageAsString();
            }
            catch (ArgumentException)
            {
                return; // not a string message
            }

            JsonObject? msg;
            try
            {
                msg = JsonNode.Parse(raw) as JsonObject;
            }
            catch (JsonException)
            {
                return; // a message from the page itself
            }

            if (msg?["__tv"]?.GetValue<string>() != id) return;

            var ok = msg["ok"]?.GetValue<bool>() == true;
            var status = msg["status"]?.GetValue<int>() ?? -1;
            var text = msg["body"]?.GetValue<string>() ?? "";
            if (ok) tcs.TrySetResult(new WebFetchResponse(status, text));
            else tcs.TrySetException(new WebSessionException($"웹 요청 실패: {text}"));
        }

        var args = JsonSerializer.Serialize(new
        {
            id,
            url = pathOrUrl,
            method,
            headers = headers ?? new Dictionary<string, string>(),
            body = body ?? "",
        });

        var script = $$"""
            (async () => {
              const a = {{args}};
              const post = (o) => window.chrome.webview.postMessage(JSON.stringify(Object.assign({ __tv: a.id }, o)));
              try {
                const init = { method: a.method, credentials: 'include', headers: a.headers };
                if (a.body) init.body = a.body;
                const resp = await fetch(a.url, init);
                const text = await resp.text();
                post({ ok: true, status: resp.status, body: text });
              } catch (err) {
                post({ ok: false, status: -1, body: String(err && err.message ? err.message : err) });
              }
            })();
            """;

        webView.WebMessageReceived += OnMessage;
        try
        {
            await webView.ExecuteScriptAsync(script);
            var finished = await Task.WhenAny(tcs.Task, Task.Delay(FetchTimeout));
            if (finished != tcs.Task) throw new WebSessionException("웹 요청 시간이 초과되었습니다.");
            return await tcs.Task;
        }
        finally
        {
            webView.WebMessageReceived -= OnMessage;
        }
    }

    // MARK: - Background controllers

    private async Task<CoreWebView2Controller> CreateControllerAsync()
    {
        var controller = await Environment.CreateCoreWebView2ControllerAsync(_hostHwnd);
        controller.IsVisible = false;
        controller.Bounds = new System.Drawing.Rectangle(0, 0, 800, 600);
        controller.CoreWebView2.Settings.AreDevToolsEnabled = false;
        controller.CoreWebView2.Settings.IsStatusBarEnabled = false;
        // Background pages must never open popups or dialogs.
        controller.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
        controller.CoreWebView2.ScriptDialogOpening += (_, e) => e.Accept();
        return controller;
    }

    private async Task<CoreWebView2Controller> EnsureLoadedAsync(Uri origin)
    {
        var hostKey = origin.Host;
        if (_controllers.TryGetValue(hostKey, out var existing) &&
            Uri.TryCreate(existing.CoreWebView2.Source, UriKind.Absolute, out var current) &&
            current.Host.Contains(hostKey, StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        if (existing is null)
        {
            existing = await CreateControllerAsync();
            _controllers[hostKey] = existing;
        }

        await NavigateAndWaitAsync(existing.CoreWebView2, origin.AbsoluteUri);
        return existing;
    }

    /// <summary>
    /// Navigates and waits for completion, giving up after 15s (a Cloudflare interstitial may keep
    /// the page loading; the same-origin fetch can still succeed once cookies are set).
    /// </summary>
    private static async Task NavigateAndWaitAsync(CoreWebView2 webView, string url)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult(e.IsSuccess);

        webView.NavigationCompleted += OnCompleted;
        try
        {
            webView.Navigate(url);
            await Task.WhenAny(tcs.Task, Task.Delay(NavigationTimeout));
        }
        finally
        {
            webView.NavigationCompleted -= OnCompleted;
        }
    }

    public void Dispose()
    {
        foreach (var c in _controllers.Values) c.Close();
        _controllers.Clear();
        _cookieController?.Close();
        _hostWindow?.Close();
    }
}
