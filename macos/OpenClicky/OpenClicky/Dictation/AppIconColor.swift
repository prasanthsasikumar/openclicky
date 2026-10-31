//
//  AppIconColor.swift
//  OpenClicky
//
//  The app icon's colour, picked in Settings → General: the five directions from the Claude Design
//  "OpenClicky Icon Directions" (2026-10-30). Signal is the bundle's own icon; the others are
//  AppIcon-<name> images drawn by scripts/make-app-icon.py. The pick is shown in the Dock (while the
//  window is open) and in OpenClicky's own windows. Finder and Launchpad keep the bundle's icon:
//  writing a custom icon into the app bundle breaks its code signature, and with it the macOS
//  permissions.
//

import AppKit
import SwiftUI

enum AppIconColor: String, CaseIterable, Identifiable {
    case signal, ultraviolet, sunset, paper, aqua
    var id: String { rawValue }

    var title: String {
        switch self {
        case .signal: return "Signal"
        case .ultraviolet: return "Ultraviolet"
        case .sunset: return "Sunset"
        case .paper: return "Paper"
        case .aqua: return "Aqua"
        }
    }

    /// The asset catalogue image for this colour (512 px).
    var imageName: String { "AppIcon-\(rawValue)" }

    /// The icon to hand the Dock: nil for Signal, which gives the bundle's own icon back.
    var dockImage: NSImage? {
        self == .signal ? nil : NSImage(named: imageName)
    }

    /// Shows this colour in the Dock and in OpenClicky's windows.
    @MainActor func apply() {
        NSApp.applicationIconImage = dockImage
    }
}
