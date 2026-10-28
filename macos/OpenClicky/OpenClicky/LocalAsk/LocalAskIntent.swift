//
//  LocalAskIntent.swift
//  OpenClicky
//
//  What the on-device model understood, as plain values, and how that becomes one of the fast
//  lane's tool calls. The model fills in a `LocalAskIntent` (through guided generation in
//  `AppleLocalAskModel`); `LocalAskIntentMapper` is where the safety sits: anything not named, a
//  pronoun, a website that isn't one, a volume outside 0–100, goes to the cloud instead of being
//  guessed at, and every quick action is run through `MacAction.parse` so the fast lane's own
//  validation applies before anything is performed.
//

import Foundation

/// The model's reading of the request. Built by hand in tests; the @Generable type converts to it.
struct LocalAskIntent: Equatable {
    enum Kind: String, CaseIterable {
        case openApp, openWebsite, createFolder, showFolder, setVolume, playback, rewriteText, needsCloud
    }
    var kind: Kind
    /// The app, folder or website named, as said; empty when none.
    var name: String = ""
    /// desktop, downloads, documents or home when a folder's place was said; else empty.
    var location: String = ""
    /// 0…100 when a level was said; -1 otherwise.
    var volumeLevel: Int = -1
    /// play, pause, next or previous; else empty.
    var playback: String = ""
    /// For rewriteText: the instruction in a few words ("shorter", "warmer", "fix the grammar").
    var rewriteInstruction: String = ""
}

enum LocalAskMapping: Equatable {
    case quickAction(toolName: String, arguments: [String: String])
    case rewrite(instruction: String)
    case cloud(reason: String)
}

enum LocalAskIntentMapper {

    /// Words that name nothing: "open it" must never open an app called "it".
    static let pronouns: Set<String> = ["it", "that", "this", "them", "the app", "that one", "this one", "the thing"]

    /// The most words an app name may have; anything longer is a description, not a name.
    static let maximumAppNameWords = 4

    nonisolated static func map(_ intent: LocalAskIntent, hasWritingTarget: Bool, resolveApp: (String) -> String?) -> LocalAskMapping {
        let mapping = unvalidatedMapping(intent, hasWritingTarget: hasWritingTarget, resolveApp: resolveApp)
        // The fast lane's own validation is the last word: a mapping it would refuse goes to the
        // cloud rather than being spoken as a failure.
        if case .quickAction(let toolName, let arguments) = mapping {
            guard case .action = MacAction.parse(toolName: toolName, arguments: arguments) else {
                return .cloud(reason: "fast lane refused")
            }
        }
        return mapping
    }

    private nonisolated static func unvalidatedMapping(_ intent: LocalAskIntent, hasWritingTarget: Bool, resolveApp: (String) -> String?) -> LocalAskMapping {
        let name = intent.name.trimmingCharacters(in: .whitespacesAndNewlines)
        switch intent.kind {
        case .openApp:
            let lowered = name.lowercased()
            guard !name.isEmpty, !pronouns.contains(lowered), lowered.split(separator: " ").count <= maximumAppNameWords else {
                return .cloud(reason: "no app named")
            }
            guard let installedName = resolveApp(name) else { return .cloud(reason: "no app match") }
            return .quickAction(toolName: "open_app", arguments: ["name": installedName])

        case .openWebsite:
            guard let url = websiteURL(spoken: name) else { return .cloud(reason: "not a website") }
            return .quickAction(toolName: "open_url", arguments: ["url": url])

        case .createFolder, .showFolder:
            guard let itemName = MacActionValidation.folderName(name) else { return .cloud(reason: "no item named") }
            var arguments = ["name": itemName]
            let spokenLocation = intent.location.trimmingCharacters(in: .whitespacesAndNewlines)
            if !spokenLocation.isEmpty, let location = MacActionLocation.named(spokenLocation) {
                arguments["location"] = location.rawValue
            }
            return .quickAction(toolName: intent.kind == .createFolder ? "create_folder" : "reveal_in_finder", arguments: arguments)

        case .setVolume:
            guard (0...100).contains(intent.volumeLevel) else { return .cloud(reason: "no volume level") }
            return .quickAction(toolName: "set_volume", arguments: ["level": String(intent.volumeLevel)])

        case .playback:
            switch intent.playback.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() {
            case "play", "pause", "playpause": return .quickAction(toolName: "media_control", arguments: ["action": "playpause"])
            case "next": return .quickAction(toolName: "media_control", arguments: ["action": "next"])
            case "previous": return .quickAction(toolName: "media_control", arguments: ["action": "previous"])
            default: return .cloud(reason: "no playback action")
            }

        case .rewriteText:
            guard hasWritingTarget else { return .cloud(reason: "no text to rewrite") }
            return .rewrite(instruction: intent.rewriteInstruction.trimmingCharacters(in: .whitespacesAndNewlines))

        case .needsCloud:
            return .cloud(reason: "model declined")
        }
    }

    /// "youtube" → https://youtube.com, "bbc.co.uk" → https://bbc.co.uk, a full http(s) URL as it
    /// is; nil for anything with spaces or that is not shaped like a host ("the news").
    nonisolated static func websiteURL(spoken: String) -> String? {
        let trimmed = spoken.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !trimmed.isEmpty, !trimmed.contains(" ") else { return nil }
        let candidate: String
        if trimmed.hasPrefix("http://") || trimmed.hasPrefix("https://") {
            candidate = trimmed
        } else if trimmed.contains(".") {
            // A host with a dot: letters or digits on both sides of every dot.
            let parts = trimmed.split(separator: "/", maxSplits: 1, omittingEmptySubsequences: false)
            let host = String(parts[0])
            let labels = host.split(separator: ".", omittingEmptySubsequences: false)
            guard labels.count >= 2, labels.allSatisfy({ !$0.isEmpty && $0.allSatisfy { $0.isLetter || $0.isNumber || $0 == "-" } }) else { return nil }
            candidate = "https://" + trimmed
        } else {
            // One bare word: a well-known site said by name ("youtube", "gmail").
            guard (2...40).contains(trimmed.count), trimmed.allSatisfy({ $0.isLetter || $0.isNumber || $0 == "-" }) else { return nil }
            candidate = "https://" + trimmed + ".com"
        }
        guard let url = MacActionValidation.webURL(candidate) else { return nil }
        return url.absoluteString
    }
}
