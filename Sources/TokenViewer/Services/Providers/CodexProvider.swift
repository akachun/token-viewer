import Foundation

/// Monitors OpenAI / ChatGPT Codex subscription quota via in-app web session (`chatgpt.com`)
/// or an optional manually saved access token in Keychain.
@MainActor
final class CodexProvider: UsageProvider {
    let type: ProviderType = .codex

    private let origin = URL(string: "https://chatgpt.com")!
    private let manualTokenAccount = "codex.manualAccessToken"

    func saveManualToken(_ token: String) {
        let trimmed = token.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty {
            KeychainStore.delete(account: manualTokenAccount)
        } else {
            KeychainStore.save(trimmed, account: manualTokenAccount)
        }
    }

    func loadManualToken() -> String? {
        KeychainStore.load(account: manualTokenAccount)
    }

    private let verifiedFlagKey = "tv.webSessionVerified.codex"

    func hasStoredSession() async -> Bool {
        if let token = loadManualToken(), !token.isEmpty {
            return true
        }
        if UserDefaults.standard.bool(forKey: verifiedFlagKey) {
            return true
        }
        let cookies = await WebSessionBridge.shared.cookies(matching: "chatgpt.com")
        return cookies.contains {
            $0.name.contains("next-auth.session-token") || $0.name.contains("session-token")
        }
    }

    func signOut() async {
        UserDefaults.standard.removeObject(forKey: verifiedFlagKey)
        KeychainStore.delete(account: manualTokenAccount)
        await WebSessionBridge.shared.clearSession(domains: ["chatgpt.com", "openai.com", "auth.openai.com"])
    }

    func fetchUsage() async throws -> ProviderUsageSnapshot {
        // 1. Acquire access token either from manual Keychain token or from ChatGPT web session
        var accessToken: String? = loadManualToken()
        var accountEmail: String?

        if accessToken == nil || accessToken?.isEmpty == true {
            let sessionResp = try await WebSessionBridge.shared.fetch(
                origin: origin,
                pathOrURL: "/api/auth/session"
            )
            guard sessionResp.status == 200 else {
                throw ProviderError.sessionExpired("HTTP \(sessionResp.status)")
            }
            guard let sessionData = sessionResp.body.data(using: .utf8),
                  let sessionJSON = try? JSONSerialization.jsonObject(with: sessionData) as? [String: Any],
                  let token = sessionJSON["accessToken"] as? String, !token.isEmpty else {
                throw ProviderError.notAuthenticated
            }
            accessToken = token
            if let user = sessionJSON["user"] as? [String: Any] {
                accountEmail = user["email"] as? String ?? user["name"] as? String
            }
        }

        guard let token = accessToken else {
            throw ProviderError.notAuthenticated
        }

        // 2. Call ChatGPT/Codex usage endpoint inside the same browser context
        let usageResp = try await WebSessionBridge.shared.fetch(
            origin: origin,
            pathOrURL: "/backend-api/wham/usage",
            headers: [
                "Authorization": "Bearer \(token)",
                "Accept": "application/json"
            ]
        )

        if usageResp.status == 401 || usageResp.status == 403 {
            throw ProviderError.sessionExpired("HTTP \(usageResp.status)")
        }
        if usageResp.status == 429 {
            throw ProviderError.rateLimited
        }
        guard (200...299).contains(usageResp.status) else {
            throw ProviderError.networkError("HTTP \(usageResp.status)")
        }

        guard let data = usageResp.body.data(using: .utf8) else {
            throw ProviderError.parsingError("빈 응답")
        }

        return try parseWhamUsage(data: data, accountEmail: accountEmail)
    }

    // MARK: - Response Parsing

    private struct WhamUsageResponse: Decodable {
        let planType: String?
        let rateLimit: RateLimit?

        enum CodingKeys: String, CodingKey {
            case planType = "plan_type"
            case rateLimit = "rate_limit"
        }

        struct RateLimit: Decodable {
            let allowed: Bool?
            let limitReached: Bool?
            let primaryWindow: Window?
            let secondaryWindow: Window?

            enum CodingKeys: String, CodingKey {
                case allowed
                case limitReached = "limit_reached"
                case primaryWindow = "primary_window"
                case secondaryWindow = "secondary_window"
            }
        }

        struct Window: Decodable {
            let usedPercent: Double
            let limitWindowSeconds: Int?
            let resetAfterSeconds: Int?
            let resetAt: TimeInterval?

            enum CodingKeys: String, CodingKey {
                case usedPercent = "used_percent"
                case limitWindowSeconds = "limit_window_seconds"
                case resetAfterSeconds = "reset_after_seconds"
                case resetAt = "reset_at"
            }
        }
    }

    private func parseWhamUsage(data: Data, accountEmail: String?) throws -> ProviderUsageSnapshot {
        let decoded: WhamUsageResponse
        do {
            decoded = try JSONDecoder().decode(WhamUsageResponse.self, from: data)
        } catch {
            throw ProviderError.parsingError(error.localizedDescription)
        }

        var windows: [UsageWindow] = []

        if let primary = decoded.rateLimit?.primaryWindow {
            let label = windowLabel(seconds: primary.limitWindowSeconds, fallback: "5시간 세션")
            let resetDate = resolveResetDate(resetAt: primary.resetAt, resetAfter: primary.resetAfterSeconds)
            let remaining = max(0.0, min(1.0, 1.0 - (primary.usedPercent / 100.0)))
            windows.append(UsageWindow(
                id: "primary",
                label: label,
                remainingFraction: remaining,
                resetsAt: resetDate
            ))
        }

        if let secondary = decoded.rateLimit?.secondaryWindow {
            let label = windowLabel(seconds: secondary.limitWindowSeconds, fallback: "주간 한도")
            let resetDate = resolveResetDate(resetAt: secondary.resetAt, resetAfter: secondary.resetAfterSeconds)
            let remaining = max(0.0, min(1.0, 1.0 - (secondary.usedPercent / 100.0)))
            windows.append(UsageWindow(
                id: "secondary",
                label: label,
                remainingFraction: remaining,
                resetsAt: resetDate
            ))
        }

        let planDisplay = decoded.planType.map { "ChatGPT \($0.capitalized)" }

        return ProviderUsageSnapshot(
            provider: .codex,
            authStatus: .authenticated(accountLabel: accountEmail ?? planDisplay),
            planName: planDisplay,
            windows: windows,
            lastUpdated: Date(),
            errorMessage: nil,
            isLoading: false
        )
    }

    private func windowLabel(seconds: Int?, fallback: String) -> String {
        guard let seconds, seconds > 0 else { return fallback }
        if seconds >= 86400 * 6 {
            let days = seconds / 86400
            return "\(days)일 주간 한도"
        } else if seconds >= 3600 {
            let hours = seconds / 3600
            return "\(hours)시간 윈도우"
        }
        return fallback
    }

    private func resolveResetDate(resetAt: TimeInterval?, resetAfter: Int?) -> Date? {
        if let resetAt, resetAt > 0 {
            return Date(timeIntervalSince1970: resetAt)
        }
        if let resetAfter, resetAfter > 0 {
            return Date().addingTimeInterval(TimeInterval(resetAfter))
        }
        return nil
    }
}
