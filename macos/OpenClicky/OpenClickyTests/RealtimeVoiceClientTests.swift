//
//  RealtimeVoiceClientTests.swift
//  OpenClickyTests
//
//  `model` in the backend's realtime session response is server-supplied, not a compile-time
//  literal — a malformed value must not force-unwrap the websocket URL into a crash.
//

import AVFoundation
import Foundation
import Testing
@testable import OpenClicky

struct RealtimeVoiceClientTests {
    @Test func realtimeSocketURLBuildsANormalModelName() {
        let url = RealtimeVoiceClient.realtimeSocketURL(forModel: "gpt-realtime-mini")
        #expect(url?.absoluteString == "wss://api.openai.com/v1/realtime?model=gpt-realtime-mini")
    }

    /// The point of this fix is that `openConnection` never force-unwraps a URL built from the
    /// backend-supplied `model` field. On the Foundation version this app ships against, `URL(string:)`
    /// percent-encodes almost anything into a valid URL rather than returning nil for this fixed
    /// "wss://api.openai.com/v1/realtime?model=<value>" shape (confirmed by hand for spaces,
    /// newlines, NUL, RTL override characters, emoji, and stray query-syntax characters) — so the
    /// "gpt-realtime" fallback branch is not reachable through this exact string today. The
    /// guard-based fix is still correct: it removes the crash risk outright rather than depending on
    /// today's parser behavior, and this test pins down that a wide range of adversarial values a
    /// hostile or buggy backend could send all still produce a well-formed, non-nil URL.
    @Test func realtimeSocketURLNeverCrashesOnAdversarialModelNames() {
        let adversarialModelNames = [
            "",
            " ",
            "gpt realtime",
            "gpt\nrealtime",
            "gpt\u{0000}realtime",
            "\u{202E}reversed",
            "gpt&model=something-else",
            String(repeating: "x", count: 10_000)
        ]
        for modelName in adversarialModelNames {
            let url = RealtimeVoiceClient.realtimeSocketURL(forModel: modelName)
            #expect(url != nil)
            #expect(url?.scheme == "wss")
            #expect(url?.host == "api.openai.com")
        }
    }
}

/// Stands in for a `URLSessionWebSocketTask` whose handshake has not finished: until `open()` every
/// send fails with "Socket is not connected", as the first `session.update` did in issue #1.
private final class HandshakePendingSocket: RealtimeSocket, @unchecked Sendable {
    private let lock = NSLock()
    private var isOpen = false
    private var pendingReceive: (@Sendable (Result<URLSessionWebSocketTask.Message, Error>) -> Void)?
    private(set) var sentTexts: [String] = []
    private(set) var sendsBeforeOpen = 0
    private(set) var wasResumed = false
    var response: URLResponse?
    var closeReason: Data?

    func resume() { lock.withLock { wasResumed = true } }

    func send(_ message: URLSessionWebSocketTask.Message, completionHandler: @escaping @Sendable (Error?) -> Void) {
        let accepted: Bool = lock.withLock {
            guard isOpen else {
                sendsBeforeOpen += 1
                return false
            }
            if case .string(let text) = message { sentTexts.append(text) }
            return true
        }
        completionHandler(accepted ? nil : NSError(domain: NSPOSIXErrorDomain, code: Int(ENOTCONN)))
    }

    func receive(completionHandler: @escaping @Sendable (Result<URLSessionWebSocketTask.Message, Error>) -> Void) {
        lock.withLock { pendingReceive = completionHandler }
    }

    func sendPing(pongReceiveHandler: @escaping @Sendable (Error?) -> Void) { pongReceiveHandler(nil) }

    func cancel(with closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?) {}

    var isWaitingToReceive: Bool { lock.withLock { pendingReceive != nil } }

    /// The handshake completes and the server's first event arrives.
    func open(firstEvent: String) {
        let receive: (@Sendable (Result<URLSessionWebSocketTask.Message, Error>) -> Void)? = lock.withLock {
            isOpen = true
            defer { pendingReceive = nil }
            return pendingReceive
        }
        receive?(.success(.string(firstEvent)))
    }

    /// The handshake is refused with `statusCode`.
    func refuse(statusCode: Int) {
        response = HTTPURLResponse(url: URL(string: "wss://api.openai.com/v1/realtime")!, statusCode: statusCode, httpVersion: nil, headerFields: nil)
        let receive: (@Sendable (Result<URLSessionWebSocketTask.Message, Error>) -> Void)? = lock.withLock {
            defer { pendingReceive = nil }
            return pendingReceive
        }
        receive?(.failure(URLError(.badServerResponse)))
    }
}

