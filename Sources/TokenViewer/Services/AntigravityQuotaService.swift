import Foundation

/// Parsed quota result returned by the local Antigravity `language_server` Connect RPC (`GetUserStatus`).
struct AntigravityQuotaResult: Codable {
    let accountLabel: String?
    let planName: String?
    let windows: [UsageWindow]
    let fetchedAt: Date
}

enum AntigravityError: LocalizedError {
    case notRunning
    case noListeningPorts
    case rpcFailed(String)

    var errorDescription: String? {
        switch self {
        case .notRunning:
            return "Antigravity IDE가 실행 중이 아닙니다. Antigravity를 실행하면 자동으로 잔여량을 동기화합니다."
        case .noListeningPorts:
            return "Antigravity 로컬 서버 포트를 찾을 수 없습니다."
        case .rpcFailed(let detail):
            return "Antigravity 로컬 상태 조회 실패: \(detail)"
        }
    }
}

/// Discovers the running local Antigravity `language_server` process and queries `GetUserStatus`
/// over localhost Connect RPC (zero OAuth setup, zero billing, works with personal Google AI plans).
final class AntigravityQuotaService: @unchecked Sendable {
    static let shared = AntigravityQuotaService()

    private let session: URLSession

    private init() {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 3.0
        config.timeoutIntervalForResource = 5.0
        self.session = URLSession(configuration: config, delegate: LocalhostTLSDelegate(), delegateQueue: nil)
    }

    /// Returns true if an Antigravity `language_server` process with a CSRF token is currently running.
    func isAntigravityRunning() async -> Bool {
        await Task.detached(priority: .utility) {
            self.findLanguageServerProcess() != nil
        }.value
    }

    /// Queries the local Antigravity language server for current Gemini model quotas and plan info.
    func fetchQuota() async throws -> AntigravityQuotaResult {
        let processInfo: (pid: String, csrfToken: String)? = await Task.detached(priority: .utility) {
            self.findLanguageServerProcess()
        }.value

        guard let processInfo else {
            throw AntigravityError.notRunning
        }

        let ports: [Int] = await Task.detached(priority: .utility) {
            self.findListeningPorts(pid: processInfo.pid)
        }.value

        guard !ports.isEmpty else {
            throw AntigravityError.noListeningPorts
        }

        var lastError: Error = AntigravityError.rpcFailed("응답하는 포트가 없습니다.")

        for port in ports {
            for scheme in ["https", "http"] {
                do {
                    let result = try await callGetUserStatus(
                        scheme: scheme,
                        port: port,
                        csrfToken: processInfo.csrfToken
                    )
                    return result
                } catch {
                    lastError = error
                }
            }
        }

        throw lastError
    }

    // MARK: - Process & Port Discovery

