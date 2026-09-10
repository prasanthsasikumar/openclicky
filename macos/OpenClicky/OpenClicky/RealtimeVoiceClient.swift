//
//  RealtimeVoiceClient.swift
//  OpenClicky
//
//  The fast voice lane (HeyClicky's RealtimeVoiceClient): speech in, speech out over one
//  OpenAI Realtime WebSocket, no transcribe → think → synthesize chain.
//
//  - The backend mints a short-lived client secret (`POST /agent/realtime/session`); the real
//    key never reaches the app.
//  - AVAudioEngine with Apple's voice-processing unit gives acoustic echo cancellation, so the
//    mic can stay open while OpenClicky talks (full duplex, barge-in).
//  - Push-to-talk: audio streams while the shortcut is held; on release a 400 ms tail is kept,
//    then the buffer is committed and a response requested. Always-on: server VAD turns.
//  - `send_to_agent` tool calls hand real work to the Codex agent lane and speak the result.
//  - `point_at` tool calls fly the cursor buddy to a spot in the screenshot attached to the turn,
//    one call per step, so the model can point while it explains.
//

import AVFoundation
import Combine
import Foundation

/// The screen context attached to a Realtime turn: the JPEG the model sees, a caption with the
/// pointer position, and the capture it came from (display frame + pixel size) so `point_at`
/// coordinates can be mapped back onto the display.
struct RealtimeScreenContext {
    let jpeg: Data
    let caption: String
    let capture: CompanionScreenCapture
}

@MainActor
final class RealtimeVoiceClient: NSObject, ObservableObject {
    enum TurnMode {
        case pushToTalk
        case alwaysOn
    }

    enum TranscriptRole {
        case user
        case assistant
    }

    @Published private(set) var isConnected = false
    /// True while assistant audio is queued or playing.
    @Published private(set) var isSpeaking = false
    /// 0…1 microphone level for the waveform while capturing.
    @Published private(set) var inputLevel: CGFloat = 0

    var onTranscript: ((TranscriptRole, String) -> Void)?
    var onEvent: ((String) -> Void)?
    var onResponseStarted: (() -> Void)?
    var onResponseFinished: (() -> Void)?
    /// Runs the agent for a `send_to_agent` tool call; the returned text is spoken by the model.

    /// Supplies the screen context attached to every turn: a JPEG of the cursor screen plus a short
    /// caption (pointer position, screen size). Nil → the turn goes out without an image.
    var screenContextProvider: (() async -> RealtimeScreenContext?)?
    /// A `point_at` tool call: screenshot pixel coordinates (origin top-left), the model's 1–3 word
    /// label, and the capture the coordinates refer to (the last one attached to the conversation).
    var onPointAt: ((CGPoint, String, CompanionScreenCapture) -> Void)?
    /// The capture behind the most recently attached screen context; `point_at` maps onto it.
    private(set) var lastScreenCapture: CompanionScreenCapture?
    /// OCR of `lastScreenCapture`, started as soon as the screen is attached so it is ready
    /// (~400 ms) by the time the model's first `point_at` arrives; `point_at` snaps to it.
    private var lastScreenText: Task<[ScreenTextLine], Never>?
    /// `point_at` calls resolve one after another (OCR snap is instant; the Claude fallback for
    /// icons takes 2–4 s) so a multi-step walkthrough flies in the order the model gave.
    private var pointingChain: Task<Void, Never>?
    /// The last thing the user said, given to Claude when it has to locate an icon.
    private var lastUserTranscript: String?

    /// Waits for every queued `point_at` to resolve and fly (the smoke harness exits after this).
    func awaitPendingPointing() async {
        await pointingChain?.value
    }
    /// True while microphone audio is being streamed to the server.
    @Published private(set) var isCapturing = false
    var onAgentTask: ((String) async -> String)?
    /// Performs a fast local action (open an app, make a folder…) and returns the sentence to say.
    /// Main-actor isolated: `MacActionRunner` touches AppKit.
    var onMacAction: (@MainActor (MacAction) async -> MacActionOutcome)?
    /// Builds the session instructions for the next turn: the base prompt plus the skills that
    /// apply right now (the app in front, the user's activated skills). Read at connect and before
    /// every turn; only a change is sent to the server as `session.update`.
    var instructionsProvider: (() -> String)?
    /// The instructions the server currently holds, so unchanged turns send nothing.
    private var sentInstructions = ""

    private(set) var turnMode: TurnMode = .pushToTalk
    private var voice: String?
    private var instructions: String
    /// The prompt this client was created with; providers compose their skills block onto it.
    var baseInstructions: String { instructions }

    private var webSocketTask: URLSessionWebSocketTask?
    private lazy var urlSession = URLSession(configuration: .default)
    private var connectTask: Task<Void, Error>?
    private var keepWarmTask: Task<Void, Never>?

    /// Audio runs off the main actor: CoreAudio's first-time setup hops synchronously to the main
    /// queue, so configuring the engine from a main-actor task deadlocks the app.
    private let audio = RealtimeAudioEngine()
    private var isAudioWired = false
    private var isForwardingMicrophone = false
    private var currentMode: TurnMode = .pushToTalk
    private var pushToTalkArmed = false
    private var idlePauseTask: Task<Void, Never>?
    private var microphoneAppends = 0

    /// Capture/send statistics for diagnostics.
    func debugSummary() async -> String {
        "mic: \(await audio.debugSummary()); appends sent \(microphoneAppends); forwarding \(isForwardingMicrophone); connected \(isConnected)"
    }
    private var pushToTalkTailTask: Task<Void, Never>?
    private var assistantTranscriptBuffer = ""
    private var responseInProgress = false
    /// Set when a tool output was sent during the current response. A model may emit several
    /// tool calls in one response (e.g. `point_at` per step); each output goes out at once so the
    /// point fires while it is still talking, but the continuation `response.create` is sent only
    /// once, on `response.done` — a second one while a response is active is rejected by the server.
    private var needsContinuationAfterResponse = false
    /// Id of the response the server is generating right now (`response.created`), so tool calls
    /// can be matched to it; nil between responses.
    private var activeResponseId: String?
    /// Responses we cancelled (barge-in). Their late tool calls still get an output item, but
    /// must never arm a continuation — the server's `response.done` for them is status "cancelled".
    private var cancelledResponseIds: Set<String> = []

