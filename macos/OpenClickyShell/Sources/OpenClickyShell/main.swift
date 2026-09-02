import AppKit

// OpenClicky native shell (scaffold): a menu-bar agent app with a floating panel that drives the
// `openclicky` CLI. No Dock icon (accessory activation policy), like HeyClicky's LSUIElement app.
//
//   swift run                           # launch
//   swift run OpenClickyShell --smoke   # start, verify wiring, exit (used by headless checks)

nonisolated(unsafe) var appDelegate: AppDelegate?

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    appDelegate = delegate // NSApplication.delegate is weak; keep it alive
    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    app.run()
}
