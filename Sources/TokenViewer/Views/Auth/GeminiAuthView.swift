import AppKit
import SwiftUI

/// Native window for Gemini authentication supporting:
/// 1) Antigravity Local Server Auto-Sync (Recommended — zero OAuth/billing, works with personal Google AI plans)
/// 2) Enterprise Google OAuth 2.0 (PKCE)
/// 3) Manual Access Token entry
struct GeminiAuthView: View {
    let geminiProvider: GeminiProvider
    let onComplete: () -> Void
    let onCancel: () -> Void

    @State private var selectedTab: Int = 0
    @State private var antigravityRunning: Bool = false

    @State private var clientId: String = ""
    @State private var clientSecret: String = ""
    @State private var manualAccessToken: String = ""
    @State private var manualEmail: String = ""
    @State private var isAuthenticating: Bool = false
    @State private var errorMessage: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            // Header
            HStack(spacing: 10) {
                ZStack {
                    RoundedRectangle(cornerRadius: 8)
                        .fill(ProviderType.gemini.accentColor.opacity(0.16))
                        .frame(width: 36, height: 36)
                    Image(systemName: ProviderType.gemini.sfSymbolName)
                        .foregroundStyle(ProviderType.gemini.accentColor)
                        .font(.system(size: 16, weight: .semibold))
                }
                VStack(alignment: .leading, spacing: 2) {
                    Text("Gemini (Google) 연동 설정")
                        .font(.system(size: 15, weight: .bold))
                    Text("개인 Google AI 구독은 'Antigravity 자동 연동'으로 별도 인증·과금 없이 실시간 잔여량을 조회할 수 있습니다.")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }
            }

            Picker("", selection: $selectedTab) {
                Text("Antigravity 자동 연동 (권장)").tag(0)
                Text("OAuth 2.0 (기업용)").tag(1)
                Text("Access Token 입력").tag(2)
            }
            .pickerStyle(.segmented)

            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    switch selectedTab {
                    case 0:
                        antigravityTab
                    case 1:
                        oauthTab
                    default:
                        manualTokenTab
                    }

