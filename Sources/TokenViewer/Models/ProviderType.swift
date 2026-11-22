import SwiftUI

/// Supported AI subscription providers.
enum ProviderType: String, CaseIterable, Codable, Identifiable, Hashable {
    case codex
    case claude
    case gemini

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .codex: return "Codex"
        case .claude: return "Claude"
        case .gemini: return "Gemini"
        }
    }

    var companyName: String {
        switch self {
        case .codex: return "OpenAI"
        case .claude: return "Anthropic"
        case .gemini: return "Google"
        }
    }

    var shortLabel: String {
        switch self {
        case .codex: return "CX"
        case .claude: return "CL"
        case .gemini: return "GM"
        }
    }

    var singleLetter: String {
        switch self {
        case .codex: return "O"
        case .claude: return "C"
        case .gemini: return "G"
        }
    }

    var sfSymbolName: String {
        switch self {
        case .codex: return "chevron.left.forwardslash.chevron.right"
        case .claude: return "sparkle"
        case .gemini: return "sparkles"
        }
    }

    var accentColor: Color {
        switch self {
        case .codex: return Color(red: 0.06, green: 0.64, blue: 0.50)   // Emerald / OpenAI green
        case .claude: return Color(red: 0.85, green: 0.47, blue: 0.34)  // Anthropic terracotta
        case .gemini: return Color(red: 0.26, green: 0.52, blue: 0.96)  // Google blue
        }
    }

    var loginURL: URL {
        switch self {
        case .codex: return URL(string: "https://chatgpt.com")!
        case .claude: return URL(string: "https://claude.ai/login")!
        case .gemini: return URL(string: "https://gemini.google.com")!
        }
    }

    var usageDashboardURL: URL {
        switch self {
        case .codex: return URL(string: "https://chatgpt.com/codex/settings/usage")!
        case .claude: return URL(string: "https://claude.ai/settings/usage")!
        case .gemini: return URL(string: "https://gemini.google.com")!
        }
    }
}
