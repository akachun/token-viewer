import SwiftUI

/// Appearance & menu bar customization tab with a live Retina menu bar preview.
struct AppearanceSettingsView: View {
    @ObservedObject var viewModel: UsageMonitorViewModel
    @ObservedObject var settings: AppSettings

    @State private var previewDarkBackground: Bool = true

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                // Live Menu Bar Preview
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Text("상태바 실시간 미리보기")
                            .font(.system(size: 12.5, weight: .semibold))
                        Spacer()
                        Picker("", selection: $previewDarkBackground) {
                            Text("다크 메뉴바").tag(true)
                            Text("라이트 메뉴바").tag(false)
                        }
                        .pickerStyle(.segmented)
                        .frame(width: 170)
                    }

                    HStack {
                        Spacer()
                        StatusBarContentView(
                            snapshots: previewSnapshots,
                            settings: settings,
                            isDarkMenuBar: previewDarkBackground
                        )
                        .padding(.horizontal, 10)
                        .padding(.vertical, 4)
                        .background(
                            RoundedRectangle(cornerRadius: 7, style: .continuous)
                                .fill(previewDarkBackground ? Color(white: 0.14) : Color(white: 0.92))
                        )
                        .overlay(
                            RoundedRectangle(cornerRadius: 7, style: .continuous)
                                .stroke(Color.primary.opacity(0.12), lineWidth: 1)
                        )
                        Spacer()
                    }
                    .padding(.vertical, 10)
                    .background(Color(nsColor: .controlBackgroundColor).opacity(0.5))
                    .clipShape(RoundedRectangle(cornerRadius: 10))
                }

                // Display Style Selection
                VStack(alignment: .leading, spacing: 8) {
                    Text("표시 모양")
                        .font(.system(size: 12.5, weight: .semibold))

                    VStack(spacing: 6) {
                        ForEach(StatusBarDisplayStyle.allCases) { style in
                            styleRow(style)
                        }
                    }
                }

                Divider()

                // Metric, Color, and Window Strategy
                Form {
                    Picker("표시 기준", selection: $settings.metricMode) {
                        ForEach(QuotaMetricMode.allCases) { mode in
                            Text(mode.title).tag(mode)
                        }
                    }

                    Picker("색상 테마", selection: $settings.colorMode) {
                        ForEach(ColorMode.allCases) { mode in
                            Text(mode.title).tag(mode)
                        }
                    }

                    Picker("대표 윈도우 선택", selection: $settings.windowStrategy) {
                        ForEach(WindowSelectionStrategy.allCases) { strategy in
                            Text(strategy.title).tag(strategy)
                        }
                    }
                }
                .formStyle(.grouped)
                .scrollDisabled(true)
                .frame(minHeight: 145)

                // Fine-grained toggles
                VStack(alignment: .leading, spacing: 10) {
                    Text("세부 옵션")
                        .font(.system(size: 12.5, weight: .semibold))

                    Toggle("게이지/도트 옆에 퍼센트 수치 함께 표시", isOn: $settings.showPercentWithShapes)
                        .disabled(settings.displayStyle == .text || settings.displayStyle == .iconValue)

                    Toggle("상태바에 리셋까지 남은 시간 표시 (예: 2h)", isOn: $settings.showResetTimeInMenuBar)

                    Toggle("컴팩트 간격 사용 (메뉴바 공간 절약)", isOn: $settings.compactSpacing)
                }
                .padding(12)
                .background(Color(nsColor: .controlBackgroundColor).opacity(0.5))
                .clipShape(RoundedRectangle(cornerRadius: 10))
            }
            .padding(20)
        }
    }

    private func styleRow(_ style: StatusBarDisplayStyle) -> some View {
        let selected = settings.displayStyle == style
        return Button {
            settings.displayStyle = style
        } label: {
            HStack(spacing: 10) {
                Image(systemName: selected ? "largecircle.fill.circle" : "circle")
                    .foregroundStyle(selected ? Color.accentColor : Color.secondary)
                    .font(.system(size: 14))

                VStack(alignment: .leading, spacing: 2) {
                    Text(style.title)
                        .font(.system(size: 12.5, weight: .semibold))
                        .foregroundStyle(.primary)
                    Text(style.description)
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }
                Spacer()
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .background(selected ? Color.accentColor.opacity(0.10) : Color(nsColor: .controlBackgroundColor).opacity(0.5))
            .overlay(
                RoundedRectangle(cornerRadius: 8)
                    .stroke(selected ? Color.accentColor.opacity(0.5) : Color.primary.opacity(0.07), lineWidth: 1)
            )
            .clipShape(RoundedRectangle(cornerRadius: 8))
        }
        .buttonStyle(.plain)
    }

    /// Uses real snapshots if any provider is authenticated, otherwise sample preview data so the preview is always meaningful.
    private var previewSnapshots: [ProviderType: ProviderUsageSnapshot] {
        let hasAnyData = viewModel.snapshots.values.contains { !$0.windows.isEmpty }
        if hasAnyData {
            return viewModel.snapshots
        }
        let now = Date()
        return [
            .codex: ProviderUsageSnapshot(
                provider: .codex,
                authStatus: .authenticated(accountLabel: nil),
                planName: "Plus",
                windows: [UsageWindow(id: "p", label: "5시간", remainingFraction: 0.82, resetsAt: now.addingTimeInterval(7200))],
                lastUpdated: now,
                errorMessage: nil,
                isLoading: false
            ),
            .claude: ProviderUsageSnapshot(
                provider: .claude,
                authStatus: .authenticated(accountLabel: nil),
                planName: "Max",
                windows: [UsageWindow(id: "p", label: "5시간", remainingFraction: 0.19, resetsAt: now.addingTimeInterval(3600))],
                lastUpdated: now,
                errorMessage: nil,
                isLoading: false
            ),
            .gemini: ProviderUsageSnapshot(
                provider: .gemini,
                authStatus: .authenticated(accountLabel: nil),
                planName: "Pro",
                windows: [UsageWindow(id: "p", label: "Pro", remainingFraction: 0.91, resetsAt: now.addingTimeInterval(14400))],
                lastUpdated: now,
                errorMessage: nil,
                isLoading: false
            )
        ]
    }
}
