//
//  HandheldViewerPageTests.swift
//  OpenClickyTests
//
//  The handheld's 3D page really loaded in a web view, through the app's own scheme handler, with
//  a model made for the test: the page must read the model and say it is ready. (The first build
//  showed "the picture of the device could not be shown": the page's request for the model was
//  answered without an HTTP status, which a page's fetch takes as a failure.)
//

import Foundation
import Testing
import WebKit
@testable import OpenClicky

@MainActor
struct HandheldViewerPageTests {
    /// The smallest binary glTF: one triangle in one node, named like a control's part.
    private func makeOneTriangleModel(partName: String) -> Data {
        let positions: [Float32] = [0, 0, 0, 1, 0, 0, 0, 1, 0]
        let normals: [Float32] = [0, 0, 1, 0, 0, 1, 0, 0, 1]
        var binaryChunk = Data()
        positions.forEach { value in withUnsafeBytes(of: value.bitPattern.littleEndian) { binaryChunk.append(contentsOf: $0) } }
        normals.forEach { value in withUnsafeBytes(of: value.bitPattern.littleEndian) { binaryChunk.append(contentsOf: $0) } }
        let description: [String: Any] = [
            "asset": ["version": "2.0"],
            "scenes": [["nodes": [0]]],
            "nodes": [["name": partName, "mesh": 0]],
            "meshes": [["primitives": [["attributes": ["POSITION": 0, "NORMAL": 1], "mode": 4]]]],
            "accessors": [
                ["bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3"],
                ["bufferView": 1, "componentType": 5126, "count": 3, "type": "VEC3"],
            ],
            "bufferViews": [["buffer": 0, "byteOffset": 0, "byteLength": 36], ["buffer": 0, "byteOffset": 36, "byteLength": 36]],
            "buffers": [["byteLength": 72]],
        ]
        var jsonChunk = try! JSONSerialization.data(withJSONObject: description)
        while jsonChunk.count % 4 != 0 { jsonChunk.append(0x20) }

        func littleEndianBytes(_ value: UInt32) -> Data { withUnsafeBytes(of: value.littleEndian) { Data($0) } }
        var file = Data()
        file.append(littleEndianBytes(0x4654_6C67))                                  // "glTF"
        file.append(littleEndianBytes(2))
        file.append(littleEndianBytes(UInt32(12 + 8 + jsonChunk.count + 8 + binaryChunk.count)))
        file.append(littleEndianBytes(UInt32(jsonChunk.count)))
        file.append(littleEndianBytes(0x4E4F_534A))                                  // "JSON"
        file.append(jsonChunk)
        file.append(littleEndianBytes(UInt32(binaryChunk.count)))
        file.append(littleEndianBytes(0x004E_4942))                                  // "BIN"
        file.append(binaryChunk)
        return file
    }

    @Test func thePageReadsTheModelThroughTheAppsScheme() async throws {
        let scratchFolder = FileManager.default.temporaryDirectory.appendingPathComponent("HandheldViewerPageTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: scratchFolder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: scratchFolder) }
        let modelFile = scratchFolder.appendingPathComponent("model.glb")
        try makeOneTriangleModel(partName: "按键1").write(to: modelFile)

        let webViewConfiguration = WKWebViewConfiguration()
        webViewConfiguration.setURLSchemeHandler(HandheldViewerSchemeHandler(modelFile: modelFile), forURLScheme: HandheldViewerScheme.scheme)
        webViewConfiguration.userContentController.addUserScript(WKUserScript(
            source: HandheldDeviceViewMessage.configurationScript(for: WhisPromptHandheld.device, selectedControlID: "keyA", reduceMotion: true),
            injectionTime: .atDocumentStart, forMainFrameOnly: true))
        let webView = WKWebView(frame: CGRect(x: 0, y: 0, width: 400, height: 300), configuration: webViewConfiguration)
        webView.load(URLRequest(url: HandheldViewerScheme.pageURL))

        var pageState = "nothing yet"
        for _ in 0..<150 {
            try await Task.sleep(nanoseconds: 100_000_000)
            let answer = try? await webView.evaluateJavaScript(
                "window.handheldViewerIsReady ? 'ready' : (document.getElementById('status') ? document.getElementById('status').textContent : 'no status line')")
            pageState = answer as? String ?? pageState
            if pageState == "ready" || pageState.contains("could not") { break }
        }
        #expect(pageState == "ready")
    }
}
