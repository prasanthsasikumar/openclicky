//
//  DrawStageWindow.swift
//  OpenClicky
//
//  The drawing stage's window: a see-through, click-through panel over the whole screen that
//  hosts the shared stage page (draw-stage/, bundled) in a WKWebView. The page is served over the
//  app's own URL scheme from the bundled folder only, so its Content-Security-Policy ('self', no
//  connections) means what it says and nothing else can be loaded; the app and the page talk
//  through one script message handler and window.drawStage.handle (DrawStageMessages).
//
//  The panel ignores the mouse except over what the page says it can grab (a card to drag, a 3D
//  figure to turn, the resize grip), reported as `figures` boxes. Hiding the panel puts the page to
//  sleep (its animation loops stop), because a page animating under a hidden window kept the GPU
//  busy for the rest of the session (the idle-drain bug of October 2026).
//
//  The window is the app's own, so CompanionScreenCaptureUtility leaves it out of the next
//  question's screenshot without any help. Windows twin: DrawStageWindow.cs.
//

import AppKit
import WebKit

/// The URL scheme the bundled page is served over, and the path rules that keep it in its folder.
enum DrawStageScheme {
    static let scheme = "openclicky-stage"
    static let host = "page"
    static let pageURL = URL(string: "\(scheme)://\(host)/index.html")!

    /// The bundled file for a request path (the part after the host), or nil when the path escapes
    /// the folder or the file is missing. `..` in any spelling, a double leading slash and an
    /// empty path are refused before anything is joined.
    static func fileURL(forRequestPath path: String, in folder: URL, requireExists: Bool = true) -> URL? {
        let withoutQuery = path.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false).first.map(String.init) ?? ""
        guard let decoded = withoutQuery.removingPercentEncoding, decoded.hasPrefix("/"), !decoded.hasPrefix("//") else { return nil }
        let components = decoded.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        guard !components.isEmpty, !components.contains(where: { $0 == ".." || $0 == "." || $0.contains("\\") }) else { return nil }
        let folderPath = folder.standardizedFileURL.path
        let fileURL = folder.appendingPathComponent(components.joined(separator: "/")).standardizedFileURL
        guard fileURL.path.hasPrefix(folderPath + "/") else { return nil }
        if requireExists, !FileManager.default.fileExists(atPath: fileURL.path) { return nil }
        return fileURL
    }

    static func mediaType(forExtension fileExtension: String) -> String {
        switch fileExtension.lowercased() {
        case "html": return "text/html"
        case "js", "mjs": return "text/javascript"
        case "css": return "text/css"
        case "json": return "application/json"
        case "png": return "image/png"
        case "jpg", "jpeg": return "image/jpeg"
        case "svg": return "image/svg+xml"
        case "md", "txt": return "text/plain"
        case "woff2": return "font/woff2"
        default: return "application/octet-stream"
        }
    }

    /// The bundled draw-stage folder (Contents/Resources/draw-stage), copied in by the "Embed draw stage" build phase.
    static var bundledFolder: URL? {
        Bundle(for: DrawStageWindow.self).resourceURL?.appendingPathComponent("draw-stage", isDirectory: true)
    }
}

@MainActor
final class DrawStageWindow: NSObject, DrawStageSurface, DrawStagePreparing {
    static let shared = DrawStageWindow(controller: .shared)

    private let controller: DrawStageController
    private var panel: NSPanel?
    private var webView: WKWebView?
    /// Where the window takes the mouse, in screen points (AppKit, origin bottom-left).
    private var mouseTakingRects: [CGRect] = []
    private var mouseMonitors: [Any] = []
    private(set) var isShowing = false

