import SwiftUI

/// Displays a single AI provider's status, quota windows, reset timers, and quick auth actions inside the Popover.
struct ProviderCardView: View {
    let snapshot: ProviderUsageSnapshot
    let settings: AppSettings
    let onRefresh: () -> Void
    let onLogin: () -> Void
    let onSignOut: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            // Top header row
            HStack(spacing: 8) {
                ZStack {
                    RoundedRectangle(cornerRadius: 7, style: .continuous)
                        .fill(snapshot.provider.accentColor.opacity(0.16))
                        .frame(width: 28, height: 28)
                    Image(systemName: snapshot.provider.sfSymbolName)
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(snapshot.provider.accentColor)
                }

                VStack(alignment: .leading, spacing: 1) {
                    HStack(spacing: 5) {
                        Text(snapshot.provider.displayName)
                            .font(.system(size: 13, weight: .semibold))
                        if let plan = snapshot.planName, !plan.isEmpty {
                            Text(plan)
                                .font(.system(size: 10, weight: .medium))
                                .padding(.horizontal, 5)
                                .padding(.vertical, 1.5)
                                .background(snapshot.provider.accentColor.opacity(0.14))
                                .foregroundStyle(snapshot.provider.accentColor)
                                .clipShape(Capsule())
                        }
                    }

                    Text(snapshot.authStatus.statusText)
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }

                Spacer()

                if snapshot.isLoading {
                    ProgressView()
                        .controlSize(.small)
                } else if snapshot.authStatus.isAuthenticated {
                    Button(action: onRefresh) {
                        Image(systemName: "arrow.clockwise")
                            .font(.system(size: 11, weight: .medium))
                    }
                    .buttonStyle(.borderless)
                    .help("새로고침")
                } else {
                    Button("로그인", action: onLogin)
                        .buttonStyle(.borderedProminent)
                        .tint(snapshot.provider.accentColor)
                        .controlSize(.small)
                }
            }

            // Quota windows
            if snapshot.authStatus.isAuthenticated && !snapshot.windows.isEmpty {
                VStack(spacing: 8) {
                    ForEach(snapshot.windows) { window in
                        quotaWindowRow(window)
                    }
                }
            } else if let error = snapshot.errorMessage {
                HStack(spacing: 6) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .font(.system(size: 11))
                        .foregroundStyle(.orange)
                    Text(error)
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer()
                    if case .expired = snapshot.authStatus {
                        Button("재인증", action: onLogin)
                            .buttonStyle(.bordered)
                            .controlSize(.mini)
                    }
                }
                .padding(8)
                .background(Color.orange.opacity(0.08))
                .clipShape(RoundedRectangle(cornerRadius: 8))
            } else if snapshot.authStatus.isAuthenticated && snapshot.windows.isEmpty && !snapshot.isLoading {
                Text(snapshot.notice ?? "표시할 쿼터 윈도우가 없습니다.")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(12)
        .background(Color(nsColor: .controlBackgroundColor).opacity(0.65))
        .overlay(
            RoundedRectangle(cornerRadius: 11, style: .continuous)
                .stroke(Color.primary.opacity(0.07), lineWidth: 1)
        )
        .clipShape(RoundedRectangle(cornerRadius: 11, style: .continuous))
        .contextMenu {
            Button("새로고침", action: onRefresh)
            Button("웹 대시보드 열기") {
                NSWorkspace.shared.open(snapshot.provider.usageDashboardURL)
            }
            Divider()
            if snapshot.authStatus.isAuthenticated {
                Button("다시 로그인", action: onLogin)
                Button("로그아웃", role: .destructive, action: onSignOut)
            } else {
                Button("로그인", action: onLogin)
            }
        }
    }

    private func quotaWindowRow(_ window: UsageWindow) -> some View {
        let barColor = snapshot.healthColor(for: window, warningThreshold: settings.warningThresholdPercent)
        let pct = window.displayPercent(for: settings.metricMode)
        let fraction = window.displayFraction(for: settings.metricMode)

        return VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(window.label)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(.primary)

                Spacer()

                if let resetText = window.formattedTimeUntilReset() {
                    Text("리셋 \(resetText)")
                        .font(.system(size: 10))
                        .foregroundStyle(.secondary)
                }

                Text("\(pct)% \(settings.metricMode.shortTitle)")
                    .font(.system(size: 11, weight: .semibold, design: .monospaced))
                    .foregroundStyle(barColor)
            }

            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    Capsule()
                        .fill(Color.primary.opacity(0.09))
                        .frame(height: 6)
                    Capsule()
                        .fill(barColor)
                        .frame(width: max(4, geo.size.width * fraction), height: 6)
                        .animation(.easeInOut(duration: 0.25), value: fraction)
                }
            }
            .frame(height: 6)
        }
    }
}
