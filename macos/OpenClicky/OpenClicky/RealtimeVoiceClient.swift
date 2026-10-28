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

import AppKit
import AVFoundation
import Combine
import Foundation
import os

/// The screen context attached to a Realtime turn: the JPEG the model sees, a caption with the
/// pointer position, and the capture it came from (display frame + pixel size) so `point_at`
/// coordinates can be mapped back onto the display.
struct RealtimeScreenContext {
    let jpeg: Data
    let caption: String
    let capture: CompanionScreenCapture
}

/// The part of `URLSessionWebSocketTask` the client uses, so a test can stand in a socket that
/// behaves like one whose handshake has not finished yet (it refuses every send).
nonisolated protocol RealtimeSocket: AnyObject, Sendable {
    var response: URLResponse? { get }
    func resume()
    func send(_ message: URLSessionWebSocketTask.Message, completionHandler: @escaping @Sendable (Error?) -> Void)
    func receive(completionHandler: @escaping @Sendable (Result<URLSessionWebSocketTask.Message, Error>) -> Void)
    func sendPing(pongReceiveHandler: @escaping @Sendable (Error?) -> Void)
    func cancel(with closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?)
}

extension URLSessionWebSocketTask: RealtimeSocket {}

@MainActor
final class RealtimeVoiceClient: NSObject, ObservableObject {
    enum TurnMode {
        case pushToTalk
        case alwaysOn
    }

