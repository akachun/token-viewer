using System.Globalization;
using System.Text.Json.Nodes;
using TokenViewer.Core.Models;

namespace TokenViewer.Core.Providers;

public enum ProviderErrorKind
{
    NotAuthenticated,
    SessionExpired,
    Network,
    Parsing,
    RateLimited,
}

public sealed class ProviderException : Exception
{
    public ProviderErrorKind Kind { get; }
    public string? Detail { get; }

    private ProviderException(ProviderErrorKind kind, string? detail)
        : base(BuildMessage(kind, detail))
    {
        Kind = kind;
        Detail = detail;
    }

    public static ProviderException NotAuthenticated() => new(ProviderErrorKind.NotAuthenticated, null);
    public static ProviderException SessionExpired(string detail) => new(ProviderErrorKind.SessionExpired, detail);
    public static ProviderException Network(string detail) => new(ProviderErrorKind.Network, detail);
    public static ProviderException Parsing(string detail) => new(ProviderErrorKind.Parsing, detail);
    public static ProviderException RateLimited() => new(ProviderErrorKind.RateLimited, null);

    private static string BuildMessage(ProviderErrorKind kind, string? detail) => kind switch
    {
        ProviderErrorKind.NotAuthenticated => "로그인이 필요합니다.",
        ProviderErrorKind.SessionExpired => $"세션이 만료되었습니다. 다시 로그인해 주세요. ({detail})",
        ProviderErrorKind.Network => $"네트워크 오류: {detail}",
        ProviderErrorKind.Parsing => $"응답 파싱 오류: {detail}",
        ProviderErrorKind.RateLimited => "요청이 많아 일시적으로 제한되었습니다 (429). 잠시 후 다시 시도합니다.",
        _ => detail ?? "",
    };

    /// <summary>Maps an HTTP status from a web-session API call to the macOS app's error semantics.</summary>
    public static void ThrowIfFailed(int status)
    {
        if (status is 401 or 403) throw SessionExpired($"HTTP {status}");
        if (status == 429) throw RateLimited();
        if (status is < 200 or > 299) throw Network($"HTTP {status}");
    }
}

/// <summary>Common interface implemented by the Codex, Claude, and Gemini usage monitors.</summary>
public interface IUsageProvider
{
    ProviderType Type { get; }
    Task<bool> HasStoredSessionAsync();
    Task<ProviderUsageSnapshot> FetchUsageAsync();
    Task SignOutAsync();
}

public static class DateParser
{
    /// <summary>Parses ISO-8601 timestamps with or without fractional seconds / offsets.</summary>
    public static DateTimeOffset? ParseIso8601(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
            out var result)
            ? result
            : null;
    }
}

/// <summary>Lenient accessors for loosely-typed JSON (numbers may arrive as strings in protobuf JSON).</summary>
internal static class Json
{
    public static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static double? Num(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<string>(out var s) &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    public static JsonObject? Obj(JsonNode? node) => node as JsonObject;
    public static JsonArray? Arr(JsonNode? node) => node as JsonArray;
}