    private func findLanguageServerProcess() -> (pid: String, csrfToken: String)? {
        guard let output = runShell("/bin/ps", arguments: ["-axww", "-o", "pid=,command="]) else {
            return nil
        }

        let csrfRegex = try? NSRegularExpression(pattern: #"--csrf_token[=\s]+([^\s]+)"#)

        for line in output.components(separatedBy: .newlines) {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard !trimmed.isEmpty,
                  trimmed.contains("language_server"),
                  trimmed.contains("--csrf_token"),
                  !trimmed.contains("grep") else {
                continue
            }

            let parts = trimmed.split(maxSplits: 1, whereSeparator: { $0.isWhitespace })
            guard parts.count == 2 else { continue }
            let pid = String(parts[0])
            let command = String(parts[1])

            let nsRange = NSRange(command.startIndex..., in: command)
            if let match = csrfRegex?.firstMatch(in: command, range: nsRange),
               match.numberOfRanges > 1,
               let tokenRange = Range(match.range(at: 1), in: command) {
                return (pid, String(command[tokenRange]))
            }
        }
        return nil
    }

    private func findListeningPorts(pid: String) -> [Int] {
        guard let output = runShell("/usr/sbin/lsof", arguments: ["-nP", "-a", "-p", pid, "-iTCP", "-sTCP:LISTEN"]) else {
            return []
        }

        let portRegex = try? NSRegularExpression(pattern: #":(\d+)\s+\(LISTEN\)"#)
        var ports = Set<Int>()
        for line in output.components(separatedBy: .newlines) {
            let nsRange = NSRange(line.startIndex..., in: line)
            if let match = portRegex?.firstMatch(in: line, range: nsRange),
               match.numberOfRanges > 1,
               let range = Range(match.range(at: 1), in: line),
               let port = Int(line[range]) {
                ports.insert(port)
            }
        }
        return ports.sorted()
    }

    private func runShell(_ executable: String, arguments: [String]) -> String? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments

        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice

        do {
            try process.run()
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit()
            return String(data: data, encoding: .utf8)
        } catch {
            return nil
        }
    }

    // MARK: - Connect RPC Call & Parsing

    private func callGetUserStatus(
        scheme: String,
        port: Int,
        csrfToken: String
    ) async throws -> AntigravityQuotaResult {
        guard let url = URL(string: "\(scheme)://127.0.0.1:\(port)/exa.language_server_pb.LanguageServerService/GetUserStatus") else {
            throw AntigravityError.rpcFailed("잘못된 로컬 URL")
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("1", forHTTPHeaderField: "Connect-Protocol-Version")
        request.setValue(csrfToken, forHTTPHeaderField: "X-Codeium-Csrf-Token")
        request.httpBody = #"{"metadata":{"ideName":"antigravity","extensionName":"antigravity","locale":"en"}}"#.data(using: .utf8)

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse,
              (200...299).contains(http.statusCode) else {
            let code = (response as? HTTPURLResponse)?.statusCode ?? -1
            throw AntigravityError.rpcFailed("HTTP \(code)")
        }

        return try parseUserStatus(data: data)
    }

    private func parseUserStatus(data: Data) throws -> AntigravityQuotaResult {
        guard let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let userStatus = root["userStatus"] as? [String: Any] else {
            throw AntigravityError.rpcFailed("userStatus 필드를 찾을 수 없습니다.")
        }

        let email = userStatus["email"] as? String ?? userStatus["name"] as? String
        let planStatus = userStatus["planStatus"] as? [String: Any]
        let planInfo = planStatus?["planInfo"] as? [String: Any]
        let planName = (planInfo?["planName"] as? String)
            ?? (planInfo?["teamsTier"] as? String)
            ?? "Antigravity"

        var windows: [UsageWindow] = []

        if let cascade = userStatus["cascadeModelConfigData"] as? [String: Any],
           let configs = cascade["clientModelConfigs"] as? [[String: Any]] {
            windows = extractGeminiWindows(from: configs)
        }

        // Fallback to prompt credits if model configs didn't contain quotaInfo
        if windows.isEmpty,
           let planStatus,
           let available = planStatus["availablePromptCredits"] as? Double,
           let monthly = planInfo?["monthlyPromptCredits"] as? Double,
           monthly > 0 {
            windows.append(UsageWindow(
                id: "antigravity-credits",
                label: "Prompt Credits",
                remainingFraction: min(max(available / monthly, 0.0), 1.0),
                resetsAt: nil
            ))
        }

        return AntigravityQuotaResult(
            accountLabel: email,
            planName: planName,
            windows: windows,
            fetchedAt: Date()
        )
    }

    /// Groups Gemini model buckets cleanly (Pro / Flash / Other Gemini) while avoiding duplicate shared pools.
    private func extractGeminiWindows(from configs: [[String: Any]]) -> [UsageWindow] {
        var geminiPro: (label: String, rem: Double, reset: Date?)?
        var geminiFlash: (label: String, rem: Double, reset: Date?)?
        var fallbackModels: [UsageWindow] = []

        for config in configs {
            guard let label = config["label"] as? String,
                  let quotaInfo = config["quotaInfo"] as? [String: Any] else {
                continue
            }

            // Some responses omit remainingFraction when 0.0 (exhausted)
            let rem = (quotaInfo["remainingFraction"] as? Double) ?? 0.0
            let reset = DateParser.parseISO8601(quotaInfo["resetTime"] as? String)
            let lower = label.lowercased()

            if lower.contains("gemini") {
                if lower.contains("flash") {
                    if geminiFlash == nil || rem < (geminiFlash?.rem ?? 1.0) {
                        geminiFlash = ("Gemini Flash", rem, reset)
                    }
                } else {
                    if geminiPro == nil || rem < (geminiPro?.rem ?? 1.0) {
                        let cleanName = lower.contains("pro") ? "Gemini Pro" : label
                        geminiPro = (cleanName, rem, reset)
                    }
                }
            } else {
                fallbackModels.append(UsageWindow(
                    id: label,
                    label: label,
                    remainingFraction: rem,
                    resetsAt: reset
                ))
            }
        }

        var result: [UsageWindow] = []
        if let pro = geminiPro {
            result.append(UsageWindow(id: "ag-gemini-pro", label: pro.label, remainingFraction: pro.rem, resetsAt: pro.reset))
        }
        if let flash = geminiFlash {
            result.append(UsageWindow(id: "ag-gemini-flash", label: flash.label, remainingFraction: flash.rem, resetsAt: flash.reset))
        }
        if result.isEmpty {
            // If no explicit "Gemini" label was present, show up to 2 unique buckets
            var seen = Set<String>()
            for win in fallbackModels {
                let sig = "\(win.remainingPercent)-\(Int(win.resetsAt?.timeIntervalSince1970 ?? 0))"
                if seen.insert(sig).inserted {
                    result.append(win)
                }
                if result.count >= 2 { break }
            }
        }
        return result
    }
}

/// Accepts self-signed TLS certificates ONLY for localhost (`127.0.0.1` / `localhost`) Antigravity RPC calls.
private final class LocalhostTLSDelegate: NSObject, URLSessionDelegate, @unchecked Sendable {
    func urlSession(
        _ session: URLSession,
        didReceive challenge: URLAuthenticationChallenge,
        completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void
    ) {
        let host = challenge.protectionSpace.host
        if (host == "127.0.0.1" || host == "localhost"),
           challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
           let trust = challenge.protectionSpace.serverTrust {
            completionHandler(.useCredential, URLCredential(trust: trust))
        } else {
            completionHandler(.performDefaultHandling, nil)
        }
    }
}
