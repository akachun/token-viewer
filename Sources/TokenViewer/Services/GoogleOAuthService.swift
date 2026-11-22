import AppKit
import CryptoKit
import Foundation
import Network

/// Stored Google OAuth 2.0 credentials in macOS Keychain.
struct GoogleOAuthCredentials: Codable {
    var clientId: String
    var clientSecret: String
    var accessToken: String
    var refreshToken: String?
    var expiresAt: Date?
    var email: String?

    var isAccessTokenExpired: Bool {
        guard let expiresAt else { return false }
        return Date().addingTimeInterval(60) >= expiresAt
    }
}

enum GoogleOAuthError: LocalizedError {
    case missingClientId
    case listenerFailed(String)
    case callbackTimeout
    case stateMismatch
    case authorizationDenied(String)
    case tokenExchangeFailed(String)
    case missingRefreshToken

    var errorDescription: String? {
        switch self {
        case .missingClientId:
            return "Google OAuth Client ID를 먼저 입력해 주세요."
        case .listenerFailed(let msg):
            return "로컬 OAuth 콜백 서버 시작 실패: \(msg)"
        case .callbackTimeout:
            return "Google 로그인 응답 시간이 초과되었습니다."
        case .stateMismatch:
            return "보안 상태(state) 값이 일치하지 않습니다."
        case .authorizationDenied(let msg):
            return "Google 로그인이 취소되었거나 거부되었습니다: \(msg)"
        case .tokenExchangeFailed(let msg):
            return "토큰 교환 실패: \(msg)"
        case .missingRefreshToken:
            return "Refresh Token이 없어 다시 로그인해야 합니다."
        }
    }
}

/// Performs independent Google OAuth 2.0 Authorization Code + PKCE flow via the system browser
/// and a temporary localhost callback server (`http://127.0.0.1:<port>/oauth2callback`).
final class GoogleOAuthService {
    static let shared = GoogleOAuthService()

    private let scopes = [
        "https://www.googleapis.com/auth/cloud-platform",
        "https://www.googleapis.com/auth/userinfo.email",
        "https://www.googleapis.com/auth/userinfo.profile"
    ]

    private init() {}

    /// Launches the system browser for Google OAuth sign-in and returns exchanged credentials.
    func signIn(clientId: String, clientSecret: String) async throws -> GoogleOAuthCredentials {
        let trimmedClientId = clientId.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedSecret = clientSecret.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedClientId.isEmpty else {
            throw GoogleOAuthError.missingClientId
        }

        let codeVerifier = generateCodeVerifier()
        let codeChallenge = generateCodeChallenge(from: codeVerifier)
        let state = UUID().uuidString.replacingOccurrences(of: "-", with: "")

        let server = try LoopbackOAuthServer()
        let port = try await server.start()
        let redirectURI = "http://127.0.0.1:\(port)/oauth2callback"

        var components = URLComponents(string: "https://accounts.google.com/o/oauth2/v2/auth")!
        components.queryItems = [
            URLQueryItem(name: "client_id", value: trimmedClientId),
            URLQueryItem(name: "redirect_uri", value: redirectURI),
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "scope", value: scopes.joined(separator: " ")),
            URLQueryItem(name: "access_type", value: "offline"),
            URLQueryItem(name: "prompt", value: "consent"),
            URLQueryItem(name: "state", value: state),
            URLQueryItem(name: "code_challenge", value: codeChallenge),
            URLQueryItem(name: "code_challenge_method", value: "S256")
        ]

        guard let authURL = components.url else {
            server.stop()
            throw GoogleOAuthError.tokenExchangeFailed("OAuth URL 생성 실패")
        }

        await MainActor.run {
            _ = NSWorkspace.shared.open(authURL)
        }

        let callback = try await server.waitForCallback(timeoutSeconds: 180)
        server.stop()

        if let error = callback["error"] {
            throw GoogleOAuthError.authorizationDenied(error)
        }
        guard callback["state"] == state else {
            throw GoogleOAuthError.stateMismatch
        }
        guard let code = callback["code"] else {
            throw GoogleOAuthError.authorizationDenied("인증 코드가 누락되었습니다.")
        }

