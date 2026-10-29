//
//  OpenClickyApp.swift
//  OpenClicky
//
//  Menu bar-only companion app. No dock icon, no main window — just an
//  always-available status item in the macOS menu bar. Clicking the icon
//  opens a floating panel with companion voice controls.
//

import AVFoundation
import ServiceManagement
import SwiftUI
import Sparkle

/// The entry point. The Kokoro and Parakeet helpers (the same binary, started by the app with a flag) branch
/// off here, before SwiftUI creates the app delegate and with it the island, the menu bar item and
/// the shortcut tap.
@main
enum OpenClickyMain {
    static func main() {
        if CommandLine.arguments.contains(KokoroHelper.flag) { KokoroHelper.run() }
        if CommandLine.arguments.contains(ParakeetHelper.flag) { ParakeetHelper.run() }
        OpenClickyApp.main()
    }
}

struct OpenClickyApp: App {
    @NSApplicationDelegateAdaptor(CompanionAppDelegate.self) var appDelegate

    var body: some Scene {
        // The app lives entirely in the menu bar panel managed by the AppDelegate.
        // This empty Settings scene satisfies SwiftUI's requirement for at least
        // one scene but is never shown (LSUIElement=true removes the app menu).
        Settings {
            EmptyView()
        }
    }
}

/// Manages the companion lifecycle: creates the menu bar panel and starts
/// the companion voice pipeline on launch.
@MainActor
final class CompanionAppDelegate: NSObject, NSApplicationDelegate {
    private var menuBarPanelManager: MenuBarPanelManager?
    private let companionManager = CompanionManager()
    private var sparkleUpdaterController: SPUStandardUpdaterController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Line-buffer stdout so `open -a OpenClicky --stdout app.log` shows prints as they happen
        // (fully buffered, nothing appears until the app exits).
        setvbuf(stdout, nil, _IOLBF, 0)

        // OpenClicky: headless check of the agent lane (gate + Codex run via the CLI) without any
        // GUI, permissions, or screen capture. Used by scripted builds:
        //   OpenClicky.app/Contents/MacOS/OpenClicky --openclicky-smoke-run "create a file called x.txt containing 'y'"
        let launchArguments = CommandLine.arguments
        if let argumentIndex = launchArguments.firstIndex(of: "--openclicky-smoke-run"), argumentIndex + 1 < launchArguments.count {
            let transcript = launchArguments[argumentIndex + 1]
            Task { @MainActor in
                let agentClient = OpenClickyAgentClient()
                let lane = await agentClient.classifyLane(for: transcript)
                print("smoke: lane=\(lane)")
                var exitCode: Int32 = 0
                if lane == "agent" {
                    do {
                        let result = try await agentClient.runAgent(task: transcript, screenshotPath: nil, threadId: nil) { milestone in
                            print("smoke: ▸ \(milestone)")
                        }
                        print("smoke: status=\(result.status) thread=\(result.threadId ?? "-")")
                        print("smoke: text=\(result.text)")
                        print("smoke: artifacts=\(result.artifacts)")
                        if result.status != "completed" { exitCode = 2 }
                    } catch {
                        print("smoke: error=\(error.localizedDescription)")
                        exitCode = 1
                    }
                }
                exit(exitCode)
            }
            return
        }

        if let argumentIndex = launchArguments.firstIndex(of: "--openclicky-smoke-transcribe"), argumentIndex + 1 < launchArguments.count {
            // Headless check of a dictation engine with a recording instead of the microphone:
            //   OpenClicky --openclicky-smoke-transcribe take.wav [offline|sarvam|openclicky]
            // The Sarvam key comes from shell.json or OPENCLICKY_SARVAM_KEY. Prints the raw words,
            // the formatted take, and exits 0 when something was heard.
            let filePath = launchArguments[argumentIndex + 1]
            let engineName = argumentIndex + 2 < launchArguments.count ? launchArguments[argumentIndex + 2] : "offline"
            setvbuf(stdout, nil, _IONBF, 0)
            Task { @MainActor in
                let exitCode = await DictationSmokeRun.transcribe(filePath: filePath, engineName: engineName)
                exit(exitCode)
            }
            return
        }

