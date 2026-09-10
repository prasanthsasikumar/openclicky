//
//  ScreenElementGrounder.swift
//  OpenClicky
//
//  `point_at` fallback for elements OCR cannot snap to (icons, symbols, captions the recognizer
//  missed). The Realtime model's own pixel guess is hundreds of pixels off for icons, but Claude
//  locates an element on the same 1280 px screenshot to within a few pixels (measured ≤ 8 px on
//  a "+" header button and a small "New" button, 2–4 s with Haiku 4.5), so the description the
//  Realtime model gave is handed to Claude through the backend's /chat proxy.
//

import CoreGraphics
import Foundation

nonisolated enum ScreenElementGrounder {
    /// Fast and as accurate as Sonnet on this task.
    static let model = "claude-haiku-4-5"

    /// Asks Claude where `label`/`text` is on `capture`'s screenshot. Nil when the backend is not
    /// configured, the request fails, or Claude answers [POINT:none].
    static func locate(label: String, text: String, userRequest: String?, in capture: CompanionScreenCapture) async -> CGPoint? {
        guard await OpenClickyConfiguration.isConfigured else { return nil }
        let imageSize = CGSize(width: capture.screenshotWidthInPixels, height: capture.screenshotHeightInPixels)
        let api = await ClaudeAPI(proxyURL: "\(OpenClickyConfiguration.backendBaseURL)/chat", model: model)
        let reply = try? await api.analyzeImage(
            images: [(data: capture.imageData, label: "Screenshot of the user's screen, \(Int(imageSize.width))×\(Int(imageSize.height)) px, origin top-left.")],
            systemPrompt: "You locate UI elements in screenshots and answer only with a [POINT:x,y] tag in the screenshot's pixel coordinates.",
            userPrompt: prompt(label: label, text: text, userRequest: userRequest, imageSize: imageSize)
        )
        guard let reply else { return nil }
        return parsePoint(from: reply.text, imageSize: imageSize)
    }

    static func prompt(label: String, text: String, userRequest: String?, imageSize: CGSize) -> String {
        var lines = ["The screenshot is \(Int(imageSize.width))×\(Int(imageSize.height)) px, origin top-left."]
        var element = "Find the center of this UI element: \(label)"
        if !text.isEmpty { element += " (it shows the text \"\(text)\")" }
        lines.append(element + ".")
        if let userRequest, !userRequest.isEmpty {
            lines.append("The user asked: \"\(userRequest)\" — pick the element that answers that.")
        }
        lines.append("Reply with only [POINT:x,y], or [POINT:none] if it is not visible.")
        return lines.joined(separator: "\n")
    }

    /// `[POINT:x,y]` (spaces allowed) inside the screenshot; nil for `[POINT:none]`, no tag, or
    /// coordinates off the image.
    static func parsePoint(from reply: String, imageSize: CGSize) -> CGPoint? {
        let pattern = try! NSRegularExpression(pattern: #"\[POINT:\s*(\d+)\s*,\s*(\d+)\s*\]"#)
        let wholeReply = NSRange(reply.startIndex..., in: reply)
        guard let match = pattern.firstMatch(in: reply, range: wholeReply),
              let xRange = Range(match.range(at: 1), in: reply), let yRange = Range(match.range(at: 2), in: reply),
              let x = Double(reply[xRange]), let y = Double(reply[yRange]),
              x >= 0, y >= 0, x <= imageSize.width, y <= imageSize.height else { return nil }
        return CGPoint(x: x, y: y)
    }
}
