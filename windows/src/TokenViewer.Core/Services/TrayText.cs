using TokenViewer.Core.Models;

namespace TokenViewer.Core.Services;

/// <summary>Text shown in tray icons and tooltips.</summary>
public static class TrayText
{
    /// <summary>Windows truncates NOTIFYICONDATA tooltips at 127 characters.</summary>
    public const int MaxTooltipLength = 127;

    /// <summary>Center text for an icon: the percentage, or "–" when there is nothing to show.</summary>
    public static string IconValue(ProviderUsageSnapshot snapshot, AppSettings settings)
    {
        var window = snapshot.PrimaryDisplayWindow(settings.WindowStrategy);
        if (!snapshot.AuthStatus.IsAuthenticated || window is null) return "–";
        return window.DisplayPercent(settings.MetricMode).ToString();
    }

    public static string Tooltip(ProviderUsageSnapshot snapshot, AppSettings settings, DateTimeOffset? now = null)
    {
        var name = snapshot.Provider.DisplayName();
        string text;

        if (snapshot.AuthStatus.State is AuthState.SignedOut)
        {
            text = $"{name} · 로그인 필요";
        }
        else if (snapshot.AuthStatus.State is AuthState.Expired)
        {
            text = $"{name} · 세션 만료";
        }
        else if (snapshot.Windows.Count == 0)
        {
            text = snapshot.ErrorMessage is { } err ? $"{name} · {err}" : $"{name} · 표시할 쿼터 없음";
        }
        else
        {
            var lines = new List<string> { snapshot.PlanName is { } plan ? $"{name} ({plan})" : name };
            var metric = settings.MetricMode.ShortTitle();
            foreach (var w in snapshot.Windows.Take(3))
            {
                var reset = w.FormatTimeUntilReset(now, compact: true) is { } r ? $" · {r}" : "";
                lines.Add($"{w.Label} {w.DisplayPercent(settings.MetricMode)}% {metric}{reset}");
            }
            text = string.Join('\n', lines);
        }

        return text.Length <= MaxTooltipLength ? text : text[..(MaxTooltipLength - 1)] + "…";
    }
}
