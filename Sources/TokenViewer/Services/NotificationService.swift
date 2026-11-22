import Foundation
import UserNotifications

/// Sends macOS local notifications when an AI provider's remaining quota drops below the user's threshold.
@MainActor
final class NotificationService {
    static let shared = NotificationService()

    /// Tracks alerted keys ("provider:windowId:resetEpoch") so the same quota window doesn't spam alerts.
    private var alertedKeys: Set<String> = []
    private var isAvailable: Bool {
        Bundle.main.bundlePath.hasSuffix(".app")
    }

    private init() {}

    func requestAuthorizationIfNeeded() {
        guard isAvailable else { return }
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound, .badge]) { _, error in
            if let error {
                print("[NotificationService] Authorization error: \(error.localizedDescription)")
            }
        }
    }

    func evaluateThresholdAlert(
        snapshot: ProviderUsageSnapshot,
        settings: AppSettings
    ) {
        guard settings.notificationsEnabled,
              settings.notifiedProviders.contains(snapshot.provider),
              snapshot.authStatus.isAuthenticated else {
            return
        }

        let threshold = settings.warningThresholdPercent
        for window in snapshot.windows {
            let resetBucket = Int(window.resetsAt?.timeIntervalSince1970 ?? 0) / 300
            let key = "\(snapshot.provider.rawValue):\(window.id):\(resetBucket)"

            if window.remainingPercent <= threshold {
                guard !alertedKeys.contains(key) else { continue }
                alertedKeys.insert(key)

                let resetText = window.formattedTimeUntilReset().map { " · 리셋까지 \($0)" } ?? ""
                sendNotification(
                    title: "\(snapshot.provider.displayName) 잔여량 부족 (\(window.remainingPercent)%)",
                    body: "\(window.label) 잔여량이 설정 임계치(\(threshold)%) 이하로 떨어졌습니다.\(resetText)"
                )
            } else if window.remainingPercent > threshold + 5 {
                // Reset dedup key if quota recovered (e.g., window rolled over)
                alertedKeys.remove(key)
            }
        }
    }

    func sendTestNotification() {
        sendNotification(
            title: "TokenViewer 알림 테스트",
            body: "AI 구독 잔여량이 임계치 미만일 때 이와 같이 알림이 표시됩니다."
        )
    }

    func resetAlertHistory() {
        alertedKeys.removeAll()
    }

    private func sendNotification(title: String, body: String) {
        guard isAvailable else {
            print("[NotificationService] (Unbundled CLI run) \(title): \(body)")
            return
        }

        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.sound = .default

        let request = UNNotificationRequest(
            identifier: UUID().uuidString,
            content: content,
            trigger: nil
        )
        UNUserNotificationCenter.current().add(request) { error in
            if let error {
                print("[NotificationService] Failed to deliver notification: \(error.localizedDescription)")
            }
        }
    }
}