/// Issue #1: nothing may go out on the realtime socket before the server's first event.
@MainActor
struct RealtimeSessionHandshakeOrderTests {
    private static func waitUntil(_ condition: () -> Bool) async {
        for _ in 0..<1_000 where !condition() {
            try? await Task.sleep(nanoseconds: 2_000_000)
        }
    }

    @Test func sessionUpdateWaitsForTheServersFirstEvent() async throws {
        let client = RealtimeVoiceClient()
        let socket = HandshakePendingSocket()
        let session = Task { @MainActor in
            try await client.startSession(on: socket, model: "gpt-realtime", mode: .pushToTalk, instructions: "be brief", whileWaitingForHandshake: {})
        }
        await Self.waitUntil { socket.isWaitingToReceive }
        #expect(socket.wasResumed)
        #expect(!client.isConnected)

        socket.open(firstEvent: #"{"type":"session.created"}"#)
        try await session.value

        #expect(socket.sendsBeforeOpen == 0)
        let firstMessage = try #require(socket.sentTexts.first)
        let firstEvent = try #require(try JSONSerialization.jsonObject(with: Data(firstMessage.utf8)) as? [String: Any])
        #expect(firstEvent["type"] as? String == "session.update")
        #expect(client.isConnected)
        client.disconnect(reason: nil)
    }

    @Test func aRefusedHandshakeSaysWhatWasRefusedAndSendsNothing() async throws {
        let client = RealtimeVoiceClient()
        let socket = HandshakePendingSocket()
        let session = Task { @MainActor in
            try await client.startSession(on: socket, model: "gpt-realtime", mode: .alwaysOn, instructions: "be brief", whileWaitingForHandshake: {})
        }
        await Self.waitUntil { socket.isWaitingToReceive }
        socket.refuse(statusCode: 401)

        await #expect {
            try await session.value
        } throws: { error in
            error.localizedDescription.contains("401")
        }
        #expect(socket.sendsBeforeOpen == 0)
        #expect(socket.sentTexts.isEmpty)
        #expect(!client.isConnected)
    }

    private static func toolNames(inSessionUpdate message: String) throws -> [String] {
        let event = try #require(try JSONSerialization.jsonObject(with: Data(message.utf8)) as? [String: Any])
        let session = try #require(event["session"] as? [String: Any])
        let tools = try #require(session["tools"] as? [[String: Any]])
        return tools.compactMap { $0["name"] as? String }
    }

