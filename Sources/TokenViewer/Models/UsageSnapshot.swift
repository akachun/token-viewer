import Foundation
import SwiftUI

/// Represents a single rate-limit / quota window (e.g., 5-hour session, 7-day weekly, or model bucket).
struct UsageWindow: Identifiable, Codable, Equatable {
    let id: String
    let label: String
    /// Remaining fraction in range 0.0 ... 1.0
    let remainingFraction: Double
    /// Exact timestamp when this window resets, if provided by the backend.
    let resetsAt: Date?

    var clampedRemainingFraction: Double {
        min(max(remainingFraction, 0.0), 1.0)
    }

    var remainingPercent: Int {
        Int((clampedRemainingFraction * 100.0).rounded())
    }

    var usedFraction: Double {
        1.0 - clampedRemainingFraction
    }

    var usedPercent: Int {
        100 - remainingPercent
    }

    func displayPercent(for metric: QuotaMetricMode) -> Int {
        switch metric {
        case .remaining: return remainingPercent
        case .used: return usedPercent
        }
    }

    func displayFraction(for metric: QuotaMetricMode) -> Double {
        switch metric {
        case .remaining: return clampedRemainingFraction
        case .used: return usedFraction
        }
    }

    /// Short human-readable countdown string (e.g., "2h 14m", "3d 5h", "<1m").
    func formattedTimeUntilReset(from now: Date = Date(), compact: Bool = false) -> String? {
        guard let resetsAt else { return nil }
        let seconds = Int(resetsAt.timeIntervalSince(now))
        if seconds <= 0 {
            return compact ? "0m" : "곧 리셋"
        }
        let days = seconds / 86400
        let hours = (seconds % 86400) / 3600
        let minutes = (seconds % 3600) / 60

        if days > 0 {
            return compact ? "\(days)d" : "\(days)일 \(hours)시간"
        } else if hours > 0 {
            return compact ? "\(hours)h" : "\(hours)시간 \(minutes)분"
        } else if minutes > 0 {
            return compact ? "\(minutes)m" : "\(minutes)분"
        } else {
            return compact ? "<1m" : "1분 미만"
        }
    }
}

enum AuthStatus: Equatable {
    case signedOut
    case authenticating
    case authenticated(accountLabel: String?)
    case expired(reason: String)

    var isAuthenticated: Bool {
        if case .authenticated = self { return true }
        return false
    }

    var statusText: String {
        switch self {
        case .signedOut: return "미인증"
        case .authenticating: return "인증 중..."
        case .authenticated(let label):
            if let label, !label.isEmpty {
                return label
            }
            return "연결됨"
        case .expired(let reason): return "세션 만료 (\(reason))"
        }
    }
}

/// Complete snapshot of one AI provider's current authentication and quota state.
struct ProviderUsageSnapshot: Equatable {
    let provider: ProviderType
    var authStatus: AuthStatus
    var planName: String?
    var windows: [UsageWindow]
    var lastUpdated: Date?
    var errorMessage: String?
    var isLoading: Bool

    static func empty(for provider: ProviderType) -> ProviderUsageSnapshot {
        ProviderUsageSnapshot(
            provider: provider,
            authStatus: .signedOut,
            planName: nil,
            windows: [],
            lastUpdated: nil,
            errorMessage: nil,
            isLoading: false
        )
    }

    /// Selects the window to display in the compact status bar.
    func primaryDisplayWindow(strategy: WindowSelectionStrategy) -> UsageWindow? {
        guard !windows.isEmpty else { return nil }
        switch strategy {
        case .primary:
            return windows.first
        case .mostConstrained:
            return windows.min(by: { $0.clampedRemainingFraction < $1.clampedRemainingFraction })
        }
    }

    /// Health status color based on remaining quota percentage.
    func healthColor(for window: UsageWindow?, warningThreshold: Int) -> Color {
        guard let window else { return .secondary }
        let rem = window.remainingPercent
        if rem <= max(warningThreshold / 2, 10) {
            return Color(red: 0.94, green: 0.27, blue: 0.27) // Critical red
        } else if rem <= warningThreshold {
            return Color(red: 0.96, green: 0.62, blue: 0.04) // Warning amber
        } else {
            return Color(red: 0.13, green: 0.77, blue: 0.37) // Healthy green
        }
    }
}
