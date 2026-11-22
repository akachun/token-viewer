using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TokenViewer.Core.Models;
using TokenViewer.Core.Providers;

namespace TokenViewer.Core.Services;

/// <summary>Parsed quota result from the local Antigravity <c>language_server</c> (<c>GetUserStatus</c>).</summary>
public sealed record AntigravityQuotaResult(
    string? AccountLabel,
    string? PlanName,
    IReadOnlyList<UsageWindow> Windows,
    DateTimeOffset FetchedAt);

public enum AntigravityErrorKind
{
    NotRunning,
    NoListeningPorts,
    RpcFailed,
}

public sealed class AntigravityException : Exception
{
    public AntigravityErrorKind Kind { get; }

    public AntigravityException(AntigravityErrorKind kind, string? detail = null) : base(kind switch
    {
        AntigravityErrorKind.NotRunning => "Antigravity IDE가 실행 중이 아닙니다. Antigravity를 실행하면 자동으로 잔여량을 동기화합니다.",
        AntigravityErrorKind.NoListeningPorts => "Antigravity 로컬 서버 포트를 찾을 수 없습니다.",
        _ => $"Antigravity 로컬 상태 조회 실패: {detail}",
    })
    {
        Kind = kind;
    }
}

public sealed record LanguageServerProcess(int Pid, string CsrfToken);

/// <summary>
/// OS-specific discovery of the Antigravity language server. On Windows this uses WMI
/// (process command lines) and <c>GetExtendedTcpTable</c> (listening ports), replacing
/// <c>ps</c> / <c>lsof</c> on macOS.
/// </summary>
public interface ILanguageServerProbe
{
    LanguageServerProcess? FindLanguageServer();
    IReadOnlyList<int> FindListeningPorts(int pid);
}

/// <summary>
/// Queries the local Antigravity <c>language_server</c> over localhost Connect RPC
/// (no OAuth setup or billing; works with personal Google AI plans).
/// </summary>
public sealed class AntigravityQuotaService
{
    private static readonly Regex CsrfRegex = new(@"--csrf_token[=\s]+""?([^\s""]+)", RegexOptions.Compiled);

    private readonly ILanguageServerProbe _probe;
    private readonly HttpClient _http;

    /// <param name="localhostHttp">
    /// An HttpClient that accepts the language server's self-signed certificate for 127.0.0.1 only,
    /// with a short timeout (~3s).
    /// </param>
    public AntigravityQuotaService(ILanguageServerProbe probe, HttpClient localhostHttp)
    {
        _probe = probe;
        _http = localhostHttp;
    }

    /// <summary>Extracts the CSRF token from a language_server command line, if present.</summary>
    public static string? ExtractCsrfToken(string commandLine)
    {
        if (!commandLine.Contains("language_server", StringComparison.OrdinalIgnoreCase)) return null;
        var m = CsrfRegex.Match(commandLine);
        return m.Success ? m.Groups[1].Value : null;
    }

    public Task<bool> IsRunningAsync() => Task.Run(() => _probe.FindLanguageServer() is not null);

    public async Task<AntigravityQuotaResult> FetchQuotaAsync()
    {
        var process = await Task.Run(_probe.FindLanguageServer)
            ?? throw new AntigravityException(AntigravityErrorKind.NotRunning);

        var ports = await Task.Run(() => _probe.FindListeningPorts(process.Pid));
        if (ports.Count == 0) throw new AntigravityException(AntigravityErrorKind.NoListeningPorts);

        Exception lastError = new AntigravityException(AntigravityErrorKind.RpcFailed, "응답하는 포트가 없습니다.");
        foreach (var port in ports)
        {
            foreach (var scheme in new[] { "https", "http" })
            {
                try
                {
                    return await CallGetUserStatusAsync(scheme, port, process.CsrfToken);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or AntigravityException or JsonException)
                {
                    lastError = e;
                }
            }
        }
        throw lastError;
    }

