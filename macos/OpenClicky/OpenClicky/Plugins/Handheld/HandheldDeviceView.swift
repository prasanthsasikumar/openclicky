//
//  HandheldDeviceView.swift
//  OpenClicky
//
//  The handheld's 3D picture on its settings page: the device turns under the pointer, and a
//  click on one of its buttons chooses that button, whose choices the page then shows.
//
//  OpenClicky ships no model of anyone's device. The picture is drawn from a model file the
//  person has on their own Mac (`~/.openclicky/handheld/<device id>.glb`); without one the page
//  shows the plain list of buttons instead. The page itself (`Viewer/`) is in the app and is
//  served, with three.js from the drawing stage's folder, over a scheme of its own.
//

import AppKit
import SwiftUI
import WebKit

/// Where a device's model file is looked for.
enum HandheldDeviceModelFile {
    static func url(for device: HandheldDevice, homeDirectory: URL = URL(fileURLWithPath: NSHomeDirectory())) -> URL {
        homeDirectory.appendingPathComponent(".openclicky/handheld/\(device.id).glb")
    }

    static func isAvailable(for device: HandheldDevice) -> Bool {
        FileManager.default.fileExists(atPath: url(for: device).path)
    }
}

/// The few files the picture's page may ask for, and nothing else.
enum HandheldViewerScheme {
    static let scheme = "openclicky-handheld"
    static let pageURL = URL(string: "\(scheme)://viewer/index.html")!

    /// The file for a request path, or nil for anything that is not one of the page's own files.
    /// A fixed table, so no path the page could ask for reaches outside it.
    static func fileURL(forRequestPath path: String, bundleResources: URL, modelFile: URL) -> URL? {
        switch path {
        case "/index.html": return bundleResources.appendingPathComponent("handheld-viewer.html")
        case "/viewer.js": return bundleResources.appendingPathComponent("handheld-viewer.js")
        case "/three/three.module.min.js": return bundleResources.appendingPathComponent("draw-stage/vendor/three/three.module.min.js")
        case "/three/three.core.min.js": return bundleResources.appendingPathComponent("draw-stage/vendor/three/three.core.min.js")
        case "/model.glb": return modelFile
        default: return nil
        }
    }

    static func mediaType(forRequestPath path: String) -> String {
        if path.hasSuffix(".html") { return "text/html" }
        if path.hasSuffix(".js") { return "text/javascript" }
        return "application/octet-stream"
    }
}

/// What the page and the app say to each other.
enum HandheldDeviceViewMessage {
    static let handlerName = "handheld"

    /// The control a click on the picture chose, from the page's message.
    static func selectedControlID(fromMessageBody messageBody: Any) -> String? {
        guard let message = messageBody as? [String: Any], message["type"] as? String == "select" else { return nil }
        return message["control"] as? String
    }

    /// Written into the page before its script runs: which part of the model is which control,
    /// which one is chosen, and whether motion is reduced.
    static func configurationScript(for device: HandheldDevice, selectedControlID: String?, reduceMotion: Bool) -> String {
        var controls: [String: [String: String]] = [:]
        for control in device.controls {
            guard let partName = device.modelPartNameByControlID[control.id] else { continue }
            controls[control.id] = ["name": control.name, "part": partName]
        }
        let configuration: [String: Any] = [
            "controls": controls, "selected": selectedControlID ?? NSNull(), "reduceMotion": reduceMotion,
        ]
        let encoded = (try? JSONSerialization.data(withJSONObject: configuration, options: [.sortedKeys])).flatMap { String(data: $0, encoding: .utf8) } ?? "{}"
        return "window.handheldViewerConfiguration = \(encoded);"
    }

    /// A control id is only letters and digits; anything else never reaches the page's script.
    static func isSafeControlID(_ controlID: String) -> Bool {
        !controlID.isEmpty && controlID.allSatisfy { $0.isLetter || $0.isNumber }
    }
}

final class HandheldViewerSchemeHandler: NSObject, WKURLSchemeHandler {
    private let modelFile: URL

    init(modelFile: URL) {
        self.modelFile = modelFile
    }

    func webView(_ webView: WKWebView, start urlSchemeTask: WKURLSchemeTask) {
        guard let url = urlSchemeTask.request.url, let bundleResources = Bundle.main.resourceURL,
              let fileURL = HandheldViewerScheme.fileURL(forRequestPath: url.path, bundleResources: bundleResources, modelFile: modelFile),
              let data = try? Data(contentsOf: fileURL) else {
            urlSchemeTask.didFailWithError(URLError(.fileDoesNotExist))
            return
        }
        // An HTTP answer with a status: the page reads its model with `fetch`, which takes an
        // answer without a status as a failure ("the model could not be read").
        guard let response = HTTPURLResponse(url: url, statusCode: 200, httpVersion: "HTTP/1.1", headerFields: [
            "Content-Type": HandheldViewerScheme.mediaType(forRequestPath: url.path),
            "Content-Length": String(data.count),
        ]) else {
            urlSchemeTask.didFailWithError(URLError(.badServerResponse))
            return
        }
        urlSchemeTask.didReceive(response)
        urlSchemeTask.didReceive(data)
        urlSchemeTask.didFinish()
    }

