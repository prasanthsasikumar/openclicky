//
//  AccountTakePolisher.swift
//  OpenClicky
//
//  Polish and Hey Clicky edits for signed-in accounts: the app says what to do and sends the
//  words; the backend picks the model (a cheap one) and bills the grant. A limit throws a typed
//  error so the take still pastes with local cleanup.
//

import Foundation

enum AccountPolishError: Error, Equatable {
    case limit(AccountLimitError)
    case unavailable(Int)
}

extension AccountPolishError: LocalizedError {
    /// A plain sentence for the orb and Hey Clicky edits, never "error 0".
    var errorDescription: String? {
        if case .limit(let limit) = self, !limit.message.isEmpty { return limit.message }
        if case .unavailable(503) = self { return AccountLimitError.serviceTroubleMessage }
        return "couldn't reach openclicky right now."
    }
}

struct AccountTakePolisher: TakePolisher {
    var purpose: String = "polish"
    var send: (URLRequest) async throws -> (Data, URLResponse) = { try await URLSession.shared.data(for: $0) }

    var displayName: String { "OpenClicky" }

    func polish(system: String, user: String) async throws -> String {
        guard let url = URL(string: "\(OpenClickyConfiguration.backendBaseURL)/v1/polish") else { throw AccountPolishError.unavailable(0) }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 20
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        OpenClickyConfiguration.authorize(&request)
        request.httpBody = try JSONSerialization.data(withJSONObject: ["purpose": purpose, "system": system, "text": user])
        let (data, response) = try await send(request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if let limit = AccountLimitError.from(status: status, body: data) { throw AccountPolishError.limit(limit) }
        guard (200..<300).contains(status),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let text = json["text"] as? String else { throw AccountPolishError.unavailable(status) }
        return text
    }
}
