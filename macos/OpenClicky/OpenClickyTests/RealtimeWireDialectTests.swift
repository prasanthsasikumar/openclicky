import Foundation
import Testing
@testable import OpenClicky

/// The Gemini Live translation, against the message shapes gemini-3.8-live sent on 2026-10-28.
struct RealtimeWireDialectTests {
    private func gemini(_ turnDetection: Any = NSNull()) -> GeminiLiveDialect {
        let dialect = GeminiLiveDialect(sampleRate: 24_000, resumptionHandle: nil)
        _ = dialect.outgoing(["type": "session.update", "session": [
            "instructions": "be brief",
            "tools": [
                ["type": "function", "name": "open_app", "description": "Open an app.",
                 "parameters": ["type": "object", "properties": ["name": ["type": "string"]], "required": ["name"]]],
                ["type": "function", "name": "stop_listening", "description": "Stop.",
                 "parameters": ["type": "object", "properties": [String: Any](), "required": [String]()]],
            ],
            "audio": ["input": ["turn_detection": turnDetection]],
        ]])
        return dialect
    }

    /// 100 ms of 24 kHz PCM16 is 4,800 bytes.
    private func audio(bytes: Int) -> [String: Any] {
        ["type": "input_audio_buffer.append", "audio": Data(count: bytes).base64EncodedString()]
    }

    private func types(_ events: [[String: Any]]) -> [String] { events.compactMap { $0["type"] as? String } }

    @Test func choosesOpenAIFirstThenGeminiThenTheAccount() {
        let both = ProviderCredentials(openAIKey: "sk", anthropicKey: nil, accountToken: nil, geminiKey: "g")
        #expect(RealtimeVoiceProvider.choose(credentials: both, preferred: nil) == .openAI)
        #expect(RealtimeVoiceProvider.choose(credentials: both, preferred: .gemini) == .gemini)
        let geminiOnly = ProviderCredentials(openAIKey: nil, anthropicKey: nil, accountToken: nil, geminiKey: "g")
        #expect(RealtimeVoiceProvider.choose(credentials: geminiOnly, preferred: .openAI) == .gemini)
        let account = ProviderCredentials(openAIKey: nil, anthropicKey: nil, accountToken: "t")
        #expect(RealtimeVoiceProvider.choose(credentials: account, preferred: .gemini) == .openAI)
        #expect(RealtimeVoiceProvider.choose(credentials: ProviderCredentials(openAIKey: nil, anthropicKey: nil, accountToken: nil), preferred: nil) == nil)
    }

    @Test func aGeminiKeyAloneTurnsOnRealtime() {
        let capabilities = AccountCapabilities(kind: .signedOut, ownRealtimeKey: true)
        #expect(capabilities.usesRealtime)
        #expect(!AccountCapabilities(kind: .signedOut).usesRealtime)
    }

    @Test func setupCarriesInstructionsToolsAndManualTurns() throws {
        let dialect = GeminiLiveDialect(sampleRate: 24_000, resumptionHandle: "h1")
        let messages = dialect.outgoing(["type": "session.update", "session": [
            "instructions": "be brief",
            "tools": [["type": "function", "name": "stop_listening", "parameters": ["type": "object", "properties": [String: Any]()]]],
            "audio": ["input": ["turn_detection": NSNull()]],
        ]])
        let setup = try #require(messages.first?["setup"] as? [String: Any])
        #expect(setup["model"] as? String == "models/gemini-3.8-live")
        #expect(((setup["realtimeInputConfig"] as? [String: Any])?["automaticActivityDetection"] as? [String: Any])?["disabled"] as? Bool == true)
        #expect((setup["sessionResumption"] as? [String: Any])?["handle"] as? String == "h1")
        let declarations = try #require(((setup["tools"] as? [[String: Any]])?.first)?["functionDeclarations"] as? [[String: Any]])
        // Gemini refuses an object schema without properties.
        #expect(declarations.first?["parameters"] == nil)
        // The same instructions again send nothing; new ones go in as context.
        #expect(dialect.outgoing(["type": "session.update", "session": ["instructions": "be brief"]]).isEmpty)
        let update = dialect.outgoing(["type": "session.update", "session": ["instructions": "use skill X"]])
        #expect((update.first?["clientContent"] as? [String: Any])?["turnComplete"] as? Bool == false)
    }