        if let argumentIndex = launchArguments.firstIndex(of: "--openclicky-smoke-talk-file"), argumentIndex + 1 < launchArguments.count {
            // Headless push-to-talk check with a recorded utterance instead of the microphone:
            // connect, press, stream the file as mic audio in real time, release, print the reply.
            let filePath = launchArguments[argumentIndex + 1]
            setvbuf(stdout, nil, _IONBF, 0)
            print("smoke: push-to-talk with \(filePath)")
            Task { @MainActor in
                let client = RealtimeVoiceClient()
                var finished = false
                client.onEvent = { print("smoke: ▸ \($0)") }
                client.onTranscript = { role, text in print("smoke: \(role == .user ? "you" : "openclicky"): \(text)") }
                client.onAgentTask = { task in "smoke agent would run: \(task)" }
                client.onMacAction = { action in .failed("smoke run does not perform \(action)") }
                client.screenContextProvider = { await CompanionScreenCaptureUtility.captureCursorScreenContext() }
                client.writingTargetProvider = { await WritingTarget.captureFromFrontApp() }
                client.onPointAt = { screenshotPoint, label, capture in
                    // No overlay in the smoke harness: print what the buddy would fly to.
                    let screenLocation = CompanionManager.screenLocation(forScreenshotPoint: screenshotPoint, in: capture)
                    let formatted = { (value: CGFloat) in String(format: "%.0f", value) }
                    print("🎯 Element pointing: (\(formatted(screenshotPoint.x)), \(formatted(screenshotPoint.y))) → \"\(label)\" → screen (\(formatted(screenLocation.x)), \(formatted(screenLocation.y)))")
                }
                client.onResponseFinished = { finished = true }
                do {
                    let pcm = try Self.loadPCM16Mono24k(path: filePath)
                    let started = Date()
                    try await client.connectIfNeeded(mode: .pushToTalk)
                    print("smoke: connected in \(Int(Date().timeIntervalSince(started) * 1000)) ms")
                    // OPENCLICKY_SMOKE_TEACHER=1 offers the Teacher's tools (switching it on) and runs
                    // what the model asks for as the app would, printing the call.
                    let offersTeacher = ProcessInfo.processInfo.environment["OPENCLICKY_SMOKE_TEACHER"] == "1"
                    if offersTeacher {
                        client.pluginToolsProvider = { TeacherPlugin.realtimeToolDefinitions() }
                        client.onTranscript = { role, text in
                            print("smoke: \(role == .user ? "you" : "openclicky"): \(text)")
                            if role == .user { TeacherPlugin.startLesson(for: text, capture: client.lastScreenCapture) }
                        }
                        client.onPluginToolCall = { toolName, arguments, capture in
                            guard TeacherPlugin.toolNames.contains(toolName) else { return nil }
                            print("smoke: \(toolName) \(arguments)")
                            var settings = IslandComponentSettings(defaults: .standard)
                            if !settings.enabledPlugins.contains(TeacherPlugin.id) { settings.enabledPlugins.append(TeacherPlugin.id) }
                            return await TeacherPlugin.perform(toolName: toolName, arguments: arguments, capture: capture)
                        }
                    }
                    // Installed after connect (like a skill toggled / app switch mid-session) so the
                    // key-up tail refresh has a changed prompt to send: expect `instructions updated (N chars)`.
                    client.instructionsProvider = {
                        offersTeacher
                            ? RealtimeVoiceClient.defaultInstructions + "\n\n" + TeacherPlugin.promptInstruction
                            : RealtimeVoiceClient.defaultInstructions + "\n\n## Skill: smoke\nAlways mention the word 'harness' once."
                    }
                    client.beginPushToTalk()
                    var waitedForMic = 0
                    while !client.isCapturing && waitedForMic < 40 {
                        try? await Task.sleep(nanoseconds: 50_000_000)
                        waitedForMic += 1
                    }
                    let chunk = 4800 // 100 ms
                    var offset = 0
                    while offset < pcm.count {
                        let end = min(offset + chunk, pcm.count)
                        client.injectMicrophoneAudio(pcm16: pcm.subdata(in: offset..<end))
                        offset = end
                        try? await Task.sleep(nanoseconds: 100_000_000)
                    }
                    client.endPushToTalk()
                    var waited = 0.0
                    while !finished && waited < 20 {
                        try? await Task.sleep(nanoseconds: 250_000_000)
                        waited += 0.25
                    }
                    print("smoke: \(await client.debugSummary())")
                    // A `point_at` that fell back to Claude is still resolving when the reply ends.
                    await client.awaitPendingPointing()
                    try? await Task.sleep(nanoseconds: 3_000_000_000)
                    // The drawing is written in the background and then drawn; give it time to show.
                    if offersTeacher { try? await Task.sleep(nanoseconds: 14_000_000_000) }
                    print("smoke: after idle → \(await client.debugSummary())")
                    client.disconnect(reason: finished ? "smoke done" : "smoke timed out waiting for the reply")
                    exit(finished ? 0 : 1)
                } catch {
                    print("smoke: error=\(error.localizedDescription)")
                    exit(1)
                }
            }
            return
        }

