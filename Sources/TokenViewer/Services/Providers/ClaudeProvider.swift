import Foundation
import WebKit

/// Monitors Anthropic Claude subscription quota via in-app `claude.ai` web session
/// or an optional manually saved `sessionKey` cookie in Keychain.
@MainActor
final class ClaudeProvider: UsageProvider {
    let type: ProviderType = .claude

    private let origin = URL(string: "https://claude.ai")!
    private let manualSessionKeyAccount = "claude.manualSessionKey"

    func saveManualSessionKey(_ key: String) async {
        let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty {
            KeychainStore.delete(account: manualSessionKeyAccount)
            return
        }
        KeychainStore.save(trimmed, account: manualSessionKeyAccount)
        await injectSessionKeyCookie(trimmed)
    }

    func loadManualSessionKey() -> String? {
        KeychainStore.load(account: manualSessionKeyAccount)
    }

    private let verifiedFlagKey = "tv.webSessionVerified.claude"

    func hasStoredSession() async -> Bool {
        if let key = loadManualSessionKey(), !key.isEmpty {
            return true
        }
        if UserDefaults.standard.bool(forKey: verifiedFlagKey) {
            return true
        }
        let cookies = await WebSessionBridge.shared.cookies(matching: "claude.ai")
        return cookies.contains { $0.name == "sessionKey" && !$0.value.isEmpty }
    }

    func signOut() async {
        UserDefaults.standard.removeObject(forKey: verifiedFlagKey)
        KeychainStore.delete(account: manualSessionKeyAccount)
        await WebSessionBridge.shared.clearSession(domains: ["claude.ai", "anthropic.com"])
    }

    func fetchUsage() async throws -> ProviderUsageSnapshot {
        if let manualKey = loadManualSessionKey(), !manualKey.isEmpty {
            await injectSessionKeyCookie(manualKey)
        }

        // 1. Fetch organizations to discover org UUID and account/plan metadata
        let orgResp = try await WebSessionBridge.shared.fetch(
            origin: origin,
            pathOrURL: "/api/organizations",
            headers: [
                "Accept": "application/json",
                "anthropic-client-platform": "web_claude_ai"
            ]
        )

        if orgResp.status == 401 || orgResp.status == 403 {
            throw ProviderError.sessionExpired("HTTP \(orgResp.status)")
        }
        if orgResp.status == 429 {
            throw ProviderError.rateLimited
        }
        guard (200...299).contains(orgResp.status),
              let orgData = orgResp.body.data(using: .utf8) else {
            throw ProviderError.networkError("HTTP \(orgResp.status)")
        }

        let orgs = try parseOrganizations(data: orgData)
        guard let primaryOrg = orgs.first(where: { $0.capabilities?.contains("chat") == true }) ?? orgs.first else {
            throw ProviderError.notAuthenticated
        }

        // 2. Fetch usage windows for the active organization
        let usageResp = try await WebSessionBridge.shared.fetch(
            origin: origin,
            pathOrURL: "/api/organizations/\(primaryOrg.uuid)/usage",
            headers: [
                "Accept": "application/json",
                "anthropic-client-platform": "web_claude_ai"
            ]
        )

        if usageResp.status == 401 || usageResp.status == 403 {
            throw ProviderError.sessionExpired("HTTP \(usageResp.status)")
        }
        if usageResp.status == 429 {
            throw ProviderError.rateLimited
        }
        guard (200...299).contains(usageResp.status),
              let usageData = usageResp.body.data(using: .utf8) else {
            throw ProviderError.networkError("HTTP \(usageResp.status)")
        }

        return try parseClaudeUsage(data: usageData, org: primaryOrg)
    }

    // MARK: - Helpers

