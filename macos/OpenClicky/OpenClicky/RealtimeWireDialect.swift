//
//  RealtimeWireDialect.swift
//  OpenClicky
//
//  The realtime client speaks OpenAI's Realtime protocol: it sends `session.update`,
//  `input_audio_buffer.append`, `response.create`… and handles `response.created`,
//  `response.output_audio.delta`, `response.function_call_arguments.done`, `response.done`…
//  A dialect sits between that and the socket, so another vendor's live API runs the same turns,
//  tools, pointing and barge-in: OpenAI's is the identity; Gemini Live's translates both ways.
//
//  Everything here is pure (JSON in, JSON out) and unit-tested in RealtimeWireDialectTests.
//

import Foundation

/// Who answers the fast voice lane and hands-free.
nonisolated enum RealtimeVoiceProvider: String, CaseIterable, Sendable {
    case openAI = "openai"
    case gemini

    var displayName: String {
        switch self {
        case .openAI: return "OpenAI"
        case .gemini: return "Gemini"
        }
    }

    /// The provider the next connection uses, with how it pays: an own key goes straight to its
    /// vendor; with no realtime key, an account's token gets OpenAI through the backend.
    /// `preferred` is the person's pick (nil: automatic, OpenAI first, its prompts and pointing
    /// were tuned on it). A pick without its key falls back to automatic.
    static func choose(credentials: ProviderCredentials, preferred: RealtimeVoiceProvider?) -> RealtimeVoiceProvider? {
        func usable(_ provider: RealtimeVoiceProvider) -> Bool {
            switch provider {
            case .openAI: return credentials.openAIKey != nil || credentials.accountToken != nil
            case .gemini: return credentials.geminiKey != nil
            }
        }
        if let preferred, usable(preferred) { return preferred }
        if credentials.openAIKey != nil { return .openAI }
        if credentials.geminiKey != nil { return .gemini }
        if credentials.accountToken != nil { return .openAI }
        return nil
    }

    /// Whether any own key can run realtime voice (an account alone is decided by its plan).
    static func hasOwnKey(_ credentials: ProviderCredentials) -> Bool {
        credentials.openAIKey != nil || credentials.geminiKey != nil
    }
}

/// Translates between the client's OpenAI-shaped events and one vendor's wire messages.
/// Used on the main actor only, like the client that owns it.
nonisolated protocol RealtimeWireDialect: AnyObject {
    /// For log lines and the sentences a person sees ("the connection to Gemini dropped").
    var providerName: String { get }
    /// OpenAI greets a new socket first (`session.created`); Gemini says nothing until the
    /// client's setup message, so the client sends it before waiting for the handshake.
    var clientSpeaksFirst: Bool { get }
    /// Hands-free: the vendor replies on its own once the person stops, without `response.create`
    /// (so the screen goes up when speech starts instead of when the utterance is committed).
    var respondsWithoutRequest: Bool { get }
    /// A connection is replaced before this age, ahead of the vendor's own cap.
    var maximumConnectionAge: TimeInterval { get }
    /// The wire messages for one client event (none when the vendor has no equivalent).
    func outgoing(_ event: [String: Any]) -> [[String: Any]]
    /// The client events for one wire message.
    func incoming(_ message: [String: Any]) -> [[String: Any]]
}

/// OpenAI's own protocol: nothing to translate.
nonisolated final class OpenAIWireDialect: RealtimeWireDialect {
    let providerName = "OpenAI"
    let clientSpeaksFirst = false
    let respondsWithoutRequest = false
    /// OpenAI cuts a session at 60 minutes.
    let maximumConnectionAge: TimeInterval = 50 * 60
    func outgoing(_ event: [String: Any]) -> [[String: Any]] { [event] }
    func incoming(_ message: [String: Any]) -> [[String: Any]] { [message] }
}

