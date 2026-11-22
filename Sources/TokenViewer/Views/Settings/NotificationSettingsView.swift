import SwiftUI

/// Configures macOS quota threshold notifications.
struct NotificationSettingsView: View {
    @ObservedObject var settings: AppSettings

    var body: some View {
        Form {
            Section("잔여량 부족 알림") {
                Toggle("임계치 도달 시 macOS 알림 받기", isOn: $settings.notificationsEnabled)

                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text("경고 임계치 (잔여량 기준)")
                        Spacer()
                        Text("\(settings.warningThresholdPercent)% 이하")
                            .font(.system(size: 12, weight: .semibold, design: .monospaced))
                            .foregroundStyle(.orange)
                    }
                    Slider(
                        value: Binding(
                            get: { Double(settings.warningThresholdPercent) },
                            set: { settings.warningThresholdPercent = Int($0) }
                        ),
                        in: 5...50,
                        step: 5
                    )
                    .disabled(!settings.notificationsEnabled)
                }
            }

            Section("알림 대상 AI") {
                ForEach(ProviderType.allCases) { provider in
                    Toggle(provider.displayName, isOn: Binding(
                        get: { settings.notifiedProviders.contains(provider) },
                        set: { enabled in
                            if enabled {
                                settings.notifiedProviders.insert(provider)
                            } else {
                                settings.notifiedProviders.remove(provider)
                            }
                        }
                    ))
                    .disabled(!settings.notificationsEnabled)
                }
            }

            Section {
                HStack {
                    Button("테스트 알림 보내기") {
                        NotificationService.shared.requestAuthorizationIfNeeded()
                        NotificationService.shared.sendTestNotification()
                    }
                    .buttonStyle(.bordered)

                    Spacer()

                    Button("알림 중복 기록 초기화") {
                        NotificationService.shared.resetAlertHistory()
                    }
                    .buttonStyle(.borderless)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                }
            }
        }
        .formStyle(.grouped)
        .padding(.vertical, 6)
    }
}
