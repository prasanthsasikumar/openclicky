//
//  AccessibilityDragPanel.swift
//  OpenClicky
//
//  HeyClicky's "I'm HeyClicky — drag me into the list above" panel. macOS does not always add an
//  app to System Settings → Privacy & Security → Accessibility by itself (an unsigned or
//  freshly-rebuilt bundle often never appears), and the officially supported fix is to drag the
//  app bundle into the list from Finder. This panel is that drag, without the trip to Finder: it
//  floats under the Settings window and its icon row is a drag source for OpenClicky.app.
//
//  Unlike HeyClicky's, the panel does not close when the drag ends — a drop that the list quietly
//  refuses would otherwise leave nothing on screen to try again with. It closes when
//  `AXIsProcessTrusted()` actually flips, or when the card behind it is dismissed.
//

import AppKit
import SwiftUI
import UniformTypeIdentifiers

@MainActor
final class AccessibilityDragPanelController: AccessibilityDragPresenting {

    private var panel: NSPanel?
    /// The Settings window moves while the panel is up, so its position is re-checked on a timer.
    private var followTimer: Timer?

    private static let panelSize = NSSize(width: 520, height: 108)

    func showAccessibilityDragHelper() {
        if panel == nil { createPanel() }
        positionPanel()
        panel?.orderFrontRegardless()
        if followTimer == nil {
            followTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
                Task { @MainActor [weak self] in self?.positionPanel() }
            }
        }
    }

    func hideAccessibilityDragHelper() {
        followTimer?.invalidate()
        followTimer = nil
        panel?.orderOut(nil)
    }

    // MARK: - Private

    private func createPanel() {
        let hudPanel = NSPanel(
            contentRect: NSRect(origin: .zero, size: Self.panelSize),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        hudPanel.isOpaque = false
        hudPanel.backgroundColor = .clear
        hudPanel.hasShadow = true
        // Above System Settings, and on every Space so it follows the user there.
        hudPanel.level = .popUpMenu
        hudPanel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        hudPanel.isMovableByWindowBackground = false
        hudPanel.ignoresMouseEvents = false

        let hostingView = NSHostingView(rootView: AccessibilityDragCardView())
        hostingView.frame = NSRect(origin: .zero, size: Self.panelSize)
        hudPanel.contentView = hostingView
        panel = hudPanel
    }

    /// Centers the panel on the System Settings window, over the lower part of the Accessibility
    /// list so the drag is a short one — high enough that it doesn't hang off the bottom of the
    /// window. With no Settings window on screen it sits in the lower middle of the display the
    /// pointer is on (never `NSScreen.main`, which follows keyboard focus and can be a display the
    /// user isn't even looking at).
    private func positionPanel() {
        guard let panel else { return }
        let panelSize = Self.panelSize
        let origin: CGPoint

        if let settingsFrame = Self.systemSettingsWindowFrame() {
            let heightAboveTheWindowsBottomEdge: CGFloat = 120
            origin = CGPoint(
                x: settingsFrame.midX - panelSize.width / 2,
                y: settingsFrame.minY + heightAboveTheWindowsBottomEdge
            )
        } else if let screen = Self.screenUnderPointer() {
            origin = CGPoint(
                x: screen.frame.midX - panelSize.width / 2,
                y: screen.frame.minY + screen.frame.height * 0.28
            )
        } else {
            return
        }

        panel.setFrame(NSRect(origin: origin, size: panelSize), display: true)
    }

    private static func screenUnderPointer() -> NSScreen? {
        let mouseLocation = NSEvent.mouseLocation
        return NSScreen.screens.first(where: { $0.frame.contains(mouseLocation) }) ?? NSScreen.screens.first
    }

    /// The System Settings window in AppKit coordinates (bottom-left origin), if it is on screen.
    /// `CGWindowListCopyWindowInfo` gives bounds and the owning app's name without needing Screen
    /// Recording permission — only window *titles* are withheld — which matters here, because this
    /// panel exists precisely when permissions are still missing.
    private static func systemSettingsWindowFrame() -> CGRect? {
        guard let primaryScreen = NSScreen.screens.first else { return nil }
        let options: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
        guard let windowInfoList = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else {
            return nil
        }

        for windowInfo in windowInfoList {
            guard let ownerName = windowInfo[kCGWindowOwnerName as String] as? String,
                  ownerName == "System Settings" || ownerName == "System Preferences",
                  let boundsDictionary = windowInfo[kCGWindowBounds as String] as? [String: Any],
                  let windowBounds = CGRect(dictionaryRepresentation: boundsDictionary as CFDictionary),
                  // Skip the tiny helper windows (tooltips, shadows) the app also owns.
                  windowBounds.width > 400, windowBounds.height > 300
            else { continue }

            // CGWindowList is top-left origin over the whole display arrangement; AppKit is
            // bottom-left origin from the primary screen.
            let flippedY = primaryScreen.frame.maxY - windowBounds.maxY
            return CGRect(x: windowBounds.minX, y: flippedY, width: windowBounds.width, height: windowBounds.height)
        }
        return nil
    }
}

// MARK: - View

/// The panel's content: an arrow, one line of instruction, and the app row you drag out of.
struct AccessibilityDragCardView: View {
    @State private var isHoveringDragRow = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                Image(systemName: "arrow.up")
                    .font(.system(size: 12, weight: .bold))
                    .foregroundColor(.white)
                    .frame(width: 22, height: 22)
                    .background(Circle().fill(Color.white.opacity(0.14)))

                Text("I'm OpenClicky — drag me into the list above.")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundColor(.white)
            }

            dragRow
        }
        .padding(16)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(Color.black.opacity(0.92))
        )
    }

    private var dragRow: some View {
        HStack(spacing: 10) {
            Image(nsImage: NSApp.applicationIconImage)
                .resizable()
                .aspectRatio(contentMode: .fit)
                .frame(width: 26, height: 26)

            Text("OpenClicky")
                .font(.system(size: 13, weight: .medium))
                .foregroundColor(.white)

            Spacer()
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .frame(maxWidth: .infinity)
        .background(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .fill(Color.white.opacity(isHoveringDragRow ? 0.16 : 0.10))
        )
        .contentShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        // The bundle URL as a plain file promise: the same `public.file-url` drag Finder makes,
        // which is what the Accessibility list accepts.
        .onDrag {
            NSItemProvider(object: Bundle.main.bundleURL as NSURL)
        }
        .onHover { isHovering in
            isHoveringDragRow = isHovering
            if isHovering {
                NSCursor.openHand.push()
            } else {
                NSCursor.pop()
            }
        }
    }
}
