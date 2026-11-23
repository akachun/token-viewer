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
        guard !orgs.isEmpty else { throw ProviderError.notAuthenticated }

        // Team / Enterprise members usually belong to several organizations (personal + company).
        // Prefer the one selected in claude.ai (lastActiveOrg cookie), then try the rest until one reports windows.
        let cookies = await WebSessionBridge.shared.cookies(matching: "claude.ai")
        let lastActive = cookies.first { $0.name == "lastActiveOrg" }?.value
        let candidates = Array(orderOrganizations(orgs, lastActiveOrg: lastActive).prefix(5))

        // 2. Fetch usage windows: first organization that reports any
        var fallback: ProviderUsageSnapshot?
        var firstError: Error?
        for org in candidates {
            do {
                let usageResp = try await WebSessionBridge.shared.fetch(
                    origin: origin,
                    pathOrURL: "/api/organizations/\(org.uuid)/usage",
                    headers: [
                        "Accept": "application/json",
                        "anthropic-client-platform": "web_claude_ai"
                    ]
                )
                if usageResp.status == 429 { throw ProviderError.rateLimited }
                if usageResp.status == 401 || usageResp.status == 403 {
                    throw ProviderError.sessionExpired("HTTP \(usageResp.status)")
                }
                guard (200...299).contains(usageResp.status),
                      let usageData = usageResp.body.data(using: .utf8) else {
                    throw ProviderError.networkError("HTTP \(usageResp.status)")
                }
                let snap = try parseClaudeUsage(data: usageData, org: org)
                if !snap.windows.isEmpty { return snap }
                if fallback == nil { fallback = snap }
            } catch ProviderError.rateLimited {
                throw ProviderError.rateLimited
            } catch {
                // A company org may refuse /usage for members; keep trying other orgs.
                if firstError == nil { firstError = error }
            }
        }

        if var fallback {
            let names = candidates.map { $0.name ?? formatTierName($0.rateLimitTier) ?? String($0.uuid.prefix(8)) }
                .joined(separator: ", ")
            fallback.notice = "\(fallback.planName ?? "이 조직")은(는) 고정 사용량 한도(5시간/주간)를 제공하지 않습니다. "
                + "Enterprise·사용량 기반 플랜이면 관리자 콘솔에서 확인하세요. (확인한 조직: \(names))"
            return fallback
        }
        throw firstError ?? ProviderError.notAuthenticated
    }

    /// lastActiveOrg first, then chat-capable orgs (paid tiers before free), then the rest.
    private func orderOrganizations(_ orgs: [Organization], lastActiveOrg: String?) -> [Organization] {
        func rank(_ o: Organization) -> (Int, Int, Int) {
            let active = (lastActiveOrg != nil && o.uuid.caseInsensitiveCompare(lastActiveOrg!) == .orderedSame) ? 0 : 1
            let chat = o.capabilities?.contains("chat") == true ? 0 : 1
            let tier = o.rateLimitTier ?? ""
            let free = (tier.isEmpty || tier.lowercased().contains("free")) ? 1 : 0
            return (active, chat, free)
        }
        return orgs.enumerated()
            .sorted { a, b in
                let ra = rank(a.element), rb = rank(b.element)
                return ra != rb ? ra < rb : a.offset < b.offset
            }
            .map(\.element)
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

    private func parseOrganizations(data: Data) throws -> [Organization] {
        do {
            return try JSONDecoder().decode([Organization].self, from: data)
        } catch {
            throw ProviderError.notAuthenticated
        }
    }

    private static let knownBuckets: [(id: String, label: String)] = [
        ("five_hour", "5시간 세션"),
        ("seven_day", "7일 주간 한도"),
        ("seven_day_sonnet", "Sonnet 주간"),
        ("seven_day_opus", "Opus 주간"),
        ("seven_day_oauth_apps", "앱 연동 주간"),
        ("extra_usage", "추가 사용량 (월)")
    ]

    /// Every top-level object with a numeric `utilization` becomes a window, so buckets that only some plans
    /// return (Team/Enterprise extras) are shown too. Known buckets come first.
    private func parseClaudeUsage(data: Data, org: Organization) throws -> ProviderUsageSnapshot {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
            throw ProviderError.parsingError("빈 응답")
        }

        struct Bucket { let id: String; let label: String; let util: Double; let resetsAt: String? }
        func order(_ id: String) -> Int { Self.knownBuckets.firstIndex { $0.id == id } ?? Int.max }

        let buckets: [Bucket] = root.compactMap { key, value in
            guard let obj = value as? [String: Any] else { return nil }
            let util: Double?
            if let n = obj["utilization"] as? NSNumber, !(n is Bool) { util = n.doubleValue }
            else if let str = obj["utilization"] as? String { util = Double(str) }
            else { util = nil }
            guard let util else { return nil }
            let label = Self.knownBuckets.first { $0.id == key }?.label
                ?? key.replacingOccurrences(of: "seven_day", with: "주간")
                    .replacingOccurrences(of: "five_hour", with: "5시간")
                    .replacingOccurrences(of: "_", with: " ")
                    .trimmingCharacters(in: .whitespaces)
            return Bucket(id: key, label: label, util: util, resetsAt: obj["resets_at"] as? String)
        }
        .sorted { (order($0.id), $0.id) < (order($1.id), $1.id) }

        // If any bucket is > 1.0, the API is returning 0...100 percentage values.
        // If all values are 0...1 and at least one is fractional (0 < v < 1), it's 0...1 fraction scale.
        // Otherwise (e.g. integers 0 or 1), default to 0...100 percentage scale (Claude's standard format).
        let allUtils = buckets.map(\.util)
        let isFractionScale = !allUtils.contains(where: { $0 > 1.0 })
            && allUtils.contains(where: { $0 > 0.0 && $0 < 1.0 })

        var windows = buckets.map {
            makeWindow(id: $0.id, label: $0.label, utilization: $0.util, isFractionScale: isFractionScale, resetsAt: $0.resetsAt)
        }
        applyExtraUsageAmounts(root: root, windows: &windows)

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

    // MARK: - Extra usage (monthly spend)

    private func number(_ value: Any?) -> Double? {
        if let n = value as? NSNumber, !(n is Bool) { return n.doubleValue }
        if let s = value as? String { return Double(s) }
        return nil
    }

    /// Prefers `spend.used/limit` ({amount_minor, exponent}), then `extra_usage.used_credits/monthly_limit`
    /// scaled by `decimal_places` (both are minor currency units).
    private func extraUsageAmounts(root: [String: Any]) -> (used: Double, limit: Double, currency: String?)? {
        func minor(_ value: Any?) -> (Double, String?)? {
            guard let m = value as? [String: Any], let amount = number(m["amount_minor"]) else { return nil }
            return (amount / pow(10, number(m["exponent"]) ?? 2), m["currency"] as? String)
        }
        if let spend = root["spend"] as? [String: Any],
           let used = minor(spend["used"]), let limit = minor(spend["limit"]) {
            return (used.0, limit.0, used.1 ?? limit.1)
        }
        if let extra = root["extra_usage"] as? [String: Any],
           let used = number(extra["used_credits"]), let limit = number(extra["monthly_limit"]) {
            let scale = pow(10, number(extra["decimal_places"]) ?? 2)
            return (used / scale, limit / scale, extra["currency"] as? String)
        }
        return nil
    }

    private func applyExtraUsageAmounts(root: [String: Any], windows: inout [UsageWindow]) {
        guard let money = extraUsageAmounts(root: root) else { return }
        let reached = (root["extra_usage"] as? [String: Any])?["spend_limit_reached"] as? Bool == true
        let label = "추가 사용량 (월) \(formatMoney(money.used, money.currency))/\(formatMoney(money.limit, money.currency))"
        if let idx = windows.firstIndex(where: { $0.id == "extra_usage" }) {
            let w = windows[idx]
            windows[idx] = UsageWindow(id: w.id, label: label, remainingFraction: reached ? 0 : w.remainingFraction, resetsAt: w.resetsAt)
        } else if money.limit > 0 {
            let remaining = reached ? 0 : max(0, min(1, 1 - money.used / money.limit))
            windows.append(UsageWindow(id: "extra_usage", label: label, remainingFraction: remaining, resetsAt: nil))
        }
    }

    private func formatMoney(_ amount: Double, _ currency: String?) -> String {
        let f = NumberFormatter()
        f.numberStyle = .decimal
        f.locale = Locale(identifier: "en_US")
        f.minimumFractionDigits = 0
        f.maximumFractionDigits = 2
        let n = f.string(from: NSNumber(value: amount)) ?? "\(amount)"
        switch currency?.uppercased() {
        case nil, "": return n
        case "USD": return "$\(n)"
        case "EUR": return "€\(n)"
        case "KRW": return "₩\(n)"
        case let c?: return "\(n) \(c)"
        }
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
        if lower.contains("enterprise") { return "Claude Enterprise" }
        if lower.contains("team") || lower.contains("raven") { return "Claude Team" }
        return tier.replacingOccurrences(of: "_", with: " ").capitalized
    }
}