    /// Builds the OpenAI realtime websocket URL for a given model name. `model` arrives verbatim in
    /// the backend's JSON session response, so a malformed or hostile value (a stray space, an
    /// unescaped character, …) must not force-unwrap into a crash: this falls back to the same
    /// "gpt-realtime" default used elsewhere when the backend omits the field, and only returns nil
    /// in the (practically unreachable) case where even that literal fails to parse. Pulled out as a
    /// pure static function so it can be unit tested without opening a real connection.
    nonisolated static func realtimeSocketURL(forModel model: String) -> URL? {
        URL(string: "wss://api.openai.com/v1/realtime?model=\(model)")
            ?? URL(string: "wss://api.openai.com/v1/realtime?model=gpt-realtime")
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
    /// The socket went away (or a send failed) while it was thought to be connected. Without this
    /// a turn in flight never got its `response.done`, `voiceState` stayed on processing, and that
    /// in turn blocked dictation, until the app was restarted.
    var onConnectionLost: ((String) -> Void)?
    /// A push-to-talk turn ended with (almost) no audio, and was not sent. See `endPushToTalk`.
    var onTurnHeardNothing: (() -> Void)?
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
    /// Tools a plugin adds to the session while it is on (the Teacher's drawing tools). Read with
    /// every session update; a plugin switched on or off also changes the instructions, which is
    /// what sends the update.
    var pluginToolsProvider: (() -> [[String: Any]])?
    /// Runs a plugin's tool call with the screenshot the turn carried; nil when no plugin owns the
    /// tool. Must return quickly: tool calls are handled on the receive loop.
    var onPluginToolCall: (@MainActor (String, [String: Any], CompanionScreenCapture?) async -> String?)?
    /// Hands-free only: the person signed off by voice ("that's all, thanks"). Called once the
    /// goodbye that answers the sign-off has finished playing; the companion turns hands-free off.
    var onStopListeningRequested: (@MainActor () -> Void)?
    /// A sign-off was heard: hands-free ends after the goodbye response is done and has played.
    private var stopListeningAfterGoodbye = false
    /// Reads the text a "rephrase this" would replace (the highlight, or the focused text box) when
    /// a turn's screen is attached; the turn quotes it and `write_in_place` writes over it.
    var writingTargetProvider: (@MainActor () async -> WritingTarget?)?
    /// The text to write over for the turn in flight, captured with its screen.
    private(set) var lastWritingTarget: WritingTarget?
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

    private var webSocketTask: RealtimeSocket?
    /// True once the server's first event arrived on `webSocketTask`. Until then `send` refuses:
    /// a message written while the WebSocket handshake is still in flight can fail with "Socket is
    /// not connected", and a failed send tears the socket down (issue #1).
    private var isSocketOpen = false
    private lazy var urlSession = URLSession(configuration: .default)
    private var connectTask: Task<Void, Error>?
    private var keepWarmTask: Task<Void, Never>?
    /// When the current socket was accepted, and when the server last sent anything on it.
    private var connectedAt: Date?
    private var lastServerEventAt: Date?
    /// When the user last started a turn (voice or typed). The keep-warm loop reconnects in the
    /// background only while this is recent: every session is minted by the backend and billed,
    /// and a Mac left running overnight must not buy one an hour for nobody.
    private var lastTurnStartedAt: Date?
    /// Resolved by the first server event on a new socket, or failed by the socket's first error.
    private var handshakeContinuation: CheckedContinuation<Void, Error>?
    /// A refusal that arrived before anything was waiting for the handshake — kept so the wait
    /// reports it instead of timing out without a reason.
    private var handshakeFailureBeforeWaiting: String?
    private var wakeObserver: NSObjectProtocol?

    /// Audio runs off the main actor: CoreAudio's first-time setup hops synchronously to the main
    /// queue, so configuring the engine from a main-actor task deadlocks the app.
    /// Not `private` so a test can read the microphone it was handed.
    let audio = RealtimeAudioEngine()
    /// Settings → microphone (a CoreAudio UID; nil = the Mac's default), the same one fn dictation
    /// and the classic ⌃⌥ lane hear with. Applied when the audio graph is next set up.
    var preferredMicrophoneUID: String? {
        didSet { audio.setPreferredMicrophoneUID(preferredMicrophoneUID) }
    }
    private var isAudioWired = false
    private var isForwardingMicrophone = false
    private var currentMode: TurnMode = .pushToTalk
    private var pushToTalkArmed = false
    private var idlePauseTask: Task<Void, Never>?
    private var microphoneAppends = 0
    /// PCM16 bytes forwarded since the current push-to-talk press. See `hasEnoughAudioForATurn`.
    private var pushToTalkTurnAudioBytes = 0

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
    You are OpenClicky, a friendly, fast macOS voice assistant. \
    Keep spoken replies short (one or two sentences). Every request comes with a screenshot of the user's current screen \
    (with the pointer position noted): "this", "here", "that" refer to what is on screen, usually near the pointer. Look \
    at the screenshot and answer about it directly; never say you cannot see the screen and never mention a camera. \
    Answer quick questions yourself. When the user asks how to do something, where something is, or what to \
    click, point at it with the point_at tool while you explain, one call per step, in order, with the element's \
    exact on-screen text when it has any, and keep speaking between calls. Do not point for general questions or things they are obviously already looking \
    at. \
    Do simple local things yourself with the fast tools (open_app, open_url, create_folder, \
    reveal_in_finder, set_volume, media_control) and then say the one sentence they give back. \
    For anything bigger (creating or editing files or code, running commands, using integrations, research, \
    multi-step tasks), first say one short sentence acknowledging it, then call the send_to_agent \
    tool with a clear, self-contained task, and afterwards tell the user in one sentence what happened. \
    Never pretend work was done without the tool. Do not read file paths aloud character by character. \
    When the screen context quotes text they highlighted or the text box they are in, and they ask you to rephrase, \
    rewrite, shorten, fix, polish or translate it ("rephrase this", "make this sound professional", "do that for me"), \
    call write_in_place with the complete new text, then say only a few words about what you changed; never read the \
    new text out. If they ask how to rephrase it or for suggestions, answer in words and change nothing. \
    If no text was quoted, ask them to highlight it and ask again.
    """

    init(voice: String? = nil, instructions: String = RealtimeVoiceClient.defaultInstructions) {
        self.voice = voice
        self.instructions = instructions
        super.init()
        // After sleep a socket can look open and be dead. Forget when the server was last heard
        // from, so the next press checks it (a ping) before speaking into it.
        wakeObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didWakeNotification, object: nil, queue: .main
        ) { [weak self] _ in
            // Delivered on the main queue (`queue: .main`), so this is already the main actor.
            MainActor.assumeIsolated { self?.lastServerEventAt = .distantPast }
        }
    }

    // MARK: - Connection

    /// Connect (or reuse the live connection). Safe to call before every push-to-talk press.
    ///
    /// A live connection is reused only if it is still worth trusting: one close to OpenAI's
    /// 60-minute session cap is replaced before it is cut off mid-turn, and one the server has been
    /// quiet on for a while (sleep, a network change) is pinged first.
    func connectIfNeeded(mode: TurnMode) async throws {
        if isConnected, turnMode == mode {
            let now = Date()
            if !Self.isTooOld(connectedAt: connectedAt, now: now) {
                if !Self.needsPing(lastServerEventAt: lastServerEventAt, now: now) { return }
                if let webSocketTask, await Self.ping(webSocketTask) {
                    lastServerEventAt = Date()
                    return
                }
            }
            log("realtime connection stale, reconnecting")
        }
        if let connectTask, turnMode == mode {
            try await connectTask.value
            return
        }
        closeConnection()
        turnMode = mode
        let task = Task { try await self.openConnection(mode: mode) }
        connectTask = task
        defer { connectTask = nil }
        try await task.value
    }

    /// Keep a session open from launch so the first press only starts audio. Reconnects on drop,
    /// while it is being used (see `lastTurnStartedAt`) or when always-on needs it.
    ///
    /// It used to stop after its first connect: `connectIfNeeded` called `disconnect`, which
    /// cancelled this very task, so the "reconnects on drop" loop exited on its next sleep and a
    /// dropped socket was only noticed on the next press.
    func keepWarm(mode: TurnMode) {
        keepWarmTask?.cancel()
        let warmStartedAt = Date()
        keepWarmTask = Task { [weak self] in
            var hasConnectedOnce = false
            while !Task.isCancelled {
                guard let self else { return }
                let recentlyUsed = Self.isRecentlyUsed(lastTurnStartedAt: self.lastTurnStartedAt ?? warmStartedAt, now: Date())
                if !self.isConnected, !hasConnectedOnce || mode == .alwaysOn || recentlyUsed {
                    do {
                        try await self.connectIfNeeded(mode: mode)
                        hasConnectedOnce = true
                    } catch {
                        self.log("warm-up failed: \(error.localizedDescription)")
                    }
                }
                try? await Task.sleep(nanoseconds: 20_000_000_000)
            }
        }
    }

    /// OpenAI refuses to commit less than 100 ms of audio; a turn shorter than that heard nothing.
    nonisolated static func hasEnoughAudioForATurn(pcm16Bytes: Int) -> Bool {
        let minimumBytes = Int(RealtimeAudioEngine.sampleRate * 0.1) * MemoryLayout<Int16>.size
        return pcm16Bytes >= minimumBytes
    }

    /// Replaced before OpenAI's 60-minute cap, with room to finish a conversation.
    nonisolated static let maximumConnectionAge: TimeInterval = 50 * 60
    /// Quiet this long, a socket is pinged before a turn is spoken into it.
    nonisolated static let pingAfterSilence: TimeInterval = 60
    /// How long after the last turn the keep-warm loop still reconnects in the background.
    nonisolated static let keepWarmWhileUsedWithin: TimeInterval = 15 * 60

    nonisolated static func isTooOld(connectedAt: Date?, now: Date) -> Bool {
        guard let connectedAt else { return true }
        return now.timeIntervalSince(connectedAt) >= maximumConnectionAge
    }

    nonisolated static func needsPing(lastServerEventAt: Date?, now: Date) -> Bool {
        guard let lastServerEventAt else { return true }
        return now.timeIntervalSince(lastServerEventAt) >= pingAfterSilence
    }

    nonisolated static func isRecentlyUsed(lastTurnStartedAt: Date, now: Date) -> Bool {
        now.timeIntervalSince(lastTurnStartedAt) < keepWarmWhileUsedWithin
    }

    /// A pong within two seconds, or the socket is treated as gone.
    private static func ping(_ socketTask: RealtimeSocket) async -> Bool {
        final class ResumeOnce: @unchecked Sendable {
            private let lock = NSLock()
            private var continuation: CheckedContinuation<Bool, Never>?
            init(_ continuation: CheckedContinuation<Bool, Never>) { self.continuation = continuation }
            func resume(_ pongArrived: Bool) {
                lock.lock()
                let pendingContinuation = continuation
                continuation = nil
                lock.unlock()
                pendingContinuation?.resume(returning: pongArrived)
            }
        }
        return await withCheckedContinuation { continuation in
            let resumeOnce = ResumeOnce(continuation)
            socketTask.sendPing { error in resumeOnce.resume(error == nil) }
            DispatchQueue.global().asyncAfter(deadline: .now() + 2) { resumeOnce.resume(false) }
        }
    }

    /// What a refused socket handshake means, in words a person can act on. URLSession reports it
    /// as "bad server response", which reads as a network fault.
    nonisolated static func handshakeFailureDescription(statusCode: Int?, underlyingError: String) -> String {
        switch statusCode {
        case 401: return "OpenAI refused the realtime session (401): the session key was rejected. Check your sign-in, or your own OpenAI key in ~/.openclicky/shell.json."
        case 403: return "OpenAI refused the realtime session (403): this key has no access to the realtime model."
        case 429: return "OpenAI refused the realtime session (429): this key is over its rate limit or out of credit."
        case let status? where status >= 400: return "OpenAI refused the realtime session (\(status))."
        default: return underlyingError
        }
    }

    func disconnect(reason: String?) {
        keepWarmTask?.cancel()
        keepWarmTask = nil
        closeConnection()
        if let reason { log(reason) }
    }

    /// Everything `disconnect` does except stopping the keep-warm loop, which is what calls this
    /// when it reconnects.
    private func closeConnection() {
        failPendingHandshake(RealtimeVoiceError.notConnected)
        handshakeFailureBeforeWaiting = nil
        connectedAt = nil
        lastServerEventAt = nil
        stopForwardingMicrophone()
        pushToTalkArmed = false
        // A sign-off heard on a session that is going away belongs to that session only.
        stopListeningAfterGoodbye = false
        idlePauseTask?.cancel()
        flushPlayback()
        audio.release()
        webSocketTask?.cancel(with: .normalClosure, reason: nil)
        webSocketTask = nil
        isSocketOpen = false
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
    }

    private func failPendingHandshake(_ error: Error) {
        let pendingHandshake = handshakeContinuation
        handshakeContinuation = nil
        pendingHandshake?.resume(throwing: error)
    }

    private func openConnection(mode: TurnMode) async throws {
        guard OpenClickyConfiguration.isConfigured else { throw RealtimeVoiceError.notConfigured }
        let connectInstructions = currentInstructions()
        let secretData: Data
        let secretResponse: URLResponse
        switch ProviderRouter.current(for: .realtime) {
        case .openAIDirect:
            // The person's own OpenAI key mints the client secret at OpenAI itself; the answer
            // has the same shape as the backend's.
            guard let openAIKey = ProviderRouter.currentCredentials().openAIKey else { throw RealtimeVoiceError.notConfigured }
            let secretRequest = try DirectProviderRequests.openAIRealtimeSecret(key: openAIKey, instructions: connectInstructions, voice: voice)
            (secretData, secretResponse) = try await urlSession.data(for: secretRequest)
        case .unavailable(let sentence):
            throw ProviderRouteUnavailable(capability: .realtime, sentence: sentence)
        case .backend, .anthropicDirect, .answerProvider:
            // backendBaseURL is user-configurable (shell.json or an environment override), not a
            // compile-time literal, so a malformed value must throw instead of crashing the app.
            guard let secretRequestURL = URL(string: "\(OpenClickyConfiguration.backendBaseURL)/agent/realtime/session") else {
                throw RealtimeVoiceError.invalidURL("OpenClicky backend URL is invalid: \(OpenClickyConfiguration.backendBaseURL)")
            }
            var secretRequest = URLRequest(url: secretRequestURL)
            secretRequest.httpMethod = "POST"
            secretRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
            OpenClickyConfiguration.authorize(&secretRequest)
            secretRequest.httpBody = try JSONSerialization.data(withJSONObject: ["voice": voice as Any, "instructions": connectInstructions].compactMapValues { $0 })
            (secretData, secretResponse) = try await BackendSignIn.send(secretRequest) { [urlSession] in try await urlSession.data(for: $0) }
        }
        let secretStatusCode = (secretResponse as? HTTPURLResponse)?.statusCode ?? 0
        guard (200...299).contains(secretStatusCode),
              let secretJSON = try JSONSerialization.jsonObject(with: secretData) as? [String: Any],
              let clientSecret = secretJSON["value"] as? String else {
            throw RealtimeVoiceError.backend(Self.backendRefusalDescription(statusCode: secretStatusCode, body: secretData))
        }
        let model = (secretJSON["session"] as? [String: Any])?["model"] as? String ?? "gpt-realtime"

        // `model` arrives verbatim in the backend's JSON response, so a malformed or hostile value
        // must not be allowed to force-unwrap into a crash.
        guard let socketRequestURL = Self.realtimeSocketURL(forModel: model) else {
            throw RealtimeVoiceError.invalidURL("OpenAI realtime URL is invalid for model \(model)")
        }
        var socketRequest = URLRequest(url: socketRequestURL)
        socketRequest.setValue("Bearer \(clientSecret)", forHTTPHeaderField: "Authorization")
        try await startSession(
            on: urlSession.webSocketTask(with: socketRequest),
            model: model,
            mode: mode,
            instructions: connectInstructions,
            whileWaitingForHandshake: {
                // Always-on keeps the microphone open, so its graph is built while the socket comes
                // up. Push-to-talk builds it at the press instead (`beginPushToTalk`). Nothing is
                // forwarded yet: that starts with a press or with `startListeningContinuously`, both
                // after the connect returns.
                guard Self.startsAudioWhileConnecting(mode: mode) else { return }
                try await self.startAudioIfNeeded()
            })
    }

    /// Whether connecting also sets up the audio graph. Not for push-to-talk: the graph carries
    /// Apple's voice-processing unit, which turns every other app's sound down for as long as it
    /// exists, paused or not. Built at launch and only paused, it kept headphones quiet until
    /// OpenClicky quit (2026-10-24); the idle release in `scheduleIdlePauseIfNeeded` only follows a
    /// turn. The first press after launch now opens the microphone cold (~0.7 s instead of ~0.1 s),
    /// as every press after half a minute idle already did.
    nonisolated static func startsAudioWhileConnecting(mode: TurnMode) -> Bool {
        mode == .alwaysOn
    }

    /// Opens `socket` and brings the session up, in this order: resume, wait for the server's first
    /// event (normally `session.created`), then `session.update`, then connected. `session.update`
    /// used to go out right after `resume()`, while the WebSocket handshake could still be in
    /// flight; on the reporter's Mac that first send failed with "Socket is not connected" and the
    /// fresh socket was torn down (issue #1). `whileWaitingForHandshake` overlaps the audio warm-up
    /// with the handshake and must not send — `send` refuses until the first event anyway.
    /// Not `private` so a test can drive it with a fake socket.
    func startSession(
        on socket: RealtimeSocket,
        model: String,
        mode: TurnMode,
        instructions connectInstructions: String,
        whileWaitingForHandshake: () async throws -> Void
    ) async throws {
        webSocketTask = socket
        isSocketOpen = false
        socket.resume()
        receiveLoop(socket)
        currentMode = mode
        // Connected means OpenAI accepted the socket, not that `resume()` returned. A refused
        // handshake (a 401) fails the wait with its status, so the press says what was refused.
        do {
            try await whileWaitingForHandshake()
            try await awaitHandshake(on: socket)
        } catch {
            closeConnection()
            throw error
        }
        try send(sessionUpdate(mode: mode, instructions: connectInstructions))
        sentInstructions = connectInstructions
        connectedAt = Date()
        lastServerEventAt = Date()
        isConnected = true
        log("realtime connected (\(model), \(mode == .pushToTalk ? "push-to-talk" : "always on"))")
    }

    /// Waits for the server's first event on `task` (normally `session.created`), for up to ten
    /// seconds. The receive loop resolves it; the socket's first error fails it.
    private func awaitHandshake(on task: RealtimeSocket) async throws {
        if let refusal = handshakeFailureBeforeWaiting {
            handshakeFailureBeforeWaiting = nil
            throw RealtimeVoiceError.backend(refusal)
        }
        // The first event may already be in (it arrived during the audio warm-up). Not judged by
        // `lastServerEventAt`: waking from sleep sets that to `.distantPast`, mid-connect too.
        if isSocketOpen, webSocketTask === task { return }
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            handshakeContinuation = continuation
            Task { @MainActor [weak self] in
                try? await Task.sleep(nanoseconds: 10_000_000_000)
                self?.failPendingHandshake(RealtimeVoiceError.backend("OpenAI did not answer the realtime connection within 10 seconds."))
            }
        }
    }

    /// The backend's own error text where it gave one, with the status, so a rejected key reads
    /// as a rejected key rather than as JSON.
    nonisolated static func backendRefusalDescription(statusCode: Int, body: Data) -> String {
        let bodyJSON = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any]
        let stated = (bodyJSON?["error"] as? String)
            ?? ((bodyJSON?["error"] as? [String: Any])?["message"] as? String)
            ?? String(data: body, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines)
        let detail = stated.flatMap { $0.isEmpty ? nil : $0 } ?? "no client secret"
        switch statusCode {
        case 401, 403:
            return "the backend refused (\(statusCode)): \(detail). Check your sign-in, or your own OpenAI key in ~/.openclicky/shell.json."
        case 0:
            return detail
        default:
            return "the backend answered \(statusCode): \(detail)"
        }
    }

    private func sessionUpdate(mode: TurnMode, instructions text: String) -> [String: Any] {
        var input: [String: Any] = [
            "format": ["type": "audio/pcm", "rate": Int(Self.sampleRate)],
            // Pinned, like the reply: unpinned, English audio was transcribed as Chinese and the
            // model took that as the user switching language. See ReplyLanguage.swift.
            "transcription": ["model": "gpt-4o-mini-transcribe", "language": ReplyLanguage.currentCode],
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
        // Ending hands-free by voice only makes sense while hands-free is on.
        let handsFreeTools = mode == .alwaysOn ? [Self.stopListeningToolDefinition()] : []
        return [
            "type": "session.update",
            "session": [
                "type": "realtime",
                "instructions": text,
                "tools": [tool, pointTool, WriteInPlace.realtimeToolDefinition()] + MacAction.realtimeToolDefinitions() + handsFreeTools + (pluginToolsProvider?() ?? []),
                "tool_choice": "auto",
                "audio": ["input": input, "output": output],
            ],
        ]
    }

    static let stopListeningToolName = "stop_listening"

    /// The model decides what counts as a sign-off, since people end in many ways; fixed words
    /// would also fire on "how do I quit Photoshop". A missed sign-off costs one key press, a
    /// wrong one cuts the person off, so the description leans towards keeping on listening.
    nonisolated static func stopListeningToolDefinition() -> [String: Any] {
        [
            "type": "function",
            "name": stopListeningToolName,
            "description": "Turn hands-free listening off and go back to push-to-talk. Call it only when the user clearly signs off from talking to you: \"that's all I need\", \"stop listening\", \"I'm done, you can stop\", \"ok bye\", \"that's it for now\". Never for thanks alone (\"thank you\", \"great, thanks\"), and never when they ask to stop or quit something else (an app, music, a task, an answer). If you are not sure, keep listening and do not call it. Call it before saying anything; its result tells you what to say.",
            "parameters": ["type": "object", "properties": [String: Any](), "required": [String]()],
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
        lastTurnStartedAt = Date()
        pushToTalkTurnAudioBytes = 0
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
            // No audio, no turn. Released before the microphone opened (a press during the ~1.5 s
            // connect, or a tap), the commit used to go out empty — OpenAI answered "buffer only
            // has 0.00ms of audio" — and the reply was requested anyway, with the screen attached:
            // the model invented a request ("create a project folder named MyApp…"), handed it to
            // the agent, and the HUD sat on "Looking at your screen" for a task nobody gave.
            let turnAudioBytes = self.pushToTalkTurnAudioBytes
            self.pushToTalkTurnAudioBytes = 0
            guard Self.hasEnoughAudioForATurn(pcm16Bytes: turnAudioBytes) else {
                try? self.send(["type": "input_audio_buffer.clear"])
                self.log("turn not sent: \(turnAudioBytes) bytes of audio")
                self.onTurnHeardNothing?()
                self.scheduleIdlePauseIfNeeded()
                return
            }
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
        lastTurnStartedAt = Date()
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
    /// The turn's caption: the screenshot's own, then the text a rewrite would replace (quoted in
    /// full) or, without one, the highlighted text as context for "what does this mean".
    nonisolated static func screenCaption(_ caption: String, writingTarget: WritingTarget?, selectedText: String?) -> String {
        if let writingTarget { return caption + " " + writingTarget.contextLine }
        if let selectedText { return caption + " " + SelectedTextReader.contextLine(for: selectedText) }
        return caption
    }

    private func attachScreenContext() async {
        guard let screenContextProvider else { return }
        let started = Date()
        guard let context = await screenContextProvider() else {
            log("no screen context (screen recording permission?)")
            return
        }
        lastWritingTarget = await writingTargetProvider?()
        let caption = Self.screenCaption(context.caption, writingTarget: lastWritingTarget,
                                         selectedText: lastWritingTarget == nil ? SelectedTextReader.currentSelection() : nil)
        let item: [String: Any] = [
            "type": "message",
            "role": "user",
            "content": [
                ["type": "input_text", "text": "[Screen context, not spoken] \(caption)"],
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
        if !isAudioWired {
            log(Self.microphoneAuthorizationDescription(
                captureAuthorization: microphoneAuthorization,
                recordPermission: AVAudioApplication.shared.recordPermission))
        }
        guard microphoneAuthorization != .denied, microphoneAuthorization != .restricted else {
            throw RealtimeVoiceError.audio("microphone access is denied for OpenClicky (System Settings → Privacy & Security → Microphone)")
        }
        if !isAudioWired {
            isAudioWired = true
            audio.setCallbacks(
                onMicrophoneFrame: { [weak self] pcm16, level in
                    Task { @MainActor in
                        guard let self else { return }
                        self.inputLevel = level
                        guard self.isForwardingMicrophone else { return }
                        self.forwardMicrophoneAudio(pcm16: pcm16)
                    }
                },
                onPlaybackActiveChanged: { [weak self] isPlaying in
                    Task { @MainActor in
                        guard let self else { return }
                        self.isSpeaking = isPlaying
                        if !isPlaying { self.scheduleIdlePauseIfNeeded() }
                    }
                }
            )
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
        forwardMicrophoneAudio(pcm16: pcm16)
    }

    /// Counts a frame only once it was handed to an open socket, so "appends sent" in the smoke
    /// summary and the push-to-talk turn's audio total mean audio the server can have received —
    /// the reporter's summary of issue #1 said "appends sent 30" for a socket that was already gone.
    private func forwardMicrophoneAudio(pcm16: Data) {
        do {
            try send(["type": "input_audio_buffer.append", "audio": pcm16.base64EncodedString()])
        } catch {
            return
        }
        microphoneAppends += 1
        pushToTalkTurnAudioBytes += pcm16.count
    }

    /// The microphone line in the log. Run straight from a terminal (`--openclicky-smoke-talk`),
    /// macOS attributes the microphone to the terminal, so this process reads "not determined"
    /// while audio is captured fine; the line said "0 (3 = authorized)", which looked like a fault.
    nonisolated static func microphoneAuthorizationDescription(
        captureAuthorization: AVAuthorizationStatus,
        recordPermission: AVAudioApplication.recordPermission
    ) -> String {
        let captureWords: String
        switch captureAuthorization {
        case .authorized: captureWords = "authorized"
        case .denied: captureWords = "denied"
        case .restricted: captureWords = "restricted"
        case .notDetermined: captureWords = "not determined for this process (launched from a terminal? then the terminal's permission applies)"
        @unknown default: captureWords = "unknown (\(captureAuthorization.rawValue))"
        }
        let recordWords: String
        switch recordPermission {
        case .granted: recordWords = "granted"
        case .denied: recordWords = "denied"
        case .undetermined: recordWords = "undetermined"
        @unknown default: recordWords = "unknown"
        }
        return "microphone authorization: \(captureWords); record permission: \(recordWords)"
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
        guard let webSocketTask, isSocketOpen else { throw RealtimeVoiceError.notConnected }
        let data = try JSONSerialization.data(withJSONObject: object)
        let sendingTask = webSocketTask
        webSocketTask.send(.string(String(decoding: data, as: UTF8.self))) { [weak self] error in
            guard let error else { return }
            Task { @MainActor in
                guard let self else { return }
                self.log("send failed: \(error.localizedDescription)")
                // A failed send means the socket is gone; say so once, and let the next press reconnect.
                guard self.webSocketTask === sendingTask else { return }
                self.markConnectionLost("the connection to OpenAI dropped (\(error.localizedDescription))")
            }
        }
    }

    private func receiveLoop(_ task: RealtimeSocket) {
        task.receive { [weak self] result in
            Task { @MainActor in
                guard let self, self.webSocketTask === task else { return }
                switch result {
                case .failure(let error):
                    let reason = Self.handshakeFailureDescription(
                        statusCode: (task.response as? HTTPURLResponse)?.statusCode,
                        underlyingError: error.localizedDescription)
                    if self.handshakeContinuation != nil {
                        self.failPendingHandshake(RealtimeVoiceError.backend(reason))
                        return
                    }
                    if !self.isConnected {
                        // Still connecting, and nothing is waiting yet: keep the reason for the wait.
                        self.handshakeFailureBeforeWaiting = reason
                        return
                    }
                    self.markConnectionLost(reason)
                case .success(let message):
                    self.lastServerEventAt = Date()
                    self.isSocketOpen = true
                    if let pendingHandshake = self.handshakeContinuation {
                        self.handshakeContinuation = nil
                        pendingHandshake.resume()
                    }
                    if case .string(let text) = message { await self.handleServerEvent(text) }
                    self.receiveLoop(task)
                }
            }
        }
    }

    private func markConnectionLost(_ reason: String) {
        let wasConnected = isConnected
        isConnected = false
        webSocketTask = nil
        isSocketOpen = false
        connectedAt = nil
        stopForwardingMicrophone()
        responseInProgress = false
        log("realtime disconnected: \(reason)")
        if wasConnected { onConnectionLost?(reason) }
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
                if stopListeningAfterGoodbye {
                    stopListeningAfterGoodbye = false
                    endHandsFreeOnceTheGoodbyeHasPlayed()
                }
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
            try? send(["type": "conversation.item.create", "item": ["type": "function_call_output", "call_id": callId, "output": String(outcome.spokenSentence.prefix(4000))]])
            requestContinuation(after: event)
            return
        case .badArguments(let outcome):
            log("mac action: \(name) rejected, \(outcome.spokenSentence)")
            try? send(["type": "conversation.item.create", "item": ["type": "function_call_output", "call_id": callId, "output": String(outcome.spokenSentence.prefix(4000))]])
            requestContinuation(after: event)
            return
        case .notAFastAction:
            break
        }
        var output = "unknown tool \(name)"
        switch name {
        case Self.stopListeningToolName:
            if currentMode == .alwaysOn {
                log("hands-free: sign-off heard")
                stopListeningAfterGoodbye = true
                output = "Hands-free listening is ending. Say a short goodbye in a few words, nothing else."
            } else {
                output = "Hands-free is not on; nothing to stop. Do not mention it."
            }
        case "send_to_agent":
            let task = arguments["task"] as? String ?? ""
            log("agent task: \(task)")
            let startedAt = Date()
            output = await onAgentTask?(task) ?? "The agent lane is not available in this session."
            log("agent task finished in \(String(format: "%.2f", Date().timeIntervalSince(startedAt))) s")
        case WriteInPlace.toolName:
            let newText = arguments["text"] as? String ?? ""
            if let target = lastWritingTarget {
                let outcome = await WriteInPlace.replace(target, with: newText)
                log("write in place: \(outcome)")
                output = outcome.spokenSentence + " Say only that, in a few words; never read the new text out."
            } else {
                log("write in place: no text to write over")
                output = "No highlighted text or text box was found, so nothing was changed. Ask them to highlight the text and ask again; do not read a rewrite out."
            }
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
                        // A window's close / minimize / zoom buttons have no caption and are 12 px
                        // across; macOS reports their exact frames.
                        if let control = WindowControlLocator.locate(label: label, text: elementText, userRequest: userRequest, in: capture) {
                            point = control.point
                            resolved = true
                            resolution = "\(control.control.name) button of \(control.appName) (\(formatted(point.x)), \(formatted(point.y)))"
                        }
                        // A browser tab is a favicon plus a truncated title, which no screenshot model
                        // reads reliably; the browser's own tab list and Accessibility frames do.
                        if !resolved, let tab = BrowserTabLocator.locate(label: label, text: elementText, userRequest: userRequest, in: capture) {
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
                                point = located.point
                                resolution = "located by Claude (\(formatted(point.x)), \(formatted(point.y))) in \(Int(Date().timeIntervalSince(started) * 1000)) ms"
                                // Claude picks the right element and misplaces it by up to ~150 px;
                                // the text it named has an exact box in the OCR of the same screenshot.
                                // Claude's point is far closer than the Realtime guess, so the snap
                                // uses a tighter ambiguity margin than the one above.
                                if let visibleText = located.visibleText,
                                   let lines = await screenText?.value, !lines.isEmpty,
                                   let match = ScreenTextLocator.locate(
                                       visibleText, near: located.point, in: lines,
                                       maxDistance: radius * 0.6, ambiguityMargin: ambiguityMargin * 0.5
                                   ) {
                                    point = match.center
                                    resolution += ", snapped to \"\(match.text)\" (\(formatted(point.x)), \(formatted(point.y)), \(formatted(match.distance)) px from Claude's)"
                                }
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
            if let pluginOutput = await onPluginToolCall?(name, arguments, lastScreenCapture) {
                log("plugin tool: \(name)")
                output = pluginOutput
            }
        }
        // The output goes out immediately; the follow-up response is requested once, on response.done.
        try? send(["type": "conversation.item.create", "item": ["type": "function_call_output", "call_id": callId, "output": String(output.prefix(4000))]])
        requestContinuation(after: event)
    }

    /// response.done arrives when the server has sent the goodbye, not when it has played: turning
    /// hands-free off closes the session, which would cut it off. Waits for playback to end (at most
    /// 8 s, in case no audio came) before asking the companion to stop.
    private func endHandsFreeOnceTheGoodbyeHasPlayed() {
        Task { @MainActor [weak self] in
            let deadline = Date().addingTimeInterval(8)
            while let self, self.isSpeaking, Date() < deadline {
                try? await Task.sleep(nanoseconds: 100_000_000)
            }
            guard let self else { return }
            self.log("hands-free: ending after the goodbye")
            self.onStopListeningRequested?()
        }
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
    /// The person asked Clicky to stop talking: cancel the reply and drop what is queued to play.
    func stopSpeaking() {
        cancelActiveResponse()
        flushPlayback()
    }

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
    case invalidURL(String)

    var errorDescription: String? {
        switch self {
        case .notConfigured: return "OpenClicky backend token is not configured."
        case .notConnected: return "Realtime session is not connected."
        case .backend(let message): return "Realtime session could not be created: \(message)"
        case .audio(let message): return "Audio setup failed: \(message)"
        case .invalidURL(let message): return message
        }
    }
}


// MARK: - Audio engine (off the main actor)

/// Owns the AVAudioEngine graph: microphone → PCM16 mono 24 kHz frames out, PCM16 playback in.
/// Tries Apple's voice-processing unit (echo cancellation) first and falls back to plain capture.
/// Every engine call happens on `queue`, never on the main thread (see RealtimeVoiceClient).
/// Two threads own parts of this class and neither of them is the main one, so it opts out of the
/// project's `SWIFT_DEFAULT_ACTOR_ISOLATION = MainActor`. Without `nonisolated` every stored
/// property below is implicitly main-actor isolated while in fact being touched from `queue` and
/// from CoreAudio's render thread — a claim the Swift 5 language mode does not check, and which
/// produced 53 warnings that said nothing about which accesses were actually unsafe.
///
/// The contract: everything private is `queue`'s, except `microphoneCapture`, which is the one
/// piece the render thread reads and is therefore behind a lock.
nonisolated final class RealtimeAudioEngine: @unchecked Sendable {
    static let sampleRate: Double = 24_000
    private static let pcm16Format = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: sampleRate, channels: 1, interleaved: true)!
    private static let playbackFormat = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: 1)!

    /// Everything the render thread needs, as one immutable unit it can copy out in a single
    /// locked read. Published by `configure`, dropped by `tearDown`; a tap already in flight keeps
    /// its own reference alive, so teardown cannot pull the converter out from under it.
    private final class MicrophoneCapture: @unchecked Sendable {
        let converter: AVAudioConverter
        let monoFormat: AVAudioFormat
        let onFrame: (Data, CGFloat) -> Void

        init(converter: AVAudioConverter, monoFormat: AVAudioFormat, onFrame: @escaping (Data, CGFloat) -> Void) {
            self.converter = converter
            self.monoFormat = monoFormat
            self.onFrame = onFrame
        }
    }

    private let queue = DispatchQueue(label: "org.openclicky.realtime.audio")
    /// `os_unfair_lock` rather than `NSLock`: it donates priority, so the app thread holding it for
    /// three pointer loads cannot invert the priority of the real-time audio thread waiting on it.
    private let microphoneCapture = OSAllocatedUnfairLock<MicrophoneCapture?>(initialState: nil)
    private var onMicrophoneFrame: ((Data, CGFloat) -> Void)?
    private var onPlaybackActiveChanged: ((Bool) -> Void)?
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
    /// The input device to use (CoreAudio UID; nil = the Mac's default), and the one the running
    /// graph was set up with: a change rebuilds the graph at the next `start()`.
    private var preferredMicrophoneUID: String?
    private var configuredMicrophoneUID: String?

    /// Called from the main actor; stored on `queue` like the rest of the engine's state.
    func setPreferredMicrophoneUID(_ uid: String?) {
        queue.async { self.preferredMicrophoneUID = uid }
    }

    /// For tests: the UID the next `start()` will select.
    func currentPreferredMicrophoneUID() async -> String? {
        await withCheckedContinuation { continuation in
            queue.async { continuation.resume(returning: self.preferredMicrophoneUID) }
        }
    }

    /// Both callbacks are read from `queue` and from the audio thread, so they are installed on
    /// `queue` rather than assigned directly. Call before `start()`.
    func setCallbacks(
        onMicrophoneFrame: @escaping (Data, CGFloat) -> Void,
        onPlaybackActiveChanged: @escaping (Bool) -> Void
    ) {
        queue.sync {
            self.onMicrophoneFrame = onMicrophoneFrame
            self.onPlaybackActiveChanged = onPlaybackActiveChanged
        }
    }

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
                // A microphone chosen since the graph was set up: pause() keeps the old device's
                // graph, so it is rebuilt rather than restarted.
                if self.isRunning, self.configuredMicrophoneUID != self.preferredMicrophoneUID {
                    self.tearDown()
                }
                if self.isRunning {
                    if !self.engine.isRunning {
                        do { try self.engine.start() } catch { continuation.resume(throwing: error); return }
                    }
                    continuation.resume(returning: "audio already running")
                    return
                }
                do {
                    var description: String
                    // Echo cancellation (the voice-processing unit) always records from the Mac's
                    // default input; a device set on it is accepted and ignored. A microphone the
                    // person chose that is not the default is honoured by recording without it.
                    if MicrophoneDevices.choiceDiffersFromDefault(preferredUID: self.preferredMicrophoneUID) {
                        description = try self.configure(voiceProcessing: false)
                        description += " (echo cancellation off: it only hears the mac default input)"
                    } else {
                    do {
                        description = try self.configure(voiceProcessing: true)
                    } catch {
                        self.tearDown()
                        description = try self.configure(voiceProcessing: false)
                        description += " (voice processing unavailable: \(error.localizedDescription))"
                    }
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
        var notes: [String] = []
        configuredMicrophoneUID = preferredMicrophoneUID
        if voiceProcessing {
            // The voice-processing unit records from the Mac's default input whatever device is
            // set on it, so none is set (start() only gets here when that is the person's choice).
            notes.append("microphone mac default")
        } else {
            // Settings → microphone, set before outputNode is first touched: from then on the input
            // unit refuses a device change (-10851) and, with voice processing on, the engine failed
            // to start at all (-10875) — realtime voice was down on the 2026-10-11 build.
            let selectedMicrophone = MicrophoneDevices.apply(preferredUID: preferredMicrophoneUID, to: engine)
            notes.append(selectedMicrophone ? "microphone \(preferredMicrophoneUID ?? "mac default")" : "microphone not selected, system input")
        }
        let inputNode = engine.inputNode
        let outputNode = engine.outputNode
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
        if let onMicrophoneFrame {
            microphoneCapture.withLock {
                $0 = MicrophoneCapture(converter: converter, monoFormat: monoFormat, onFrame: onMicrophoneFrame)
            }
        }
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
        microphoneCapture.withLock { $0 = nil }
        converter = nil
        monoFormat = nil
        configuredMicrophoneUID = nil
        queuedBuffers = 0
        isRunning = false
    }

    /// Audio thread: convert to PCM16 mono 24 kHz and hand the bytes to the client.
    ///
    /// Everything this touches on `self` is either the capture snapshot (behind the lock) or a
    /// counter bumped on `queue`. Reading `converter`/`monoFormat`/`onMicrophoneFrame` off `self`
    /// here — as this did — races `tearDown()` nilling them on `queue`; the Thread Sanitizer
    /// reports it and aborts, which `RealtimeAudioEngineConcurrencyTests` pins down.
    ///
    /// Not `private` so that test can drive it directly from a background thread.
    func handleMicrophoneBuffer(_ buffer: AVAudioPCMBuffer) {
        queue.async { self.tapCount += 1 }
        guard let capture = microphoneCapture.withLock({ $0 }) else { return }
        let converter = capture.converter
        let monoFormat = capture.monoFormat
        let onMicrophoneFrame = capture.onFrame
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