/// Gemini Live (BidiGenerateContent). Checked against gemini-3.8-live on 2026-10-28:
/// every server message arrives as a binary frame of JSON; `setupComplete` answers the setup;
/// `voiceActivity` reports ACTIVITY_START / ACTIVITY_END in both turn modes; a tool call is followed
/// by `generationComplete` + `turnComplete`, and the `toolResponse` alone makes the model go on
/// (a new turn of audio), which maps onto OpenAI's response.done → output → response.create.
nonisolated final class GeminiLiveDialect: RealtimeWireDialect {
    static let defaultModel = "gemini-3.8-live"
    static let socketURL = URL(string: "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent")!

    let providerName = "Gemini"
    let clientSpeaksFirst = true
    let respondsWithoutRequest = true
    /// Gemini closes a connection after about ten minutes (it says so with `goAway` first);
    /// the session itself carries on through the resumption handle.
    let maximumConnectionAge: TimeInterval = 9 * 60

    let model: String
    let sampleRate: Int
    /// The latest handle Gemini offered; a new connection given it continues this conversation.
    private(set) var resumptionHandle: String?

    /// Push-to-talk turns are marked by the client (activityStart / activityEnd); hands-free
    /// leaves them to Gemini's voice activity detection.
    private(set) var manualTurns = true
    private var setupSent = false
    private var sentInstructions = ""
    /// Push-to-talk audio is held until there is enough for a turn (the client's own 100 ms
    /// rule), so a tap that the client then clears never opens a turn Gemini would answer.
    private var heldAudio: [String] = []
    private var heldAudioBytes = 0
    private var activityOpen = false
    /// Hands-free: between Gemini's ACTIVITY_START and ACTIVITY_END.
    private var personIsSpeaking = false
    /// Screen context and typed text not sent yet: they go out as one `clientContent` turn.
    private var pendingParts: [[String: Any]] = []
    private var responseOpen = false
    private var responseCount = 0
    /// After `response.cancel` the rest of that turn is dropped, up to its `interrupted` or
    /// `turnComplete`: Gemini has no cancel, so its audio would otherwise keep playing.
    private var discardingTurn = false
    private var userTranscript = ""
    private var callNames: [String: String] = [:]
    /// Calls of a turn that was cancelled or moved on from: their outputs are not sent, since a
    /// `toolResponse` alone would make Gemini carry on talking about a turn the person left.
    private var abandonedCalls: Set<String> = []
    private var openCalls: Set<String> = []

    init(model: String = GeminiLiveDialect.defaultModel, sampleRate: Int, resumptionHandle: String?) {
        self.model = model
        self.sampleRate = sampleRate
        self.resumptionHandle = resumptionHandle
    }

    private var currentResponseId: String { "gemini-response-\(responseCount)" }

    // MARK: Client → Gemini

    func outgoing(_ event: [String: Any]) -> [[String: Any]] {
        switch event["type"] as? String {
        case "session.update":
            return sessionUpdate(event["session"] as? [String: Any] ?? [:])
        case "input_audio_buffer.append":
            guard let audio = event["audio"] as? String else { return [] }
            if !manualTurns || activityOpen { return [audioMessage(audio)] }
            heldAudio.append(audio)
            heldAudioBytes += Data(base64Encoded: audio)?.count ?? 0
            return heldAudioBytes >= minimumTurnBytes ? openActivity() : []
        case "input_audio_buffer.clear":
            heldAudio.removeAll()
            heldAudioBytes = 0
            abandonOpenCalls()
            return []
        case "input_audio_buffer.commit":
            guard manualTurns else { return [] }
            var messages = activityOpen ? [] : openActivity()
            messages.append(["realtimeInput": ["activityEnd": [String: Any]()]])
            activityOpen = false
            return messages
        case "conversation.item.create":
            return conversationItem(event["item"] as? [String: Any] ?? [:])
        case "response.create":
            var messages: [[String: Any]] = []
            if let instructions = (event["response"] as? [String: Any])?["instructions"] as? String, !instructions.isEmpty {
                pendingParts.append(["text": "[Say this now, in your own voice] \(instructions)"])
            }
            if !pendingParts.isEmpty {
                messages.append(clientContent(turnComplete: true))
            }
            // Otherwise the reply is already coming: activityEnd, Gemini's own turn detection
            // or a toolResponse each start one.
            return messages
        case "response.cancel":
            if responseOpen {
                responseOpen = false
                discardingTurn = true
            }
            abandonOpenCalls()
            return []
        default:
            return []
        }
    }

    private var minimumTurnBytes: Int { sampleRate / 10 * MemoryLayout<Int16>.size }

    private func openActivity() -> [[String: Any]] {
        activityOpen = true
        userTranscript = ""
        abandonOpenCalls()
        var messages: [[String: Any]] = [["realtimeInput": ["activityStart": [String: Any]()]]]
        messages += heldAudio.map(audioMessage)
        heldAudio.removeAll()
        heldAudioBytes = 0
        return messages
    }

    private func audioMessage(_ base64: String) -> [String: Any] {
        ["realtimeInput": ["audio": ["data": base64, "mimeType": "audio/pcm;rate=\(sampleRate)"]]]
    }

    private func abandonOpenCalls() {
        abandonedCalls.formUnion(openCalls)
        openCalls.removeAll()
    }

    private func sessionUpdate(_ session: [String: Any]) -> [[String: Any]] {
        let instructions = session["instructions"] as? String ?? ""
        guard !setupSent else {
            // A live session's setup is fixed; changed instructions (another app in front, a skill
            // switched on) go in as context the model reads from here on.
            guard instructions != sentInstructions else { return [] }
            sentInstructions = instructions
            return [["clientContent": [
                "turns": [["role": "user", "parts": [["text": "[Updated instructions; follow these from now on, do not answer this] \(instructions)"]]]],
                "turnComplete": false,
            ]]]
        }
        setupSent = true
        sentInstructions = instructions
        let input = (session["audio"] as? [String: Any])?["input"] as? [String: Any] ?? [:]
        let turnDetection = input["turn_detection"] as? [String: Any]
        manualTurns = turnDetection == nil
        var activityDetection: [String: Any] = ["disabled": manualTurns]
        if let silence = turnDetection?["silence_duration_ms"] as? Int { activityDetection["silenceDurationMs"] = silence }
        let declarations = (session["tools"] as? [[String: Any]] ?? []).compactMap(Self.functionDeclaration)
        var setup: [String: Any] = [
            "model": "models/\(model)",
            "generationConfig": ["responseModalities": ["AUDIO"]],
            "systemInstruction": ["parts": [["text": instructions]]],
            "realtimeInputConfig": ["automaticActivityDetection": activityDetection],
            "inputAudioTranscription": [String: Any](),
            "outputAudioTranscription": [String: Any](),
            // Without compression an audio-and-screenshots session is cut after a few minutes.
            "contextWindowCompression": ["slidingWindow": [String: Any]()],
            "sessionResumption": resumptionHandle.map { ["handle": $0] } ?? [String: Any](),
        ]
        if !declarations.isEmpty { setup["tools"] = [["functionDeclarations": declarations]] }
        return [["setup": setup]]
    }

    /// An OpenAI function tool as a Gemini function declaration. Gemini refuses an object schema
    /// with no properties, so a tool without arguments (stop_listening) is declared without one.
    static func functionDeclaration(_ tool: [String: Any]) -> [String: Any]? {
        guard (tool["type"] as? String ?? "function") == "function", let name = tool["name"] as? String else { return nil }
        var declaration: [String: Any] = ["name": name]
        if let description = tool["description"] as? String { declaration["description"] = description }
        if let parameters = tool["parameters"] as? [String: Any],
           let properties = parameters["properties"] as? [String: Any], !properties.isEmpty {
            declaration["parameters"] = GeminiAnswerProvider.schemaGeminiAccepts(from: parameters)
        }
        return declaration
    }

    private func conversationItem(_ item: [String: Any]) -> [[String: Any]] {
        switch item["type"] as? String {
        case "function_call_output":
            let callId = item["call_id"] as? String ?? ""
            openCalls.remove(callId)
            guard !abandonedCalls.contains(callId), let name = callNames[callId] else { return [] }
            // Context added since (the screen after an action) goes first, so the model's
            // continuation already sees it.
            var messages = pendingParts.isEmpty ? [] : [clientContent(turnComplete: false)]
            messages.append(["toolResponse": ["functionResponses": [[
                "id": callId, "name": name, "response": ["output": item["output"] as? String ?? ""],
            ]]]])
            return messages
        case "message":
            let parts = (item["content"] as? [[String: Any]] ?? []).compactMap(Self.part)
            // While the person is talking the screen belongs to that turn, so it streams in with
            // the audio; otherwise it waits for the next response.create.
            if activityOpen || (!manualTurns && personIsSpeaking) {
                return parts.map { part in
                    if let inline = part["inlineData"] { return ["realtimeInput": ["video": inline]] }
                    return ["realtimeInput": ["text": part["text"] ?? ""]]
                }
            }
            pendingParts += parts
            return []
        default:
            return []
        }
    }

    /// One OpenAI content part as a Gemini part: text as text, a data-URL image as inline data.
    static func part(_ content: [String: Any]) -> [String: Any]? {
        switch content["type"] as? String {
        case "input_text":
            return (content["text"] as? String).map { ["text": $0] }
        case "input_image":
            guard let url = content["image_url"] as? String, url.hasPrefix("data:"),
                  let comma = url.firstIndex(of: ",") else { return nil }
            let header = url[url.index(url.startIndex, offsetBy: 5)..<comma]
            let mimeType = header.split(separator: ";").first.map(String.init) ?? "image/jpeg"
            return ["inlineData": ["mimeType": mimeType, "data": String(url[url.index(after: comma)...])]]
        default:
            return nil
        }
    }

    private func clientContent(turnComplete: Bool) -> [String: Any] {
        let parts = pendingParts
        pendingParts.removeAll()
        return ["clientContent": ["turns": [["role": "user", "parts": parts]], "turnComplete": turnComplete]]
    }

    // MARK: Gemini → client

    func incoming(_ message: [String: Any]) -> [[String: Any]] {
        var events: [[String: Any]] = []
        if message["setupComplete"] != nil {
            events.append(["type": "session.created"])
        }
        if let update = message["sessionResumptionUpdate"] as? [String: Any],
           update["resumable"] as? Bool == true, let handle = update["newHandle"] as? String, !handle.isEmpty {
            resumptionHandle = handle
        }
        if let goAway = message["goAway"] as? [String: Any] {
            events.append(["type": "session.go_away", "time_left": goAway["timeLeft"] as? String ?? ""])
        }
        if let activity = (message["voiceActivity"] as? [String: Any])?["type"] as? String, !manualTurns {
            switch activity {
            case "ACTIVITY_START":
                personIsSpeaking = true
                userTranscript = ""
                events.append(["type": "input_audio_buffer.speech_started"])
            case "ACTIVITY_END":
                personIsSpeaking = false
                events.append(["type": "input_audio_buffer.speech_stopped"])
            default:
                break
            }
        }
        if let content = message["serverContent"] as? [String: Any] {
            events += serverContent(content)
        }
        if let calls = (message["toolCall"] as? [String: Any])?["functionCalls"] as? [[String: Any]], !discardingTurn {
            events += openResponseIfNeeded()
            for call in calls {
                guard let id = call["id"] as? String, let name = call["name"] as? String else { continue }
                callNames[id] = name
                openCalls.insert(id)
                let arguments = (try? JSONSerialization.data(withJSONObject: call["args"] as? [String: Any] ?? [:]))
                    .map { String(decoding: $0, as: UTF8.self) } ?? "{}"
                events.append(["type": "response.function_call_arguments.done", "call_id": id, "name": name,
                               "arguments": arguments, "response_id": currentResponseId])
            }
        }
        if let ids = (message["toolCallCancellation"] as? [String: Any])?["ids"] as? [String] {
            abandonedCalls.formUnion(ids)
            openCalls.subtract(ids)
        }
        return events
    }

    private func serverContent(_ content: [String: Any]) -> [[String: Any]] {
        var events: [[String: Any]] = []
        if let text = (content["inputTranscription"] as? [String: Any])?["text"] as? String {
            userTranscript += text
        }
        if content["interrupted"] as? Bool == true {
            if discardingTurn {
                discardingTurn = false
            } else if responseOpen {
                responseOpen = false
                events.append(["type": "response.done", "response": ["id": currentResponseId, "status": "cancelled"]])
            }
            return events
        }
        if !discardingTurn {
            for part in (content["modelTurn"] as? [String: Any])?["parts"] as? [[String: Any]] ?? [] {
                guard part["thought"] as? Bool != true,
                      let inline = part["inlineData"] as? [String: Any],
                      (inline["mimeType"] as? String ?? "").hasPrefix("audio/"),
                      let data = inline["data"] as? String else { continue }
                events += openResponseIfNeeded()
                events.append(["type": "response.output_audio.delta", "delta": data])
            }
            if let text = (content["outputTranscription"] as? [String: Any])?["text"] as? String, !text.isEmpty {
                events += openResponseIfNeeded()
                events.append(["type": "response.output_audio_transcript.delta", "delta": text])
            }
        }
        let generationDone = content["generationComplete"] as? Bool == true
        let turnDone = content["turnComplete"] as? Bool == true
        if generationDone || turnDone {
            if discardingTurn {
                if turnDone { discardingTurn = false }
            } else {
                events += userTranscriptEvent()
                if responseOpen {
                    responseOpen = false
                    events.append(["type": "response.output_audio_transcript.done"])
                    events.append(["type": "response.done", "response": ["id": currentResponseId, "status": "completed"]])
                }
            }
        }
        return events
    }

    private func openResponseIfNeeded() -> [[String: Any]] {
        guard !responseOpen else { return [] }
        responseOpen = true
        responseCount += 1
        return userTranscriptEvent() + [["type": "response.created", "response": ["id": currentResponseId]]]
    }

    /// What the person said, once per turn, before the reply to it.
    private func userTranscriptEvent() -> [[String: Any]] {
        let transcript = userTranscript.trimmingCharacters(in: .whitespacesAndNewlines)
        userTranscript = ""
        guard !transcript.isEmpty else { return [] }
        return [["type": "conversation.item.input_audio_transcription.completed", "transcript": transcript]]
    }
}
