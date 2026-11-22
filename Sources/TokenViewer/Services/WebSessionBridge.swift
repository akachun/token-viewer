import Foundation
import WebKit

/// Result of a same-origin fetch executed inside a real WKWebView browser context.
struct WebFetchResponse {
    let status: Int
    let body: String
}

enum WebSessionError: LocalizedError {
    case navigationTimeout
    case invalidJavaScriptResult
    case fetchFailed(String)

    var errorDescription: String? {
        switch self {
        case .navigationTimeout:
            return "웹 세션 로드 시간이 초과되었습니다."
        case .invalidJavaScriptResult:
            return "웹 세션 응답을 해석할 수 없습니다."
        case .fetchFailed(let message):
            return message
        }
    }
}

/// Manages persistent hidden WKWebViews sharing `WKWebsiteDataStore.default()` so that
/// authenticated web API calls (e.g. chatgpt.com, claude.ai) run with real browser cookies
/// and TLS fingerprint, avoiding Cloudflare blocks.
@MainActor
final class WebSessionBridge: NSObject {
    static let shared = WebSessionBridge()

    private var webViews: [String: WKWebView] = [:]
    private var navigationContinuations: [ObjectIdentifier: CheckedContinuation<Void, Error>] = [:]

    private override init() {
        super.init()
    }

    var dataStore: WKWebsiteDataStore {
        .default()
    }

    /// Creates a shared WKWebViewConfiguration using the persistent default website data store.
    func makeConfiguration() -> WKWebViewConfiguration {
        let config = WKWebViewConfiguration()
        config.websiteDataStore = dataStore
        config.defaultWebpagePreferences.allowsContentJavaScript = true
        return config
    }

    /// Reuses an already-authenticated WKWebView (e.g., right after login) for background fetches.
    func adoptAuthenticatedWebView(_ webView: WKWebView, forOrigin origin: URL) {
        let hostKey = origin.host ?? origin.absoluteString
        webView.navigationDelegate = self
        webViews[hostKey] = webView
    }

    /// Returns all cookies whose domain contains `domainSubstring`.
    func cookies(matching domainSubstring: String) async -> [HTTPCookie] {
        await withCheckedContinuation { continuation in
            dataStore.httpCookieStore.getAllCookies { cookies in
                let matched = cookies.filter {
                    $0.domain.localizedCaseInsensitiveContains(domainSubstring)
                }
                continuation.resume(returning: matched)
            }
        }
    }

    /// Clears cookies and website storage for the given domain substrings.
    func clearSession(domains: [String]) async {
        let allCookies: [HTTPCookie] = await withCheckedContinuation { cont in
            dataStore.httpCookieStore.getAllCookies { cont.resume(returning: $0) }
        }
        for cookie in allCookies where domains.contains(where: { cookie.domain.localizedCaseInsensitiveContains($0) }) {
            await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
                dataStore.httpCookieStore.delete(cookie) { cont.resume() }
            }
        }

        let allTypes = WKWebsiteDataStore.allWebsiteDataTypes()
        let records: [WKWebsiteDataRecord] = await withCheckedContinuation { cont in
            dataStore.fetchDataRecords(ofTypes: allTypes) { cont.resume(returning: $0) }
        }
        let matchingRecords = records.filter { record in
            domains.contains(where: { record.displayName.localizedCaseInsensitiveContains($0) })
        }
        if !matchingRecords.isEmpty {
            await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
                dataStore.removeData(ofTypes: allTypes, for: matchingRecords) { cont.resume() }
            }
        }

        for key in webViews.keys where domains.contains(where: { key.localizedCaseInsensitiveContains($0) }) {
            webViews.removeValue(forKey: key)
        }
    }

    /// Executes an authenticated same-origin `fetch()` inside a hidden WKWebView on `origin`.
    func fetch(
        origin: URL,
        pathOrURL: String,
        method: String = "GET",
        headers: [String: String] = [:],
        body: String? = nil
    ) async throws -> WebFetchResponse {
        let hostKey = origin.host ?? origin.absoluteString
        let webView = try await ensureLoadedWebView(hostKey: hostKey, origin: origin)
        return try await fetch(on: webView, pathOrURL: pathOrURL, method: method, headers: headers, body: body)
    }

    /// Executes an authenticated `fetch()` directly inside a specific WKWebView instance.
    func fetch(
        on webView: WKWebView,
        pathOrURL: String,
        method: String = "GET",
        headers: [String: String] = [:],
        body: String? = nil
    ) async throws -> WebFetchResponse {
        let js = """
        try {
            const reqInit = {
                method: method,
                credentials: 'include',
                headers: headers
            };
            if (typeof reqBody === 'string' && reqBody.length > 0) {
                reqInit.body = reqBody;
            }
            const resp = await fetch(targetUrl, reqInit);
            const text = await resp.text();
            return { ok: true, status: resp.status, body: text };
        } catch (err) {
            return { ok: false, status: -1, body: String(err && err.message ? err.message : err) };
        }
        """

        let args: [String: Any] = [
            "targetUrl": pathOrURL,
            "method": method,
            "headers": headers,
            "reqBody": body ?? ""
        ]

        let result = try await webView.callAsyncJavaScript(
            js,
            arguments: args,
            in: nil,
            contentWorld: .page
        )

        guard let dict = result as? [String: Any],
              let ok = dict["ok"] as? Bool,
              let status = dict["status"] as? Int,
              let responseBody = dict["body"] as? String else {
            throw WebSessionError.invalidJavaScriptResult
        }

        if !ok {
            throw WebSessionError.fetchFailed("웹 요청 실패: \(responseBody)")
        }

        return WebFetchResponse(status: status, body: responseBody)
    }

    private func ensureLoadedWebView(hostKey: String, origin: URL) async throws -> WKWebView {
        if let existing = webViews[hostKey],
           let currentHost = existing.url?.host,
           currentHost.localizedCaseInsensitiveContains(hostKey) {
            return existing
        }

        let webView = WKWebView(frame: NSRect(x: 0, y: 0, width: 800, height: 600), configuration: makeConfiguration())
        webView.navigationDelegate = self
        webView.customUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15"
        webViews[hostKey] = webView

        try await loadAndWait(webView: webView, url: origin)
        return webView
    }

    private func loadAndWait(webView: WKWebView, url: URL) async throws {
        let id = ObjectIdentifier(webView)
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            navigationContinuations[id] = continuation
            webView.load(URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20))

            Task { @MainActor [weak self] in
                try? await Task.sleep(nanoseconds: 15_000_000_000)
                if let pending = self?.navigationContinuations.removeValue(forKey: id) {
                    pending.resume()
                }
            }
        }
    }
}

extension WebSessionBridge: WKNavigationDelegate {
    nonisolated func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        let id = ObjectIdentifier(webView)
        Task { @MainActor in
            if let cont = navigationContinuations.removeValue(forKey: id) {
                cont.resume()
            }
        }
    }

    nonisolated func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        let id = ObjectIdentifier(webView)
        Task { @MainActor in
            if let cont = navigationContinuations.removeValue(forKey: id) {
                cont.resume(throwing: error)
            }
        }
    }

    nonisolated func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        let id = ObjectIdentifier(webView)
        Task { @MainActor in
            if let cont = navigationContinuations.removeValue(forKey: id) {
                cont.resume(throwing: error)
            }
        }
    }
}