                    if let errorMessage {
                        HStack(alignment: .top, spacing: 6) {
                            Image(systemName: "exclamationmark.triangle.fill")
                                .foregroundStyle(.red)
                                .padding(.top, 1)
                            Text(errorMessage)
                                .font(.system(size: 11))
                                .foregroundStyle(.red)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        .padding(10)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(Color.red.opacity(0.08))
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                    }
                }
                .padding(.vertical, 2)
            }

            Divider()

            // Footer buttons
            HStack {
                if selectedTab == 0 {
                    Button("Antigravity 다운로드") {
                        openURL("https://antigravity.google")
                    }
                    .buttonStyle(.link)
                    .font(.system(size: 11))

                    Text("·").foregroundStyle(.quaternary)

                    Button("실행 상태 다시 확인") {
                        checkAntigravityStatus()
                    }
                    .buttonStyle(.link)
                    .font(.system(size: 11))
                } else if selectedTab == 1 {
                    Button("① OAuth 동의 화면") {
                        openURL("https://console.cloud.google.com/apis/credentials/consent")
                    }
                    .buttonStyle(.link)
                    .font(.system(size: 11))

                    Text("·").foregroundStyle(.quaternary)

                    Button("② 사용자 인증 정보") {
                        openURL("https://console.cloud.google.com/apis/credentials")
                    }
                    .buttonStyle(.link)
                    .font(.system(size: 11))
                } else {
                    Button("Google OAuth Playground 열기") {
                        openURL("https://developers.google.com/oauthplayground/")
                    }
                    .buttonStyle(.link)
                    .font(.system(size: 11))
                }

                Spacer()

                Button("취소", action: onCancel)
                    .buttonStyle(.bordered)

                if selectedTab == 0 {
                    Button {
                        activateAntigravityAutoSync()
                    } label: {
                        if isAuthenticating {
                            HStack(spacing: 6) {
                                ProgressView().controlSize(.small)
                                Text("동기화 중...")
                            }
                        } else {
                            Text(antigravityRunning ? "지금 바로 연동하기" : "자동 연동 활성화")
                        }
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(ProviderType.gemini.accentColor)
                    .disabled(isAuthenticating)
                } else if selectedTab == 1 {
                    Button {
                        startOAuthSignIn()
                    } label: {
                        if isAuthenticating {
                            HStack(spacing: 6) {
                                ProgressView().controlSize(.small)
                                Text("브라우저 로그인 대기 중...")
                            }
                        } else {
                            Text("Google로 로그인")
                        }
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(ProviderType.gemini.accentColor)
                    .disabled(isAuthenticating || clientId.trimmingCharacters(in: .whitespaces).isEmpty)
                } else {
                    Button("토큰 저장") {
                        saveManualToken()
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(ProviderType.gemini.accentColor)
                    .disabled(manualAccessToken.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
        }
        .padding(20)
        .frame(width: 560, height: 500)
        .onAppear {
            checkAntigravityStatus()
            if let existing = geminiProvider.loadCredentials() {
                clientId = existing.clientId
                clientSecret = existing.clientSecret
                manualEmail = existing.email ?? ""
            }
        }
    }

    // MARK: - Tab 0: Antigravity Local Server Auto-Sync (Recommended)

    private var antigravityTab: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 10) {
                Image(systemName: antigravityRunning ? "checkmark.circle.fill" : "clock.badge.checkmark")
                    .font(.system(size: 18))
                    .foregroundStyle(antigravityRunning ? .green : ProviderType.gemini.accentColor)
                VStack(alignment: .leading, spacing: 2) {
                    Text(antigravityRunning ? "Antigravity 로컬 서버가 감지되었습니다!" : "Antigravity IDE 실행 대기 중")
                        .font(.system(size: 12.5, weight: .semibold))
                    Text(
                        antigravityRunning
                            ? "별도 로그인 없이 지금 바로 Gemini Pro / Flash 잔여량을 가져올 수 있습니다."
                            : "지금 '자동 연동 활성화'를 눌러 두시면, Antigravity IDE를 실행하는 즉시 자동으로 동기화됩니다."
                    )
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                }
                Spacer()
            }
            .padding(12)
            .background((antigravityRunning ? Color.green : ProviderType.gemini.accentColor).opacity(0.09))
            .overlay(
                RoundedRectangle(cornerRadius: 10)
                    .stroke((antigravityRunning ? Color.green : ProviderType.gemini.accentColor).opacity(0.3), lineWidth: 1)
            )
            .clipShape(RoundedRectangle(cornerRadius: 10))

            guideBox(
                title: "사용 가이드 (무인증 · 과금 0원)",
                steps: [
                    ("1", "우측 하단의 '자동 연동 활성화' (또는 '지금 바로 연동하기') 버튼을 클릭합니다."),
                    ("2", "Google Antigravity IDE를 설치하고 본인 Google 계정으로 로그인해 실행합니다. (하단 'Antigravity 다운로드' 링크 이용)"),
                    ("3", "Antigravity IDE가 실행되면 TokenViewer가 로컬 서버(GetUserStatus)를 자동 탐지해 Gemini Pro / Flash 잔여량(%)과 리셋 시간을 실시간 표시합니다."),
                    ("4", "Antigravity IDE를 종료해도 마지막으로 조회된 잔여량과 리셋 카운트다운은 캐시되어 상태바에 계속 유지됩니다.")
                ]
            )
        }
    }

    // MARK: - Tab 1: Enterprise OAuth 2.0

    private var oauthTab: some View {
        VStack(alignment: .leading, spacing: 12) {
            guideBox(
                title: "기업용 Gemini Code Assist OAuth 2.0 가이드",
                steps: [
                    ("1", "유료 Gemini Code Assist Standard/Enterprise 라이선스가 연결된 GCP 프로젝트에서 진행합니다."),
                    ("2", "하단 '① OAuth 동의 화면'에서 테스트 사용자에 본인 계정을 추가하고, '② 사용자 인증 정보'에서 데스크톱 앱 OAuth 클라이언트 ID를 생성합니다."),
                    ("3", "발급된 Client ID / Secret을 아래에 입력하고 'Google로 로그인'을 클릭합니다.")
                ]
            )

            VStack(alignment: .leading, spacing: 4) {
                Text("OAuth Client ID (필수)")
                    .font(.system(size: 11.5, weight: .semibold))
                HStack(spacing: 6) {
                    TextField("예: 1234567890-abcdefg.apps.googleusercontent.com", text: $clientId)
                        .textFieldStyle(.roundedBorder)
                    pasteButton { clientId = $0 }
                }
            }

            VStack(alignment: .leading, spacing: 4) {
                Text("OAuth Client Secret")
                    .font(.system(size: 11.5, weight: .semibold))
                HStack(spacing: 6) {
                    SecureField("예: GOCSPX-xxxxxxxxxxxxxxxx", text: $clientSecret)
                        .textFieldStyle(.roundedBorder)
                    pasteButton { clientSecret = $0 }
                }
            }
        }
    }

    // MARK: - Tab 2: Manual Access Token

    private var manualTokenTab: some View {
        VStack(alignment: .leading, spacing: 12) {
            guideBox(
                title: "Access Token 직접 입력 가이드 (단기 테스트용)",
                steps: [
                    ("1", "보유 중인 Google OAuth Access Token(ya29.a0...)을 아래에 붙여넣고 저장합니다."),
                    ("2", "개인 계정은 Google 정책상 403 제한이 발생할 수 있으므로 1번 탭(Antigravity 자동 연동)을 권장합니다.")
                ]
            )

            VStack(alignment: .leading, spacing: 4) {
                Text("OAuth Access Token (필수)")
                    .font(.system(size: 11.5, weight: .semibold))
                HStack(spacing: 6) {
                    SecureField("예: ya29.a0AfB_byC...", text: $manualAccessToken)
                        .textFieldStyle(.roundedBorder)
                    pasteButton { manualAccessToken = $0 }
                }
            }

            VStack(alignment: .leading, spacing: 4) {
                Text("계정 이메일 (카드 표시용, 선택)")
                    .font(.system(size: 11.5, weight: .semibold))
                TextField("예: you@gmail.com", text: $manualEmail)
                    .textFieldStyle(.roundedBorder)
            }
        }
    }

    private func pasteButton(assign: @escaping (String) -> Void) -> some View {
        Button {
            if let str = NSPasteboard.general.string(forType: .string) {
                assign(str.trimmingCharacters(in: .whitespacesAndNewlines))
            }
        } label: {
            Image(systemName: "doc.on.clipboard")
                .font(.system(size: 11))
        }
        .buttonStyle(.bordered)
        .controlSize(.small)
        .help("클립보드에서 붙여넣기")
    }

    private func guideBox(title: String, steps: [(String, String)]) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                Image(systemName: "info.circle.fill")
                    .foregroundStyle(ProviderType.gemini.accentColor)
                    .font(.system(size: 12))
                Text(title)
                    .font(.system(size: 12, weight: .semibold))
            }

            VStack(alignment: .leading, spacing: 6) {
                ForEach(steps, id: \.0) { num, text in
                    HStack(alignment: .top, spacing: 7) {
                        Text(num)
                            .font(.system(size: 10, weight: .bold, design: .rounded))
                            .frame(width: 16, height: 16)
                            .background(ProviderType.gemini.accentColor.opacity(0.18))
                            .foregroundStyle(ProviderType.gemini.accentColor)
                            .clipShape(Circle())
                            .padding(.top, 1)

                        Text(text)
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                            .textSelection(.enabled)
                    }
                }
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color(nsColor: .controlBackgroundColor).opacity(0.6))
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .stroke(Color.primary.opacity(0.08), lineWidth: 1)
        )
        .clipShape(RoundedRectangle(cornerRadius: 10))
    }

    private func checkAntigravityStatus() {
        Task {
            antigravityRunning = await AntigravityQuotaService.shared.isAntigravityRunning()
        }
    }

    private func activateAntigravityAutoSync() {
        isAuthenticating = true
        errorMessage = nil
        geminiProvider.enableAntigravityMode()
        isAuthenticating = false
        onComplete()
    }

    private func openURL(_ string: String) {
        if let url = URL(string: string) {
            NSWorkspace.shared.open(url)
        }
    }

    private func startOAuthSignIn() {
        isAuthenticating = true
        errorMessage = nil
        Task {
            do {
                let creds = try await GoogleOAuthService.shared.signIn(
                    clientId: clientId,
                    clientSecret: clientSecret
                )
                geminiProvider.saveCredentials(creds)
                isAuthenticating = false
                onComplete()
            } catch {
                isAuthenticating = false
                errorMessage = error.localizedDescription
            }
        }
    }

    private func saveManualToken() {
        geminiProvider.saveManualAccessToken(
            manualAccessToken,
            email: manualEmail.isEmpty ? nil : manualEmail
        )
        onComplete()
    }
}
