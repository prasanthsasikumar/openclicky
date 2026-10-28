//
//  EmailActions.swift
//  OpenClicky
//
//  "Check my email" and "draft an email to Ana saying…", in Apple Mail or Gmail. Apple Mail is
//  scripted (unread list, a new message left open); Gmail is opened in the browser (the inbox, a
//  pre-filled compose window). Nothing here sends: there is no send, and never will be by voice.
//

import AppKit
import Foundation

nonisolated enum EmailApp: String {
    case mail
    case gmail

    /// Which app a check or draft goes to: the one the person named, else the setting, else
    /// Apple Mail when it has accounts on this Mac and Gmail otherwise (every Mac has Mail as its
    /// mailto handler out of the box, set up or not, so the handler says nothing).
    static func choose(requested: String?, setting: String?, mailHasAccounts: Bool) -> EmailApp {
        if let requested, let app = EmailApp(rawValue: requested.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()) { return app }
        if let setting, let app = EmailApp(rawValue: setting.lowercased()) { return app }
        return mailHasAccounts ? .mail : .gmail
    }

    /// Whether Mail has been set up here: its accounts file exists. Read from disk, so no
    /// Automation prompt for an app the person may never use.
    static func mailHasAccounts(home: URL = FileManager.default.homeDirectoryForCurrentUser) -> Bool {
        let mailFolder = home.appendingPathComponent("Library/Mail")
        guard let versions = try? FileManager.default.contentsOfDirectory(atPath: mailFolder.path) else { return false }
        return versions.contains { version in
            version.hasPrefix("V") && FileManager.default.fileExists(atPath: mailFolder.appendingPathComponent("\(version)/MailData/Accounts.plist").path)
        }
    }
}

nonisolated struct UnreadMail: Equatable {
    let sender: String
    let subject: String
}

nonisolated enum MailScriptError: Error, Equatable {
    /// Apple's -1743: the person said no to "OpenClicky wants to control Mail", or was never asked.
    case notAllowed
    case timedOut
    case failed(String)
}

/// The AppleScript sources for Mail, as strings, so they can be checked without Mail.
nonisolated enum MailScripts {
    private static let fieldSeparator = "(ASCII character 30)"
    private static let recordSeparator = "(ASCII character 29)"

    /// Sender and subject of the newest `limit` unread inbox messages, fields joined by ASCII 30
    /// and records by ASCII 29. Bodies are never fetched. Capped at five seconds: Mail answers a
    /// huge inbox slowly, and the receive loop must not wait.
    static func unreadInboxSource(limit: Int = 10) -> String {
        """
        with timeout of 5 seconds
        tell application "Mail"
            set unreadMessages to (messages of inbox whose read status is false)
            set messageCount to count of unreadMessages
            if messageCount > \(limit) then set messageCount to \(limit)
            set outputText to ""
            repeat with messageIndex from 1 to messageCount
                set theMessage to item messageIndex of unreadMessages
                if messageIndex > 1 then set outputText to outputText & \(recordSeparator)
                set outputText to outputText & (sender of theMessage) & \(fieldSeparator) & (subject of theMessage)
            end repeat
            return outputText
        end tell
        end timeout
        """
    }

    static func parseUnread(_ reply: String) -> [UnreadMail] {
        reply.components(separatedBy: String(UnicodeScalar(29))).compactMap { record in
            let fields = record.components(separatedBy: String(UnicodeScalar(30)))
            guard fields.count == 2 else { return nil }
            return UnreadMail(sender: fields[0], subject: fields[1])
        }
    }

    /// A new message, open and in front, with the recipient, subject and body filled in. An
    /// address goes in as an address; a name goes in as a name, and Mail offers its own contact
    /// matches when the person clicks the field (OpenClicky never reads Contacts).
    static func draftSource(to recipient: String, subject: String, body: String) -> String {
        let recipientProperties = recipient.contains("@") ? "{address:\"\(escaped(recipient))\"}" : "{name:\"\(escaped(recipient))\"}"
        return """
        tell application "Mail"
            set newMessage to make new outgoing message with properties {subject:"\(escaped(subject))", content:"\(escaped(body))", visible:true}
            tell newMessage to make new to recipient at end of to recipients with properties \(recipientProperties)
            activate
        end tell
        """
    }

    /// Backslashes and double quotes are the two characters that would end an AppleScript string.
    static func escaped(_ text: String) -> String {
        text.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
    }
}

nonisolated enum GmailURLs {
    static let inbox = URL(string: "https://mail.google.com/mail/u/0/#inbox")!

    /// Gmail's compose window, pre-filled. Everything is percent-encoded by hand: a bare `+` is
    /// a space to Gmail, `&` and `#` would cut the query, a newline must travel as %0A.
    static func compose(to recipient: String, subject: String, body: String) -> URL {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~")
        let encode = { (text: String) in text.addingPercentEncoding(withAllowedCharacters: allowed) ?? "" }
        return URL(string: "https://mail.google.com/mail/?view=cm&fs=1&to=\(encode(recipient))&su=\(encode(subject))&body=\(encode(body))")!
    }
}

/// Runs a Mail script on a utility queue, one at a time, and names the way it failed.
nonisolated enum MailAppleScriptRunner {
    private static let queue = DispatchQueue(label: "openclicky.mail.applescript", qos: .utility)
    private static let notPermittedError = -1743
    private static let timedOutError = -1712

    static func run(_ source: String) async -> Result<String, MailScriptError> {
        await withCheckedContinuation { continuation in
            queue.async {
                var errorInfo: NSDictionary?
                let result = NSAppleScript(source: source)?.executeAndReturnError(&errorInfo)
                if let result {
                    continuation.resume(returning: .success(result.stringValue ?? ""))
                    return
                }
                let errorNumber = (errorInfo?[NSAppleScript.errorNumber] as? NSNumber)?.intValue
                let message = errorInfo?[NSAppleScript.errorMessage] as? String ?? "unknown"
                AppLog.append("mail script failed (\(errorNumber ?? 0)): \(message)")
                switch errorNumber {
                case notPermittedError: continuation.resume(returning: .failure(.notAllowed))
                case timedOutError: continuation.resume(returning: .failure(.timedOut))
                default: continuation.resume(returning: .failure(.failed(message)))
                }
            }
        }
    }
}