    init(controller: DrawStageController) {
        self.controller = controller
        super.init()
        controller.figuresChanged = { [weak self] rects in
            guard let self else { return }
            self.takesMouse(over: rects, pageSize: self.webView?.bounds.size ?? .zero)
        }
        NotificationCenter.default.addObserver(self, selector: #selector(screensChanged), name: NSApplication.didChangeScreenParametersNotification, object: nil)
    }

    @objc private func screensChanged() {
        controller.displaysChanged()
    }

    // MARK: - DrawStagePreparing

    /// Makes the panel and the web view once and loads the page; the talk key going down calls it
    /// so the first drawing doesn't wait for WebKit.
    func load() {
        guard panel == nil else { return }
        guard DrawStageScheme.bundledFolder.map({ FileManager.default.fileExists(atPath: $0.path) }) == true else {
            AppLog.append("draw: the bundled draw-stage folder is missing; the stage cannot load")
            controller.receive(DrawStageMessages.simple("error").replacingOccurrences(of: "}", with: ",\"text\":\"the stage did not start: no page\"}"))
            return
        }
        let configuration = WKWebViewConfiguration()
        configuration.setURLSchemeHandler(self, forURLScheme: DrawStageScheme.scheme)
        configuration.userContentController.add(self, name: "stage")
        let webView = WKWebView(frame: .zero, configuration: configuration)
        webView.navigationDelegate = self
        webView.uiDelegate = self
        // No white page behind the drawing: the page's transparency reaches the desktop.
        webView.setValue(false, forKey: "drawsBackground")
        webView.underPageBackgroundColor = .clear
        if #available(macOS 13.3, *) {
            webView.isInspectable = ProcessInfo.processInfo.environment["OPENCLICKY_DRAWSTAGE_DEVTOOLS"] == "1"
        }
        webView.autoresizingMask = [.width, .height]

        let panel = NSPanel(contentRect: NSScreen.main?.frame ?? .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        // Under the pointer's overlay (screen saver level) and the island, over ordinary windows.
        panel.level = .statusBar
        panel.ignoresMouseEvents = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.acceptsMouseMovedEvents = true
        webView.frame = panel.contentView?.bounds ?? .zero
        panel.contentView?.addSubview(webView)
        self.panel = panel
        self.webView = webView
        webView.load(URLRequest(url: DrawStageScheme.pageURL))
        AppLog.append("draw: stage page loading")
    }

    // MARK: - DrawStageSurface

    func post(_ json: String) {
        webView?.evaluateJavaScript("window.drawStage && window.drawStage.handle(\(json))") { _, error in
            if let error { AppLog.append("draw: post failed: \(error.localizedDescription)") }
        }
    }

    func show(on displayFrame: CGRect) {
        load()
        guard let panel else { return }
        panel.setFrame(displayFrame, display: true)
        webView?.frame = panel.contentView?.bounds ?? .zero
        panel.orderFrontRegardless()
        isShowing = true
        post(DrawStageMessages.simple("wake"))
    }

    func hide() {
        post(DrawStageMessages.simple("sleep"))
        panel?.orderOut(nil)
        isShowing = false
        takesMouse(over: [], pageSize: .zero)
    }

    /// A JavaScript expression's value as text, for the smoke command's diagnostics only.
    func evaluateForDiagnostics(_ javaScript: String) async -> String {
        guard let webView else { return "no web view" }
        do {
            let value = try await webView.evaluateJavaScript(javaScript)
            return value.map { "\($0)" } ?? "undefined"
        } catch {
            return "error: \(error.localizedDescription)"
        }
    }

    func snapshot(of bounds: DrawStageBounds) async -> NSImage? {
        guard let webView, !bounds.isEmpty else { return nil }
        let configuration = WKSnapshotConfiguration()
        configuration.rect = CGRect(x: bounds.x, y: bounds.y, width: bounds.width, height: bounds.height)
        return try? await webView.takeSnapshot(configuration: configuration)
    }

    // MARK: - Taking the mouse over the page's figures

    /// The page's boxes (page pixels, origin top-left) as screen rects (points, origin bottom-left).
    nonisolated static func screenRects(from pageRects: [CGRect], pageSize: CGSize, panelFrame: CGRect) -> [CGRect] {
        guard pageSize.width > 0, pageSize.height > 0 else { return [] }
        let scaleX = panelFrame.width / pageSize.width
        let scaleY = panelFrame.height / pageSize.height
        return pageRects.map { rect in
            CGRect(x: panelFrame.minX + rect.minX * scaleX,
                   y: panelFrame.minY + (pageSize.height - rect.maxY) * scaleY,
                   width: rect.width * scaleX,
                   height: rect.height * scaleY)
        }
    }

    /// Over `rects` the panel takes the mouse (so a card can be dragged and a figure turned);
    /// anywhere else clicks go through to the screen underneath.
    func takesMouse(over rects: [CGRect], pageSize: CGSize) {
        guard let panel else { return }
        mouseTakingRects = Self.screenRects(from: rects, pageSize: pageSize, panelFrame: panel.frame)
        if mouseTakingRects.isEmpty {
            mouseMonitors.forEach(NSEvent.removeMonitor)
            mouseMonitors.removeAll()
            panel.ignoresMouseEvents = true
            return
        }
        if mouseMonitors.isEmpty {
            let handler: (NSEvent) -> Void = { [weak self] _ in self?.mouseMoved() }
            if let global = NSEvent.addGlobalMonitorForEvents(matching: [.mouseMoved, .leftMouseDragged], handler: handler) { mouseMonitors.append(global) }
            if let local = NSEvent.addLocalMonitorForEvents(matching: [.mouseMoved, .leftMouseDragged], handler: { event in handler(event); return event }) {
                mouseMonitors.append(local)
            }
        }
        mouseMoved()
    }

    private func mouseMoved() {
        guard let panel else { return }
        let mouse = NSEvent.mouseLocation
        let over = mouseTakingRects.contains { $0.contains(mouse) }
        if panel.ignoresMouseEvents == over { panel.ignoresMouseEvents = !over }
    }
}

// MARK: - The page's side

extension DrawStageWindow: WKScriptMessageHandler {
    nonisolated func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard JSONSerialization.isValidJSONObject(message.body),
              let data = try? JSONSerialization.data(withJSONObject: message.body) else { return }
        let json = String(decoding: data, as: UTF8.self)
        Task { @MainActor in self.controller.receive(json) }
    }
}

