using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using TokenViewer.Core.Models;

namespace TokenViewer.Core.Services;

public static class AppPaths
{
    /// <summary>%APPDATA%\TokenViewer — settings and state (roams with the user profile).</summary>
    public static string RoamingDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenViewer");

    /// <summary>%LOCALAPPDATA%\TokenViewer — machine-local data such as the WebView2 profile.</summary>
    public static string LocalDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokenViewer");

    public static string SettingsFile => Path.Combine(RoamingDir, "settings.json");
    public static string StateFile => Path.Combine(RoamingDir, "state.json");
    public static string WebViewDataDir => Path.Combine(LocalDir, "WebView2");
}

/// <summary>Loads <see cref="AppSettings"/> from JSON and saves it on every property change.</summary>
public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string path) => _path = path;

    public AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonDefaults.Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            settings = new AppSettings();
        }

        settings.Normalize();
        settings.PropertyChanged += OnChanged;
        return settings;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonDefaults.Options));
        File.Move(tmp, _path, overwrite: true);
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not AppSettings s) return;
        try
        {
            Save(s);
        }
        catch (IOException)
        {
            // Best effort: the next change retries.
        }
    }
}

/// <summary>Small JSON-file backed key/value store, written through on every change.</summary>
public sealed class JsonKeyValueStore : IKeyValueStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly JsonObject _root;

    public JsonKeyValueStore(string path)
    {
        _path = path;
        try
        {
            _root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            _root = [];
        }
    }

    public bool GetBool(string key)
    {
        lock (_lock)
            return _root[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    public void SetBool(string key, bool value) => Write(key, JsonValue.Create(value));

    public string? GetString(string key)
    {
        lock (_lock)
            return _root[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    public void SetString(string key, string value) => Write(key, JsonValue.Create(value));

    public T? Get<T>(string key) where T : class
    {
        lock (_lock)
        {
            if (_root[key] is not { } node) return null;
            try
            {
                return node.Deserialize<T>(JsonDefaults.Options);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public void Set<T>(string key, T value) =>
        Write(key, JsonSerializer.SerializeToNode(value, JsonDefaults.Options));

    public void Remove(string key)
    {
        lock (_lock)
        {
            if (_root.Remove(key)) Flush();
        }
    }

    private void Write(string key, JsonNode? node)
    {
        lock (_lock)
        {
            _root[key] = node;
            Flush();
        }
    }

    private void Flush()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, _root.ToJsonString(JsonDefaults.Options));
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _items = new();
    public void Save(string account, string secret) => _items[account] = secret;
    public string? Load(string account) => _items.GetValueOrDefault(account);
    public void Delete(string account) => _items.Remove(account);
}

public sealed class ShellBrowserLauncher : IBrowserLauncher
{
    public void Open(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
}