        if let argumentIndex = launchArguments.firstIndex(of: "--openclicky-smoke-handsfree-file"), argumentIndex + 1 < launchArguments.count {
            // Standard hands-free on a recording instead of the room: a second and a half of quiet
            // either side, cut into turns by the same detector and heard by the ask lane's engine.
            let filePath = launchArguments[argumentIndex + 1]
            setvbuf(stdout, nil, _IONBF, 0)
            Task { @MainActor in
                guard let samples = StandardHandsFreeListener.samples16k(fileURL: URL(fileURLWithPath: filePath)) else {
                    print("smoke: cannot read \(filePath)")
                    exit(2)
                }
                let quiet = [Float](repeating: 0, count: UtteranceDetector.sampleRate * 3 / 2)
                let stream = quiet + samples + quiet + quiet
                var detector = UtteranceDetector()
                var offset = 0
                while offset + UtteranceDetector.frameLength <= stream.count {
                    let frame = Array(stream[offset..<offset + UtteranceDetector.frameLength])
                    offset += UtteranceDetector.frameLength
                    switch detector.process(frame: frame) {
                    case .started?: print("smoke: ▸ speech started at \(String(format: "%.2f", Double(offset) / 16_000)) s")
                    case .discarded?: print("smoke: ▸ too short, dropped")
                    case .finished(let turn)?:
                        print("smoke: ▸ turn of \(String(format: "%.1f", Double(turn.count) / 16_000)) s")
                        let started = Date()
                        let heard = await StandardHandsFreeListener.transcribe(turn)
                        print("smoke: heard in \(Int(Date().timeIntervalSince(started) * 1000)) ms: \(heard)\(HandsFreeSignOff.isSignOff(heard) ? "  [sign-off]" : "")")
                    case nil: break
                    }
                }
                exit(0)
            }
            return
        }

        if let argumentIndex = launchArguments.firstIndex(of: "--openclicky-smoke-handsfree"), argumentIndex + 1 < launchArguments.count {
            // Standard hands-free's ears, headless: listen for N seconds and print each turn it cuts
            // out of the microphone and what the ask lane's engine heard in it. Nothing is asked.
            let seconds = Double(launchArguments[argumentIndex + 1]) ?? 20
            setvbuf(stdout, nil, _IONBF, 0)
            print("smoke: hands-free listening for \(Int(seconds)) s")
            Task { @MainActor in
                let listener = StandardHandsFreeListener()
                do {
                    try await listener.start(preferredMicrophoneUID: nil) { event in
                        Task { @MainActor in
                            switch event {
                            case .started: print("smoke: ▸ speech started")
                            case .discarded: print("smoke: ▸ too short, dropped")
                            case .finished(let samples):
                                let length = String(format: "%.1f", Double(samples.count) / Double(UtteranceDetector.sampleRate))
                                print("smoke: ▸ turn of \(length) s")
                                let started = Date()
                                let heard = await StandardHandsFreeListener.transcribe(samples)
                                print("smoke: heard in \(Int(Date().timeIntervalSince(started) * 1000)) ms: \(heard)\(HandsFreeSignOff.isSignOff(heard) ? "  [sign-off]" : "")")
                            }
                        }
                    }
                } catch {
                    print("smoke: error=\(error.localizedDescription)")
                    exit(1)
                }
                listener.setAccepting(true)
                try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
                await listener.stop()
                exit(0)
            }
            return
        }

        if let argumentIndex = launchArguments.firstIndex(of: "--openclicky-smoke-talk"), argumentIndex + 1 < launchArguments.count {
            // Headless Realtime check: connect, greet, listen for N seconds, print transcripts, exit.
            let seconds = Double(launchArguments[argumentIndex + 1]) ?? 10
            setvbuf(stdout, nil, _IONBF, 0)
            print("smoke: starting (\(seconds)s)")
            Task { @MainActor in
                let client = RealtimeVoiceClient()
                client.onEvent = { print("smoke: ▸ \($0)") }
                client.onTranscript = { role, text in print("smoke: \(role == .user ? "you" : "openclicky"): \(text)") }
                client.onAgentTask = { task in "smoke agent would run: \(task)" }
                client.onMacAction = { action in .failed("smoke run does not perform \(action)") }
                do {
                    try await withThrowingTaskGroup(of: Void.self) { group in
                        group.addTask { try await client.connectIfNeeded(mode: .alwaysOn) }
                        group.addTask { try await Task.sleep(nanoseconds: 20_000_000_000); throw RealtimeVoiceError.backend("connect timed out after 20 s") }
                        try await group.next()
                        group.cancelAll()
                    }
                    client.startListeningContinuously()
                    client.requestResponse(instructions: "Greet the user in English in one short sentence as OpenClicky.")
                    var elapsed = 0.0
                    while elapsed < seconds {
                        try? await Task.sleep(nanoseconds: 3_000_000_000)
                        elapsed += 3
                        print("smoke: \(await client.debugSummary())")
                    }
                    client.disconnect(reason: "smoke done")
                    exit(0)
                } catch {
                    print("smoke: error=\(error.localizedDescription)")
                    exit(1)
                }
            }
            return
        }

