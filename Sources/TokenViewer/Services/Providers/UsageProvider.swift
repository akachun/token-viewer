import Foundation

enum ProviderError: LocalizedError {
    case notAuthenticated
    case sessionExpired(String)
    case networkError(String)
    case parsingError(String)
    case rateLimited

    var errorDescription: String? {
        switch self {
        case .notAuthenticated:
            return "로그인이 필요합니다."
        case .sessionExpired(let msg):
            return "세션이 만료되었습니다. 다시 로그인해 주세요. (\(msg))"
        case .networkError(let msg):
            return "네트워크 오류: \(msg)"
        case .parsingError(let msg):
            return "응답 파싱 오류: \(msg)"
        case .rateLimited:
            return "요청이 많아 일시적으로 제한되었습니다 (429). 잠시 후 다시 시도합니다."
        }
    }
}

/// Common interface implemented by Codex, Claude, and Gemini usage monitors.
@MainActor
protocol UsageProvider {
    var type: ProviderType { get }
    func hasStoredSession() async -> Bool
    func fetchUsage() async throws -> ProviderUsageSnapshot
    func signOut() async
}

/// Robust ISO8601 date parser that handles fractional seconds and timezone offsets.
enum DateParser {
    private static let withFractional: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    private static let standard: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    static func parseISO8601(_ string: String?) -> Date? {
        guard let string, !string.isEmpty else { return nil }
        if let date = withFractional.date(from: string) {
            return date
        }
        return standard.date(from: string)
    }
}
