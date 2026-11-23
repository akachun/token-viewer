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
        var sessionAccountId: String?

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
            if let account = sessionJSON["account"] as? [String: Any] {
                sessionAccountId = account["id"] as? String ?? account["account_id"] as? String
            }
        }

        guard let token = accessToken else {
            throw ProviderError.notAuthenticated
        }

        // 2. Workspace (Business / Enterprise / Edu) accounts: without ChatGPT-Account-Id the server answers
        //    for the personal account, which has no Codex limits. Use the workspace selected in chatgpt.com.
        let cookies = await WebSessionBridge.shared.cookies(matching: "chatgpt.com")
        let accountId = Self.resolveAccountId(
            cookie: cookies.first { $0.name == "_account" }?.value,
            sessionAccountId: sessionAccountId,
            accessToken: token
        )
        var headers = [
            "Authorization": "Bearer \(token)",
            "Accept": "application/json"
        ]
        if let accountId { headers["ChatGPT-Account-Id"] = accountId }

        // 3. Call ChatGPT/Codex usage endpoint inside the same browser context
        let usageResp = try await WebSessionBridge.shared.fetch(
            origin: origin,
            pathOrURL: "/backend-api/wham/usage",
            headers: headers
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

    // MARK: - Workspace account

    /// `_account` cookie (workspace picked in the web UI) → session `account.id` → JWT `chatgpt_account_id`.
    static func resolveAccountId(cookie: String?, sessionAccountId: String?, accessToken: String?) -> String? {
        let cookieValue = (cookie?.removingPercentEncoding ?? cookie ?? "")
            .trimmingCharacters(in: CharacterSet(charactersIn: "\"").union(.whitespaces))
        if !cookieValue.isEmpty, cookieValue.lowercased() != "personal" { return cookieValue }
        if let sessionAccountId, !sessionAccountId.isEmpty { return sessionAccountId }
        return accountIdFromJWT(accessToken)
    }

    private static func accountIdFromJWT(_ jwt: String?) -> String? {
        guard let parts = jwt?.split(separator: "."), parts.count >= 2 else { return nil }
        var payload = String(parts[1]).replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        payload += String(repeating: "=", count: (4 - payload.count % 4) % 4)
        guard let data = Data(base64Encoded: payload),
              let claims = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let auth = claims["https://api.openai.com/auth"] as? [String: Any],
              let id = auth["chatgpt_account_id"] as? String, !id.isEmpty else { return nil }
        return id
    }

    // MARK: - Response Parsing

    private func number(_ value: Any?) -> Double? {
        if let n = value as? NSNumber, !(n is Bool) { return n.doubleValue }
        if let s = value as? String { return Double(s) }
        return nil
    }

    /// Parses `rate_limit.primary/secondary_window` plus other `*_rate_limit` groups with the same shape.
    /// Workspace plans without fixed windows get a `notice` describing credits instead.
    private func parseWhamUsage(data: Data, accountEmail: String?) throws -> ProviderUsageSnapshot {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
            throw ProviderError.parsingError("빈 응답")
        }

        var windows: [UsageWindow] = []
        func add(_ value: Any?, id: String, fallback: String, prefix: String? = nil) {
            guard let w = value as? [String: Any], let used = number(w["used_percent"]) else { return }
            let label = windowLabel(seconds: number(w["limit_window_seconds"]).map { Int($0) }, fallback: fallback)
            windows.append(UsageWindow(
                id: id,
                label: prefix.map { "\($0) · \(label)" } ?? label,
                remainingFraction: max(0.0, min(1.0, 1.0 - used / 100.0)),
                resetsAt: resolveResetDate(
                    resetAt: number(w["reset_at"]),
                    resetAfter: number(w["reset_after_seconds"]).map { Int($0) }
                )
            ))
        }

        let rateLimit = root["rate_limit"] as? [String: Any]
        add(rateLimit?["primary_window"], id: "primary", fallback: "5시간 세션")
        add(rateLimit?["secondary_window"], id: "secondary", fallback: "주간 한도")

        for key in root.keys.sorted() where key != "rate_limit" {
            guard let group = root[key] as? [String: Any] else { continue }
            let prefix = key == "code_review_rate_limit"
                ? "코드 리뷰"
                : key.replacingOccurrences(of: "_rate_limit", with: "").replacingOccurrences(of: "_", with: " ")
            add(group["primary_window"], id: "\(key).primary", fallback: "5시간", prefix: prefix)
            add(group["secondary_window"], id: "\(key).secondary", fallback: "주간", prefix: prefix)
        }

        // Business / Enterprise credit budgets: spend_control.individual_limit (and any other *_limit object).
        if let spend = root["spend_control"] as? [String: Any] {
            for key in spend.keys.sorted(by: { ($0 == "individual_limit" ? 0 : 1, $0) < ($1 == "individual_limit" ? 0 : 1, $1) }) {
                guard let limit = spend[key] as? [String: Any] else { continue }
                let used = number(limit["used"]), max = number(limit["limit"])
                var usedFraction: Double
                if let used, let max, max > 0 { usedFraction = used / max }
                else if let pct = number(limit["used_percent"]) { usedFraction = pct / 100.0 }
                else if let rem = number(limit["remaining_percent"]) { usedFraction = 1.0 - rem / 100.0 }
                else { continue }
                if limit["reached"] as? Bool == true { usedFraction = 1.0 }

                let unitRaw = limit["unit"] as? String
                let unit = (unitRaw == nil || unitRaw == "credit" || unitRaw == "credits") ? "크레딧" : unitRaw!
                let scope = key == "individual_limit"
                    ? "월간"
                    : key.replacingOccurrences(of: "_limit", with: "").replacingOccurrences(of: "_", with: " ")
                var amounts = ""
                if let used, let max {
                    let f = NumberFormatter()
                    f.numberStyle = .decimal
                    f.maximumFractionDigits = 0
                    amounts = " (\(f.string(from: NSNumber(value: used)) ?? "")/\(f.string(from: NSNumber(value: max)) ?? ""))"
                }
                windows.append(UsageWindow(
                    id: "spend_\(key)",
                    label: "\(scope) \(unit) 한도\(amounts)",
                    remainingFraction: Swift.max(0.0, Swift.min(1.0, 1.0 - usedFraction)),
                    resetsAt: resolveResetDate(
                        resetAt: number(limit["reset_at"]),
                        resetAfter: number(limit["reset_after_seconds"]).map { Int($0) }
                    )
                ))
            }
        }

        let planDisplay = (root["plan_type"] as? String).flatMap { $0.isEmpty ? nil : "ChatGPT \($0.capitalized)" }

        var snapshot = ProviderUsageSnapshot(
            provider: .codex,
            authStatus: .authenticated(accountLabel: accountEmail ?? planDisplay),
            planName: planDisplay,
            windows: windows,
            lastUpdated: Date(),
            errorMessage: nil,
            isLoading: false
        )
        if windows.isEmpty {
            let who = planDisplay ?? "이 계정"
            let credits = root["credits"] as? [String: Any]
            if credits?["unlimited"] as? Bool == true {
                snapshot.notice = "\(who): 고정 사용량 한도 없음 (무제한 크레딧)."
            } else if let balance = number(credits?["balance"]) {
                snapshot.notice = "\(who): 고정 사용량 한도 없음 (크레딧 잔액 \(String(format: "%.2f", balance)))."
            } else if rateLimit?["allowed"] as? Bool == false {
                snapshot.notice = "\(who): Codex 사용 권한이 없습니다. 워크스페이스 관리자에게 Codex 활성화를 요청하세요."
            } else {
                snapshot.notice = "\(who): 고정 사용량 한도(5시간/주간)가 보고되지 않았습니다. "
                    + "Business/Enterprise 워크스페이스라면 chatgpt.com에서 해당 워크스페이스를 선택한 뒤 다시 로그인해 보세요."
            }
        }
        return snapshot
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
