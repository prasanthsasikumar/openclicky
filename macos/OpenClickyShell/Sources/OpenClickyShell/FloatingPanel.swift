import AppKit

/// Floating, non-activating panel anchored under the notch / top-center of the main screen.
/// Mirrors HeyClicky's notch HUD: stays above other windows, does not steal focus from the app
/// the user is working in until they type into it.
final class FloatingPanel: NSPanel {
    init(contentRect: NSRect) {
        super.init(
            contentRect: contentRect,
            styleMask: [.nonactivatingPanel, .titled, .fullSizeContentView, .closable, .resizable],
            backing: .buffered,
            defer: false
        )
        isFloatingPanel = true
        level = .floating
        titleVisibility = .hidden
        titlebarAppearsTransparent = true
        isMovableByWindowBackground = true
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
        backgroundColor = NSColor.windowBackgroundColor.withAlphaComponent(0.96)
        standardWindowButton(.miniaturizeButton)?.isHidden = true
        standardWindowButton(.zoomButton)?.isHidden = true
    }

    override var canBecomeKey: Bool { true }

    func positionTopCenter() {
        guard let screen = NSScreen.main else { return }
        let visible = screen.visibleFrame
        let x = visible.midX - frame.width / 2
        let y = visible.maxY - frame.height - 8
        setFrameOrigin(NSPoint(x: x, y: y))
    }

    func show() {
        positionTopCenter()
        makeKeyAndOrderFront(nil)
    }
}