    private static let sampleRate: Double = RealtimeAudioEngine.sampleRate

    static let defaultInstructions = """
    You are OpenClicky, a friendly, fast macOS voice assistant. Speak English unless the user speaks another language. \
    Keep spoken replies short (one or two sentences). Every request comes with a screenshot of the user's current screen \
    (with the pointer position noted): "this", "here", "that" refer to what is on screen, usually near the pointer. Look \
    at the screenshot and answer about it directly; never say you cannot see the screen and never mention a camera. \
    Answer quick questions yourself. When the user asks how to do something, where something is, or what to \
    click, point at it with the point_at tool while you explain — one call per step, in order, with the element's \
    exact on-screen text when it has any, and keep speaking between calls. Do not point for general questions or things they are obviously already looking \
    at. \
    Do simple local things yourself with the fast tools — open_app, open_url, create_folder, \
    reveal_in_finder, set_volume, media_control — and then say the one sentence they give back. \
    For anything bigger — editing files or code, running commands, using integrations, research, \
    multi-step tasks — first say one short sentence acknowledging it, then call the send_to_agent \
    tool with a clear, self-contained task, and afterwards tell the user in one sentence what happened. \
    Never pretend work was done without the tool. Do not read file paths aloud character by character.
    """

    init(voice: String? = nil, instructions: String = RealtimeVoiceClient.defaultInstructions) {
        self.voice = voice
        self.instructions = instructions
        super.init()
    }

    // MARK: - Connection

    /// Connect (or reuse the live connection). Safe to call before every push-to-talk press.
    func connectIfNeeded(mode: TurnMode) async throws {
        if isConnected, turnMode == mode { return }
        if let connectTask, turnMode == mode {
            try await connectTask.value
            return
        }
        disconnect(reason: nil)
        turnMode = mode
        let task = Task { try await self.openConnection(mode: mode) }
        connectTask = task
        defer { connectTask = nil }
        try await task.value
    }