    private async Task<AntigravityQuotaResult> CallGetUserStatusAsync(string scheme, int port, string csrfToken)
    {
        var url = $"{scheme}://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/GetUserStatus";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                """{"metadata":{"ideName":"antigravity","extensionName":"antigravity","locale":"en"}}""",
                Encoding.UTF8,
                "application/json"),
        };
        req.Headers.Add("Connect-Protocol-Version", "1");
        req.Headers.Add("X-Codeium-Csrf-Token", csrfToken);

        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            throw new AntigravityException(AntigravityErrorKind.RpcFailed, $"HTTP {(int)resp.StatusCode}");

        return ParseUserStatus(await resp.Content.ReadAsStringAsync(), DateTimeOffset.Now);
    }

    public static AntigravityQuotaResult ParseUserStatus(string json, DateTimeOffset now)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        if (Json.Obj(root?["userStatus"]) is not { } userStatus)
            throw new AntigravityException(AntigravityErrorKind.RpcFailed, "userStatus 필드를 찾을 수 없습니다.");

        var email = Json.Str(userStatus["email"]) ?? Json.Str(userStatus["name"]);
        var planStatus = Json.Obj(userStatus["planStatus"]);
        var planInfo = Json.Obj(planStatus?["planInfo"]);
        var planName = Json.Str(planInfo?["planName"]) ?? Json.Str(planInfo?["teamsTier"]) ?? "Antigravity";

        var windows = new List<UsageWindow>();
        if (Json.Arr(Json.Obj(userStatus["cascadeModelConfigData"])?["clientModelConfigs"]) is { } configs)
            windows = ExtractGeminiWindows(configs.OfType<JsonObject>());

        // Fallback to prompt credits if model configs didn't contain quotaInfo
        if (windows.Count == 0 &&
            Json.Num(planStatus?["availablePromptCredits"]) is { } available &&
            Json.Num(planInfo?["monthlyPromptCredits"]) is double monthly && monthly > 0)
        {
            windows.Add(new UsageWindow("antigravity-credits", "Prompt Credits", Math.Clamp(available / monthly, 0, 1), null));
        }

        return new AntigravityQuotaResult(email, planName, windows, now);
    }

    /// <summary>Groups Gemini buckets (Pro / Flash / other) while avoiding duplicate shared pools.</summary>
    internal static List<UsageWindow> ExtractGeminiWindows(IEnumerable<JsonObject> configs)
    {
        (string Label, double Rem, DateTimeOffset? Reset)? pro = null;
        (string Label, double Rem, DateTimeOffset? Reset)? flash = null;
        var fallback = new List<UsageWindow>();

        foreach (var config in configs)
        {
            if (Json.Str(config["label"]) is not { } label || Json.Obj(config["quotaInfo"]) is not { } quota) continue;

            // Some responses omit remainingFraction when 0.0 (exhausted)
            var rem = Json.Num(quota["remainingFraction"]) ?? 0.0;
            var reset = DateParser.ParseIso8601(Json.Str(quota["resetTime"]));
            var lower = label.ToLowerInvariant();

            if (lower.Contains("gemini"))
            {
                if (lower.Contains("flash"))
                {
                    if (flash is null || rem < flash.Value.Rem) flash = ("Gemini Flash", rem, reset);
                }
                else if (pro is null || rem < pro.Value.Rem)
                {
                    pro = (lower.Contains("pro") ? "Gemini Pro" : label, rem, reset);
                }
            }
            else
            {
                fallback.Add(new UsageWindow(label, label, rem, reset));
            }
        }

        var result = new List<UsageWindow>();
        if (pro is { } p) result.Add(new UsageWindow("ag-gemini-pro", p.Label, p.Rem, p.Reset));
        if (flash is { } f) result.Add(new UsageWindow("ag-gemini-flash", f.Label, f.Rem, f.Reset));

        if (result.Count == 0)
        {
            // No explicit "Gemini" label: show up to 2 unique buckets
            var seen = new HashSet<string>();
            foreach (var w in fallback)
            {
                if (seen.Add($"{w.RemainingPercent}-{w.ResetsAt?.ToUnixTimeSeconds() ?? 0}")) result.Add(w);
                if (result.Count >= 2) break;
            }
        }
        return result;
    }
}
