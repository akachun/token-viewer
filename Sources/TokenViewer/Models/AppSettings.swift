import Foundation
import Combine

/// Visual representation style in the macOS menu bar.
enum StatusBarDisplayStyle: String, CaseIterable, Codable, Identifiable {
    case text           // CX 82% · CL 64% · GM 91%
    case miniBar        // CX [████░] 82%
    case ring           // ◔ CX  ◕ CL  ● GM
    case statusDot      // ● CX 82%  ● CL 64%
    case iconValue      // [icon] 82%

    var id: String { rawValue }

    var title: String {
        switch self {
        case .text: return "텍스트 퍼센트"
        case .miniBar: return "미니 프로그레스 바"
        case .ring: return "원형 게이지 링"
        case .statusDot: return "상태 도트 + 수치"
        case .iconValue: return "아이콘 + 수치"
        }
    }

    var description: String {
        switch self {
        case .text: return "약어와 퍼센트를 깔끔하게 표시 (예: CX 82% · CL 64%)"
        case .miniBar: return "각 AI별 미니 막대 게이지와 수치를 함께 표시"
        case .ring: return "원형 진행 링으로 잔여량을 직관적으로 표시"
        case .statusDot: return "잔여량 상태 색상 도트(초록/노랑/빨강)와 수치 표시"
        case .iconValue: return "AI별 심볼 아이콘과 퍼센트 표시"
        }
    }
}

/// Whether percentages represent remaining quota or consumed usage.
enum QuotaMetricMode: String, CaseIterable, Codable, Identifiable {
    case remaining
    case used

    var id: String { rawValue }

    var title: String {
        switch self {
        case .remaining: return "잔여량 (Remaining %)"
        case .used: return "사용량 (Used %)"
        }
    }

    var shortTitle: String {
        switch self {
        case .remaining: return "잔여"
        case .used: return "사용"
        }
    }
}

/// Which quota window to summarize in the menu bar when a provider has multiple windows (e.g., 5h vs 7d).
enum WindowSelectionStrategy: String, CaseIterable, Codable, Identifiable {
    case primary
    case mostConstrained

    var id: String { rawValue }

    var title: String {
        switch self {
        case .primary: return "기본 윈도우 (5시간 / 주력 모델)"
        case .mostConstrained: return "가장 부족한 윈도우 우선"
        }
    }
}

enum ColorMode: String, CaseIterable, Codable, Identifiable {
    case healthColor    // Green / Amber / Red based on remaining quota
    case brandColor     // OpenAI green, Claude coral, Gemini blue
    case monochrome     // Adapts to macOS menu bar tint

    var id: String { rawValue }

    var title: String {
        switch self {
        case .healthColor: return "상태 색상 (초록/노랑/빨강)"
        case .brandColor: return "AI 브랜드 색상"
        case .monochrome: return "모노크롬 (시스템 메뉴바 스타일)"
        }
    }
}

enum RefreshIntervalOption: Int, CaseIterable, Codable, Identifiable {
    case oneMinute = 60
    case threeMinutes = 180
    case fiveMinutes = 300
    case tenMinutes = 600
    case thirtyMinutes = 1800

    var id: Int { rawValue }

    var title: String {
        switch self {
        case .oneMinute: return "1분마다"
        case .threeMinutes: return "3분마다"
        case .fiveMinutes: return "5분마다 (권장)"
        case .tenMinutes: return "10분마다"
        case .thirtyMinutes: return "30분마다"
        }
    }
}

/// Persistent user preferences for TokenViewer.
@MainActor
final class AppSettings: ObservableObject {
    private let defaults = UserDefaults.standard

    private enum Keys {
        static let providerOrder = "tv.providerOrder"
        static let visibleProviders = "tv.visibleProviders"
        static let displayStyle = "tv.displayStyle"
        static let metricMode = "tv.metricMode"
        static let windowStrategy = "tv.windowStrategy"
        static let colorMode = "tv.colorMode"
        static let showPercentWithShapes = "tv.showPercentWithShapes"
        static let showResetTimeInMenuBar = "tv.showResetTimeInMenuBar"
        static let compactSpacing = "tv.compactSpacing"
        static let refreshInterval = "tv.refreshInterval"
        static let notificationsEnabled = "tv.notificationsEnabled"
        static let warningThresholdPercent = "tv.warningThresholdPercent"
        static let notifiedProviders = "tv.notifiedProviders"
        static let launchAtLogin = "tv.launchAtLogin"
        static let demoModeEnabled = "tv.demoModeEnabled"
    }

    @Published var providerOrder: [ProviderType] {
        didSet { saveCodable(providerOrder, forKey: Keys.providerOrder) }
    }

    @Published var visibleProviders: Set<ProviderType> {
        didSet { saveCodable(Array(visibleProviders), forKey: Keys.visibleProviders) }
    }

    @Published var displayStyle: StatusBarDisplayStyle {
        didSet { defaults.set(displayStyle.rawValue, forKey: Keys.displayStyle) }
    }

    @Published var metricMode: QuotaMetricMode {
        didSet { defaults.set(metricMode.rawValue, forKey: Keys.metricMode) }
    }

    @Published var windowStrategy: WindowSelectionStrategy {
        didSet { defaults.set(windowStrategy.rawValue, forKey: Keys.windowStrategy) }
    }

    @Published var colorMode: ColorMode {
        didSet { defaults.set(colorMode.rawValue, forKey: Keys.colorMode) }
    }