extension DrawStageWindow: WKNavigationDelegate, WKUIDelegate {
    nonisolated func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                             decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        // Only the bundled page, ever: a program cannot navigate the stage anywhere.
        decisionHandler(navigationAction.request.url?.scheme == DrawStageScheme.scheme ? .allow : .cancel)
    }

    nonisolated func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        Task { @MainActor in AppLog.append("draw: stage page loaded") }
    }

    nonisolated func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        Task { @MainActor in
            AppLog.append("draw: the stage page failed to load: \(error.localizedDescription)")
            self.controller.receive(#"{"type":"error","text":"the stage did not start: \#(error.localizedDescription)"}"#)
        }
    }

    nonisolated func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        self.webView(webView, didFail: navigation, withError: error)
    }

    nonisolated func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                             for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        nil
    }
}

extension DrawStageWindow: WKURLSchemeHandler {
    nonisolated func webView(_ webView: WKWebView, start urlSchemeTask: WKURLSchemeTask) {
        guard let url = urlSchemeTask.request.url, let folder = DrawStageScheme.bundledFolder,
              let fileURL = DrawStageScheme.fileURL(forRequestPath: url.path, in: folder),
              let data = try? Data(contentsOf: fileURL) else {
            let path = urlSchemeTask.request.url?.path ?? "?"
            Task { @MainActor in AppLog.append("draw: the page asked for \(path), which is not in the bundle") }
            urlSchemeTask.didFailWithError(URLError(.fileDoesNotExist))
            return
        }
        let response = URLResponse(url: url, mimeType: DrawStageScheme.mediaType(forExtension: fileURL.pathExtension),
                                   expectedContentLength: data.count, textEncodingName: "utf-8")
        urlSchemeTask.didReceive(response)
        urlSchemeTask.didReceive(data)
        urlSchemeTask.didFinish()
    }

    nonisolated func webView(_ webView: WKWebView, stop urlSchemeTask: WKURLSchemeTask) {}
}
