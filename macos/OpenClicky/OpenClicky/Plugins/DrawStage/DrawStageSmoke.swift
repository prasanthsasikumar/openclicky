//
//  DrawStageSmoke.swift
//  OpenClicky
//
//  `--openclicky-drawstage-smoke [program.js | reply:<file> | "lesson:<question>"] [--voice] [--hide-after-finish]`
//
//  Draws on the cursor's screen without a voice turn, then exits: with no argument the bundled
//  rocket (2D) and 3D printer samples on the page's own clock; a program file the same way; a
//  written reply (`reply:`) through the whole DrawingAnswer path, spoken by this Mac's voice with
//  `--voice` or silently paced; a question (`lesson:`) asked of the routed answer model with the
//  drawing prompt on a real screenshot. `--hide-after-finish` hides the stage two seconds after
//  the drawing ends and stays alive ten more, for measuring that a hidden stage costs nothing.
//

import AppKit
import Foundation

@MainActor
enum DrawStageSmoke {
    static func run(arguments: [String]) async {
        setvbuf(stdout, nil, _IONBF, 0)
        let wantsVoice = arguments.contains("--voice")
        let hidesAfterFinish = arguments.contains("--hide-after-finish")
        let argument = arguments.first { !$0.hasPrefix("--") }

        let controller = DrawStageController.shared
        controller.surface = DrawStageWindow.shared
        controller.findPhoto = { query, caption in
            guard let picture = await TeacherPictureFinder.find(query: query, caption: caption) else { return nil }
            return DrawStagePhoto(picture: picture)
        }
        controller.logged = { print("smoke page: \($0)") }
        controller.prepare()

        let context = await CompanionScreenCaptureUtility.captureCursorScreenContext()
        let displayFrame = context?.capture.displayFrame ?? NSScreen.main?.frame ?? CGRect(x: 0, y: 0, width: 1440, height: 900)
        let canvasWidth = context?.capture.screenshotWidthInPixels ?? Int(displayFrame.width)
        let canvasHeight = context?.capture.screenshotHeightInPixels ?? Int(displayFrame.height)
        print("smoke: screenshot \(context == nil ? "unavailable" : "taken"), canvas \(canvasWidth)×\(canvasHeight)")
        let readyWaitStartedAt = Date()
        while !controller.isReady, Date().timeIntervalSince(readyWaitStartedAt) < 8 {
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        print("smoke: page \(controller.isReady ? "ready" : "NOT ready") after \(String(format: "%.1f", Date().timeIntervalSince(readyWaitStartedAt))) s")
        // The 3D module and the worker both load over the app's URL scheme; say so, since a browser engine may refuse either.
        let figure3DLoaded = await DrawStageWindow.shared.evaluateForDiagnostics("typeof window.Figure3D")
        let pageIsVisible = await DrawStageWindow.shared.evaluateForDiagnostics("document.visibilityState")
        print("smoke: Figure3D module: \(figure3DLoaded); page visibility: \(pageIsVisible) (a hidden page draws nothing: its animation loop is paused)")

        var exitCode: Int32 = 0
        if let argument, argument.hasPrefix("reply:") || argument.hasPrefix("lesson:") {
            let drew = await drawAnswer(argument: argument, wantsVoice: wantsVoice, controller: controller,
                                        displayFrame: displayFrame, canvasWidth: canvasWidth, canvasHeight: canvasHeight, capture: context?.capture)
            exitCode = drew ? 0 : 1
        } else {
            let programs: [String]
            if let argument, let text = try? String(contentsOfFile: argument, encoding: .utf8) {
                programs = [text]
            } else {
                programs = ["rocket", "printer-3d"].compactMap { name in
                    DrawStageScheme.bundledFolder.flatMap { try? String(contentsOf: $0.appendingPathComponent("samples/\(name).js"), encoding: .utf8) }
                }
            }
            for program in programs {
                await playOnThePagesClock(program, controller: controller, displayFrame: displayFrame, canvasWidth: canvasWidth, canvasHeight: canvasHeight)
            }
        }

        if hidesAfterFinish {
            try? await Task.sleep(nanoseconds: 2_000_000_000)
            controller.hide(reason: "smoke")
            print("smoke: stage hidden (pid \(ProcessInfo.processInfo.processIdentifier)); staying alive 10 s")
            try? await Task.sleep(nanoseconds: 10_000_000_000)
        } else {
            try? await Task.sleep(nanoseconds: 4_000_000_000)
        }
        exit(exitCode)
    }

    /// A finished program drawn on the page's own estimate of each line's length.
    private static func playOnThePagesClock(_ program: String, controller: DrawStageController, displayFrame: CGRect, canvasWidth: Int, canvasHeight: Int) async {
        let finished = DrawingAnswer.Signal()
        controller.finished = { finished.resume() }
        controller.lineAdded = { k, text in print("smoke line \(k): \(text)") }
        controller.begin(displayFrame: displayFrame, canvasWidth: canvasWidth, canvasHeight: canvasHeight, pace: .estimate)
        controller.feed(program, done: true)
        let startedAt = Date()
        // A drawing on the page's clock takes under a minute; a locked or hidden screen never finishes one.
        let patience = Task { @MainActor in
            try? await Task.sleep(nanoseconds: 90_000_000_000)
            if !Task.isCancelled { print("smoke: gave up waiting for the drawing to finish after 90 s (is the screen visible?)"); finished.resume() }
        }
        await finished.wait()
        patience.cancel()
        print("smoke: done in \(String(format: "%.1f", Date().timeIntervalSince(startedAt))) s")
        controller.finished = nil
        controller.lineAdded = nil
    }

    /// A reply (written, or asked of the answer model) through DrawingAnswer, spoken or silently paced.
    private static func drawAnswer(argument: String, wantsVoice: Bool, controller: DrawStageController, displayFrame: CGRect,
                                   canvasWidth: Int, canvasHeight: Int, capture: CompanionScreenCapture?) async -> Bool {
        let writeReply: (@escaping @MainActor (String) -> Void) async throws -> String
        if argument.hasPrefix("reply:") {
            let path = String(argument.dropFirst("reply:".count))
            guard let reply = try? String(contentsOfFile: path, encoding: .utf8) else {
                print("smoke: cannot read \(path)")
                return false
            }
            writeReply = { onChunk in
                // Streamed a line at a time, like a model writing it.
                var soFar = ""
                for line in reply.split(separator: "\n", omittingEmptySubsequences: false) {
                    soFar += line + "\n"
                    onChunk(soFar)
                    try await Task.sleep(nanoseconds: 60_000_000)
                }
                return reply
            }
        } else {
            let question = String(argument.dropFirst("lesson:".count))
            guard let systemPrompt = DrawStageLesson.systemPrompt(canvasWidth: canvasWidth, canvasHeight: canvasHeight),
                  let userPrompt = DrawStageLesson.questionPrompt(question) else {
                print("smoke: the drawing prompts are missing from the bundle")
                return false
            }
            let answers = ClaudeAPI(currentProxyURL: { "\(OpenClickyConfiguration.backendBaseURL)/chat" })
            let images: [(data: Data, label: String)] = capture.map { [CompanionManager.imageLabeledWithItsPixelSize($0)] } ?? []
            writeReply = { onChunk in
                let result = try await answers.analyzeImageStreaming(
                    images: images, systemPrompt: systemPrompt, userPrompt: userPrompt, tools: [],
                    options: AnswerRequestOptions(maxTokens: DrawStageLesson.outputTokens, purpose: .drawing, thinkingOff: true, timeoutSeconds: 180),
                    onTextChunk: { onChunk($0) })
                return result.text
            }
        }

        let speech: SpeechQueue
        if wantsVoice {
            let player = AnswerVoicePlayer(currentProxyURL: { "\(OpenClickyConfiguration.backendBaseURL)/tts" })
            speech = SpeechQueue(fetchAudio: { try await MacVoice.shared.audio(for: $0) }, playAudio: { try await player.playToCompletion($0) })
        } else {
            speech = SpeechQueue(
                fetchAudio: { Data($0.utf8) },
                playAudio: { data in
                    let words = String(decoding: data, as: UTF8.self).split(separator: " ").count
                    try await Task.sleep(nanoseconds: UInt64(max(0.8, Double(words) / 2.6) * 1_000_000_000))
                }
            )
        }
        do {
            let result = try await DrawingAnswer.run(
                stage: controller, displayFrame: displayFrame, canvasWidth: canvasWidth, canvasHeight: canvasHeight,
                writeReply: writeReply, speech: speech, onFirstAudio: { print("smoke: voice started") },
                log: { print("smoke: \($0)") }, holdLine: DrawStageLesson.holdLine)
            print(result.drew ? "smoke: drew \(result.spokenLines.count) lines (\(result.program.count) chars)" : "smoke: not drawn")
            if let unspoken = result.unspokenAfterVoiceFailure { print("smoke: voice failed, unspoken: \(unspoken)") }
            return result.drew
        } catch {
            print("smoke: failed: \(error)")
            return false
        }
    }
}