        if let argumentIndex = launchArguments.firstIndex(of: "--openclicky-teacher-render"), argumentIndex + 2 < launchArguments.count {
            // Draws a sketch file, finished, into a PNG instead of onto the screen (comparing what
            // models draw without showing whatever else is on screen).
            //   OpenClicky --openclicky-teacher-render sketch.txt out.png [ink|chalk|paper]
            let sketchPath = launchArguments[argumentIndex + 1]
            let outputPath = launchArguments[argumentIndex + 2]
            let styleWord = argumentIndex + 3 < launchArguments.count ? launchArguments[argumentIndex + 3] : "paper"
            let style: TeacherBoardStyle = styleWord == "ink" ? .inkOnLightPage : (styleWord == "chalk" ? .chalkOnDarkPage : .paper)
            let result = TeacherSketchParser.parse((try? String(contentsOfFile: sketchPath, encoding: .utf8)) ?? "")
            guard let sketch = result.sketch else {
                print("render: nothing drawable (\(result.skippedLines.count) lines skipped)")
                exit(1)
            }
            let bounds = TeacherSketchLayout.drawingBounds(of: sketch)
            let width: CGFloat = 560
            let height = (width * bounds.height / max(bounds.width, 1)).rounded()
            let background = style == .chalkOnDarkPage ? Color(hex: "#1E1F22") : (style == .inkOnLightPage ? .white : Color(hex: "#FFFDF6"))
            let view = TeacherSketchView(sketch: sketch, style: style, startedAt: .distantPast)
                .padding(22)
                .frame(width: width, height: height)
                .background(background)
            let renderer = ImageRenderer(content: view)
            renderer.scale = 2
            guard let image = renderer.cgImage,
                  let png = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else {
                print("render: could not render")
                exit(1)
            }
            try? png.write(to: URL(fileURLWithPath: outputPath))
            print("render: \(sketch.strokes.count) strokes, skipped \(result.skippedLines) → \(outputPath)")
            exit(0)
        }

