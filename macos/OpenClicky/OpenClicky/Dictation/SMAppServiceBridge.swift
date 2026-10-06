//
//  SMAppServiceBridge.swift
//  OpenClicky
//
//  "open at restart": the login item through SMAppService, which lists the app under System
//  Settings → General → Login Items so it can be switched off there too.
//

import Foundation
import ServiceManagement

enum SMAppServiceBridge {
    static var isEnabled: Bool { SMAppService.mainApp.status == .enabled }

    /// Returns the state after the change (registration can be refused).
    static func set(enabled: Bool) -> Bool {
        do {
            if enabled { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            AppLog.append("login item: \(error.localizedDescription)")
        }
        return isEnabled
    }
}
