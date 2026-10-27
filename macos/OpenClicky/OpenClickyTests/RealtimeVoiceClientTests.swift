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

    @Test func aTerminalLaunchIsNamedInsteadOfAnUnexplainedZero() {
        let line = RealtimeVoiceClient.microphoneAuthorizationDescription(captureAuthorization: .notDetermined, recordPermission: .granted)
        #expect(line.contains("not determined for this process"))
        #expect(line.contains("record permission: granted"))
        #expect(!line.contains("3 = authorized"))
    }
}