    private func injectSessionKeyCookie(_ value: String) async {
        let props: [HTTPCookiePropertyKey: Any] = [
            .domain: ".claude.ai",
            .path: "/",
            .name: "sessionKey",
            .value: value,
            .secure: "TRUE",
            .expires: Date().addingTimeInterval(60 * 60 * 24 * 30)
        ]
        if let cookie = HTTPCookie(properties: props) {
            await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
                WebSessionBridge.shared.dataStore.httpCookieStore.setCookie(cookie) {
                    cont.resume()
                }
            }
        }
    }

    private struct Organization: Decodable {
        let uuid: String
        let name: String?
        let rateLimitTier: String?
        let capabilities: [String]?

        enum CodingKeys: String, CodingKey {
            case uuid
            case name
            case rateLimitTier = "rate_limit_tier"
            case capabilities
        }
    }

    private struct ClaudeUsageResponse: Decodable {
        let fiveHour: Bucket?
        let sevenDay: Bucket?
        let sevenDaySonnet: Bucket?
        let sevenDayOpus: Bucket?

        enum CodingKeys: String, CodingKey {
            case fiveHour = "five_hour"
            case sevenDay = "seven_day"
            case sevenDaySonnet = "seven_day_sonnet"
            case sevenDayOpus = "seven_day_opus"
        }

        struct Bucket: Decodable {
            let utilization: Double?
            let resetsAt: String?

            enum CodingKeys: String, CodingKey {
                case utilization
                case resetsAt = "resets_at"
            }
        }
    }

    private func parseOrganizations(data: Data) throws -> [Organization] {
        do {
            return try JSONDecoder().decode([Organization].self, from: data)
        } catch {
            throw ProviderError.notAuthenticated
        }
    }

    private func parseClaudeUsage(data: Data, org: Organization) throws -> ProviderUsageSnapshot {
        let decoded: ClaudeUsageResponse
        do {
            decoded = try JSONDecoder().decode(ClaudeUsageResponse.self, from: data)
        } catch {
            throw ProviderError.parsingError(error.localizedDescription)
        }

        let allUtils = [
            decoded.fiveHour?.utilization,
            decoded.sevenDay?.utilization,
            decoded.sevenDaySonnet?.utilization,
            decoded.sevenDayOpus?.utilization
        ].compactMap { $0 }

        // If any bucket is > 1.0, the API is returning 0...100 percentage values.
        // If all values are 0...1 and at least one is fractional (0 < v < 1), it's 0...1 fraction scale.
        // Otherwise (e.g. integers 0 or 1), default to 0...100 percentage scale (Claude's standard format).
        let isFractionScale = !allUtils.contains(where: { $0 > 1.0 })
            && allUtils.contains(where: { $0 > 0.0 && $0 < 1.0 })

        var windows: [UsageWindow] = []

        if let fiveHour = decoded.fiveHour, let util = fiveHour.utilization {
            windows.append(makeWindow(id: "five_hour", label: "5시간 세션", utilization: util, isFractionScale: isFractionScale, resetsAt: fiveHour.resetsAt))
        }
        if let sevenDay = decoded.sevenDay, let util = sevenDay.utilization {
            windows.append(makeWindow(id: "seven_day", label: "7일 주간 한도", utilization: util, isFractionScale: isFractionScale, resetsAt: sevenDay.resetsAt))
        }
        if let sonnet = decoded.sevenDaySonnet, let util = sonnet.utilization {
            windows.append(makeWindow(id: "seven_day_sonnet", label: "Sonnet 주간", utilization: util, isFractionScale: isFractionScale, resetsAt: sonnet.resetsAt))
        }
        if let opus = decoded.sevenDayOpus, let util = opus.utilization {
            windows.append(makeWindow(id: "seven_day_opus", label: "Opus 주간", utilization: util, isFractionScale: isFractionScale, resetsAt: opus.resetsAt))
        }

        let planLabel = formatTierName(org.rateLimitTier) ?? "Claude"

        return ProviderUsageSnapshot(
            provider: .claude,
            authStatus: .authenticated(accountLabel: org.name ?? planLabel),
            planName: planLabel,
            windows: windows,
            lastUpdated: Date(),
            errorMessage: nil,
            isLoading: false
        )
    }

    private func makeWindow(id: String, label: String, utilization: Double, isFractionScale: Bool, resetsAt: String?) -> UsageWindow {
        let usedFraction: Double = isFractionScale ? utilization : (utilization / 100.0)
        let remaining = max(0.0, min(1.0, 1.0 - usedFraction))
        return UsageWindow(
            id: id,
            label: label,
            remainingFraction: remaining,
            resetsAt: DateParser.parseISO8601(resetsAt)
        )
    }

    private func formatTierName(_ tier: String?) -> String? {
        guard let tier, !tier.isEmpty else { return nil }
        let lower = tier.lowercased()
        if lower.contains("max_20") || lower.contains("20x") { return "Claude Max (20x)" }
        if lower.contains("max_5") || lower.contains("5x") { return "Claude Max (5x)" }
        if lower.contains("max") { return "Claude Max" }
        if lower.contains("pro") { return "Claude Pro" }
        if lower.contains("team") { return "Claude Team" }
        return tier.replacingOccurrences(of: "_", with: " ").capitalized
    }
}
