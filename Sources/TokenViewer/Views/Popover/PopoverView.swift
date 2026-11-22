import SwiftUI

/// Main menu bar popover view showing all configured AI subscriptions, quick display toggles, and actions.
struct PopoverView: View {
    @ObservedObject var viewModel: UsageMonitorViewModel
    @ObservedObject var settings: AppSettings

    var body: some View {
        VStack(spacing: 0) {
            headerBar
            Divider()

            ScrollView {
                VStack(spacing: 10) {
                    ForEach(settings.providerOrder) { provider in
                        ProviderCardView(
                            snapshot: viewModel.snapshot(for: provider),
                            settings: settings,
                            onRefresh: {
                                Task { await viewModel.refresh(provider) }
                            },
                            onLogin: {
                                viewModel.startLogin(for: provider)
                            },
                            onSignOut: {
                                Task { await viewModel.signOut(provider) }
                            }
                        )
                    }
                }
                .padding(12)
            }
            .frame(maxHeight: 430)

            Divider()
            footerBar
        }
        .frame(width: 356)
    }

    private var headerBar: some View {
        HStack(spacing: 8) {
            Image(systemName: "gauge.with.dots.needle.67percent")
                .font(.system(size: 14, weight: .semibold))
                .foregroundStyle(.tint)

            Text("TokenViewer")
                .font(.system(size: 14, weight: .bold))

            if settings.demoModeEnabled {
                Text("DEMO")
                    .font(.system(size: 9, weight: .heavy, design: .rounded))
                    .padding(.horizontal, 5)
                    .padding(.vertical, 2)
                    .background(Color.purple.opacity(0.18))
                    .foregroundStyle(.purple)
                    .clipShape(Capsule())
            }

            Spacer()

            // Quick toggle between Remaining % and Used %
            Button {
                settings.metricMode = (settings.metricMode == .remaining) ? .used : .remaining
            } label: {
                Text(settings.metricMode == .remaining ? "잔여 %" : "사용 %")
                    .font(.system(size: 10.5, weight: .medium))
                    .padding(.horizontal, 6)
                    .padding(.vertical, 3)
                    .background(Color.primary.opacity(0.07))
                    .clipShape(Capsule())
            }
            .buttonStyle(.plain)
            .help("잔여량 / 사용량 표시 전환")

            Button {
                Task { await viewModel.refreshAll() }
            } label: {
                Image(systemName: "arrow.clockwise")
                    .font(.system(size: 12, weight: .medium))
                    .rotationEffect(.degrees(viewModel.isRefreshingAll ? 360 : 0))
                    .animation(
                        viewModel.isRefreshingAll
                            ? .linear(duration: 0.8).repeatForever(autoreverses: false)
                            : .default,
                        value: viewModel.isRefreshingAll
                    )
            }
            .buttonStyle(.borderless)
            .disabled(viewModel.isRefreshingAll)
            .help("전체 새로고침")

            Button {
                viewModel.onRequestSettings?()
            } label: {
                Image(systemName: "gearshape")
                    .font(.system(size: 13, weight: .medium))
            }
            .buttonStyle(.borderless)
            .help("설정 열기")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
    }

    private var footerBar: some View {
        HStack {
            if let latest = latestUpdatedDate {
                Text("업데이트: \(latest.formatted(date: .omitted, time: .shortened))")
                    .font(.system(size: 10.5))
                    .foregroundStyle(.secondary)
            } else {
                Text("카드를 우클릭해 추가 옵션을 볼 수 있습니다")
                    .font(.system(size: 10.5))
                    .foregroundStyle(.secondary)
            }

            Spacer()

            Button("설정") {
                viewModel.onRequestSettings?()
            }
            .buttonStyle(.plain)
            .font(.system(size: 11, weight: .medium))
            .foregroundStyle(.secondary)

            Text("·")
                .foregroundStyle(.quaternary)

            Button("종료") {
                NSApplication.shared.terminate(nil)
            }
            .buttonStyle(.plain)
            .font(.system(size: 11, weight: .medium))
            .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
    }

    private var latestUpdatedDate: Date? {
        viewModel.snapshots.values.compactMap(\.lastUpdated).max()
    }
}
