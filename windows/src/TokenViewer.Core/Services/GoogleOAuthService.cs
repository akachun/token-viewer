using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TokenViewer.Core.Services;

/// <summary>Stored Google OAuth 2.0 credentials.</summary>
public sealed class GoogleOAuthCredentials
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Email { get; set; }

    [JsonIgnore]
    public bool IsAccessTokenExpired => ExpiresAt is { } exp && DateTimeOffset.Now.AddSeconds(60) >= exp;
}

public sealed class GoogleOAuthException(string message) : Exception(message)
{
    public static GoogleOAuthException MissingClientId() => new("Google OAuth Client ID를 먼저 입력해 주세요.");
    public static GoogleOAuthException ListenerFailed(string m) => new($"로컬 OAuth 콜백 서버 시작 실패: {m}");
    public static GoogleOAuthException CallbackTimeout() => new("Google 로그인 응답 시간이 초과되었습니다.");
    public static GoogleOAuthException StateMismatch() => new("보안 상태(state) 값이 일치하지 않습니다.");
    public static GoogleOAuthException AuthorizationDenied(string m) => new($"Google 로그인이 취소되었거나 거부되었습니다: {m}");
    public static GoogleOAuthException TokenExchangeFailed(string m) => new($"토큰 교환 실패: {m}");
    public static GoogleOAuthException MissingRefreshToken() => new("Refresh Token이 없어 다시 로그인해야 합니다.");
}

/// <summary>
/// Google OAuth 2.0 Authorization Code + PKCE flow via the system browser and a one-shot
/// loopback callback server on <c>http://127.0.0.1:&lt;port&gt;/oauth2callback</c>.
/// A raw <see cref="TcpListener"/> is used instead of HttpListener because http.sys requires
/// a URL ACL (admin) for non-"localhost" prefixes on Windows.
/// </summary>
public sealed class GoogleOAuthService
{
    private static readonly string[] Scopes =
    [
        "https://www.googleapis.com/auth/cloud-platform",
        "https://www.googleapis.com/auth/userinfo.email",
        "https://www.googleapis.com/auth/userinfo.profile",
    ];

    private readonly HttpClient _http;
    private readonly IBrowserLauncher _browser;

    public GoogleOAuthService(HttpClient http, IBrowserLauncher browser)
    {
        _http = http;
        _browser = browser;
    }

    public async Task<GoogleOAuthCredentials> SignInAsync(string clientId, string clientSecret, CancellationToken ct = default)
    {
        clientId = clientId.Trim();
        clientSecret = clientSecret.Trim();
        if (clientId.Length == 0) throw GoogleOAuthException.MissingClientId();

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Guid.NewGuid().ToString("N");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
        }
        catch (SocketException e)
        {
            throw GoogleOAuthException.ListenerFailed(e.Message);
        }

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirectUri = $"http://127.0.0.1:{port}/oauth2callback";

            var query = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
                ["response_type"] = "code",
                ["scope"] = string.Join(' ', Scopes),
                ["access_type"] = "offline",
                ["prompt"] = "consent",
                ["state"] = state,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
            };
            var authUrl = "https://accounts.google.com/o/oauth2/v2/auth?" +
                string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

            _browser.Open(authUrl);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(180));

            Dictionary<string, string> callback;
            try
            {
                callback = await WaitForCallbackAsync(listener, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw GoogleOAuthException.CallbackTimeout();
            }

            if (callback.TryGetValue("error", out var error)) throw GoogleOAuthException.AuthorizationDenied(error);
            if (callback.GetValueOrDefault("state") != state) throw GoogleOAuthException.StateMismatch();
            if (!callback.TryGetValue("code", out var code)) throw GoogleOAuthException.AuthorizationDenied("인증 코드가 누락되었습니다.");

            var form = new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = verifier,
            };
            if (clientSecret.Length > 0) form["client_secret"] = clientSecret;

            var token = await PostTokenRequestAsync(form, ct);
            var creds = new GoogleOAuthCredentials
            {
                ClientId = clientId,
                ClientSecret = clientSecret,
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
                ExpiresAt = token.ExpiresIn is { } s ? DateTimeOffset.Now.AddSeconds(s) : null,
            };

            try
            {
                creds.Email = await FetchUserEmailAsync(creds.AccessToken, ct);
            }
            catch (HttpRequestException)
            {
                // Email is cosmetic.
            }
            return creds;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task<GoogleOAuthCredentials> RefreshAccessTokenAsync(GoogleOAuthCredentials credentials, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(credentials.RefreshToken)) throw GoogleOAuthException.MissingRefreshToken();

        var form = new Dictionary<string, string>
        {
            ["client_id"] = credentials.ClientId,
            ["refresh_token"] = credentials.RefreshToken,
            ["grant_type"] = "refresh_token",
        };
        if (credentials.ClientSecret.Length > 0) form["client_secret"] = credentials.ClientSecret;

        var token = await PostTokenRequestAsync(form, ct);
        return new GoogleOAuthCredentials
        {
            ClientId = credentials.ClientId,
            ClientSecret = credentials.ClientSecret,
            AccessToken = token.AccessToken,
            RefreshToken = string.IsNullOrEmpty(token.RefreshToken) ? credentials.RefreshToken : token.RefreshToken,
            ExpiresAt = token.ExpiresIn is { } s ? DateTimeOffset.Now.AddSeconds(s) : credentials.ExpiresAt,
            Email = credentials.Email,
        };
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn);

    private async Task<TokenResponse> PostTokenRequestAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw GoogleOAuthException.TokenExchangeFailed(body);
        return System.Text.Json.JsonSerializer.Deserialize<TokenResponse>(body)
            ?? throw GoogleOAuthException.TokenExchangeFailed("빈 응답");
    }

    private async Task<string?> FetchUserEmailAsync(string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v2/userinfo");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>(ct);
        return json?["email"]?.GetValue<string>();
    }

    private static async Task<Dictionary<string, string>> WaitForCallbackAsync(TcpListener listener, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();

            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer, ct);
            var request = Encoding.UTF8.GetString(buffer, 0, read);

            // "GET /oauth2callback?code=...&state=... HTTP/1.1"
            var firstLine = request.Split("\r\n", 2)[0];
            var parts = firstLine.Split(' ');
            var target = parts.Length >= 2 ? parts[1] : "/";

            if (!target.StartsWith("/oauth2callback", StringComparison.Ordinal))
            {
                // e.g. /favicon.ico — ignore and keep waiting.
                await WriteResponseAsync(stream, "404 Not Found", "", ct);
                continue;
            }

            await WriteResponseAsync(stream, "200 OK", SuccessHtml, ct);
            return ParseQuery(target);
        }
    }

    internal static Dictionary<string, string> ParseQuery(string target)
    {
        var result = new Dictionary<string, string>();
        var q = target.IndexOf('?');
        if (q < 0) return result;
        foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Decode(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? "" : Decode(pair[(eq + 1)..]);
            result[key] = value;
        }
        return result;

        static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string html, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
    }

    private const string SuccessHtml = """
        <!doctype html>
        <html lang="ko"><head><meta charset="utf-8"><title>TokenViewer</title>
        <style>body{font-family:"Segoe UI",sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;background:#111;color:#fff;}
        .card{background:#1c1c1e;padding:28px 36px;border-radius:14px;text-align:center;box-shadow:0 8px 30px rgba(0,0,0,.4);}</style>
        </head><body><div class="card"><h2>TokenViewer 인증 완료</h2><p>이 창을 닫고 작업 표시줄의 TokenViewer로 돌아가세요.</p></div></body></html>
        """;

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
