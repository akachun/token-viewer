using TokenViewer.Core.Models;

namespace TokenViewer.Core.Services;

public sealed record AlertMessage(string Title, string Body);

/// <summary>
/// Decides when to alert that a provider's remaining quota dropped below the threshold.
/// Keys "provider:windowId:resetBucket" deduplicate so the same window doesn't alert repeatedly.
/// </summary>
public sealed class ThresholdAlertEvaluator
{
    private readonly HashSet<string> _alertedKeys = [];

    public IReadOnlyList<AlertMessage> Evaluate(ProviderUsageSnapshot snapshot, AppSettings settings, DateTimeOffset? now = null)
    {
        if (!settings.NotificationsEnabled || !settings.IsNotified(snapshot.Provider) || !snapshot.AuthStatus.IsAuthenticated)
            return [];

        var threshold = settings.WarningThresholdPercent;
        var alerts = new List<AlertMessage>();

        foreach (var window in snapshot.Windows)
        {
            var resetBucket = (window.ResetsAt?.ToUnixTimeSeconds() ?? 0) / 300;
            var key = $"{snapshot.Provider.Key()}:{window.Id}:{resetBucket}";

            if (window.RemainingPercent <= threshold)
            {
                if (!_alertedKeys.Add(key)) continue;
                var reset = window.FormatTimeUntilReset(now) is { } r ? $" · 리셋까지 {r}" : "";
                alerts.Add(new AlertMessage(
                    $"{snapshot.Provider.DisplayName()} 잔여량 부족 ({window.RemainingPercent}%)",
                    $"{window.Label} 잔여량이 설정 임계치({threshold}%) 이하로 떨어졌습니다.{reset}"));
            }
            else if (window.RemainingPercent > threshold + 5)
            {
                // Quota recovered (e.g. the window rolled over)
                _alertedKeys.Remove(key);
            }
        }
        return alerts;
    }

    public void Reset() => _alertedKeys.Clear();

    public static AlertMessage TestMessage { get; } = new(
        "TokenViewer 알림 테스트",
        "AI 구독 잔여량이 임계치 미만일 때 이와 같이 알림이 표시됩니다.");
}