    @Test func handsFreeLeavesTurnsToGemini() throws {
        let dialect = gemini(["type": "server_vad", "silence_duration_ms": 600])
        #expect(!dialect.manualTurns)
        // Audio streams straight through, with no activity markers.
        let sent = dialect.outgoing(audio(bytes: 960))
        #expect(sent.count == 1)
        #expect(((sent.first?["realtimeInput"] as? [String: Any])?["audio"] as? [String: Any])?["mimeType"] as? String == "audio/pcm;rate=24000")
        #expect(types(dialect.incoming(["voiceActivity": ["type": "ACTIVITY_START"]])) == ["input_audio_buffer.speech_started"])
        // While the person talks, the screen streams in with the audio.
        let screen = dialect.outgoing(["type": "conversation.item.create", "item": ["type": "message", "role": "user", "content": [
            ["type": "input_text", "text": "[Screen context, not spoken] pointer at 1,2"],
            ["type": "input_image", "image_url": "data:image/jpeg;base64,AAAA"],
        ]]])
        #expect(screen.count == 2)
        #expect(((screen[1]["realtimeInput"] as? [String: Any])?["video"] as? [String: Any])?["mimeType"] as? String == "image/jpeg")
        // response.create has nothing to add: Gemini replies on its own.
        #expect(dialect.outgoing(["type": "response.create"]).isEmpty)
    }

    @Test func pushToTalkHoldsATapUntilThereIsEnoughAudio() {
        let dialect = gemini()
        #expect(dialect.outgoing(["type": "input_audio_buffer.clear"]).isEmpty)
        #expect(dialect.outgoing(audio(bytes: 2_400)).isEmpty)
        // A tap the client clears never opened a turn.
        #expect(dialect.outgoing(["type": "input_audio_buffer.clear"]).isEmpty)
        #expect(dialect.outgoing(audio(bytes: 2_400)).isEmpty)
        let opened = dialect.outgoing(audio(bytes: 2_400))
        #expect((opened.first?["realtimeInput"] as? [String: Any])?["activityStart"] != nil)
        #expect(opened.count == 3)
        #expect(dialect.outgoing(audio(bytes: 960)).count == 1)
        let ended = dialect.outgoing(["type": "input_audio_buffer.commit"])
        #expect((ended.last?["realtimeInput"] as? [String: Any])?["activityEnd"] != nil)
    }

