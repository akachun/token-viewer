import SwiftUI

/// Tabbed Preferences window for TokenViewer.
struct SettingsView: View {
    @ObservedObject var viewModel: UsageMonitorViewModel
    @ObservedObject var settings: AppSettings

    var body: some View {
        TabView {
            AppearanceSettingsView(viewModel: viewModel, settings: settings)
                .tabItem {
                    Label("상태바 모양", systemImage: "paintbrush")
                }

            ProvidersSettingsView(viewModel: viewModel, settings: settings)
                .tabItem {
                    Label("AI 계정 · 순서", systemImage: "person.badge.key")
                }

            NotificationSettingsView(settings: settings)
                .tabItem {
                    Label("알림", systemImage: "bell.badge")
                }

            generalTab
                .tabItem {
                    Label("일반", systemImage: "gearshape")
                }
        }
        .frame(width: 560, height: 520)
    }

    private var generalTab: some View {
        Form {
            Section("동기화") {
                Picker("자동 갱신 주기", selection: $settings.refreshInterval) {
                    ForEach(RefreshIntervalOption.allCases) { option in
                        Text(option.title).tag(option)
                    }
                }
            }

            Section("시스템") {
                Toggle("macOS 로그인 시 자동 실행", isOn: $settings.launchAtLogin)
                Text("자동 실행은 TokenViewer.app 번들로 실행할 때 시스템 설정에 등록됩니다.")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }

            Section("개발 / 미리보기") {
                Toggle("데모 데이터 모드 (로그인 없이 UI/알림 테스트)", isOn: $settings.demoModeEnabled)
            }

            Section("정보") {
                HStack {
                    Text("TokenViewer")
                        .fontWeight(.semibold)
                    Spacer()
                    Text("v1.0.0")
                        .foregroundStyle(.secondary)
                }
                Text("Codex, Claude, Gemini 구독 잔여량을 macOS 메뉴바에서 독립적으로 모니터링합니다.")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .padding(.vertical, 6)
    }
}
