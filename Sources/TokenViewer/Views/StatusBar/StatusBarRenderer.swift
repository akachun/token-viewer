import AppKit
import SwiftUI

/// Renders the dynamic macOS menu bar visual for all enabled AI providers.
struct StatusBarContentView: View {
    let snapshots: [ProviderType: ProviderUsageSnapshot]
    let settings: AppSettings
    let isDarkMenuBar: Bool

    private var activeProviders: [ProviderType] {
        settings.orderedVisibleProviders
    }

    private var primaryTextColor: Color {
        if settings.colorMode == .monochrome {
            return .black // Rendered as template image when monochrome
        }
        return isDarkMenuBar ? .white : .black
    }

    private var secondaryTextColor: Color {
        if settings.colorMode == .monochrome {
            return .black.opacity(0.6)
        }
        return isDarkMenuBar ? .white.opacity(0.65) : .black.opacity(0.55)
    }

    private var trackColor: Color {
        if settings.colorMode == .monochrome {
            return .black.opacity(0.25)
        }
        return isDarkMenuBar ? .white.opacity(0.22) : .black.opacity(0.16)
    }

    var body: some View {
        HStack(spacing: settings.compactSpacing ? 5 : 8) {
            if activeProviders.isEmpty {
                HStack(spacing: 4) {
                    Image(systemName: "gauge.with.dots.needle.67percent")
                        .font(.system(size: 12, weight: .semibold))
                    Text("TV")
                        .font(.system(size: 11, weight: .semibold, design: .rounded))
                }
                .foregroundStyle(primaryTextColor)
            } else {
                ForEach(Array(activeProviders.enumerated()), id: \.element) { index, provider in
                    if index > 0 && settings.displayStyle == .text {
                        Text("·")
                            .font(.system(size: 11, weight: .bold))
                            .foregroundStyle(secondaryTextColor)
                    }
                    providerItemView(for: provider)
                }
            }
        }
        .padding(.horizontal, 4)
        .frame(height: 22)
    }

