import Foundation

/// Authentication/data-source mode for Gemini quota monitoring.
enum GeminiSourceMode: String, Codable {
    case antigravityLocal
    case oauth
}

/// Monitors Google Gemini subscription quota via:
/// 1) Local Antigravity `language_server` auto-sync (`GetUserStatus`) with last-snapshot caching (Recommended)
/// 2) Enterprise Google OAuth 2.0 / Manual Access Token querying `cloudcode-pa.googleapis.com`
@MainActor
final class GeminiProvider: UsageProvider {
    let type: ProviderType = .gemini

    private let credsAccount = "gemini.googleOAuthCredentials"
    private let modeKey = "tv.gemini.sourceMode"
    private let antigravityEnabledKey = "tv.gemini.antigravityEnabled"
    private let antigravityCacheKey = "tv.gemini.antigravityCache"

    private let endpoints = [
        "https://cloudcode-pa.googleapis.com"
    ]

    var sourceMode: GeminiSourceMode {
        get {
            GeminiSourceMode(rawValue: UserDefaults.standard.string(forKey: modeKey) ?? "") ?? .antigravityLocal
        }
        set {
            UserDefaults.standard.set(newValue.rawValue, forKey: modeKey)
        }
    }

    var isAntigravityEnabled: Bool {
        get { UserDefaults.standard.bool(forKey: antigravityEnabledKey) }
        set { UserDefaults.standard.set(newValue, forKey: antigravityEnabledKey) }
    }

    func enableAntigravityMode() {
        sourceMode = .antigravityLocal
        isAntigravityEnabled = true
    }

    func loadCredentials() -> GoogleOAuthCredentials? {
        KeychainStore.loadCodable(GoogleOAuthCredentials.self, account: credsAccount)
    }

    func saveCredentials(_ creds: GoogleOAuthCredentials) {
        sourceMode = .oauth
        _ = KeychainStore.saveCodable(creds, account: credsAccount)
    }