    func webView(_ webView: WKWebView, stop urlSchemeTask: WKURLSchemeTask) {}
}

struct HandheldDeviceView: NSViewRepresentable {
    @ObservedObject var plugin: HandheldPlugin
    let device: HandheldDevice
    let reduceMotion: Bool

    func makeCoordinator() -> Coordinator {
        Coordinator(plugin: plugin)
    }

    func makeNSView(context: Context) -> WKWebView {
        let webViewConfiguration = WKWebViewConfiguration()
        webViewConfiguration.setURLSchemeHandler(HandheldViewerSchemeHandler(modelFile: HandheldDeviceModelFile.url(for: device)), forURLScheme: HandheldViewerScheme.scheme)
        webViewConfiguration.userContentController.addUserScript(WKUserScript(
            source: HandheldDeviceViewMessage.configurationScript(for: device, selectedControlID: plugin.selectedControlID, reduceMotion: reduceMotion),
            injectionTime: .atDocumentStart, forMainFrameOnly: true))
        webViewConfiguration.userContentController.add(context.coordinator, name: HandheldDeviceViewMessage.handlerName)

        let webView = WKWebView(frame: .zero, configuration: webViewConfiguration)
        // See-through, so the page's own background shows behind the device.
        webView.setValue(false, forKey: "drawsBackground")
        webView.navigationDelegate = context.coordinator
        context.coordinator.webView = webView
        context.coordinator.shownSelectedControlID = plugin.selectedControlID
        // A press on the real device dips the same button on the picture.
        plugin.onControlPressed = { [weak coordinator = context.coordinator] controlID in
            coordinator?.callViewer("press", controlID: controlID)
        }
        webView.load(URLRequest(url: HandheldViewerScheme.pageURL))
        return webView
    }

    func updateNSView(_ webView: WKWebView, context: Context) {
        guard context.coordinator.shownSelectedControlID != plugin.selectedControlID, let selectedControlID = plugin.selectedControlID else { return }
        context.coordinator.shownSelectedControlID = selectedControlID
        context.coordinator.callViewer("select", controlID: selectedControlID)
    }

    static func dismantleNSView(_ webView: WKWebView, coordinator: Coordinator) {
        webView.configuration.userContentController.removeScriptMessageHandler(forName: HandheldDeviceViewMessage.handlerName)
        coordinator.plugin.onControlPressed = nil
    }

    @MainActor
    final class Coordinator: NSObject, WKScriptMessageHandler, WKNavigationDelegate {
        let plugin: HandheldPlugin
        weak var webView: WKWebView?
        var shownSelectedControlID: String?

        init(plugin: HandheldPlugin) {
            self.plugin = plugin
        }

        func callViewer(_ functionName: String, controlID: String) {
            guard HandheldDeviceViewMessage.isSafeControlID(controlID) else { return }
            webView?.evaluateJavaScript("window.handheldViewer && window.handheldViewer.\(functionName)('\(controlID)')", completionHandler: nil)
        }

        func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
            if let controlID = HandheldDeviceViewMessage.selectedControlID(fromMessageBody: message.body) {
                shownSelectedControlID = controlID
                plugin.selectControl(withID: controlID)
            } else if let failure = message.body as? [String: Any], failure["type"] as? String == "failed" {
                AppLog.append("handheld: the device's picture could not be shown (\(failure["reason"] as? String ?? "unknown"))")
            }
        }

        /// The page is only ever the app's own; a link to anywhere else is not followed.
        func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction) async -> WKNavigationActionPolicy {
            navigationAction.request.url?.scheme == HandheldViewerScheme.scheme ? .allow : .cancel
        }
    }
}

/// The picture as a settings row: the device, and under it which button is chosen.
struct HandheldDeviceRow: View {
    @ObservedObject var plugin: HandheldPlugin
    let device: HandheldDevice
    @Environment(\.accessibilityReduceMotion) private var systemReduceMotion

    private var selectedControlName: String {
        plugin.selectedControlID.flatMap { device.control(withID: $0) }?.name ?? ""
    }

    var body: some View {
        VStack(spacing: 6) {
            HandheldDeviceView(plugin: plugin, device: device, reduceMotion: systemReduceMotion || DictationSettings.shared.reduceAnimation)
                .frame(height: 300)
                .accessibilityLabel("\(device.displayName). Click a button on it to choose what it does.")
            Text("\(selectedControlName) · click a button on the device to choose another, drag to turn it")
                .font(SettingsLook.caption)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 4)
    }
}