    @Test func aTypedTurnGoesOutAsOneClientContentTurn() throws {
        let dialect = gemini()
        #expect(dialect.outgoing(["type": "conversation.item.create", "item": ["type": "message", "content": [
            ["type": "input_text", "text": "[Screen context] x"], ["type": "input_image", "image_url": "data:image/jpeg;base64,AAAA"],
        ]]]).isEmpty)
        #expect(dialect.outgoing(["type": "conversation.item.create", "item": ["type": "message", "content": [["type": "input_text", "text": "what is this?"]]]]).isEmpty)
        let turn = try #require(dialect.outgoing(["type": "response.create"]).first?["clientContent"] as? [String: Any])
        #expect(turn["turnComplete"] as? Bool == true)
        let parts = try #require((turn["turns"] as? [[String: Any]])?.first?["parts"] as? [[String: Any]])
        #expect(parts.count == 3)
        #expect((parts[1]["inlineData"] as? [String: Any])?["data"] as? String == "AAAA")
    }

    @Test func aReplyBecomesTheOpenAIResponseLifecycle() {
        let dialect = gemini()
        #expect(types(dialect.incoming(["setupComplete": [String: Any]()])) == ["session.created"])
        #expect(dialect.incoming(["serverContent": ["inputTranscription": ["text": "open safari"]]]).isEmpty)
        let audioEvents = dialect.incoming(["serverContent": ["modelTurn": ["parts": [["inlineData": ["mimeType": "audio/pcm;rate=24000", "data": "AAAA"]]]]]])
        #expect(types(audioEvents) == ["conversation.item.input_audio_transcription.completed", "response.created", "response.output_audio.delta"])
        #expect(audioEvents.first?["transcript"] as? String == "open safari")
        #expect(types(dialect.incoming(["serverContent": ["outputTranscription": ["text": "Done."]]])) == ["response.output_audio_transcript.delta"])
        #expect(types(dialect.incoming(["serverContent": ["generationComplete": true]])) == ["response.output_audio_transcript.done", "response.done"])
        // turnComplete after generationComplete closes nothing twice.
        #expect(dialect.incoming(["serverContent": ["turnComplete": true]]).isEmpty)
    }

    @Test func aToolCallIsAnsweredWithAToolResponseNamedLikeTheCall() throws {
        let dialect = gemini()
        let events = dialect.incoming(["toolCall": ["functionCalls": [["id": "call_1", "name": "open_app", "args": ["name": "Safari"]]]]])
        #expect(types(events) == ["response.created", "response.function_call_arguments.done"])
        #expect(events.last?["call_id"] as? String == "call_1")
        #expect(events.last?["arguments"] as? String == #"{"name":"Safari"}"#)
        #expect(types(dialect.incoming(["serverContent": ["generationComplete": true]])) == ["response.output_audio_transcript.done", "response.done"])
        let answer = dialect.outgoing(["type": "conversation.item.create", "item": ["type": "function_call_output", "call_id": "call_1", "output": "Opened Safari."]])
        let response = try #require(((answer.first?["toolResponse"] as? [String: Any])?["functionResponses"] as? [[String: Any]])?.first)
        #expect(response["name"] as? String == "open_app")
        #expect((response["response"] as? [String: Any])?["output"] as? String == "Opened Safari.")
        // The continuation request adds nothing: the toolResponse already makes Gemini go on.
        #expect(dialect.outgoing(["type": "response.create"]).isEmpty)
    }

    @Test func theScreenAfterAnActionGoesBeforeTheToolResponse() {
        let dialect = gemini()
        _ = dialect.incoming(["toolCall": ["functionCalls": [["id": "c", "name": "open_app", "args": [String: Any]()]]]])
        _ = dialect.outgoing(["type": "conversation.item.create", "item": ["type": "message", "content": [["type": "input_text", "text": "[Screen context] after"]]]])
        let messages = dialect.outgoing(["type": "conversation.item.create", "item": ["type": "function_call_output", "call_id": "c", "output": "ok"]])
        #expect(messages.count == 2)
        #expect((messages.first?["clientContent"] as? [String: Any])?["turnComplete"] as? Bool == false)
        #expect(messages.last?["toolResponse"] != nil)
    }

    @Test func aCancelledReplyIsDroppedUntilItsTurnEnds() {
        let dialect = gemini()
        _ = dialect.incoming(["serverContent": ["modelTurn": ["parts": [["inlineData": ["mimeType": "audio/pcm", "data": "AA"]]]]]])
        _ = dialect.incoming(["toolCall": ["functionCalls": [["id": "late", "name": "open_app", "args": [String: Any]()]]]])
        #expect(dialect.outgoing(["type": "response.cancel"]).isEmpty)
        #expect(dialect.incoming(["serverContent": ["modelTurn": ["parts": [["inlineData": ["mimeType": "audio/pcm", "data": "BB"]]]]]]).isEmpty)
        #expect(dialect.incoming(["serverContent": ["turnComplete": true]]).isEmpty)
        // An output for a call of the cancelled reply is not sent: it would make Gemini go on.
        #expect(dialect.outgoing(["type": "conversation.item.create", "item": ["type": "function_call_output", "call_id": "late", "output": "x"]]).isEmpty)
        // The next reply plays again.
        #expect(types(dialect.incoming(["serverContent": ["modelTurn": ["parts": [["inlineData": ["mimeType": "audio/pcm", "data": "CC"]]]]]])) == ["response.created", "response.output_audio.delta"])
    }

    @Test func keepsTheLatestResumptionHandleAndReportsGoAway() {
        let dialect = gemini()
        _ = dialect.incoming(["sessionResumptionUpdate": ["newHandle": "h2", "resumable": true]])
        _ = dialect.incoming(["sessionResumptionUpdate": ["newHandle": "", "resumable": false]])
        #expect(dialect.resumptionHandle == "h2")
        #expect(types(dialect.incoming(["goAway": ["timeLeft": "50s"]])) == ["session.go_away"])
    }

    @Test func readsAServerCloseReason() {
        #expect(RealtimeVoiceClient.closeDescription(Data("Invalid model".utf8)) == "closed by the server: Invalid model")
        #expect(RealtimeVoiceClient.closeDescription(nil) == nil)
    }
}