    func saveManualAccessToken(_ token: String, email: String? = nil) {
        let trimmed = token.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            KeychainStore.delete(account: credsAccount)
            return
        }
        let creds = GoogleOAuthCredentials(
            clientId: "",
            clientSecret: "",
            accessToken: trimmed,
            refreshToken: nil,
            expiresAt: nil,
            email: email
        )
        saveCredentials(creds)
    }

    func hasStoredSession() async -> Bool {
        if sourceMode == .antigravityLocal {
            if isAntigravityEnabled { return true }
            // Also auto-detect if Antigravity IDE is currently running
            return await AntigravityQuotaService.shared.isAntigravityRunning()
        }
        guard let creds = loadCredentials() else { return false }
        return !creds.accessToken.isEmpty || (creds.refreshToken?.isEmpty == false)
    }

    func signOut() async {
        isAntigravityEnabled = false
        UserDefaults.standard.removeObject(forKey: antigravityCacheKey)
        KeychainStore.delete(account: credsAccount)
    }

    func fetchUsage() async throws -> ProviderUsageSnapshot {
        if sourceMode == .antigravityLocal {
            return await fetchFromAntigravity()
        }
        return try await fetchFromOAuth()
    }

    // MARK: - Mode 1: Antigravity Local Server (with Last-Snapshot Cache)

    private func fetchFromAntigravity() async -> ProviderUsageSnapshot {
        do {
            let result = try await AntigravityQuotaService.shared.fetchQuota()
            isAntigravityEnabled = true
            saveAntigravityCache(result)

            return ProviderUsageSnapshot(
                provider: .gemini,
                authStatus: .authenticated(accountLabel: result.accountLabel ?? "Antigravity 연결됨"),
                planName: result.planName ?? "Antigravity",
                windows: result.windows,
                lastUpdated: result.fetchedAt,
                errorMessage: nil,
                isLoading: false
            )
        } catch AntigravityError.notRunning {
            // Gracefully serve the last cached snapshot when Antigravity IDE is closed
            if let cached = loadAntigravityCache() {
                let baseLabel = cached.accountLabel ?? "Antigravity"
                return ProviderUsageSnapshot(
                    provider: .gemini,
                    authStatus: .authenticated(accountLabel: "\(baseLabel) · IDE 대기 중"),
                    planName: cached.planName ?? "Antigravity",
                    windows: cached.windows,
                    lastUpdated: cached.fetchedAt,
                    errorMessage: nil,
                    isLoading: false
                )
            }

            return ProviderUsageSnapshot(
                provider: .gemini,
                authStatus: .authenticated(accountLabel: "Antigravity IDE 대기 중"),
                planName: "Antigravity",
                windows: [],
                lastUpdated: Date(),
                errorMessage: "Antigravity IDE를 실행하면 자동으로 Gemini 잔여량을 동기화합니다.",
                isLoading: false
            )
        } catch {
            if let cached = loadAntigravityCache() {
                return ProviderUsageSnapshot(
                    provider: .gemini,
                    authStatus: .authenticated(accountLabel: cached.accountLabel ?? "Antigravity"),
                    planName: cached.planName ?? "Antigravity",
                    windows: cached.windows,
                    lastUpdated: cached.fetchedAt,
                    errorMessage: error.localizedDescription,
                    isLoading: false
                )
            }
            return ProviderUsageSnapshot(
                provider: .gemini,
                authStatus: .authenticated(accountLabel: "Antigravity 연결 오류"),
                planName: "Antigravity",
                windows: [],
                lastUpdated: Date(),
                errorMessage: error.localizedDescription,
                isLoading: false
            )
        }
    }

    private func saveAntigravityCache(_ result: AntigravityQuotaResult) {
        if let data = try? JSONEncoder().encode(result) {
            UserDefaults.standard.set(data, forKey: antigravityCacheKey)
        }
    }

    private func loadAntigravityCache() -> AntigravityQuotaResult? {
        guard let data = UserDefaults.standard.data(forKey: antigravityCacheKey) else { return nil }
        return try? JSONDecoder().decode(AntigravityQuotaResult.self, from: data)
    }

    // MARK: - Mode 2: Enterprise Google OAuth 2.0

    private func fetchFromOAuth() async throws -> ProviderUsageSnapshot {
        guard var creds = loadCredentials(), !creds.accessToken.isEmpty else {
            throw ProviderError.notAuthenticated
        }

        if creds.isAccessTokenExpired, creds.refreshToken != nil {
            do {
                creds = try await GoogleOAuthService.shared.refreshAccessToken(for: creds)
                saveCredentials(creds)
            } catch {
                throw ProviderError.sessionExpired(error.localizedDescription)
            }
        }

        let codeAssistInfo = await loadCodeAssist(accessToken: creds.accessToken)

        var lastError: Error = ProviderError.networkError("Gemini 쿼터 응답 없음")
        for baseURL in endpoints {
            do {
                let buckets = try await retrieveUserQuota(
                    baseURL: baseURL,
                    accessToken: creds.accessToken,
                    projectId: codeAssistInfo.projectId
                )
                let windows = buildWindows(from: buckets)
                return ProviderUsageSnapshot(
                    provider: .gemini,
                    authStatus: .authenticated(accountLabel: creds.email ?? codeAssistInfo.tierName ?? "Google 계정"),
                    planName: codeAssistInfo.tierName ?? "Gemini",
                    windows: windows,
                    lastUpdated: Date(),
                    errorMessage: nil,
                    isLoading: false
                )
            } catch ProviderError.sessionExpired(let msg) {
                if creds.refreshToken != nil {
                    creds = try await GoogleOAuthService.shared.refreshAccessToken(for: creds)
                    saveCredentials(creds)
                    let buckets = try await retrieveUserQuota(
                        baseURL: baseURL,
                        accessToken: creds.accessToken,
                        projectId: codeAssistInfo.projectId
                    )
                    let windows = buildWindows(from: buckets)
                    return ProviderUsageSnapshot(
                        provider: .gemini,
                        authStatus: .authenticated(accountLabel: creds.email ?? codeAssistInfo.tierName ?? "Google 계정"),
                        planName: codeAssistInfo.tierName ?? "Gemini",
                        windows: windows,
                        lastUpdated: Date(),
                        errorMessage: nil,
                        isLoading: false
                    )
                }
                throw ProviderError.sessionExpired(msg)
            } catch {
                lastError = error
            }
        }

        throw lastError
    }

    private struct CodeAssistInfo {
        let projectId: String?
        let tierName: String?
    }

    private func loadCodeAssist(accessToken: String) async -> CodeAssistInfo {
        guard let url = URL(string: "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist") else {
            return CodeAssistInfo(projectId: nil, tierName: nil)
        }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = #"{"metadata":{"ideName":"IDE_UNSPECIFIED","platform":"DARWIN_ARM64","pluginType":"GEMINI"}}"#.data(using: .utf8)

        guard let (data, response) = try? await URLSession.shared.data(for: request),
              let http = response as? HTTPURLResponse,
              (200...299).contains(http.statusCode),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return CodeAssistInfo(projectId: nil, tierName: nil)
        }

        let projectId = json["cloudaicompanionProject"] as? String
        var tierName: String?
        if let tier = json["currentTier"] as? [String: Any] {
            tierName = tier["name"] as? String ?? tier["id"] as? String
        }
        return CodeAssistInfo(projectId: projectId, tierName: formatTier(tierName))
    }

    private struct QuotaResponse: Decodable {
        let buckets: [Bucket]?

        struct Bucket: Decodable {
            let modelId: String?
            let remainingFraction: Double?
            let resetTime: String?
        }
    }

    private func retrieveUserQuota(
        baseURL: String,
        accessToken: String,
        projectId: String?
    ) async throws -> [QuotaResponse.Bucket] {
        guard let url = URL(string: "\(baseURL)/v1internal:retrieveUserQuota") else {
            throw ProviderError.networkError("잘못된 URL")
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")

        var payload: [String: Any] = [:]
        if let projectId, !projectId.isEmpty {
            payload["project"] = projectId
        }
        request.httpBody = try? JSONSerialization.data(withJSONObject: payload)

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw ProviderError.networkError("잘못된 HTTP 응답")
        }

        if http.statusCode == 401 {
            throw ProviderError.sessionExpired("HTTP 401")
        }
        if http.statusCode == 429 {
            throw ProviderError.rateLimited
        }
        guard (200...299).contains(http.statusCode) else {
            let detail = extractGoogleErrorMessage(from: data) ?? "HTTP \(http.statusCode)"
            if http.statusCode == 403 {
                throw ProviderError.networkError("Google 권한 거부 (403): \(detail)")
            }
            throw ProviderError.networkError("HTTP \(http.statusCode): \(detail)")
        }

        let decoded = try JSONDecoder().decode(QuotaResponse.self, from: data)
        return decoded.buckets ?? []
    }

    private func extractGoogleErrorMessage(from data: Data) -> String? {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let errorObj = json["error"] as? [String: Any] else {
            return String(data: data, encoding: .utf8)?.prefix(160).description
        }
        let status = errorObj["status"] as? String
        let message = errorObj["message"] as? String
        if let status, let message {
            return "[\(status)] \(message)"
        }
        return message ?? status
    }

    private func buildWindows(from buckets: [QuotaResponse.Bucket]) -> [UsageWindow] {
        var proBest: QuotaResponse.Bucket?
        var flashBest: QuotaResponse.Bucket?
        var otherWindows: [UsageWindow] = []

        for bucket in buckets {
            guard let modelId = bucket.modelId, let rem = bucket.remainingFraction else { continue }
            let lower = modelId.lowercased()
            if lower.contains("pro") {
                if proBest == nil || rem < (proBest?.remainingFraction ?? 1.0) {
                    proBest = bucket
                }
            } else if lower.contains("flash") {
                if flashBest == nil || rem < (flashBest?.remainingFraction ?? 1.0) {
                    flashBest = bucket
                }
            } else {
                otherWindows.append(UsageWindow(
                    id: modelId,
                    label: modelId,
                    remainingFraction: rem,
                    resetsAt: DateParser.parseISO8601(bucket.resetTime)
                ))
            }
        }

        var result: [UsageWindow] = []
        if let pro = proBest, let rem = pro.remainingFraction {
            result.append(UsageWindow(
                id: "gemini-pro",
                label: "Gemini Pro",
                remainingFraction: rem,
                resetsAt: DateParser.parseISO8601(pro.resetTime)
            ))
        }
        if let flash = flashBest, let rem = flash.remainingFraction {
            result.append(UsageWindow(
                id: "gemini-flash",
                label: "Gemini Flash",
                remainingFraction: rem,
                resetsAt: DateParser.parseISO8601(flash.resetTime)
            ))
        }
        result.append(contentsOf: otherWindows.prefix(2))
        return result
    }

    private func formatTier(_ raw: String?) -> String? {
        guard let raw, !raw.isEmpty else { return nil }
        let lower = raw.lowercased()
        if lower.contains("ultra") { return "Google AI Ultra" }
        if lower.contains("pro") { return "Google AI Pro" }
        if lower.contains("standard") { return "Gemini Standard" }
        if lower.contains("free") { return "Gemini Free" }
        return raw
    }
}
