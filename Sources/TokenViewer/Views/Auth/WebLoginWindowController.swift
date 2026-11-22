import AppKit
import SwiftUI
import WebKit

/// Observable state for the login toolbar inside WebLoginWindowController.
@MainActor
private final class WebLoginToolbarState: ObservableObject {
    @Published var isChecking: Bool = false
    @Published var statusMessage: String = "로그인을 완료하면 자동으로 감지됩니다."
    @Published var isError: Bool = false
}

/// Native macOS window presenting an in-app WKWebView for independent web login (Codex / Claude).
@MainActor
final class WebLoginWindowController: NSObject, NSWindowDelegate {
    private var window: NSWindow?
    private let webView: WKWebView
    private let toolbarState = WebLoginToolbarState()
    private let provider: ProviderType
    private let onComplete: () -> Void
    private let onCancel: () -> Void
    private var completed = false
    private var pollTask: Task<Void, Never>?

    init(
        provider: ProviderType,
        onComplete: @escaping () -> Void,
        onCancel: @escaping () -> Void
    ) {
        self.provider = provider
        self.onComplete = onComplete
        self.onCancel = onCancel
        let wv = WKWebView(frame: .zero, configuration: WebSessionBridge.shared.makeConfiguration())
        wv.customUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15"
        self.webView = wv
        super.init()
    }

    func show() {
        let contentView = WebLoginContainerView(
            provider: provider,
            webView: webView,
            state: toolbarState,
            onVerifyNow: { [weak self] in
                Task { await self?.checkLoginStatus(manualTrigger: true) }
            },
            onCancel: { [weak self] in
                self?.window?.close()
            }
        )

        let hosting = NSHostingController(rootView: contentView)
        let win = NSWindow(contentViewController: hosting)
        win.title = "\(provider.displayName) 로그인 — TokenViewer"
        win.setContentSize(NSSize(width: 540, height: 700))
        win.styleMask = [.titled, .closable, .resizable, .miniaturizable]
        win.center()
        win.delegate = self
        win.isReleasedWhenClosed = false

        self.window = win
        webView.load(URLRequest(url: provider.loginURL))

        NSApp.activate(ignoringOtherApps: true)
        win.makeKeyAndOrderFront(nil)

        startAutoDetectionPolling()
    }

    func close() {
        pollTask?.cancel()
        window?.close()
    }

    func windowWillClose(_ notification: Notification) {
        pollTask?.cancel()
        if !completed {
            onCancel()
        }
    }