        var creds = try await exchangeCodeForTokens(
            code: code,
            clientId: trimmedClientId,
            clientSecret: trimmedSecret,
            redirectURI: redirectURI,
            codeVerifier: codeVerifier
        )

        if let email = try? await fetchUserEmail(accessToken: creds.accessToken) {
            creds.email = email
        }
        return creds
    }

    /// Refreshes an expired access token using the stored refresh token.
    func refreshAccessToken(for credentials: GoogleOAuthCredentials) async throws -> GoogleOAuthCredentials {
        guard let refreshToken = credentials.refreshToken, !refreshToken.isEmpty else {
            throw GoogleOAuthError.missingRefreshToken
        }

        var params: [String: String] = [
            "client_id": credentials.clientId,
            "refresh_token": refreshToken,
            "grant_type": "refresh_token"
        ]
        if !credentials.clientSecret.isEmpty {
            params["client_secret"] = credentials.clientSecret
        }

        let tokenResponse = try await postTokenRequest(params: params)
        var updated = credentials
        updated.accessToken = tokenResponse.accessToken
        if let expiresIn = tokenResponse.expiresIn {
            updated.expiresAt = Date().addingTimeInterval(TimeInterval(expiresIn))
        }
        if let newRefresh = tokenResponse.refreshToken, !newRefresh.isEmpty {
            updated.refreshToken = newRefresh
        }
        return updated
    }

    private func exchangeCodeForTokens(
        code: String,
        clientId: String,
        clientSecret: String,
        redirectURI: String,
        codeVerifier: String
    ) async throws -> GoogleOAuthCredentials {
        var params: [String: String] = [
            "code": code,
            "client_id": clientId,
            "redirect_uri": redirectURI,
            "grant_type": "authorization_code",
            "code_verifier": codeVerifier
        ]
        if !clientSecret.isEmpty {
            params["client_secret"] = clientSecret
        }

        let tokenResponse = try await postTokenRequest(params: params)
        let expiresAt = tokenResponse.expiresIn.map { Date().addingTimeInterval(TimeInterval($0)) }

        return GoogleOAuthCredentials(
            clientId: clientId,
            clientSecret: clientSecret,
            accessToken: tokenResponse.accessToken,
            refreshToken: tokenResponse.refreshToken,
            expiresAt: expiresAt,
            email: nil
        )
    }

    private struct TokenResponse: Decodable {
        let accessToken: String
        let refreshToken: String?
        let expiresIn: Int?

        enum CodingKeys: String, CodingKey {
            case accessToken = "access_token"
            case refreshToken = "refresh_token"
            case expiresIn = "expires_in"
        }
    }

    private func postTokenRequest(params: [String: String]) async throws -> TokenResponse {
        var request = URLRequest(url: URL(string: "https://oauth2.googleapis.com/token")!)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")

        var bodyComponents = URLComponents()
        bodyComponents.queryItems = params.map { URLQueryItem(name: $0.key, value: $0.value) }
        request.httpBody = bodyComponents.percentEncodedQuery?.data(using: .utf8)

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw GoogleOAuthError.tokenExchangeFailed("잘못된 HTTP 응답")
        }
        guard (200...299).contains(http.statusCode) else {
            let bodyText = String(data: data, encoding: .utf8) ?? "HTTP \(http.statusCode)"
            throw GoogleOAuthError.tokenExchangeFailed(bodyText)
        }

        return try JSONDecoder().decode(TokenResponse.self, from: data)
    }

    private func fetchUserEmail(accessToken: String) async throws -> String? {
        var request = URLRequest(url: URL(string: "https://www.googleapis.com/oauth2/v2/userinfo")!)
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        let (data, _) = try await URLSession.shared.data(for: request)
        if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let email = json["email"] as? String {
            return email
        }
        return nil
    }

    // MARK: - PKCE Helpers

    private func generateCodeVerifier() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return Data(bytes).base64URLEncodedString()
    }

    private func generateCodeChallenge(from verifier: String) -> String {
        let digest = SHA256.hash(data: Data(verifier.utf8))
        return Data(digest).base64URLEncodedString()
    }
}

