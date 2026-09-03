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
//

import AVFoundation
import Combine
import Foundation

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
    var onAgentTask: ((String) async -> String)?

    private(set) var turnMode: TurnMode = .pushToTalk
    private var voice: String?
    private var instructions: String

    private var webSocketTask: URLSessionWebSocketTask?
    private lazy var urlSession = URLSession(configuration: .default)
    private var connectTask: Task<Void, Error>?
    private var keepWarmTask: Task<Void, Never>?

    /// Audio runs off the main actor: CoreAudio's first-time setup hops synchronously to the main
    /// queue, so configuring the engine from a main-actor task deadlocks the app.
    private let audio = RealtimeAudioEngine()
    private var isAudioWired = false
    private var isForwardingMicrophone = false
    private var pushToTalkTailTask: Task<Void, Never>?
    private var assistantTranscriptBuffer = ""
    private var responseInProgress = false

    private static let sampleRate: Double = RealtimeAudioEngine.sampleRate

    static let defaultInstructions = """
    You are OpenClicky, a friendly, fast macOS voice assistant. Speak English unless the user speaks another language. \
    Keep spoken replies short (one or two sentences). Answer quick questions yourself. For anything that requires doing \
    work on the computer — creating or editing files or code, running commands, using apps or integrations, research, \
    multi-step tasks — first say one short sentence acknowledging it, then call the send_to_agent tool with a clear, \
    self-contained task, and afterwards tell the user in one sentence what happened. Never pretend work was done without \
    the tool. Do not read file paths aloud character by character.
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
        flushPlayback()
        webSocketTask?.cancel(with: .normalClosure, reason: nil)
        webSocketTask = nil
        if isConnected { isConnected = false }
        responseInProgress = false
        if let reason { log(reason) }
    }

    private func openConnection(mode: TurnMode) async throws {
        guard OpenClickyConfiguration.isConfigured else { throw RealtimeVoiceError.notConfigured }
        var secretRequest = URLRequest(url: URL(string: "\(OpenClickyConfiguration.backendBaseURL)/agent/realtime/session")!)
        secretRequest.httpMethod = "POST"
        secretRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
        OpenClickyConfiguration.authorize(&secretRequest)
        secretRequest.httpBody = try JSONSerialization.data(withJSONObject: ["voice": voice as Any, "instructions": instructions].compactMapValues { $0 })
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
        try await startAudioIfNeeded()
        try send(sessionUpdate(mode: mode))
        isConnected = true
        log("realtime connected (\(model), \(mode == .pushToTalk ? "push-to-talk" : "always on"))")
        receiveLoop(task)
    }

    private func sessionUpdate(mode: TurnMode) -> [String: Any] {
        var input: [String: Any] = [
            "format": ["type": "audio/pcm", "rate": Int(Self.sampleRate)],
            "transcription": ["model": "gpt-4o-mini-transcribe"],
        ]
        switch mode {
        case .pushToTalk:
            input["turn_detection"] = NSNull()
        case .alwaysOn:
            input["turn_detection"] = ["type": "server_vad", "silence_duration_ms": 600, "create_response": true, "interrupt_response": true]
        }
        var output: [String: Any] = ["format": ["type": "audio/pcm", "rate": Int(Self.sampleRate)]]
        if let voice { output["voice"] = voice }
        let tool: [String: Any] = [
            "type": "function",
            "name": "send_to_agent",
            "description": "Hand a task that requires doing work (files, code, commands, apps, research) to the OpenClicky agent. Returns a short result summary.",
            "parameters": ["type": "object", "properties": ["task": ["type": "string", "description": "A clear, self-contained description of what to do."]], "required": ["task"]],
        ]
        return [
            "type": "session.update",
            "session": [
                "type": "realtime",
                "instructions": instructions,
                "tools": [tool],
                "tool_choice": "auto",
                "audio": ["input": input, "output": output],
            ],
        ]
    }

    // MARK: - Push-to-talk / always-on control

    /// Shortcut pressed: interrupt any reply and stream the microphone.
    func beginPushToTalk() {
        pushToTalkTailTask?.cancel()
        pushToTalkTailTask = nil
        if responseInProgress { try? send(["type": "response.cancel"]) }
        flushPlayback()
        try? send(["type": "input_audio_buffer.clear"])
        startForwardingMicrophone()
    }

    /// Shortcut released: keep the mic open 400 ms so the last word is not clipped, then commit.
    func endPushToTalk() {
        pushToTalkTailTask?.cancel()
        pushToTalkTailTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 400_000_000)
            guard !Task.isCancelled, let self else { return }
            self.stopForwardingMicrophone()
            try? self.send(["type": "input_audio_buffer.commit"])
            try? self.send(["type": "response.create"])
        }
    }

    /// Always-on: stream continuously; the server decides the turns.
    func startListeningContinuously() {
        startForwardingMicrophone()
    }

    func stopListening() {
        stopForwardingMicrophone()
    }

    /// Speak something proactively (used for the greeting on first connect).
    func requestResponse(instructions: String? = nil) {
        var body: [String: Any] = ["type": "response.create"]
        if let instructions { body["response"] = ["instructions": instructions] }
        try? send(body)
    }

    // MARK: - Audio (delegated to RealtimeAudioEngine on its own queue)

    private func startAudioIfNeeded() async throws {
        let microphoneAuthorization = AVCaptureDevice.authorizationStatus(for: .audio)
        log("microphone authorization: \(microphoneAuthorization.rawValue) (3 = authorized)")
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
                    try? self.send(["type": "input_audio_buffer.append", "audio": pcm16.base64EncodedString()])
                }
            }
            audio.onPlaybackActiveChanged = { [weak self] isPlaying in
                Task { @MainActor in self?.isSpeaking = isPlaying }
            }
        }
        log(try await audio.start())
    }

    private func startForwardingMicrophone() {
        isForwardingMicrophone = true
    }

    private func stopForwardingMicrophone() {
        isForwardingMicrophone = false
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
            flushPlayback()
            if responseInProgress { try? send(["type": "response.cancel"]) }
        case "conversation.item.input_audio_transcription.completed":
            if let transcript = event["transcript"] as? String {
                let trimmed = transcript.trimmingCharacters(in: .whitespacesAndNewlines)
                if !trimmed.isEmpty { onTranscript?(.user, trimmed) }
            }
        case "response.created":
            responseInProgress = true
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
            onResponseFinished?()
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
        var output = "unknown tool \(name)"
        if name == "send_to_agent" {
            var task = ""
            if let argumentsText = event["arguments"] as? String,
               let arguments = try? JSONSerialization.jsonObject(with: Data(argumentsText.utf8)) as? [String: Any] {
                task = arguments["task"] as? String ?? ""
            }
            log("agent task: \(task)")
            output = await onAgentTask?(task) ?? "The agent lane is not available in this session."
        }
        try? send(["type": "conversation.item.create", "item": ["type": "function_call_output", "call_id": callId, "output": String(output.prefix(4000))]])
        try? send(["type": "response.create"])
    }

    private func log(_ line: String) {
        onEvent?(line)
        print("🎙️ Realtime: \(line)")
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
    private var isRunning = false
    private var queuedBuffers = 0

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

    func enqueue(pcm16 data: Data) {
        queue.async {
            guard self.isRunning else { return }
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
        guard let converter = AVAudioConverter(from: hardwareFormat, to: Self.pcm16Format) else {
            throw RealtimeVoiceError.audio("cannot convert \(hardwareFormat) to PCM16 24 kHz")
        }
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
        queuedBuffers = 0
        isRunning = false
    }

    /// Audio thread: convert to PCM16 mono 24 kHz and hand the bytes to the client.
    private func handleMicrophoneBuffer(_ buffer: AVAudioPCMBuffer) {
        guard let converter, let onMicrophoneFrame else { return }
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
            return buffer
        }
        guard conversionError == nil, converted.frameLength > 0, let channel = converted.int16ChannelData else { return }
        let data = Data(bytes: channel[0], count: Int(converted.frameLength) * MemoryLayout<Int16>.size)
        onMicrophoneFrame(data, Self.rmsLevel(of: buffer))
    }

    private static func rmsLevel(of buffer: AVAudioPCMBuffer) -> CGFloat {
        guard let channel = buffer.floatChannelData?[0], buffer.frameLength > 0 else { return 0 }
        var sum: Float = 0
        for index in 0..<Int(buffer.frameLength) { sum += channel[index] * channel[index] }
        return CGFloat(min(1, sqrt(sum / Float(buffer.frameLength)) * 6))
    }
}
