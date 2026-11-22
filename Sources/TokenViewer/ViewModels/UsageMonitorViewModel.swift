import Combine
import Foundation

/// Central @MainActor coordinator managing authentication state, periodic polling,
/// quota snapshots, and notification triggers for all AI providers.
@MainActor
final class UsageMonitorViewModel: ObservableObject {
    @Published var snapshots: [ProviderType: ProviderUsageSnapshot] = [
        .codex: .empty(for: .codex),
        .claude: .empty(for: .claude),
        .gemini: .empty(for: .gemini)
    ]
    @Published var isRefreshingAll: Bool = false

    let settings: AppSettings
    let codexProvider = CodexProvider()
    let claudeProvider = ClaudeProvider()
    let geminiProvider = GeminiProvider()

    private var providers: [ProviderType: any UsageProvider] {
        [
            .codex: codexProvider,
            .claude: claudeProvider,
            .gemini: geminiProvider
        ]
    }

    private var pollingTask: Task<Void, Never>?
    private var cancellables: Set<AnyCancellable> = []

    /// Callback invoked by AppDelegate to open the appropriate authentication window/sheet.
    var onRequestLogin: ((ProviderType) -> Void)?
    /// Callback invoked by AppDelegate to open the Settings window.
    var onRequestSettings: (() -> Void)?

    init(settings: AppSettings) {
        self.settings = settings
        bindSettingsChanges()
        NotificationService.shared.requestAuthorizationIfNeeded()
        startPolling()

        Task {
            await bootstrap()
        }
    }

    func snapshot(for provider: ProviderType) -> ProviderUsageSnapshot {
        snapshots[provider] ?? .empty(for: provider)
    }

    func bootstrap() async {
        if settings.demoModeEnabled {
            applyDemoSnapshots()
            return
        }
        await refreshAll()
    }

    func refreshAll() async {
        if settings.demoModeEnabled {
            applyDemoSnapshots()
            return
        }
        guard !isRefreshingAll else { return }
        isRefreshingAll = true
        defer { isRefreshingAll = false }

        await withTaskGroup(of: Void.self) { group in
            for providerType in ProviderType.allCases {
                group.addTask { @MainActor in
                    await self.refresh(providerType)
                }
            }
        }
    }

    func refresh(_ providerType: ProviderType) async {
        if settings.demoModeEnabled {
            applyDemoSnapshots()
            return
        }
        guard let provider = providers[providerType] else { return }

        var current = snapshot(for: providerType)
        current.isLoading = true
        current.errorMessage = nil
        snapshots[providerType] = current

        let hasSession = await provider.hasStoredSession()
        if !hasSession {
            snapshots[providerType] = ProviderUsageSnapshot(
                provider: providerType,
                authStatus: .signedOut,
                planName: nil,
                windows: [],
                lastUpdated: Date(),
                errorMessage: nil,
                isLoading: false
            )
            return
        }

        do {
            let updated = try await provider.fetchUsage()
            snapshots[providerType] = updated
            NotificationService.shared.evaluateThresholdAlert(snapshot: updated, settings: settings)
        } catch ProviderError.notAuthenticated {
            snapshots[providerType] = ProviderUsageSnapshot(
                provider: providerType,
                authStatus: .signedOut,
                planName: nil,
                windows: [],
                lastUpdated: Date(),
                errorMessage: nil,
                isLoading: false
            )
        } catch ProviderError.sessionExpired(let reason) {
            var expired = snapshot(for: providerType)
            expired.authStatus = .expired(reason: reason)
            expired.isLoading = false
            expired.errorMessage = "세션이 만료되었습니다. 다시 로그인해 주세요."
            snapshots[providerType] = expired
        } catch {
            var failed = snapshot(for: providerType)
            if case .authenticating = failed.authStatus {
                failed.authStatus = .authenticated(accountLabel: nil)
            }
            failed.isLoading = false
            failed.errorMessage = error.localizedDescription
            snapshots[providerType] = failed
        }
    }