    /// Keep a session open from launch so the first press only starts audio. Reconnects on drop.
    func keepWarm(mode: TurnMode) {
        keepWarmTask?.cancel()
        keepWarmTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                if !self.isConnected {
                    do { try await self.connectIfNeeded(mode: mode) } catch { self.log("warm-up failed: \(error.localizedDescription)") }
                }
                try? await Task.sleep(nanoseconds: 20_000_000_000)
            }
        }
    }

    func disconnect(reason: String?) {
        keepWarmTask?.cancel()
        keepWarmTask = nil
        stopForwardingMicrophone()
        pushToTalkArmed = false
        idlePauseTask?.cancel()
        flushPlayback()
        audio.release()
        webSocketTask?.cancel(with: .normalClosure, reason: nil)
        webSocketTask = nil
        if isConnected { isConnected = false }
        responseInProgress = false
        needsContinuationAfterResponse = false
        activeResponseId = nil
        cancelledResponseIds.removeAll()
        lastScreenCapture = nil
        lastScreenText?.cancel()
        lastScreenText = nil
        pointingChain?.cancel()
        pointingChain = nil
        lastUserTranscript = nil
        sentInstructions = ""
        if let reason { log(reason) }
    }

    private func openConnection(mode: TurnMode) async throws {
        guard OpenClickyConfiguration.isConfigured else { throw RealtimeVoiceError.notConfigured }
        var secretRequest = URLRequest(url: URL(string: "\(OpenClickyConfiguration.backendBaseURL)/agent/realtime/session")!)
        secretRequest.httpMethod = "POST"
        secretRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
        OpenClickyConfiguration.authorize(&secretRequest)
        let connectInstructions = currentInstructions()
        secretRequest.httpBody = try JSONSerialization.data(withJSONObject: ["voice": voice as Any, "instructions": connectInstructions].compactMapValues { $0 })
        let (secretData, secretResponse) = try await urlSession.data(for: secretRequest)
        guard let httpResponse = secretResponse as? HTTPURLResponse, (200...299).contains(httpResponse.statusCode),
              let secretJSON = try JSONSerialization.jsonObject(with: secretData) as? [String: Any],
              let clientSecret = secretJSON["value"] as? String else {
            throw RealtimeVoiceError.backend(String(data: secretData, encoding: .utf8) ?? "no client secret")
        }
        let model = (secretJSON["session"] as? [String: Any])?["model"] as? String ?? "gpt-realtime"

        var socketRequest = URLRequest(url: URL(string: "wss://api.openai.com/v1/realtime?model=\(model)")!)
        socketRequest.setValue("Bearer \(clientSecret)", forHTTPHeaderField: "Authorization")
        let task = urlSession.webSocketTask(with: socketRequest)
        webSocketTask = task
        task.resume()
        currentMode = mode
        // Push-to-talk only opens the microphone while the shortcut is held; always-on keeps it open.
        // The graph is built once here (and released right away) so key-down is a fast engine restart.
        try await startAudioIfNeeded()
        if mode == .pushToTalk { audio.pause() }
        try send(sessionUpdate(mode: mode, instructions: connectInstructions))
        sentInstructions = connectInstructions
        isConnected = true
        log("realtime connected (\(model), \(mode == .pushToTalk ? "push-to-talk" : "always on"))")
        receiveLoop(task)
    }

    /// The local actions OpenClicky performs itself. Typed arguments only: `location` is a closed
    /// set and there is no path or command anywhere in the schema, so a mishearing cannot widen
    /// what the fast lane is able to do. See MacActions.swift.
    nonisolated static func fastActionToolDefinitions() -> [[String: Any]] {
        let locations = MacActionLocation.allCases.map(\.rawValue)
        let locationProperty: [String: Any] = [
            "type": "string",
            "enum": locations,
            "description": "Which folder. Leave it out for OpenClicky's own workspace folder.",
        ]
        return [
            [
                "type": "function",
                "name": "open_app",
                "description": "Open or switch to a Mac app by its name, e.g. 'Spotify'. Use this instead of the agent for simply opening an app.",
                "parameters": ["type": "object", "properties": ["name": ["type": "string", "description": "The app's name as the user said it."]], "required": ["name"]],
            ],
            [
                "type": "function",
                "name": "open_url",
                "description": "Open a web page in the user's browser. http and https only.",
                "parameters": ["type": "object", "properties": ["url": ["type": "string", "description": "The full https URL."]], "required": ["url"]],
            ],
            [
                "type": "function",
                "name": "create_folder",
                "description": "Make a new folder. Use this instead of the agent for a single folder.",
                "parameters": [
                    "type": "object",
                    "properties": [
                        "name": ["type": "string", "description": "The folder's name, without any slashes."],
                        "location": locationProperty,
                    ],
                    "required": ["name"],
                ],
            ],
            [
                "type": "function",
                "name": "reveal_in_finder",
                "description": "Show an existing file or folder in Finder.",
                "parameters": [
                    "type": "object",
                    "properties": [
                        "name": ["type": "string", "description": "The file or folder's name."],
                        "location": locationProperty,
                    ],
                    "required": ["name"],
                ],
            ],
            [
                "type": "function",
                "name": "set_volume",
                "description": "Set the Mac's output volume.",
                "parameters": ["type": "object", "properties": ["level": ["type": "integer", "description": "0 to 100."]], "required": ["level"]],
            ],
            [
                "type": "function",
                "name": "media_control",
                "description": "Play, pause, or skip whatever is playing.",
                "parameters": ["type": "object", "properties": ["action": ["type": "string", "enum": ["playpause", "next", "previous"]]], "required": ["action"]],
            ],
        ]
    }

    private func sessionUpdate(mode: TurnMode, instructions text: String) -> [String: Any] {
        var input: [String: Any] = [
            "format": ["type": "audio/pcm", "rate": Int(Self.sampleRate)],
            "transcription": ["model": "gpt-4o-mini-transcribe"],
        ]
        switch mode {
        case .pushToTalk:
            input["turn_detection"] = NSNull()
        case .alwaysOn:
            // create_response is off: the screen capture is attached when the server commits the
            // utterance, then the response is requested (see input_audio_buffer.committed).
            input["turn_detection"] = ["type": "server_vad", "silence_duration_ms": 600, "create_response": false, "interrupt_response": true]
        }
        var output: [String: Any] = ["format": ["type": "audio/pcm", "rate": Int(Self.sampleRate)]]
        if let voice { output["voice"] = voice }
        let tool: [String: Any] = [
            "type": "function",
            "name": "send_to_agent",
            "description": "Hand a task that requires doing work (files, code, commands, apps, research) to the OpenClicky agent. Returns a short result summary.",
            "parameters": ["type": "object", "properties": ["task": ["type": "string", "description": "A clear, self-contained description of what to do."]], "required": ["task"]],
        ]
        let pointTool: [String: Any] = [
            "type": "function",
            "name": "point_at",
            "description": "Fly the on-screen cursor buddy to a UI element in the attached screenshot and show a short label. Use it while you explain: one call per step, in order, as you say each step. Coordinates are pixels in the screenshot, origin top-left; the buddy snaps to the element's visible text near them, so always pass that text when the element has any.",
            "parameters": [
                "type": "object",
                "properties": [
                    "x": ["type": "integer", "description": "Horizontal pixel in the screenshot, from the left edge."],
                    "y": ["type": "integer", "description": "Vertical pixel in the screenshot, from the top edge."],
                    "label": ["type": "string", "description": "1-3 words naming the element, e.g. 'export button'"],
                    "text": ["type": "string", "description": "The text written on the element itself (button caption, menu item, link, tab title), copied exactly as it appears on screen. Empty string for icon-only elements."],
                ],
                "required": ["x", "y", "label", "text"],
            ],
        ]
        return [
            "type": "session.update",
            "session": [
                "type": "realtime",
                "instructions": text,
                "tools": [tool, pointTool] + Self.fastActionToolDefinitions(),
                "tool_choice": "auto",
                "audio": ["input": input, "output": output],
            ],
        ]
    }

    // MARK: - Push-to-talk / always-on control

    // MARK: - Instructions (skills)

    private func currentInstructions() -> String {
        instructionsProvider?() ?? instructions
    }

    /// Re-send the session when the provider's instructions changed (a different app is in front,
    /// a skill was toggled). Called from the push-to-talk tail (before the screen is attached and
    /// the reply requested) and at speech start in always-on. The provider walks the front app's
    /// Accessibility tree, so this must never sit on the key-down path in front of the mic open.
    /// The full session payload (tools, audio formats) is re-sent so nothing depends on the server
    /// merging a partial update; an unchanged prompt sends nothing.
    func refreshInstructionsIfNeeded() {
        guard isConnected else { return }
        let text = currentInstructions()
        guard text != sentInstructions else { return }
        do {
            try send(sessionUpdate(mode: currentMode, instructions: text))
            sentInstructions = text
            log("instructions updated (\(text.count) chars)")
        } catch {
            log("instructions update failed: \(error.localizedDescription)")
        }
    }

    /// Shortcut pressed: interrupt any reply and stream the microphone.
    func beginPushToTalk() {
        pushToTalkTailTask?.cancel()
        pushToTalkTailTask = nil
        idlePauseTask?.cancel()
        idlePauseTask = nil
        if responseInProgress { cancelActiveResponse() }
        flushPlayback()
        try? send(["type": "input_audio_buffer.clear"])
        pushToTalkArmed = true
        Task { [weak self] in
            guard let self else { return }
            let started = Date()
            do {
                try await self.startAudioIfNeeded()
            } catch {
                self.log("microphone unavailable: \(error.localizedDescription)")
                return
            }
            guard self.pushToTalkArmed else { return }
            self.log("microphone open in \(Int(Date().timeIntervalSince(started) * 1000)) ms")
            self.startForwardingMicrophone()
        }
        // No instructions refresh here: the Task above cannot start until this main-actor function
        // returns, so any work done here delays the mic open. The refresh runs in the key-up tail.
    }

    /// Shortcut released: keep the mic open 400 ms so the last word is not clipped, then commit.
    func endPushToTalk() {
        pushToTalkArmed = false
        pushToTalkTailTask?.cancel()
        pushToTalkTailTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 400_000_000)
            guard !Task.isCancelled, let self else { return }
            self.stopForwardingMicrophone()
            // Skills for the app in front go up before the screen and the response request.
            self.refreshInstructionsIfNeeded()
            await self.attachScreenContext()
            try? self.send(["type": "input_audio_buffer.commit"])
            self.requestTurnResponse()
            self.scheduleIdlePauseIfNeeded()
        }
    }

    /// A typed turn (the notch composer): the text goes into the conversation together with the
    /// current screen, then a reply is requested exactly like a released push-to-talk. The reply
    /// is spoken, so the audio graph is started for playback.
    func sendTextTurn(_ text: String) async {
        pushToTalkTailTask?.cancel()
        pushToTalkTailTask = nil
        idlePauseTask?.cancel()
        idlePauseTask = nil
        if responseInProgress { cancelActiveResponse() }
        flushPlayback()
        Task { [weak self] in try? await self?.startAudioIfNeeded() }
        refreshInstructionsIfNeeded()
        await attachScreenContext()
        let item: [String: Any] = [
            "type": "message",
            "role": "user",
            "content": [["type": "input_text", "text": text]],
        ]
        try? send(["type": "conversation.item.create", "item": item])
        requestTurnResponse()
        scheduleIdlePauseIfNeeded()
        log("text turn: \(text)")
    }

    /// Always-on: stream continuously; the server decides the turns.
    func startListeningContinuously() {
        idlePauseTask?.cancel()
        idlePauseTask = nil
        Task { [weak self] in
            guard let self else { return }
            try? await self.startAudioIfNeeded()
            self.startForwardingMicrophone()
        }
    }

    func stopListening() {
        stopForwardingMicrophone()
        scheduleIdlePauseIfNeeded()
    }

    /// Frees the microphone for another capture path. fn + control dictation records through its
    /// own AVAudioEngine, and while this client's voice-processing input unit exists — even
    /// paused, as it is from launch until the first talk turn — that other engine reads silence.
    /// Returns once the graph is torn down. `resumeListeningAfterDictation()` puts always-on back.
    func releaseMicrophoneForDictation() async {
        idlePauseTask?.cancel()
        idlePauseTask = nil
        wasListeningContinuouslyBeforeDictation = isForwardingMicrophone && currentMode == .alwaysOn
        stopForwardingMicrophone()
        await audio.releaseNow()
        log("microphone released for dictation")
    }

    func resumeListeningAfterDictation() {
        guard wasListeningContinuouslyBeforeDictation else { return }
        wasListeningContinuouslyBeforeDictation = false
        guard isConnected, currentMode == .alwaysOn else { return }
        startListeningContinuously()
        log("always-on listening resumed after dictation")
    }
    private var wasListeningContinuouslyBeforeDictation = false

    /// Adds the current screen to the conversation so the model can answer "what is this?".
    private func attachScreenContext() async {
        guard let screenContextProvider else { return }
        let started = Date()
        guard let context = await screenContextProvider() else {
            log("no screen context (screen recording permission?)")
            return
        }
        let item: [String: Any] = [
            "type": "message",
            "role": "user",
            "content": [
                ["type": "input_text", "text": "[Screen context, not spoken] \(context.caption)"],
                ["type": "input_image", "image_url": "data:image/jpeg;base64,\(context.jpeg.base64EncodedString())"],
            ],
        ]
        try? send(["type": "conversation.item.create", "item": item])
        lastScreenCapture = context.capture
        lastScreenText?.cancel()
        let jpeg = context.jpeg
        lastScreenText = Task { [weak self] in
            let started = Date()
            let lines = (try? await ScreenTextRecognizer.recognize(jpeg: jpeg)) ?? []
            if !Task.isCancelled { self?.log("screen text: \(lines.count) lines in \(Int(Date().timeIntervalSince(started) * 1000)) ms") }
            return lines
        }
        log("screen attached (\(context.jpeg.count / 1024) KB, \(Int(Date().timeIntervalSince(started) * 1000)) ms)")
    }

    /// Push-to-talk: once nothing is being captured, generated or played, release the microphone
    /// so the system's recording indicator goes away between turns. After half a minute idle the
    /// engine is torn down completely: a stopped voice-processing unit still sits on the output
    /// device, and other apps' audio only returns to normal once it is gone.
    private func scheduleIdlePauseIfNeeded() {
        guard currentMode == .pushToTalk else { return }
        idlePauseTask?.cancel()
        idlePauseTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            guard !Task.isCancelled, let self else { return }
            guard !self.isForwardingMicrophone, !self.responseInProgress, !self.isSpeaking, !self.pushToTalkArmed else { return }
            self.audio.pause()
            try? await Task.sleep(nanoseconds: 28_500_000_000)
            guard !Task.isCancelled, !self.isForwardingMicrophone, !self.responseInProgress, !self.isSpeaking, !self.pushToTalkArmed else { return }
            self.audio.release()
            self.log("audio released after idle")
        }
    }

    /// Speak something proactively (used for the greeting on first connect).
    func requestResponse(instructions: String? = nil) {
        idlePauseTask?.cancel()
        idlePauseTask = nil
        Task { [weak self] in try? await self?.startAudioIfNeeded() }
        var body: [String: Any] = ["type": "response.create"]
        if let instructions { body["response"] = ["instructions": instructions] }
        try? send(body)
    }

    // MARK: - Audio (delegated to RealtimeAudioEngine on its own queue)

    private func startAudioIfNeeded() async throws {
        let microphoneAuthorization = AVCaptureDevice.authorizationStatus(for: .audio)
        if !isAudioWired { log("microphone authorization: \(microphoneAuthorization.rawValue) (3 = authorized)") }
        guard microphoneAuthorization != .denied, microphoneAuthorization != .restricted else {
            throw RealtimeVoiceError.audio("microphone access is denied for OpenClicky (System Settings → Privacy & Security → Microphone)")
        }
        if !isAudioWired {
            isAudioWired = true
            audio.onMicrophoneFrame = { [weak self] pcm16, level in
                Task { @MainActor in
                    guard let self else { return }
                    self.inputLevel = level
                    guard self.isForwardingMicrophone else { return }
                    self.microphoneAppends += 1
                    try? self.send(["type": "input_audio_buffer.append", "audio": pcm16.base64EncodedString()])
                }
            }
            audio.onPlaybackActiveChanged = { [weak self] isPlaying in
                Task { @MainActor in
                    guard let self else { return }
                    self.isSpeaking = isPlaying
                    if !isPlaying { self.scheduleIdlePauseIfNeeded() }
                }
            }
        }
        let description = try await audio.start()
        if description != "audio already running" { log(description) }
    }

    private func startForwardingMicrophone() {
        isForwardingMicrophone = true
        isCapturing = true
    }

    /// Test hook (`--openclicky-smoke-talk-file`): feed PCM16 mono 24 kHz as if the microphone
    /// produced it, through the same forwarding path.
    func injectMicrophoneAudio(pcm16: Data) {
        guard isForwardingMicrophone else { return }
        microphoneAppends += 1
        try? send(["type": "input_audio_buffer.append", "audio": pcm16.base64EncodedString()])
    }

    private func stopForwardingMicrophone() {
        isForwardingMicrophone = false
        isCapturing = false
        inputLevel = 0
    }

    private func enqueuePlayback(base64 audio: String) {
        guard let data = Data(base64Encoded: audio), !data.isEmpty else { return }
        self.audio.enqueue(pcm16: data)
        if !isSpeaking { isSpeaking = true }
    }

    /// Barge-in: drop everything queued.
    private func flushPlayback() {
        audio.flushPlayback()
        isSpeaking = false
    }

    // MARK: - Socket plumbing

    private func send(_ object: [String: Any]) throws {
        guard let webSocketTask else { throw RealtimeVoiceError.notConnected }
        let data = try JSONSerialization.data(withJSONObject: object)
        webSocketTask.send(.string(String(decoding: data, as: UTF8.self))) { [weak self] error in
            if let error { Task { @MainActor in self?.log("send failed: \(error.localizedDescription)") } }
        }
    }

    private func receiveLoop(_ task: URLSessionWebSocketTask) {
        task.receive { [weak self] result in
            Task { @MainActor in
                guard let self, self.webSocketTask === task else { return }
                switch result {
                case .failure(let error):
                    self.isConnected = false
                    self.webSocketTask = nil
                    self.stopForwardingMicrophone()
                    self.log("realtime disconnected: \(error.localizedDescription)")
                case .success(let message):
                    if case .string(let text) = message { await self.handleServerEvent(text) }
                    self.receiveLoop(task)
                }
            }
        }
    }

    private func handleServerEvent(_ text: String) async {
        guard let data = text.data(using: .utf8),
              let event = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = event["type"] as? String else { return }
        switch type {
        case "input_audio_buffer.speech_started":
            log("listening…")
            flushPlayback()
            if responseInProgress { cancelActiveResponse() }
            if currentMode == .alwaysOn { refreshInstructionsIfNeeded() }
        case "conversation.item.input_audio_transcription.completed":
            if let transcript = event["transcript"] as? String {
                let trimmed = transcript.trimmingCharacters(in: .whitespacesAndNewlines)
                if !trimmed.isEmpty {
                    lastUserTranscript = trimmed
                    onTranscript?(.user, trimmed)
                }
            }
        case "input_audio_buffer.committed":
            log(type)
            if currentMode == .alwaysOn {
                await attachScreenContext()
                requestTurnResponse()
            }
        case "input_audio_buffer.speech_stopped", "session.created", "session.updated":
            log(type)
        case "response.created":
            responseInProgress = true
            activeResponseId = (event["response"] as? [String: Any])?["id"] as? String
            assistantTranscriptBuffer = ""
            onResponseStarted?()
        case "response.output_audio.delta", "response.audio.delta":
            if let delta = event["delta"] as? String { enqueuePlayback(base64: delta) }
        case "response.output_audio_transcript.delta", "response.audio_transcript.delta":
            assistantTranscriptBuffer += event["delta"] as? String ?? ""
        case "response.output_audio_transcript.done", "response.audio_transcript.done":
            let transcript = (event["transcript"] as? String ?? assistantTranscriptBuffer).trimmingCharacters(in: .whitespacesAndNewlines)
            assistantTranscriptBuffer = ""
            if !transcript.isEmpty { onTranscript?(.assistant, transcript) }
        case "response.function_call_arguments.done":
            await handleToolCall(event)
        case "response.done":
            responseInProgress = false
            activeResponseId = nil
            let status = (event["response"] as? [String: Any])?["status"] as? String
            let wantsContinuation = needsContinuationAfterResponse
            needsContinuationAfterResponse = false
            var continued = false
            if wantsContinuation, status != "cancelled" {
                // Tool outputs were added during this response: ask for the follow-up once.
                do {
                    try send(["type": "response.create"])
                    continued = true
                } catch {
                    log("continuation failed: \(error.localizedDescription)")
                }
            }
            if !continued {
                onResponseFinished?()
                scheduleIdlePauseIfNeeded()
            }
        case "error":
            let message = ((event["error"] as? [String: Any])?["message"] as? String) ?? text
            log("realtime error: \(message)")
        default:
            break
        }
    }

    private func handleToolCall(_ event: [String: Any]) async {
        let callId = event["call_id"] as? String ?? ""
        let name = event["name"] as? String ?? ""
        var arguments: [String: Any] = [:]
        if let argumentsText = event["arguments"] as? String,
           let parsed = try? JSONSerialization.jsonObject(with: Data(argumentsText.utf8)) as? [String: Any] {
            arguments = parsed
        }
        switch MacAction.parse(toolName: name, arguments: arguments) {
        case .action(let action):
            let startedAt = Date()
            let outcome = await onMacAction?(action) ?? .failed("OpenClicky is not available")
            log("mac action: \(name) \(outcome.spokenSentence) in \(String(format: "%.2f", Date().timeIntervalSince(startedAt))) s")
            try? send(["type": "conversation.item.create", "item": ["type": "function_call_output", "call_id": callId, "output": outcome.spokenSentence]])
            requestContinuation(after: event)
            return
        case .badArguments(let outcome):
            log("mac action: \(name) rejected — \(outcome.spokenSentence)")
            try? send(["type": "conversation.item.create", "item": ["type": "function_call_output", "call_id": callId, "output": outcome.spokenSentence]])
            requestContinuation(after: event)
            return
        case .notAFastAction:
            break
        }
        var output = "unknown tool \(name)"
        switch name {
        case "send_to_agent":
            let task = arguments["task"] as? String ?? ""
            log("agent task: \(task)")
            output = await onAgentTask?(task) ?? "The agent lane is not available in this session."
        case "point_at":
            // Coordinates come as integers (or occasionally as numeric strings); label is free text.
            let number = { (value: Any?) -> Double? in
                if let n = value as? NSNumber { return n.doubleValue }
                if let s = value as? String { return Double(s.trimmingCharacters(in: .whitespaces)) }
                return nil
            }
            var label = (arguments["label"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            if label.isEmpty { label = "here" }
            // The model controls these numbers: reject non-finite values before any Int conversion.
            if let x = number(arguments["x"]), let y = number(arguments["y"]), x.isFinite, y.isFinite {
                if let capture = lastScreenCapture {
                    // The model's guess is coarse (30–100 px off for captions, hundreds for icons).
                    // Snap it to the element's visible text (OCR of the screenshot it saw) when it named
                    // any; otherwise ask Claude to locate the element on the same screenshot (2–4 s).
                    // Resolution runs off the receive loop, one call after another, so audio keeps
                    // flowing and a walkthrough's steps fly in order.
                    let guess = CGPoint(x: x, y: y)
                    let elementText = (arguments["text"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                    let previousPointing = pointingChain
                    let screenText = lastScreenText
                    let userRequest = lastUserTranscript
                    pointingChain = Task { [weak self] in
                        await previousPointing?.value
                        guard !Task.isCancelled, let self else { return }
                        let formatted = { (value: CGFloat) in String(format: "%.0f", value) }
                        var point = guess
                        var resolution = "using the guess"
                        var resolved = false
                        // A browser tab is a favicon plus a truncated title, which no screenshot model
                        // reads reliably; the browser's own tab list and Accessibility frames do.
                        if let tab = BrowserTabLocator.locate(label: label, text: elementText, userRequest: userRequest, in: capture) {
                            point = tab.point
                            resolved = true
                            resolution = "browser tab \"\(tab.tab.title.prefix(40))\" (\(formatted(point.x)), \(formatted(point.y)))"
                        }
                        let radius = CGFloat(capture.screenshotWidthInPixels) * 0.3
                        // Repeated captions ("Edit" on every row) are the case snapping gets wrong:
                        // two candidates closer together than the model's own 30–100 px error cannot
                        // be told apart by distance, so a locator that only knows pixels reports
                        // nothing rather than guessing between them.
                        let ambiguityMargin = CGFloat(capture.screenshotWidthInPixels) * 0.1
                        if !resolved, !elementText.isEmpty, let lines = await screenText?.value, !lines.isEmpty {
                            if let match = ScreenTextLocator.locate(elementText, near: guess, in: lines, maxDistance: radius, ambiguityMargin: ambiguityMargin) {
                                point = match.center
                                resolved = true
                                resolution = "snapped to \"\(match.text)\" (\(formatted(point.x)), \(formatted(point.y)), \(formatted(match.distance)) px away)"
                            }
                        }
                        // Accessibility knows what the pixels cannot: the control's role, and the
                        // title of the row it sits in. That is what separates repeated captions, and
                        // it costs nothing, so it runs before paying for a Claude round trip.
                        if !resolved,
                           let element = AccessibleElementLocator.locate(
                               label: label, text: elementText, userRequest: userRequest, near: guess,
                               in: capture, maxDistance: radius, ambiguityMargin: ambiguityMargin
                           ) {
                            point = element.center
                            resolved = true
                            let container = element.containerTitles.first.map { " in \"\($0.prefix(30))\"" } ?? ""
                            resolution = "accessibility \"\(element.title.prefix(40))\"\(container) (\(formatted(point.x)), \(formatted(point.y)))"
                        }
                        if !resolved {
                            let started = Date()
                            if let located = await ScreenElementGrounder.locate(label: label, text: elementText, userRequest: userRequest, in: capture) {
                                point = located
                                resolution = "located by Claude (\(formatted(point.x)), \(formatted(point.y))) in \(Int(Date().timeIntervalSince(started) * 1000)) ms"
                            } else {
                                resolution = "Claude could not locate it; using the guess"
                            }
                        }
                        guard !Task.isCancelled else { return }
                        let location = CompanionManager.screenLocation(forScreenshotPoint: point, in: capture)
                        self.log("point_at (\(formatted(guess.x)), \(formatted(guess.y))) \"\(label)\" \(resolution) → screen (\(formatted(location.x)), \(formatted(location.y)))\(self.onPointAt == nil ? " (no handler)" : "")")
                        self.onPointAt?(point, label, capture)
                    }
                    output = "pointing at \(label)"
                } else {
                    log("point_at ignored: no screenshot attached to this turn")
                    output = "no screenshot attached; describe the location in words"
                }
            } else {
                output = "invalid coordinates: point_at needs finite integer x and y"
            }
        default:
            break
        }
        // The output goes out immediately; the follow-up response is requested once, on response.done.
        try? send(["type": "conversation.item.create", "item": ["type": "function_call_output", "call_id": callId, "output": String(output.prefix(4000))]])
        requestContinuation(after: event)
    }

    /// Ask the model to continue after a tool output. Extracted so the fast-action path and the
    /// existing tools share exactly one implementation of the barge-in and ordering rules.
    private func requestContinuation(after event: [String: Any]) {
        let responseId = event["response_id"] as? String
        if let responseId, cancelledResponseIds.contains(responseId) {
            // A late call from a response we cancelled (barge-in): the output is in the conversation
            // for the next turn, but nothing may follow the cancel.
            log("tool output for cancelled response \(responseId): no continuation")
        } else if responseInProgress {
            if responseId == nil || responseId == activeResponseId {
                needsContinuationAfterResponse = true
            } else {
                // Belongs to an earlier, already finished response while a newer one is active:
                // the active response will pick the output up; requesting now would collide.
                log("tool output for inactive response \(responseId ?? "?"): no continuation")
            }
        } else {
            // No response is active (event ordering can put arguments.done after done): continue now.
            try? send(["type": "response.create"])
        }
    }

    /// Barge-in: cancel the active response and drop any continuation queued for it, so no
    /// stray `response.create` follows the cancel. The response is remembered as cancelled so its
    /// tool calls that are still in flight cannot re-arm the continuation.
    private func cancelActiveResponse() {
        needsContinuationAfterResponse = false
        if let activeResponseId { cancelledResponseIds.insert(activeResponseId) }
        activeResponseId = nil
        responseInProgress = false
        try? send(["type": "response.cancel"])
    }

    /// Request the reply for a committed user turn. Any continuation queued for tool outputs of
    /// the previous response is dropped: this create supersedes it (one create per turn).
    private func requestTurnResponse() {
        needsContinuationAfterResponse = false
        try? send(["type": "response.create"])
    }

    private func log(_ line: String) {
        onEvent?(line)
        print("🎙️ Realtime: \(line)")
        AppLog.append("realtime: \(line)")
    }
}

enum RealtimeVoiceError: LocalizedError {
    case notConfigured
    case notConnected
    case backend(String)
    case audio(String)

    var errorDescription: String? {
        switch self {
        case .notConfigured: return "OpenClicky backend token is not configured."
        case .notConnected: return "Realtime session is not connected."
        case .backend(let message): return "Realtime session could not be created: \(message)"
        case .audio(let message): return "Audio setup failed: \(message)"
        }
    }
}


// MARK: - Audio engine (off the main actor)

/// Owns the AVAudioEngine graph: microphone → PCM16 mono 24 kHz frames out, PCM16 playback in.
/// Tries Apple's voice-processing unit (echo cancellation) first and falls back to plain capture.
/// Every engine call happens on `queue`, never on the main thread (see RealtimeVoiceClient).
final class RealtimeAudioEngine: @unchecked Sendable {
    static let sampleRate: Double = 24_000
    private static let pcm16Format = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: sampleRate, channels: 1, interleaved: true)!
    private static let playbackFormat = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: 1)!

    /// PCM16 mono 24 kHz bytes plus a 0…1 level, delivered from the audio thread.
    var onMicrophoneFrame: ((Data, CGFloat) -> Void)?
    var onPlaybackActiveChanged: ((Bool) -> Void)?

    private let queue = DispatchQueue(label: "org.openclicky.realtime.audio")
    private var engine = AVAudioEngine()
    private var playerNode = AVAudioPlayerNode()
    private var converter: AVAudioConverter?
    private var monoFormat: AVAudioFormat?
    private var isRunning = false
    private var queuedBuffers = 0
    private var tapCount = 0
    private var framesOut = 0
    private var peakLevel: CGFloat = 0
    private var peakOut: CGFloat = 0

    /// One-line capture statistics (for --openclicky-smoke-talk and bug reports).
    func debugSummary() async -> String {
        await withCheckedContinuation { continuation in
            queue.async {
                continuation.resume(returning: "taps \(self.tapCount), frames out \(self.framesOut), peak in \(String(format: "%.3f", self.peakLevel)) out \(String(format: "%.3f", self.peakOut)), engine running \(self.engine.isRunning)")
            }
        }
    }

    /// Starts capture and playback; returns a one-line description of the configuration.
    func start() async throws -> String {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<String, Error>) in
            queue.async {
                if self.isRunning {
                    if !self.engine.isRunning {
                        do { try self.engine.start() } catch { continuation.resume(throwing: error); return }
                    }
                    continuation.resume(returning: "audio already running")
                    return
                }
                do {
                    var description: String
                    do {
                        description = try self.configure(voiceProcessing: true)
                    } catch {
                        self.tearDown()
                        description = try self.configure(voiceProcessing: false)
                        description += " (voice processing unavailable: \(error.localizedDescription))"
                    }
                    self.isRunning = true
                    continuation.resume(returning: description)
                } catch {
                    self.tearDown()
                    continuation.resume(throwing: error)
                }
            }
        }
    }

    func stop() {
        queue.async { self.tearDown() }
    }

    /// Releases the microphone (the menu bar indicator goes off) but keeps the graph configured
    /// so the next `start()` is a fast engine restart instead of a full setup.
    func pause() {
        queue.async {
            guard self.isRunning, self.engine.isRunning else { return }
            self.playerNode.stop()
            self.queuedBuffers = 0
            self.engine.stop()
        }
    }

    /// Tears the whole graph down (voice-processing unit included) so nothing of ours touches the
    /// audio system between turns; the next `start()` is a full setup.
    func release() {
        queue.async {
            guard self.isRunning else { return }
            self.tearDown()
        }
    }

    /// `release()`, but returns once the graph is gone (another engine is about to open the mic).
    func releaseNow() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            queue.async {
                if self.isRunning { self.tearDown() }
                continuation.resume()
            }
        }
    }

    func enqueue(pcm16 data: Data) {
        queue.async {
            guard self.isRunning else { return }
            if !self.engine.isRunning {
                do { try self.engine.start() } catch { return }
            }
            let sampleCount = data.count / MemoryLayout<Int16>.size
            guard sampleCount > 0,
                  let buffer = AVAudioPCMBuffer(pcmFormat: Self.playbackFormat, frameCapacity: AVAudioFrameCount(sampleCount)),
                  let floatChannel = buffer.floatChannelData?[0] else { return }
            data.withUnsafeBytes { raw in
                let samples = raw.bindMemory(to: Int16.self)
                for index in 0..<sampleCount { floatChannel[index] = Float(samples[index]) / 32768.0 }
            }
            buffer.frameLength = AVAudioFrameCount(sampleCount)
            self.queuedBuffers += 1
            if self.queuedBuffers == 1 { self.onPlaybackActiveChanged?(true) }
            self.playerNode.scheduleBuffer(buffer) { [weak self] in
                guard let self else { return }
                self.queue.async {
                    self.queuedBuffers = max(0, self.queuedBuffers - 1)
                    if self.queuedBuffers == 0 { self.onPlaybackActiveChanged?(false) }
                }
            }
            if !self.playerNode.isPlaying { self.playerNode.play() }
        }
    }

    func flushPlayback() {
        queue.async {
            guard self.isRunning else { return }
            self.playerNode.stop()
            self.queuedBuffers = 0
            self.onPlaybackActiveChanged?(false)
            if self.engine.isRunning { self.playerNode.play() }
        }
    }

    // MARK: Internals (audio queue only)

    private func configure(voiceProcessing: Bool) throws -> String {
        let inputNode = engine.inputNode
        let outputNode = engine.outputNode
        var notes: [String] = []
        if voiceProcessing {
            try inputNode.setVoiceProcessingEnabled(true)
            // Apple's voice-processing unit ducks every other app's audio while it runs (the user's
            // music went quiet or silent whenever OpenClicky listened or spoke). Keep echo
            // cancellation, drop the ducking to its minimum and skip the "advanced" (harder) ducking.
            inputNode.voiceProcessingOtherAudioDuckingConfiguration = AVAudioVoiceProcessingOtherAudioDuckingConfiguration(
                enableAdvancedDucking: false,
                duckingLevel: .min
            )
            notes.append("other-audio ducking min")
        }
        engine.attach(playerNode)
        // With voice processing on, the output unit only initializes when the mixer → output link
        // is made explicitly at the hardware format before the player is connected. `connect`
        // raises an uncatchable ObjC exception on an invalid format, so validate first.
        let outputFormat = [outputNode.outputFormat(forBus: 0), outputNode.inputFormat(forBus: 0)]
            .first { $0.sampleRate > 0 && $0.channelCount > 0 }
        if let outputFormat {
            engine.connect(engine.mainMixerNode, to: outputNode, format: outputFormat)
            notes.append("output \(Int(outputFormat.sampleRate)) Hz × \(outputFormat.channelCount) ch")
        } else {
            engine.connect(engine.mainMixerNode, to: outputNode, format: nil)
            notes.append("output format unknown, engine default")
        }
        engine.connect(playerNode, to: engine.mainMixerNode, format: Self.playbackFormat)

        let hardwareFormat = inputNode.outputFormat(forBus: 0)
        guard hardwareFormat.sampleRate > 0, hardwareFormat.channelCount > 0 else {
            throw RealtimeVoiceError.audio("no microphone input available")
        }
        // The voice-processing input reports 5 identical channels; AVAudioConverter turns a
        // multi-channel → mono conversion into silence, so channel 0 is copied into a mono buffer
        // first and only the sample rate / sample format are converted.
        guard let monoFormat = AVAudioFormat(standardFormatWithSampleRate: hardwareFormat.sampleRate, channels: 1),
              let converter = AVAudioConverter(from: monoFormat, to: Self.pcm16Format) else {
            throw RealtimeVoiceError.audio("cannot convert \(hardwareFormat) to PCM16 24 kHz")
        }
        self.monoFormat = monoFormat
        self.converter = converter
        inputNode.installTap(onBus: 0, bufferSize: 2400, format: hardwareFormat) { [weak self] buffer, _ in
            self?.handleMicrophoneBuffer(buffer)
        }
        engine.prepare()
        try engine.start()
        return "audio running: input \(Int(hardwareFormat.sampleRate)) Hz × \(hardwareFormat.channelCount) ch, \(notes.joined(separator: ", ")), voice processing \(voiceProcessing ? "on" : "off")"
    }

    private func tearDown() {
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        engine = AVAudioEngine()
        playerNode = AVAudioPlayerNode()
        converter = nil
        monoFormat = nil
        queuedBuffers = 0
        isRunning = false
    }

    /// Audio thread: convert to PCM16 mono 24 kHz and hand the bytes to the client.
    private func handleMicrophoneBuffer(_ buffer: AVAudioPCMBuffer) {
        queue.async { self.tapCount += 1 }
        guard let converter, let monoFormat, let onMicrophoneFrame else { return }
        let mono: AVAudioPCMBuffer
        if buffer.format.channelCount == 1 && buffer.format.commonFormat == .pcmFormatFloat32 {
            mono = buffer
        } else {
            guard let copy = AVAudioPCMBuffer(pcmFormat: monoFormat, frameCapacity: buffer.frameLength),
                  let source = buffer.floatChannelData?[0], let target = copy.floatChannelData?[0] else { return }
            target.update(from: source, count: Int(buffer.frameLength))
            copy.frameLength = buffer.frameLength
            mono = copy
        }
        let ratio = Self.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 64
        guard let converted = AVAudioPCMBuffer(pcmFormat: Self.pcm16Format, frameCapacity: capacity) else { return }
        var consumedInput = false
        var conversionError: NSError?
        converter.convert(to: converted, error: &conversionError) { _, outStatus in
            if consumedInput {
                outStatus.pointee = .noDataNow
                return nil
            }
            consumedInput = true
            outStatus.pointee = .haveData
            return mono
        }
        guard conversionError == nil, converted.frameLength > 0, let channel = converted.int16ChannelData else { return }
        let data = Data(bytes: channel[0], count: Int(converted.frameLength) * MemoryLayout<Int16>.size)
        let level = Self.rmsLevel(of: buffer)
        let frameCount = Int(converted.frameLength)
        var outPeak: Int16 = 0
        for index in 0..<frameCount { outPeak = max(outPeak, abs(channel[0][index])) }
        let outLevel = CGFloat(outPeak) / 32768
        queue.async { self.framesOut += frameCount; self.peakLevel = max(self.peakLevel, level); self.peakOut = max(self.peakOut, outLevel) }
        onMicrophoneFrame(data, level)
    }

    private static func rmsLevel(of buffer: AVAudioPCMBuffer) -> CGFloat {
        guard let channel = buffer.floatChannelData?[0], buffer.frameLength > 0 else { return 0 }
        var sum: Float = 0
        for index in 0..<Int(buffer.frameLength) { sum += channel[index] * channel[index] }
        return CGFloat(min(1, sqrt(sum / Float(buffer.frameLength)) * 6))
    }
}
