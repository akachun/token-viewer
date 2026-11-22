using System.Text.Json;

namespace TokenViewer.Core.Services;

/// <summary>Result of a same-origin fetch executed inside a real browser (WebView2) context.</summary>
public sealed record WebFetchResponse(int Status, string Body);

public sealed record WebCookie(
    string Name,
    string Value,
    string Domain,
    string Path = "/",
    bool IsSecure = true,
    DateTimeOffset? Expires = null);

public sealed class WebSessionException(string message) : Exception(message);

/// <summary>
/// Authenticated browser session used for chatgpt.com / claude.ai APIs. Requests run inside a
/// real Chromium context so they carry the logged-in cookies and pass Cloudflare checks.
/// </summary>
public interface IWebSession
{
    Task<WebFetchResponse> FetchAsync(
        Uri origin,
        string pathOrUrl,
        string method = "GET",
        IReadOnlyDictionary<string, string>? headers = null,
        string? body = null);

    /// <summary>Returns all cookies whose domain contains <paramref name="domainSubstring"/>.</summary>
    Task<IReadOnlyList<WebCookie>> GetCookiesAsync(string domainSubstring);

    Task SetCookieAsync(WebCookie cookie);

    /// <summary>Deletes cookies for the given domains and drops cached background pages for them.</summary>
    Task ClearSessionAsync(IEnumerable<string> domains);
}

/// <summary>Secret storage (Windows Credential Manager in the app; in-memory in tests).</summary>
public interface ICredentialStore
{
    void Save(string account, string secret);
    string? Load(string account);
    void Delete(string account);
}

public static class CredentialStoreExtensions
{
    public static void SaveJson<T>(this ICredentialStore store, string account, T value) =>
        store.Save(account, JsonSerializer.Serialize(value, JsonDefaults.Options));

    public static T? LoadJson<T>(this ICredentialStore store, string account) where T : class
    {
        var raw = store.Load(account);
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(raw, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Non-secret app state (session-verified flags, caches). Replaces macOS UserDefaults.</summary>
public interface IKeyValueStore
{
    bool GetBool(string key);
    void SetBool(string key, bool value);
    string? GetString(string key);
    void SetString(string key, string value);
    T? Get<T>(string key) where T : class;
    void Set<T>(string key, T value);
    void Remove(string key);
}

/// <summary>Opens a URL in the user's default browser.</summary>
public interface IBrowserLauncher
{
    void Open(string url);
}