private extension Data {
    func base64URLEncodedString() -> String {
        base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}

/// Minimal one-shot localhost HTTP server for OAuth 2.0 loopback callbacks.
private final class LoopbackOAuthServer: @unchecked Sendable {
    private let listener: NWListener
    private let queue = DispatchQueue(label: "com.sechunpark.TokenViewer.oauth-loopback")
    private var callbackContinuation: CheckedContinuation<[String: String], Error>?
    private var startResumed = false
    private var isCompleted = false

    init() throws {
        let params = NWParameters.tcp
        params.requiredLocalEndpoint = NWEndpoint.hostPort(host: .ipv4(.loopback), port: .any)
        self.listener = try NWListener(using: params)
    }

    func start() async throws -> UInt16 {
        try await withCheckedThrowingContinuation { continuation in
            listener.stateUpdateHandler = { [weak self] state in
                guard let self, !self.startResumed else { return }
                switch state {
                case .ready:
                    if let port = self.listener.port?.rawValue {
                        self.startResumed = true
                        continuation.resume(returning: port)
                    }
                case .failed(let error):
                    self.startResumed = true
                    continuation.resume(throwing: GoogleOAuthError.listenerFailed(error.localizedDescription))
                default:
                    break
                }
            }

            listener.newConnectionHandler = { [weak self] connection in
                self?.handle(connection: connection)
            }
            listener.start(queue: queue)
        }
    }

    func waitForCallback(timeoutSeconds: UInt64) async throws -> [String: String] {
        try await withCheckedThrowingContinuation { continuation in
            queue.async { [weak self] in
                self?.callbackContinuation = continuation
            }
            Task { [weak self] in
                try? await Task.sleep(nanoseconds: timeoutSeconds * 1_000_000_000)
                guard let self else { return }
                self.queue.async {
                    guard !self.isCompleted else { return }
                    self.isCompleted = true
                    self.callbackContinuation?.resume(throwing: GoogleOAuthError.callbackTimeout)
                    self.callbackContinuation = nil
                }
            }
        }
    }

    func stop() {
        listener.cancel()
    }

    private func handle(connection: NWConnection) {
        connection.start(queue: queue)
        connection.receive(minimumIncompleteLength: 1, maximumLength: 8192) { [weak self] data, _, _, _ in
            guard let self, let data, let requestText = String(data: data, encoding: .utf8) else {
                connection.cancel()
                return
            }

            // Parse "GET /oauth2callback?code=...&state=... HTTP/1.1"
            let firstLine = requestText.components(separatedBy: "\r\n").first ?? ""
            let parts = firstLine.components(separatedBy: " ")
            var params: [String: String] = [:]
            if parts.count >= 2,
               let url = URL(string: "http://127.0.0.1\(parts[1])"),
               let components = URLComponents(url: url, resolvingAgainstBaseURL: false) {
                for item in components.queryItems ?? [] {
                    params[item.name] = item.value ?? ""
                }
            }

            let html = """
            <!doctype html>
            <html lang="ko"><head><meta charset="utf-8"><title>TokenViewer</title>
            <style>body{font-family:-apple-system,BlinkMacSystemFont,sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;background:#111;color:#fff;}
            .card{background:#1c1c1e;padding:28px 36px;border-radius:14px;text-align:center;box-shadow:0 8px 30px rgba(0,0,0,.4);}</style>
            </head><body><div class="card"><h2>TokenViewer 인증 완료</h2><p>이 창을 닫고 메뉴바의 TokenViewer로 돌아가세요.</p></div></body></html>
            """
            let response = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\n\r\n\(html)"
            connection.send(content: response.data(using: .utf8), completion: .contentProcessed { _ in
                connection.cancel()
            })

            if !self.isCompleted {
                self.isCompleted = true
                self.callbackContinuation?.resume(returning: params)
                self.callbackContinuation = nil
            }
        }
    }
}