        if let argumentIndex = launchArguments.firstIndex(of: "--openclicky-teacher-lesson"), argumentIndex + 2 < launchArguments.count {
            // Writes the lesson for a question the way the app starts one (whatever route this
            // Mac's keys or account give), then draws it into a PNG and prints the route and time.
            //   OpenClicky --openclicky-teacher-lesson "teach me refraction" out.png [screen.jpg]
            let question = launchArguments[argumentIndex + 1]
            let outputPath = launchArguments[argumentIndex + 2]
            let screenshotJPEG = argumentIndex + 3 < launchArguments.count ? FileManager.default.contents(atPath: launchArguments[argumentIndex + 3]) : nil
            setvbuf(stdout, nil, _IONBF, 0)
            Task { @MainActor in
                let route = TeacherSketchWriter.route(for: ProviderRouter.currentCredentials())
                print("lesson: route \(TeacherSketchWriter.routeName(route)), starts early: \(TeacherSketchWriter.canStartLessonsEarly)")
                let startedAt = Date()
                let reply = await TeacherSketchWriter.lesson(for: question, screenshotJPEG: screenshotJPEG)
                print("lesson: answered in \(String(format: "%.1f", Date().timeIntervalSince(startedAt))) s")
                switch reply {
                case .picture(let query)?:
                    print("lesson: a picture of \(query)")
                    exit(0)
                case .sketch(let written)?:
                    try? written.write(toFile: outputPath + ".txt", atomically: true, encoding: .utf8)
                    let result = TeacherSketchParser.parse(written)
                    guard let sketch = result.sketch else {
                        print("lesson: nothing drawable")
                        exit(1)
                    }
                    let bounds = TeacherSketchLayout.drawingBounds(of: sketch)
                    let view = TeacherSketchView(sketch: sketch, style: .paper, startedAt: .distantPast)
                        .padding(22)
                        .frame(width: 560, height: (560 * bounds.height / max(bounds.width, 1)).rounded())
                        .background(Color(hex: "#FFFDF6"))
                    let renderer = ImageRenderer(content: view)
                    renderer.scale = 2
                    if let image = renderer.cgImage, let png = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) {
                        try? png.write(to: URL(fileURLWithPath: outputPath))
                    }
                    print("lesson: \(sketch.strokes.count) strokes, skipped \(result.skippedLines) → \(outputPath)")
                    exit(0)
                case nil:
                    print("lesson: nothing written (see app.log)")
                    exit(1)
                }
            }
            return
        }

        if let argumentIndex = launchArguments.firstIndex(of: "--openclicky-teacher-smoke") {
            // Draws a lesson on the cursor's screen without a voice turn, then exits: the draw
            // tool's own Pythagoras example, a sketch file, or `picture:<search words>`.
            //   OpenClicky --openclicky-teacher-smoke [sketch.txt | picture:neuron diagram]
            let argument = argumentIndex + 1 < launchArguments.count ? launchArguments[argumentIndex + 1] : nil
            setvbuf(stdout, nil, _IONBF, 0)
            Task { @MainActor in
                let capture = await CompanionScreenCaptureUtility.captureCursorScreenContext()?.capture
                print("smoke: screenshot \(capture == nil ? "unavailable (paper fallback)" : "taken")")
                let board = TeacherBoard.shared
                if let argument, argument.hasPrefix("picture:") {
                    let query = String(argument.dropFirst("picture:".count))
                    guard let picture = await TeacherPictureFinder.find(query: query, caption: query) else {
                        print("smoke: no picture found")
                        exit(1)
                    }
                    board.show(picture: picture, capture: capture, requestToken: board.beginPictureSearch())
                    print("smoke: picture from \(picture.credit), \(Int(picture.image.size.width))×\(Int(picture.image.size.height))")
                    try? await Task.sleep(nanoseconds: 8_000_000_000)
                    exit(0)
                }
                let source = argument.flatMap { try? String(contentsOfFile: $0, encoding: .utf8) }
                    ?? (TeacherPlugin.sketchGrammar.components(separatedBy: "Example, Pythagoras:\n").last ?? "")
                let result = TeacherSketchParser.parse(source)
                guard let sketch = result.sketch else {
                    print("smoke: nothing drawable (\(result.skippedLines.count) lines skipped)")
                    exit(1)
                }
                board.onLabelWritten = { location, _ in print("smoke: buddy → label at (\(Int(location.x)), \(Int(location.y)))") }
                board.show(sketch: sketch, title: "smoke", capture: capture)
                print("smoke: \(sketch.strokes.count) strokes, skipped \(result.skippedLines)")
                try? await Task.sleep(nanoseconds: UInt64((TeacherSketchTimeline.totalSeconds(for: sketch) + 4) * 1_000_000_000))
                exit(0)
            }
            return
        }

        #if DEBUG
        if let localAskIndex = launchArguments.firstIndex(of: "--openclicky-local-ask"), localAskIndex + 1 < launchArguments.count {
            // Debug: the local ask rung for one request (gate, Apple's classification, the mapping),
            // each step timed, nothing performed. How the classifier prompt gets tuned.
            setvbuf(stdout, nil, _IONBF, 0)
            let request = launchArguments[localAskIndex + 1]
            Task { @MainActor in
                let gateDecision = LocalAskRung.gate(transcript: request, replyLanguageCode: ReplyLanguage.currentCode)
                print("local-ask: gate \(gateDecision)")
                guard case .tryModel(let mentionsRewriting) = gateDecision else { exit(0) }
                guard let model = LocalAskModelAvailability.makeModel() else {
                    print("local-ask: unavailable: \(LocalAskModelAvailability.unavailableReason() ?? "?")")
                    exit(2)
                }
                let installedAppNames = InstalledAppResolver.installedAppNamesOnThisMac()
                let defaultBrowserName = InstalledAppResolver.defaultBrowserNameOnThisMac()
                let startedAt = Date()
                do {
                    let intent = try await model.classify(transcript: request)
                    let classifySeconds = Date().timeIntervalSince(startedAt)
                    print("local-ask: intent kind=\(intent.kind) name=\"\(intent.name)\" location=\"\(intent.location)\" volume=\(intent.volumeLevel) playback=\"\(intent.playback)\" rewrite=\"\(intent.rewriteInstruction)\" in \(String(format: "%.2f", classifySeconds)) s")
                    let mapping = LocalAskIntentMapper.map(intent, hasWritingTarget: mentionsRewriting, resolveApp: { spoken in
                        InstalledAppResolver.resolve(spoken: spoken, installedAppNames: installedAppNames, defaultBrowserName: defaultBrowserName)
                    })
                    print("local-ask: mapping \(mapping) (total \(String(format: "%.2f", Date().timeIntervalSince(startedAt))) s)")
                    // The same request again on a session warmed the way the ⌃⌥ press warms it.
                    model.prewarm()
                    try? await Task.sleep(for: .seconds(1.5))
                    let warmStartedAt = Date()
                    _ = try await model.classify(transcript: request)
                    print("local-ask: prewarmed classify in \(String(format: "%.2f", Date().timeIntervalSince(warmStartedAt))) s")
                    exit(0)
                } catch {
                    print("local-ask: error \(error)")
                    exit(1)
                }
            }
            return
        }
        if let rewriteIndex = launchArguments.firstIndex(of: "--openclicky-local-rewrite"), rewriteIndex + 2 < launchArguments.count {
            // Debug: one rewrite on Apple's model (instruction, then the text), timed, with the check's verdict.
            setvbuf(stdout, nil, _IONBF, 0)
            let instruction = launchArguments[rewriteIndex + 1]
            let text = launchArguments[rewriteIndex + 2]
            Task { @MainActor in
                guard let model = LocalAskModelAvailability.makeModel() else {
                    print("local-rewrite: unavailable: \(LocalAskModelAvailability.unavailableReason() ?? "?")")
                    exit(2)
                }
                let startedAt = Date()
                do {
                    let rewritten = try await model.rewrite(text, instruction: instruction)
                    print("local-rewrite: \(String(format: "%.2f", Date().timeIntervalSince(startedAt))) s, \(text.count)→\(rewritten.count) chars, check: \(LocalRewriteCheck.rejectionReason(original: text, rewritten: rewritten) ?? "ok")")
                    print(rewritten)
                    exit(0)
                } catch {
                    print("local-rewrite: error \(error)")
                    exit(1)
                }
            }
            return
        }
        if let checkIndex = launchArguments.firstIndex(of: "--openclicky-check-source"), checkIndex + 1 < launchArguments.count {
            // Debug: what Settings → Account's Test button says for one source (AnswerSource raw value).
            setvbuf(stdout, nil, _IONBF, 0)
            Task { @MainActor in
                guard let source = AnswerSource(rawValue: launchArguments[checkIndex + 1]) else { print("check-source: unknown source"); exit(1) }
                let outcome = await AnswerSourceCheck.run(source, credentials: ProviderRouter.currentCredentials(),
                                                          modelOverrides: OpenClickyConfiguration.settings.answerModels ?? [:])
                print("check-source: \(source.rawValue): \(outcome.sentence)")
                if case .works = outcome { exit(0) } else { exit(1) }
            }
            return
        }
        if let voiceIndex = launchArguments.firstIndex(of: "--openclicky-voice-smoke"), voiceIndex + 1 < launchArguments.count {
            // Debug: render two sentences in one voice (SpeechVoiceChoice raw value; "kokoro"
            // downloads Kokoro first if needed) and report how long each took. Nothing is played.
            setvbuf(stdout, nil, _IONBF, 0)
            Task { @MainActor in
                guard let voice = SpeechVoiceChoice(rawValue: launchArguments[voiceIndex + 1]) else { print("voice-smoke: unknown voice"); exit(1) }
                if voice == .kokoro && !KokoroVoice.isInstalled {
                    let kokoro = KokoroVoice.shared
                    kokoro.download()
                    var lastPercent = -1
                    while true {
                        switch kokoro.state {
                        case .installed: break
                        case .failed(let message): print("voice-smoke: kokoro download failed: \(message)"); exit(1)
                        case .downloading(let fraction):
                            let percent = Int(fraction * 100)
                            if percent / 10 != lastPercent / 10 { print("voice-smoke: kokoro downloading \(percent)%"); lastPercent = percent }
                        default: break
                        }
                        if kokoro.state == .installed { break }
                        try? await Task.sleep(nanoseconds: 500_000_000)
                    }
                    print("voice-smoke: kokoro installed")
                }
                for sentence in ["see that blue button up top? click it to start.", "then choose save as from the file menu, and pick your desktop."] {
                    let started = Date()
                    do {
                        let data = try await CloudVoiceSpeaker.audio(for: sentence, voice: voice)
                        let player = try AVAudioPlayer(data: data)
                        print("voice-smoke: \(voice.rawValue) rendered \(String(format: "%.1f", player.duration)) s of audio in \(String(format: "%.2f", Date().timeIntervalSince(started))) s")
                    } catch {
                        print("voice-smoke: \(voice.rawValue) error \(error.localizedDescription)")
                        exit(1)
                    }
                }
                // With a short idle time set, stay long enough for Kokoro to unload.
                if let idle = ProcessInfo.processInfo.environment["OPENCLICKY_KOKORO_IDLE_SECONDS"].flatMap(UInt64.init) {
                    try? await Task.sleep(nanoseconds: (idle + 3) * 1_000_000_000)
                }
                exit(0)
            }
            return
        }
        if launchArguments.contains("--openclicky-mac-voice-smoke") {
            // Debug: render one sentence in this Mac's voice to audio (not played) and report it.
            setvbuf(stdout, nil, _IONBF, 0)
            Task { @MainActor in
                let started = Date()
                do {
                    let data = try await MacVoice.shared.audio(for: "hi, i'm clicky. this is how i sound on this mac.")
                    let player = try AVAudioPlayer(data: data)
                    print("mac-voice-smoke: \(MacVoice.currentVoice()?.name ?? "default") rendered \(data.count / 1024) KB, \(String(format: "%.1f", player.duration)) s of audio in \(String(format: "%.2f", Date().timeIntervalSince(started))) s")
                    print("mac-voice-smoke: installed \(MacVoice.choices().map(\.label).prefix(8).joined(separator: ", "))")
                    exit(player.duration > 1 ? 0 : 2)
                } catch {
                    print("mac-voice-smoke: error \(error.localizedDescription)")
                    exit(1)
                }
            }
            return
        }
        if let argumentIndex = launchArguments.firstIndex(of: "--openclicky-teacher-ask"), argumentIndex + 1 < launchArguments.count {
            // Debug: one real question down the ask lane's own prompt and tools (whatever source
            // shell.json routes to) with the Teacher switched on; draws what comes back, then exits.
            //   OpenClicky --openclicky-teacher-ask "teach me how refraction works"
            let question = launchArguments[argumentIndex + 1]
            setvbuf(stdout, nil, _IONBF, 0)
            var settings = IslandComponentSettings(defaults: .standard)
            if !settings.enabledPlugins.contains(TeacherPlugin.id) { settings.enabledPlugins.append(TeacherPlugin.id) }
            Task { @MainActor in
                print("teacher-ask: route \(ProviderRouter.current(for: .claude).logName)")
                let captures = (try? await CompanionScreenCaptureUtility.captureAllScreensAsJPEG()) ?? []
                let cursorCapture = captures.first(where: { $0.isCursorScreen }) ?? captures.first
                let api = ClaudeAPI(currentProxyURL: { "\(OpenClickyConfiguration.backendBaseURL)/chat" })
                let startedAt = Date()
                var firstTextAt: Date?
                do {
                    let (text, toolCalls, duration) = try await api.analyzeImageStreaming(
                        // No screenshot goes out: the drawing does not depend on it, and a test run
                        // must not send whatever happens to be on screen. It only places the board.
                        images: [],
                        systemPrompt: CompanionManager.withReplyLanguage(CompanionManager.companionVoiceResponseSystemPrompt + CompanionManager.pluginPromptLines()),
                        userPrompt: question,
                        tools: TeacherPlugin.anthropicToolDefinitions(),
                        onTextChunk: { _ in if firstTextAt == nil { firstTextAt = Date() } })
                    print("teacher-ask: first text after \(String(format: "%.1f", firstTextAt?.timeIntervalSince(startedAt) ?? -1)) s, done in \(String(format: "%.1f", duration)) s")
                    print("teacher-ask: said \"\(text)\"")
                    for call in toolCalls {
                        print("teacher-ask: \(call.name) \(call.arguments["title"] ?? call.arguments["query"] ?? "")\n\(call.arguments["sketch"] ?? "")")
                        if call.name == TeacherPlugin.drawToolName {
                            print("teacher-ask: skipped lines \(TeacherSketchParser.parse(call.arguments["sketch"] ?? "").skippedLines)")
                        }
                        print("teacher-ask: → \(await TeacherPlugin.perform(toolName: call.name, arguments: call.arguments, capture: cursorCapture) ?? "not a teacher tool")")
                    }
                    let drawingSeconds: Double
                    if case .sketch(let sketch)? = TeacherBoard.shared.lesson?.content {
                        drawingSeconds = TeacherSketchTimeline.totalSeconds(for: sketch)
                    } else {
                        drawingSeconds = 0
                    }
                    try? await Task.sleep(nanoseconds: UInt64((drawingSeconds + 8) * 1_000_000_000))
                    exit(toolCalls.isEmpty ? 2 : 0)
                } catch {
                    print("teacher-ask: error \(error.localizedDescription)")
                    exit(1)
                }
            }
            return
        }
        if launchArguments.contains("--openclicky-answer-smoke") {
            // Debug: one real question (a red square) through ClaudeAPI and whatever source
            // shell.json routes to, printing the streamed text and the answer, then exit.
            setvbuf(stdout, nil, _IONBF, 0)
            Task { @MainActor in
                let route = ProviderRouter.current(for: .claude)
                print("answer-smoke: route \(route.logName)")
                let api = ClaudeAPI(currentProxyURL: { "\(OpenClickyConfiguration.backendBaseURL)/chat" })
                let redSquare = AnswerSourceCheck.redSquarePNG()
                var chunks = 0
                do {
                    let (text, toolCalls, duration) = try await api.analyzeImageStreaming(
                        images: [(data: redSquare, label: "a test image")],
                        systemPrompt: "answer in lowercase, one word.",
                        userPrompt: "what color is this square?",
                        onTextChunk: { _ in chunks += 1 })
                    print("answer-smoke: \"\(text)\" in \(String(format: "%.1f", duration)) s, \(chunks) chunk updates, \(toolCalls.count) tool calls")
                    exit(text.lowercased().contains("red") ? 0 : 2)
                } catch {
                    print("answer-smoke: error \(error.localizedDescription)")
                    exit(1)
                }
            }
            return
        }
        #endif

        print("🎯 OpenClicky: Starting...")
        print("🎯 OpenClicky: Version \(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "unknown")")

        // Launched from the disk image: offer to move to /Applications and run from there.
        if DiskImageSelfInstaller.offerToInstallIfNeeded() { return }

        UserDefaults.standard.register(defaults: ["NSInitialToolTipDelay": 0])

        ClickyAnalytics.configure()
        ClickyAnalytics.trackAppOpened()

        menuBarPanelManager = MenuBarPanelManager(companionManager: companionManager)
        companionManager.start()
        #if DEBUG
        if let tourIndex = launchArguments.firstIndex(of: "--openclicky-ui-tour"), tourIndex + 1 < launchArguments.count {
            UITour(companionManager: companionManager, logPath: launchArguments[tourIndex + 1]).run()
        }
        if let pasteIndex = launchArguments.firstIndex(of: "--openclicky-paste-probe"), pasteIndex + 1 < launchArguments.count {
            // Debug: does this build's paste land in the front app's text box? ⌘A then the text
            // through FrontAppTextInserter, read back by PasteLanding, after 3 s for the box to be focused.
            setvbuf(stdout, nil, _IONBF, 0)
            let text = launchArguments[pasteIndex + 1]
            Task { @MainActor in
                try? await Task.sleep(for: .seconds(3))
                let before = FocusedFieldReader.snapshot()
                print("paste-probe: trusted=\(AXIsProcessTrusted()) front=\(before.appName ?? "?") role=\(before.role ?? "none") editable=\(before.isEditable)")
                _ = WriteInPlace.postCommandKey(0) // virtual key code 0 is "a": ⌘A
                try? await Task.sleep(for: .milliseconds(150))
                let outcome = FrontAppTextInserter.insert(text)
                let landing = await PasteLanding.verify(text: text, before: before)
                print("paste-probe: insert=\(outcome) landing=\(landing)")
                exit(0)
            }
        }
        if let askIndex = launchArguments.firstIndex(of: "--openclicky-ask"), askIndex + 1 < launchArguments.count {
            // Debug: one typed question through the whole ask lane (the local rung, else the cloud
            // path with its pause and screenshot), exiting once the turn is over. For live checks
            // of the lane without the keyboard; the log says which tier answered.
            setvbuf(stdout, nil, _IONBF, 0)
            let question = launchArguments[askIndex + 1]
            let companionManager = self.companionManager
            Task { @MainActor in
                try? await Task.sleep(for: .seconds(3))
                companionManager.submitTypedRequest(question)
                let startedAt = Date()
                var sawTheTurnStart = false
                while Date().timeIntervalSince(startedAt) < 90 {
                    try? await Task.sleep(for: .milliseconds(200))
                    if companionManager.isAskInFlight || companionManager.voiceState != .idle {
                        sawTheTurnStart = true
                    } else if sawTheTurnStart {
                        break
                    }
                }
                print("ask-smoke: done, source=\(companionManager.answeringSourceName ?? "-"), \(String(format: "%.1f", Date().timeIntervalSince(startedAt))) s")
                exit(0)
            }
        }
        #endif
        // OpenClicky: launching at login is opt-in (`registerAsLoginItem` in ~/.openclicky/shell.json).
        if OpenClickyConfiguration.settings.registerAsLoginItem == true {
            registerAsLoginItemIfNeeded()
        }
        // startSparkleUpdater()
    }

    func applicationWillTerminate(_ notification: Notification) {
        companionManager.stop()
    }

    /// The Dock icon (while the window is open) or `open -a OpenClicky`: show the window.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        companionManager.showDictationWindow()
        return false
    }

    /// Registers the app as a login item so it launches automatically on
    /// startup. Uses SMAppService which shows the app in System Settings >
    /// General > Login Items, letting the user toggle it off if they want.
    private func registerAsLoginItemIfNeeded() {
        let loginItemService = SMAppService.mainApp
        if loginItemService.status != .enabled {
            do {
                try loginItemService.register()
                print("🎯 OpenClicky: Registered as login item")
            } catch {
                print("⚠️ OpenClicky: Failed to register as login item: \(error)")
            }
        }
    }

    private func startSparkleUpdater() {
        let updaterController = SPUStandardUpdaterController(
            startingUpdater: false,
            updaterDelegate: nil,
            userDriverDelegate: nil
        )
        self.sparkleUpdaterController = updaterController

        do {
            try updaterController.updater.start()
        } catch {
            print("⚠️ OpenClicky: Sparkle updater failed to start: \(error)")
        }
    }
}


