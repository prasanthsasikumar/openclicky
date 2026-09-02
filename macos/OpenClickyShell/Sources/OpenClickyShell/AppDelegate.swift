import AppKit
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private var panel: FloatingPanel!
    private var hotKey: GlobalHotKey?
    let runner = AgentRunner(settings: ShellSettings.load())

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = statusItem.button {
            button.image = NSImage(systemSymbolName: "waveform.circle.fill", accessibilityDescription: "OpenClicky")
            button.toolTip = "OpenClicky — ⌥Space to toggle"
        }
        let menu = NSMenu()
        menu.addItem(withTitle: "Toggle Panel (⌥Space)", action: #selector(togglePanel), keyEquivalent: "")
        menu.addItem(withTitle: "Screenshot + Ask…", action: #selector(screenshotAsk), keyEquivalent: "")
        menu.addItem(NSMenuItem.separator())
        menu.addItem(withTitle: "Open Settings File", action: #selector(openSettings), keyEquivalent: ",")
        menu.addItem(withTitle: "Quit OpenClicky", action: #selector(quit), keyEquivalent: "q")
        menu.items.forEach { $0.target = self }
        statusItem.menu = menu

        panel = FloatingPanel(contentRect: NSRect(x: 0, y: 0, width: 520, height: 360))
        panel.contentView = NSHostingView(rootView: PanelView(runner: runner, onClose: { [weak self] in self?.panel.orderOut(nil) }))
        panel.positionTopCenter()

        // ⌥Space toggles the panel from anywhere (HeyClicky-style push-to-talk anchor).
        hotKey = GlobalHotKey(keyCode: 49 /* space */, modifiers: .option) { [weak self] in self?.togglePanel() }

        let args = CommandLine.arguments
        if args.contains("--smoke") {
            runner.appendSystem("smoke: shell initialized (status item + panel + hotkey), cli=\(runner.settings.cliCommand.joined(separator: " "))")
            print(runner.transcript)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { NSApp.terminate(nil) }
        } else if let i = args.firstIndex(of: "--smoke-run"), i + 1 < args.count {
            // Headless end-to-end: submit one request through the real CLI, print the transcript, exit with its status.
            runner.onFinished = { [weak self] status in
                print(self?.runner.transcript ?? "")
                exit(status)
            }
            runner.submit(args[i + 1])
        }
    }

    @objc func togglePanel() {
        if panel.isVisible { panel.orderOut(nil) } else { panel.show() }
    }

    @objc func screenshotAsk() {
        panel.show()
        runner.screenshotNext = true
    }

    @objc func openSettings() {
        ShellSettings.ensureFile()
        NSWorkspace.shared.open(ShellSettings.fileURL)
    }

    @objc func quit() {
        runner.cancel()
        NSApp.terminate(nil)
    }
}