    @ViewBuilder
    private func providerItemView(for provider: ProviderType) -> some View {
        let snapshot = snapshots[provider] ?? .empty(for: provider)
        let window = snapshot.primaryDisplayWindow(strategy: settings.windowStrategy)
        let tint = resolvedTint(for: snapshot, window: window)
        let percentText: String = {
            if snapshot.isLoading && window == nil { return "…" }
            guard let window else { return "--" }
            return "\(window.displayPercent(for: settings.metricMode))%"
        }()
        let fillFraction: Double = window?.displayFraction(for: settings.metricMode) ?? 0
        let resetCompact: String? = settings.showResetTimeInMenuBar
            ? window?.formattedTimeUntilReset(compact: true)
            : nil

        switch settings.displayStyle {
        case .text:
            HStack(spacing: 2.5) {
                Text(provider.shortLabel)
                    .font(.system(size: 10, weight: .bold, design: .rounded))
                    .foregroundStyle(settings.colorMode == .brandColor ? tint : secondaryTextColor)
                Text(percentText)
                    .font(.system(size: 11, weight: .semibold, design: .monospaced))
                    .foregroundStyle(settings.colorMode == .healthColor && window != nil ? tint : primaryTextColor)
                if let resetCompact {
                    Text(resetCompact)
                        .font(.system(size: 9, weight: .medium, design: .rounded))
                        .foregroundStyle(secondaryTextColor)
                }
            }

        case .miniBar:
            HStack(spacing: 3.5) {
                Text(provider.shortLabel)
                    .font(.system(size: 9.5, weight: .bold, design: .rounded))
                    .foregroundStyle(primaryTextColor)

                ZStack(alignment: .leading) {
                    Capsule()
                        .fill(trackColor)
                        .frame(width: settings.compactSpacing ? 22 : 28, height: 6)
                    Capsule()
                        .fill(tint)
                        .frame(width: max(2, (settings.compactSpacing ? 22 : 28) * fillFraction), height: 6)
                }

                if settings.showPercentWithShapes {
                    Text(percentText)
                        .font(.system(size: 10, weight: .semibold, design: .monospaced))
                        .foregroundStyle(primaryTextColor)
                }
                if let resetCompact {
                    Text(resetCompact)
                        .font(.system(size: 8.5, weight: .medium, design: .rounded))
                        .foregroundStyle(secondaryTextColor)
                }
            }

        case .ring:
            HStack(spacing: 3) {
                ZStack {
                    Circle()
                        .stroke(trackColor, lineWidth: 2.2)
                    Circle()
                        .trim(from: 0, to: window == nil ? 0 : fillFraction)
                        .stroke(tint, style: StrokeStyle(lineWidth: 2.2, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                    Text(provider.singleLetter)
                        .font(.system(size: 7.5, weight: .heavy, design: .rounded))
                        .foregroundStyle(primaryTextColor)
                }
                .frame(width: 15, height: 15)

                if settings.showPercentWithShapes {
                    Text(percentText)
                        .font(.system(size: 10.5, weight: .semibold, design: .monospaced))
                        .foregroundStyle(primaryTextColor)
                }
                if let resetCompact {
                    Text(resetCompact)
                        .font(.system(size: 8.5, weight: .medium, design: .rounded))
                        .foregroundStyle(secondaryTextColor)
                }
            }

        case .statusDot:
            HStack(spacing: 3) {
                Circle()
                    .fill(window == nil ? secondaryTextColor : tint)
                    .frame(width: 7, height: 7)
                Text(provider.shortLabel)
                    .font(.system(size: 10, weight: .bold, design: .rounded))
                    .foregroundStyle(primaryTextColor)
                if settings.showPercentWithShapes {
                    Text(percentText)
                        .font(.system(size: 10.5, weight: .medium, design: .monospaced))
                        .foregroundStyle(secondaryTextColor)
                }
                if let resetCompact {
                    Text(resetCompact)
                        .font(.system(size: 8.5, weight: .medium, design: .rounded))
                        .foregroundStyle(secondaryTextColor)
                }
            }

        case .iconValue:
            HStack(spacing: 2.5) {
                Image(systemName: provider.sfSymbolName)
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(tint)
                Text(percentText)
                    .font(.system(size: 11, weight: .semibold, design: .monospaced))
                    .foregroundStyle(primaryTextColor)
                if let resetCompact {
                    Text(resetCompact)
                        .font(.system(size: 8.5, weight: .medium, design: .rounded))
                        .foregroundStyle(secondaryTextColor)
                }
            }
        }
    }

    private func resolvedTint(for snapshot: ProviderUsageSnapshot, window: UsageWindow?) -> Color {
        switch settings.colorMode {
        case .monochrome:
            return .black
        case .brandColor:
            return snapshot.provider.accentColor
        case .healthColor:
            return snapshot.healthColor(for: window, warningThreshold: settings.warningThresholdPercent)
        }
    }
}

/// Converts `StatusBarContentView` into a crisp Retina `NSImage` for `NSStatusItem.button`.
@MainActor
enum StatusBarRenderer {
    static func renderImage(
        snapshots: [ProviderType: ProviderUsageSnapshot],
        settings: AppSettings,
        buttonAppearance: NSAppearance?
    ) -> NSImage {
        let isDark: Bool = {
            let match = buttonAppearance?.bestMatch(from: [.darkAqua, .aqua])
            return match == .darkAqua
        }()

        let view = StatusBarContentView(
            snapshots: snapshots,
            settings: settings,
            isDarkMenuBar: isDark
        )

        let renderer = ImageRenderer(content: view)
        renderer.scale = NSScreen.main?.backingScaleFactor ?? 2.0

        guard let cgImage = renderer.cgImage else {
            let fallback = NSImage(systemSymbolName: "gauge.medium", accessibilityDescription: "TokenViewer") ?? NSImage()
            fallback.isTemplate = true
            return fallback
        }

        let size = NSSize(
            width: CGFloat(cgImage.width) / renderer.scale,
            height: CGFloat(cgImage.height) / renderer.scale
        )
        let image = NSImage(cgImage: cgImage, size: size)
        image.isTemplate = (settings.colorMode == .monochrome)
        return image
    }
}