extension CompanionAppDelegate {
    /// Reads any audio file and returns PCM16 mono 24 kHz bytes (the Realtime input format).
    static func loadPCM16Mono24k(path: String) throws -> Data {
        let file = try AVAudioFile(forReading: URL(fileURLWithPath: path))
        guard let source = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)) else {
            throw RealtimeVoiceError.audio("cannot allocate a buffer for \(path)")
        }
        try file.read(into: source)
        let target = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 24_000, channels: 1, interleaved: true)!
        let monoFormat = AVAudioFormat(standardFormatWithSampleRate: file.processingFormat.sampleRate, channels: 1)!
        guard let mono = AVAudioPCMBuffer(pcmFormat: monoFormat, frameCapacity: source.frameLength),
              let from = source.floatChannelData?[0], let to = mono.floatChannelData?[0] else {
            throw RealtimeVoiceError.audio("unsupported sample format in \(path)")
        }
        to.update(from: from, count: Int(source.frameLength))
        mono.frameLength = source.frameLength
        guard let converter = AVAudioConverter(from: monoFormat, to: target),
              let out = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: AVAudioFrameCount(Double(source.frameLength) * 24_000 / monoFormat.sampleRate) + 64) else {
            throw RealtimeVoiceError.audio("cannot convert \(path)")
        }
        var consumed = false
        var error: NSError?
        converter.convert(to: out, error: &error) { _, status in
            if consumed { status.pointee = .noDataNow; return nil }
            consumed = true; status.pointee = .haveData; return mono
        }
        if let error { throw error }
        return Data(bytes: out.int16ChannelData![0], count: Int(out.frameLength) * 2)
    }
}