    private func startAutoDetectionPolling() {
        pollTask?.cancel()
        pollTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 3_000_000_000)
            while !Task.isCancelled {
                await self?.checkLoginStatus(manualTrigger: false)
                try? await Task.sleep(nanoseconds: 2_500_000_000)
            }
        }
    }

    private func checkLoginStatus(manualTrigger: Bool) async {
        guard !completed else { return }

        if manualTrigger {
            toolbarState.isChecking = true
            toolbarState.isError = false
            toolbarState.statusMessage = "로그인 세션 확인 중..."
        }
        defer {
            if manualTrigger {
                toolbarState.isChecking = false
            }
        }

        let currentHost = webView.url?.host?.lowercased() ?? ""

        switch provider {
        case .codex:
            // Only query /api/auth/session when the WebView is on chatgpt.com (not auth.openai.com)
            guard currentHost.contains("chatgpt.com") else {
                if manualTrigger {
                    toolbarState.isError = true
                    toolbarState.statusMessage = "ChatGPT 메인 화면으로 이동된 후 '인증 확인'을 눌러주세요."
                }
                return
            }

            do {
                let resp = try await WebSessionBridge.shared.fetch(
                    on: webView,
                    pathOrURL: "/api/auth/session"
                )
                if resp.status == 200,
                   let data = resp.body.data(using: .utf8),
                   let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                   let token = json["accessToken"] as? String, !token.isEmpty {
                    finishSuccess(origin: URL(string: "https://chatgpt.com")!)
                    return
                } else if manualTrigger {
                    toolbarState.isError = true
                    toolbarState.statusMessage = "아직 로그인 세션이 감지되지 않았습니다. 로그인 완료 후 다시 시도해 주세요."
                }
            } catch {
                if manualTrigger {
                    toolbarState.isError = true
                    toolbarState.statusMessage = "확인 실패: \(error.localizedDescription)"
                }
            }

        case .claude:
            guard currentHost.contains("claude.ai") else {
                if manualTrigger {
                    toolbarState.isError = true
                    toolbarState.statusMessage = "claude.ai 로그인 완료 후 다시 시도해 주세요."
                }
                return
            }

            let cookies = await WebSessionBridge.shared.cookies(matching: "claude.ai")
            let hasSessionCookie = cookies.contains { $0.name == "sessionKey" && !$0.value.isEmpty }
            let isOnLoginPath = webView.url?.path.lowercased().contains("/login") == true

            if hasSessionCookie && !isOnLoginPath {
                // Verify /api/organizations directly on the live WebView
                if let resp = try? await WebSessionBridge.shared.fetch(
                    on: webView,
                    pathOrURL: "/api/organizations",
                    headers: [
                        "Accept": "application/json",
                        "anthropic-client-platform": "web_claude_ai"
                    ]
                ), resp.status == 200, resp.body.trimmingCharacters(in: .whitespacesAndNewlines).hasPrefix("[") {
                    finishSuccess(origin: URL(string: "https://claude.ai")!)
                    return
                }
            }

            if manualTrigger {
                do {
                    let resp = try await WebSessionBridge.shared.fetch(
                        on: webView,
                        pathOrURL: "/api/organizations",
                        headers: [
                            "Accept": "application/json",
                            "anthropic-client-platform": "web_claude_ai"
                        ]
                    )
                    if resp.status == 200 && resp.body.trimmingCharacters(in: .whitespacesAndNewlines).hasPrefix("[") {
                        finishSuccess(origin: URL(string: "https://claude.ai")!)
                    } else {
                        toolbarState.isError = true
                        toolbarState.statusMessage = "세션 미감지 (HTTP \(resp.status)). 로그인 완료 후 다시 눌러주세요."
                    }
                } catch {
                    toolbarState.isError = true
                    toolbarState.statusMessage = "확인 실패: \(error.localizedDescription)"
                }
            }

        case .gemini:
            break
        }
    }

    private func finishSuccess(origin: URL) {
        completed = true
        pollTask?.cancel()
        // Mark verified session flag so providers don't rely solely on cookie name heuristics
        UserDefaults.standard.set(true, forKey: "tv.webSessionVerified.\(provider.rawValue)")
        // Hand the live, Cloudflare-cleared WKWebView directly to WebSessionBridge
        WebSessionBridge.shared.adoptAuthenticatedWebView(webView, forOrigin: origin)
        window?.close()
        onComplete()
    }
}

private struct WebLoginContainerView: View {
    let provider: ProviderType
    let webView: WKWebView
    @ObservedObject var state: WebLoginToolbarState
    let onVerifyNow: () -> Void
    let onCancel: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Image(systemName: provider.sfSymbolName)
                    .foregroundStyle(provider.accentColor)
                    .font(.system(size: 14, weight: .semibold))
                VStack(alignment: .leading, spacing: 2) {
                    Text("\(provider.displayName) 웹 로그인")
                        .font(.system(size: 13, weight: .semibold))
                    Text(state.statusMessage)
                        .font(.system(size: 10.5))
                        .foregroundStyle(state.isError ? .orange : .secondary)
                        .lineLimit(2)
                }
                Spacer()
                Button {
                    onVerifyNow()
                } label: {
                    if state.isChecking {
                        ProgressView().controlSize(.small)
                    } else {
                        Text("인증 확인")
                    }
                }
                .buttonStyle(.borderedProminent)
                .tint(provider.accentColor)
                .controlSize(.small)
                .disabled(state.isChecking)

                Button("취소", action: onCancel)
                    .buttonStyle(.bordered)
                    .controlSize(.small)
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            .background(Color(nsColor: .windowBackgroundColor))

            Divider()

            LiveWebViewRepresentable(webView: webView)
        }
    }
}

private struct LiveWebViewRepresentable: NSViewRepresentable {
    let webView: WKWebView

    func makeNSView(context: Context) -> WKWebView {
        webView
    }

    func updateNSView(_ nsView: WKWebView, context: Context) {}
}