    func startLogin(for providerType: ProviderType) {
        var current = snapshot(for: providerType)
        current.authStatus = .authenticating
        current.errorMessage = nil
        snapshots[providerType] = current
        onRequestLogin?(providerType)
    }

    func handleLoginCompleted(for providerType: ProviderType) {
        Task {
            await refresh(providerType)
        }
    }

    func handleLoginCancelled(for providerType: ProviderType) {
        Task {
            guard let provider = providers[providerType] else { return }
            let hasSession = await provider.hasStoredSession()
            if !hasSession {
                var current = snapshot(for: providerType)
                if case .authenticating = current.authStatus {
                    current.authStatus = .signedOut
                    snapshots[providerType] = current
                }
            }
        }
    }

    func signOut(_ providerType: ProviderType) async {
        guard let provider = providers[providerType] else { return }
        await provider.signOut()
        snapshots[providerType] = .empty(for: providerType)
    }

    // MARK: - Polling & Settings Observation

    private func bindSettingsChanges() {
        settings.$refreshInterval
            .dropFirst()
            .sink { [weak self] _ in
                self?.startPolling()
            }
            .store(in: &cancellables)

        settings.$demoModeEnabled
            .dropFirst()
            .sink { [weak self] enabled in
                guard let self else { return }
                Task { @MainActor in
                    if enabled {
                        self.applyDemoSnapshots()
                    } else {
                        await self.refreshAll()
                    }
                }
            }
            .store(in: &cancellables)
    }

    private func startPolling() {
        pollingTask?.cancel()
        let intervalSeconds = UInt64(settings.refreshInterval.rawValue)
        pollingTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: intervalSeconds * 1_000_000_000)
                guard !Task.isCancelled else { break }
                await self?.refreshAll()
            }
        }
    }

    // MARK: - Demo Mode Data

    func applyDemoSnapshots() {
        let now = Date()
        snapshots = [
            .codex: ProviderUsageSnapshot(
                provider: .codex,
                authStatus: .authenticated(accountLabel: "demo@openai.com"),
                planName: "ChatGPT Plus",
                windows: [
                    UsageWindow(id: "primary", label: "5시간 윈도우", remainingFraction: 0.78, resetsAt: now.addingTimeInterval(3600 * 2 + 900)),
                    UsageWindow(id: "secondary", label: "7일 주간 한도", remainingFraction: 0.61, resetsAt: now.addingTimeInterval(86400 * 3 + 7200))
                ],
                lastUpdated: now,
                errorMessage: nil,
                isLoading: false
            ),
            .claude: ProviderUsageSnapshot(
                provider: .claude,
                authStatus: .authenticated(accountLabel: "demo@anthropic.com"),
                planName: "Claude Max",
                windows: [
                    UsageWindow(id: "five_hour", label: "5시간 세션", remainingFraction: 0.18, resetsAt: now.addingTimeInterval(3600 * 1 + 1800)),
                    UsageWindow(id: "seven_day", label: "7일 주간 한도", remainingFraction: 0.54, resetsAt: now.addingTimeInterval(86400 * 4))
                ],
                lastUpdated: now,
                errorMessage: nil,
                isLoading: false
            ),
            .gemini: ProviderUsageSnapshot(
                provider: .gemini,
                authStatus: .authenticated(accountLabel: "demo@gmail.com"),
                planName: "Google AI Pro",
                windows: [
                    UsageWindow(id: "gemini-pro", label: "Gemini Pro", remainingFraction: 0.92, resetsAt: now.addingTimeInterval(3600 * 11)),
                    UsageWindow(id: "gemini-flash", label: "Gemini Flash", remainingFraction: 0.99, resetsAt: now.addingTimeInterval(3600 * 11))
                ],
                lastUpdated: now,
                errorMessage: nil,
                isLoading: false
            )
        ]
    }
}
