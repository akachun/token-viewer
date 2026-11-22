import SwiftUI

/// Manages provider authentication, menu bar visibility toggles, and display ordering.
struct ProvidersSettingsView: View {
    @ObservedObject var viewModel: UsageMonitorViewModel
    @ObservedObject var settings: AppSettings

    @State private var expandedManualProvider: ProviderType?
    @State private var codexManualToken: String = ""
    @State private var claudeManualSessionKey: String = ""

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                VStack(alignment: .leading, spacing: 3) {
                    Text("AI 구독 계정 및 상태바 표시 순서")
                        .font(.system(size: 13, weight: .semibold))
                    Text("각 AI 서비스에 앱 내에서 독립적으로 로그인하고, 상태바 표시 여부 및 순서를 조정할 수 있습니다.")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }

                VStack(spacing: 10) {
                    ForEach(Array(settings.providerOrder.enumerated()), id: \.element) { index, provider in
                        providerRow(provider: provider, index: index, total: settings.providerOrder.count)
                    }
                }
            }
            .padding(20)
        }
        .onAppear {
            codexManualToken = viewModel.codexProvider.loadManualToken() ?? ""
            claudeManualSessionKey = viewModel.claudeProvider.loadManualSessionKey() ?? ""
        }
    }

    private func providerRow(provider: ProviderType, index: Int, total: Int) -> some View {
        let snapshot = viewModel.snapshot(for: provider)
        let isVisibleBinding = Binding<Bool>(
            get: { settings.isVisible(provider) },
            set: { settings.setVisible(provider, visible: $0) }
        )

        return VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                // Reorder arrows
                VStack(spacing: 2) {
                    Button {
                        settings.moveProviderUp(provider)
                    } label: {
                        Image(systemName: "chevron.up")
                            .font(.system(size: 9, weight: .bold))
                    }
                    .buttonStyle(.borderless)
                    .disabled(index == 0)

                    Button {
                        settings.moveProviderDown(provider)
                    } label: {
                        Image(systemName: "chevron.down")
                            .font(.system(size: 9, weight: .bold))
                    }
                    .buttonStyle(.borderless)
                    .disabled(index == total - 1)
                }
                .frame(width: 18)

                // Provider badge
                ZStack {
                    RoundedRectangle(cornerRadius: 8)
                        .fill(provider.accentColor.opacity(0.16))
                        .frame(width: 32, height: 32)
                    Image(systemName: provider.sfSymbolName)
                        .foregroundStyle(provider.accentColor)
                        .font(.system(size: 14, weight: .semibold))
                }

                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 6) {
                        Text(provider.displayName)
                            .font(.system(size: 13, weight: .semibold))
                        Text("(\(provider.companyName))")
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                    }

                    HStack(spacing: 5) {
                        Circle()
                            .fill(snapshot.authStatus.isAuthenticated ? Color.green : Color.secondary.opacity(0.5))
                            .frame(width: 6, height: 6)
                        Text(snapshot.authStatus.statusText)
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                    }
                }

                Spacer()

                Toggle("상태바 표시", isOn: isVisibleBinding)
                    .toggleStyle(.switch)
                    .controlSize(.small)
                    .labelsHidden()
                    .help("상태바에 \(provider.displayName) 표시 여부")

                if snapshot.authStatus.isAuthenticated {
                    Button("재인증") {
                        viewModel.startLogin(for: provider)
                    }
                    .buttonStyle(.bordered)
                    .controlSize(.small)

                    Button("로그아웃") {
                        Task { await viewModel.signOut(provider) }
                    }
                    .buttonStyle(.bordered)
                    .tint(.red)
                    .controlSize(.small)
                } else {
                    Button("로그인") {
                        viewModel.startLogin(for: provider)
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(provider.accentColor)
                    .controlSize(.small)
                }
            }

            // Optional manual credential fallback for Codex & Claude
            if provider == .codex || provider == .claude {
                DisclosureGroup(
                    isExpanded: Binding(
                        get: { expandedManualProvider == provider },
                        set: { expandedManualProvider = $0 ? provider : nil }
                    )
                ) {
                    if provider == .codex {
                        HStack(spacing: 8) {
                            SecureField("ChatGPT Access Token (선택, 수동 입력 시 우선 사용)", text: $codexManualToken)
                                .textFieldStyle(.roundedBorder)
                                .font(.system(size: 11))
                            Button("저장") {
                                viewModel.codexProvider.saveManualToken(codexManualToken)
                                Task { await viewModel.refresh(.codex) }
                            }
                            .buttonStyle(.bordered)
                            .controlSize(.small)
                        }
                        .padding(.top, 6)
                    } else if provider == .claude {
                        HStack(spacing: 8) {
                            SecureField("Claude sessionKey 쿠키 값 (sk-ant-sid01-...)", text: $claudeManualSessionKey)
                                .textFieldStyle(.roundedBorder)
                                .font(.system(size: 11))
                            Button("저장") {
                                Task {
                                    await viewModel.claudeProvider.saveManualSessionKey(claudeManualSessionKey)
                                    await viewModel.refresh(.claude)
                                }
                            }
                            .buttonStyle(.bordered)
                            .controlSize(.small)
                        }
                        .padding(.top, 6)
                    }
                } label: {
                    Text("고급: 수동 토큰/세션 쿠키 입력")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }
            }
        }
        .padding(12)
        .background(Color(nsColor: .controlBackgroundColor).opacity(0.55))
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .stroke(Color.primary.opacity(0.08), lineWidth: 1)
        )
        .clipShape(RoundedRectangle(cornerRadius: 10))
    }
}