    private static func connectedClient(mode: RealtimeVoiceClient.TurnMode) async throws -> (RealtimeVoiceClient, HandshakePendingSocket) {
        let client = RealtimeVoiceClient()
        let socket = HandshakePendingSocket()
        let session = Task { @MainActor in
            try await client.startSession(on: socket, model: "gpt-realtime", mode: mode, instructions: "be brief", whileWaitingForHandshake: {})
        }
        await waitUntil { socket.isWaitingToReceive }
        socket.open(firstEvent: #"{"type":"session.created"}"#)
        try await session.value
        return (client, socket)
    }

    /// Hands-free ends by voice ("that's all, thanks"): the model gets a tool for it, but only while
    /// hands-free is on. Push-to-talk listens only while the keys are held, so it has nothing to end.
    @Test func theSignOffToolIsOfferedOnlyInHandsFree() async throws {
        let (handsFreeClient, handsFreeSocket) = try await Self.connectedClient(mode: .alwaysOn)
        #expect(try Self.toolNames(inSessionUpdate: try #require(handsFreeSocket.sentTexts.first)).contains(RealtimeVoiceClient.stopListeningToolName))
        handsFreeClient.disconnect(reason: nil)

        let (pushToTalkClient, pushToTalkSocket) = try await Self.connectedClient(mode: .pushToTalk)
        #expect(!(try Self.toolNames(inSessionUpdate: try #require(pushToTalkSocket.sentTexts.first)).contains(RealtimeVoiceClient.stopListeningToolName)))
        pushToTalkClient.disconnect(reason: nil)
    }

    /// The sign-off is answered with a goodbye, and hands-free ends only once that response is done,
    /// so closing the session never cuts the goodbye off.
    @Test func aSignOffEndsHandsFreeAfterTheGoodbye() async throws {
        let (client, socket) = try await Self.connectedClient(mode: .alwaysOn)
        var stopRequests = 0
        client.onStopListeningRequested = { stopRequests += 1 }

        await Self.waitUntil { socket.isWaitingToReceive }
        socket.open(firstEvent: #"{"type":"response.function_call_arguments.done","name":"stop_listening","call_id":"call_1","response_id":"resp_1","arguments":"{}"}"#)
        await Self.waitUntil { socket.sentTexts.contains { $0.contains("function_call_output") } }
        #expect(socket.sentTexts.contains { $0.contains("function_call_output") && $0.contains("call_1") })
        #expect(stopRequests == 0)

        await Self.waitUntil { socket.isWaitingToReceive }
        socket.open(firstEvent: #"{"type":"response.done","response":{"status":"completed"}}"#)
        await Self.waitUntil { stopRequests > 0 }
        #expect(stopRequests == 1)
        client.disconnect(reason: nil)
    }

    private static let screenFixture = RealtimeScreenContext(
        jpeg: Data(), caption: "Screenshot of the user's screen (1280×720 px).",
        capture: CompanionScreenCapture(
            imageData: Data(), label: "test", isCursorScreen: true,
            displayWidthInPoints: 1920, displayHeightInPoints: 1080,
            displayFrame: CGRect(x: 0, y: 0, width: 1920, height: 1080),
            screenshotWidthInPixels: 1280, screenshotHeightInPixels: 720))

    @MainActor private final class FrontApp { var pid: pid_t = 1 }

    /// Delivers one server event once the client is waiting for it.
    private static func deliver(_ event: String, on socket: HandshakePendingSocket) async {
        await waitUntil { socket.isWaitingToReceive }
        socket.open(firstEvent: event)
    }

    /// An action that changed the screen (a page opened) attaches a fresh screenshot before its
    /// output, so the model describes what it sees now, not what it saw when the person spoke.
    @Test func anActionThatLooksAfterwardsAttachesTheScreenBeforeItsOutput() async throws {
        let (client, socket) = try await Self.connectedClient(mode: .alwaysOn)
        client.screenContextProvider = { Self.screenFixture }
        // The browser comes to the front once the page is opened.
        let front = FrontApp()
        client.frontAppProvider = { front.pid }
        client.onMacAction = { _ in
            front.pid = 2
            return .openedURL("https://example.com")
        }
        await Self.deliver(#"{"type":"response.function_call_arguments.done","name":"open_url","call_id":"c1","response_id":"r1","arguments":"{\"url\":\"https://example.com\"}"}"#, on: socket)
        await Self.waitUntil { socket.sentTexts.contains { $0.contains("function_call_output") } }
        let screenIndex = try #require(socket.sentTexts.firstIndex { $0.contains("[Screen context") })
        let outputIndex = try #require(socket.sentTexts.firstIndex { $0.contains("function_call_output") })
        #expect(screenIndex < outputIndex)
        #expect(socket.sentTexts[outputIndex].contains("Opened it in your browser."))
        client.disconnect(reason: nil)
    }

    /// A barge-in while the action still waits: the output goes out (it is in the conversation
    /// for the next turn) but nothing may follow the cancel.
    @Test func aLateOutputAfterACancelledResponseNeverAsksForAContinuation() async throws {
        let (client, socket) = try await Self.connectedClient(mode: .alwaysOn)
        client.screenContextProvider = {
            try? await Task.sleep(nanoseconds: 300_000_000)
            return Self.screenFixture
        }
        let front = FrontApp()
        client.frontAppProvider = { front.pid }
        client.onMacAction = { _ in
            front.pid = 2
            return .openedURL("https://example.com")
        }
        await Self.deliver(#"{"type":"response.created","response":{"id":"r1"}}"#, on: socket)
        await Self.deliver(#"{"type":"response.function_call_arguments.done","name":"open_url","call_id":"c1","response_id":"r1","arguments":"{\"url\":\"https://example.com\"}"}"#, on: socket)
        // The person starts talking again before the page settled.
        await Self.deliver(#"{"type":"input_audio_buffer.speech_started"}"#, on: socket)
        await Self.waitUntil { socket.sentTexts.contains { $0.contains("function_call_output") } }
        let cancelIndex = try #require(socket.sentTexts.firstIndex { $0.contains("response.cancel") })
        let outputIndex = try #require(socket.sentTexts.firstIndex { $0.contains("function_call_output") })
        #expect(cancelIndex < outputIndex)
        #expect(!socket.sentTexts[outputIndex...].contains { $0.contains("response.create") })
        client.disconnect(reason: nil)
    }

    /// The spec's two phases: wait for another app to come to the front (1.5 s at most), then a
    /// second for the page to paint; four seconds in all at most.
    @Test func theSettleWaitIsAFrontChangePlusASecondAndFourSecondsAtMost() {
        #expect(RealtimeVoiceClient.settleDelay(frontChangedAt: nil, elapsed: 0.5) == 0.25)
        #expect(RealtimeVoiceClient.settleDelay(frontChangedAt: nil, elapsed: 2.4) == 0.25)
        #expect(RealtimeVoiceClient.settleDelay(frontChangedAt: nil, elapsed: 2.5) == nil)
        #expect(RealtimeVoiceClient.settleDelay(frontChangedAt: 0.5, elapsed: 1.4) == 0.25)
        #expect(RealtimeVoiceClient.settleDelay(frontChangedAt: 0.5, elapsed: 1.5) == nil)
        #expect(RealtimeVoiceClient.settleDelay(frontChangedAt: 3.8, elapsed: 4.0) == nil)
    }

    /// The common timing: the model's response finishes while the action still waits. Esc then
    /// must stop the late continuation, or Clicky reads the inbox the person just cancelled.
    @Test func escAfterTheResponseFinishedStopsTheLateContinuation() async throws {
        let (client, socket) = try await Self.connectedClient(mode: .alwaysOn)
        client.screenContextProvider = {
            try? await Task.sleep(nanoseconds: 300_000_000)
            return Self.screenFixture
        }
        let front = FrontApp()
        client.frontAppProvider = { front.pid }
        client.onMacAction = { _ in
            front.pid = 2
            return .openedURL("https://example.com")
        }
        await Self.deliver(#"{"type":"response.created","response":{"id":"r1"}}"#, on: socket)
        await Self.deliver(#"{"type":"response.function_call_arguments.done","name":"open_url","call_id":"c1","response_id":"r1","arguments":"{\"url\":\"https://example.com\"}"}"#, on: socket)
        await Self.deliver(#"{"type":"response.done","response":{"id":"r1","status":"completed"}}"#, on: socket)
        client.stopSpeaking()
        await Self.waitUntil { socket.sentTexts.contains { $0.contains("function_call_output") } }
        let outputIndex = try #require(socket.sentTexts.firstIndex { $0.contains("function_call_output") })
        #expect(!socket.sentTexts[outputIndex...].contains { $0.contains("response.create") })
        client.disconnect(reason: nil)
    }

    @Test func speechAfterTheResponseFinishedStopsTheLateContinuation() async throws {
        let (client, socket) = try await Self.connectedClient(mode: .alwaysOn)
        client.screenContextProvider = {
            try? await Task.sleep(nanoseconds: 300_000_000)
            return Self.screenFixture
        }
        let front = FrontApp()
        client.frontAppProvider = { front.pid }
        client.onMacAction = { _ in
            front.pid = 2
            return .openedURL("https://example.com")
        }
        await Self.deliver(#"{"type":"response.created","response":{"id":"r1"}}"#, on: socket)
        await Self.deliver(#"{"type":"response.function_call_arguments.done","name":"open_url","call_id":"c1","response_id":"r1","arguments":"{\"url\":\"https://example.com\"}"}"#, on: socket)
        await Self.deliver(#"{"type":"response.done","response":{"id":"r1","status":"completed"}}"#, on: socket)
        await Self.deliver(#"{"type":"input_audio_buffer.speech_started"}"#, on: socket)
        await Self.waitUntil { socket.sentTexts.contains { $0.contains("function_call_output") } }
        let outputIndex = try #require(socket.sentTexts.firstIndex { $0.contains("function_call_output") })
        #expect(!socket.sentTexts[outputIndex...].contains { $0.contains("response.create") })
        client.disconnect(reason: nil)
    }

    @Test func aTerminalLaunchIsNamedInsteadOfAnUnexplainedZero() {
        let line = RealtimeVoiceClient.microphoneAuthorizationDescription(captureAuthorization: .notDetermined, recordPermission: .granted)
        #expect(line.contains("not determined for this process"))
        #expect(line.contains("record permission: granted"))
        #expect(!line.contains("3 = authorized"))
    }
}
