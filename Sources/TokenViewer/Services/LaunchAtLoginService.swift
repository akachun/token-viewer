import Foundation
import ServiceManagement

/// Manages macOS Launch at Login via SMAppService.
enum LaunchAtLoginService {
    static var isEnabled: Bool {
        SMAppService.mainApp.status == .enabled
    }

    static func setEnabled(_ enabled: Bool) {
        // SMAppService only works when running from a bundled .app
        guard Bundle.main.bundlePath.hasSuffix(".app") else {
            return
        }
        do {
            if enabled {
                if SMAppService.mainApp.status != .enabled {
                    try SMAppService.mainApp.register()
                }
            } else {
                if SMAppService.mainApp.status == .enabled {
                    try SMAppService.mainApp.unregister()
                }
            }
        } catch {
            print("[LaunchAtLoginService] Failed to update login item: \(error.localizedDescription)")
        }
    }
}
