import AppKit
import Combine
import SwiftUI

/// Owns the macOS NSStatusItem, NSPopover, Settings window, and per-provider Login windows.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private var popover: NSPopover!
    private var settingsWindow: NSWindow?
    private var geminiAuthWindow: NSWindow?
    private var activeWebLoginController: WebLoginWindowController?

    private let settings = AppSettings()
    private lazy var viewModel = UsageMonitorViewModel(settings: settings)
    private var cancellables: Set<AnyCancellable> = []
    private var appearanceObservation: NSKeyValueObservation?
    private var eventMonitor: Any?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)

        setupMainMenu()
        setupStatusItem()
        setupPopover()
        bindUpdates()

        viewModel.onRequestLogin = { [weak self] provider in
            self?.presentLogin(for: provider)
        }
        viewModel.onRequestSettings = { [weak self] in
            self?.openSettingsWindow()
        }

        renderStatusBar()
    }

    func applicationWillTerminate(_ notification: Notification) {
        if let eventMonitor {
            NSEvent.removeMonitor(eventMonitor)
        }
    }

    // MARK: - Setup

    /// Registers a standard Edit menu so Cmd+C, Cmd+V, Cmd+X, Cmd+A, and Cmd+Z work in
    /// TextField, SecureField, and WKWebView across all windows of this menu bar app.
    private func setupMainMenu() {
        let mainMenu = NSMenu()

        // App Menu
        let appMenuItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "TokenViewer 종료", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appMenuItem.submenu = appMenu
        mainMenu.addItem(appMenuItem)

        // Edit Menu (enables standard clipboard & text editing shortcuts via responder chain)
        let editMenuItem = NSMenuItem()
        let editMenu = NSMenu(title: "편집")
        editMenu.addItem(withTitle: "실행 취소", action: Selector(("undo:")), keyEquivalent: "z")
        editMenu.addItem(withTitle: "다시 실행", action: Selector(("redo:")), keyEquivalent: "Z")
        editMenu.addItem(.separator())
        editMenu.addItem(withTitle: "오려두기", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: "복사", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "붙여넣기", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: "전체 선택", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editMenuItem.submenu = editMenu
        mainMenu.addItem(editMenuItem)

        // Window Menu
        let windowMenuItem = NSMenuItem()
        let windowMenu = NSMenu(title: "윈도우")
        windowMenu.addItem(withTitle: "닫기", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        windowMenuItem.submenu = windowMenu
        mainMenu.addItem(windowMenuItem)

        NSApp.mainMenu = mainMenu
    }

    private func setupStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        guard let button = statusItem.button else { return }

        button.target = self
        button.action = #selector(handleStatusBarClick(_:))
        button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        button.imagePosition = .imageOnly

        appearanceObservation = button.observe(\.effectiveAppearance, options: [.new]) { [weak self] _, _ in
            Task { @MainActor in
                self?.renderStatusBar()
            }
        }
    }

    private func setupPopover() {
        popover = NSPopover()
        popover.behavior = .transient
        popover.animates = true
        popover.contentViewController = NSHostingController(
            rootView: PopoverView(viewModel: viewModel, settings: settings)
        )
    }

    private func bindUpdates() {
        viewModel.$snapshots
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in
                self?.renderStatusBar()
            }
            .store(in: &cancellables)

        settings.objectWillChange
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in
                // Run on next tick after @Published property finishes mutating
                DispatchQueue.main.async {
                    self?.renderStatusBar()
                }
            }
            .store(in: &cancellables)
    }

    private func renderStatusBar() {
        guard let button = statusItem?.button else { return }
        let image = StatusBarRenderer.renderImage(
            snapshots: viewModel.snapshots,
            settings: settings,
            buttonAppearance: button.effectiveAppearance
        )
        button.image = image
        button.toolTip = buildTooltip()
    }

    private func buildTooltip() -> String {
        let lines = settings.orderedVisibleProviders.map { provider -> String in
            let snap = viewModel.snapshot(for: provider)
            if let win = snap.primaryDisplayWindow(strategy: settings.windowStrategy) {
                let pct = win.displayPercent(for: settings.metricMode)
                let reset = win.formattedTimeUntilReset(compact: true).map { " (\($0))" } ?? ""
                return "\(provider.displayName): \(pct)% \(settings.metricMode.shortTitle)\(reset)"
            }
            return "\(provider.displayName): \(snap.authStatus.statusText)"
        }
        return lines.isEmpty ? "TokenViewer" : lines.joined(separator: "\n")
    }

    // MARK: - Actions

    @objc private func handleStatusBarClick(_ sender: NSStatusBarButton) {
        guard let event = NSApp.currentEvent else {
            togglePopover(sender)
            return
        }

        if event.type == .rightMouseUp {
            showContextMenu()
        } else {
            togglePopover(sender)
        }
    }

    private func togglePopover(_ sender: NSView) {
        if popover.isShown {
            popover.performClose(sender)
        } else {
            NSApp.activate(ignoringOtherApps: true)
            popover.show(relativeTo: sender.bounds, of: sender, preferredEdge: .minY)
            popover.contentViewController?.view.window?.makeKey()
        }
    }

    private func showContextMenu() {
        let menu = NSMenu()
        menu.addItem(withTitle: "전체 새로고침", action: #selector(refreshAllMenuAction), keyEquivalent: "r")
        menu.addItem(withTitle: "설정 열기...", action: #selector(openSettingsMenuAction), keyEquivalent: ",")
        menu.addItem(.separator())
        menu.addItem(withTitle: "TokenViewer 종료", action: #selector(quitMenuAction), keyEquivalent: "q")

        for item in menu.items {
            item.target = self
        }

        statusItem.menu = menu
        statusItem.button?.performClick(nil)
        statusItem.menu = nil
    }

    @objc private func refreshAllMenuAction() {
        Task { await viewModel.refreshAll() }
    }

    @objc private func openSettingsMenuAction() {
        openSettingsWindow()
    }

    @objc private func quitMenuAction() {
        NSApp.terminate(nil)
    }

    // MARK: - Windows

    func openSettingsWindow() {
        popover.performClose(nil)

        if let existing = settingsWindow {
            NSApp.activate(ignoringOtherApps: true)
            existing.makeKeyAndOrderFront(nil)
            return
        }

        let hosting = NSHostingController(rootView: SettingsView(viewModel: viewModel, settings: settings))
        let win = NSWindow(contentViewController: hosting)
        win.title = "TokenViewer 설정"
        win.styleMask = [.titled, .closable, .miniaturizable]
        win.center()
        win.isReleasedWhenClosed = false
        self.settingsWindow = win

        NSApp.activate(ignoringOtherApps: true)
        win.makeKeyAndOrderFront(nil)
    }

    func presentLogin(for provider: ProviderType) {
        popover.performClose(nil)

        switch provider {
        case .codex, .claude:
            activeWebLoginController?.close()
            let controller = WebLoginWindowController(
                provider: provider,
                onComplete: { [weak self] in
                    self?.activeWebLoginController = nil
                    self?.viewModel.handleLoginCompleted(for: provider)
                },
                onCancel: { [weak self] in
                    self?.activeWebLoginController = nil
                    self?.viewModel.handleLoginCancelled(for: provider)
                }
            )
            activeWebLoginController = controller
            controller.show()

        case .gemini:
            if let existing = geminiAuthWindow {
                existing.close()
            }
            let authView = GeminiAuthView(
                geminiProvider: viewModel.geminiProvider,
                onComplete: { [weak self] in
                    self?.geminiAuthWindow?.close()
                    self?.geminiAuthWindow = nil
                    self?.viewModel.handleLoginCompleted(for: .gemini)
                },
                onCancel: { [weak self] in
                    self?.geminiAuthWindow?.close()
                    self?.geminiAuthWindow = nil
                    self?.viewModel.handleLoginCancelled(for: .gemini)
                }
            )
            let hosting = NSHostingController(rootView: authView)
            let win = NSWindow(contentViewController: hosting)
            win.title = "Gemini (Google) 인증 — TokenViewer"
            win.styleMask = [.titled, .closable]
            win.center()
            win.isReleasedWhenClosed = false
            self.geminiAuthWindow = win

            NSApp.activate(ignoringOtherApps: true)
            win.makeKeyAndOrderFront(nil)
        }
    }
}