    @Published var showPercentWithShapes: Bool {
        didSet { defaults.set(showPercentWithShapes, forKey: Keys.showPercentWithShapes) }
    }

    @Published var showResetTimeInMenuBar: Bool {
        didSet { defaults.set(showResetTimeInMenuBar, forKey: Keys.showResetTimeInMenuBar) }
    }

    @Published var compactSpacing: Bool {
        didSet { defaults.set(compactSpacing, forKey: Keys.compactSpacing) }
    }

    @Published var refreshInterval: RefreshIntervalOption {
        didSet { defaults.set(refreshInterval.rawValue, forKey: Keys.refreshInterval) }
    }

    @Published var notificationsEnabled: Bool {
        didSet { defaults.set(notificationsEnabled, forKey: Keys.notificationsEnabled) }
    }

    @Published var warningThresholdPercent: Int {
        didSet { defaults.set(warningThresholdPercent, forKey: Keys.warningThresholdPercent) }
    }

    @Published var notifiedProviders: Set<ProviderType> {
        didSet { saveCodable(Array(notifiedProviders), forKey: Keys.notifiedProviders) }
    }

    @Published var launchAtLogin: Bool {
        didSet {
            defaults.set(launchAtLogin, forKey: Keys.launchAtLogin)
            LaunchAtLoginService.setEnabled(launchAtLogin)
        }
    }

    @Published var demoModeEnabled: Bool {
        didSet { defaults.set(demoModeEnabled, forKey: Keys.demoModeEnabled) }
    }

    var orderedVisibleProviders: [ProviderType] {
        providerOrder.filter { visibleProviders.contains($0) }
    }

    init() {
        if let savedOrder: [ProviderType] = Self.loadCodable(forKey: Keys.providerOrder),
           !savedOrder.isEmpty {
            // Ensure any newly added providers are appended
            let missing = ProviderType.allCases.filter { !savedOrder.contains($0) }
            self.providerOrder = savedOrder + missing
        } else {
            self.providerOrder = ProviderType.allCases
        }

        if let savedVisible: [ProviderType] = Self.loadCodable(forKey: Keys.visibleProviders) {
            self.visibleProviders = Set(savedVisible)
        } else {
            self.visibleProviders = Set(ProviderType.allCases)
        }

        self.displayStyle = StatusBarDisplayStyle(rawValue: defaults.string(forKey: Keys.displayStyle) ?? "") ?? .miniBar
        self.metricMode = QuotaMetricMode(rawValue: defaults.string(forKey: Keys.metricMode) ?? "") ?? .remaining
        self.windowStrategy = WindowSelectionStrategy(rawValue: defaults.string(forKey: Keys.windowStrategy) ?? "") ?? .primary
        self.colorMode = ColorMode(rawValue: defaults.string(forKey: Keys.colorMode) ?? "") ?? .healthColor

        self.showPercentWithShapes = defaults.object(forKey: Keys.showPercentWithShapes) as? Bool ?? true
        self.showResetTimeInMenuBar = defaults.object(forKey: Keys.showResetTimeInMenuBar) as? Bool ?? false
        self.compactSpacing = defaults.object(forKey: Keys.compactSpacing) as? Bool ?? false

        let savedInterval = defaults.integer(forKey: Keys.refreshInterval)
        self.refreshInterval = RefreshIntervalOption(rawValue: savedInterval) ?? .fiveMinutes

        self.notificationsEnabled = defaults.object(forKey: Keys.notificationsEnabled) as? Bool ?? true
        let savedThreshold = defaults.integer(forKey: Keys.warningThresholdPercent)
        self.warningThresholdPercent = savedThreshold > 0 ? savedThreshold : 20

        if let savedNotified: [ProviderType] = Self.loadCodable(forKey: Keys.notifiedProviders) {
            self.notifiedProviders = Set(savedNotified)
        } else {
            self.notifiedProviders = Set(ProviderType.allCases)
        }

        self.launchAtLogin = defaults.bool(forKey: Keys.launchAtLogin)
        self.demoModeEnabled = defaults.bool(forKey: Keys.demoModeEnabled)
    }

    func moveProvider(from source: IndexSet, to destination: Int) {
        providerOrder.move(fromOffsets: source, toOffset: destination)
    }

    func moveProviderUp(_ provider: ProviderType) {
        guard let idx = providerOrder.firstIndex(of: provider), idx > 0 else { return }
        providerOrder.swapAt(idx, idx - 1)
    }

    func moveProviderDown(_ provider: ProviderType) {
        guard let idx = providerOrder.firstIndex(of: provider), idx < providerOrder.count - 1 else { return }
        providerOrder.swapAt(idx, idx + 1)
    }

    func isVisible(_ provider: ProviderType) -> Bool {
        visibleProviders.contains(provider)
    }

    func setVisible(_ provider: ProviderType, visible: Bool) {
        if visible {
            visibleProviders.insert(provider)
        } else {
            visibleProviders.remove(provider)
        }
    }

    private func saveCodable<T: Encodable>(_ value: T, forKey key: String) {
        if let data = try? JSONEncoder().encode(value) {
            defaults.set(data, forKey: key)
        }
    }

    private static func loadCodable<T: Decodable>(forKey key: String) -> T? {
        guard let data = UserDefaults.standard.data(forKey: key) else { return nil }
        return try? JSONDecoder().decode(T.self, from: data)
    }
}
