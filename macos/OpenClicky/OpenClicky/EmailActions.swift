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

    /// Mail's sender is "Name <address>"; the name is what gets said, never the address.
    var spokenSender: String {
        guard let bracket = sender.firstIndex(of: "<") else { return sender }
        let name = sender[..<bracket].trimmingCharacters(in: .whitespaces)
        return name.isEmpty ? sender : name
    }
}

nonisolated enum MailScriptError: Error, Equatable {
    /// Apple's -1743: the person said no to "OpenClicky wants to control Mail", or was never asked.
    case notAllowed
    case timedOut
    /// The message was made and is open, but Mail refused the recipient.
    case recipientNotFilled
    case failed(String)
}

/// The AppleScript sources for Mail, as strings, so they can be checked without Mail.
nonisolated enum MailScripts {
    private static let fieldSeparator = "(ASCII character 30)"
    private static let recordSeparator = "(ASCII character 29)"

    /// Senders and subjects of the first `limit` unread inbox messages: the senders joined by
    /// ASCII 30, then ASCII 29, then the subjects joined by ASCII 30 (the tab locator's pattern).
    /// Three Apple events in all, each under the five-second cap, instead of two per message — the
    /// timeout applies per event, so a loop was never really capped. Bodies are never fetched.
    static func unreadInboxSource(limit: Int = 10) -> String {
        """
        with timeout of 5 seconds
        tell application "Mail"
            set unreadMessages to (messages of inbox whose read status is false)
            set messageCount to count of unreadMessages
            if messageCount is 0 then return ""
            if messageCount > \(limit) then set messageCount to \(limit)
            set recentMessages to items 1 thru messageCount of unreadMessages
            set senderList to sender of recentMessages
            set subjectList to subject of recentMessages
        end tell
        end timeout
        set savedDelimiters to AppleScript's text item delimiters
        set AppleScript's text item delimiters to \(fieldSeparator)
        set outputText to (senderList as text) & \(recordSeparator) & (subjectList as text)
        set AppleScript's text item delimiters to savedDelimiters
        return outputText
        """
    }

    static func parseUnread(_ reply: String) -> [UnreadMail] {
        guard !reply.isEmpty else { return [] }
        let halves = reply.components(separatedBy: String(UnicodeScalar(29)))
        guard halves.count == 2 else { return [] }
        let senders = halves[0].components(separatedBy: String(UnicodeScalar(30)))
        let subjects = halves[1].components(separatedBy: String(UnicodeScalar(30)))
        return senders.enumerated().map { index, sender in
            UnreadMail(sender: sender, subject: index < subjects.count ? subjects[index] : "")
        }
    }

    /// A new message, open and in front, with the recipient, subject and body filled in. An
    /// address goes in as an address; a name goes in as a name, and Mail offers its own contact
    /// matches when the person clicks the field (OpenClicky never reads Contacts).
    /// Capped at five seconds (Mail launching, a first-use Automation prompt). A recipient Mail
    /// refuses does not throw the open message away: the script answers "no-recipient" instead.
    static func draftSource(to recipient: String, subject: String, body: String) -> String {
        let recipientProperties = recipient.contains("@") ? "{address:\"\(escaped(recipient))\"}" : "{name:\"\(escaped(recipient))\"}"
        return """
        with timeout of 5 seconds
        tell application "Mail"
            set newMessage to make new outgoing message with properties {subject:"\(escaped(subject))", content:"\(escaped(body))", visible:true}
            activate
            try
                tell newMessage to make new to recipient at end of to recipients with properties \(recipientProperties)
            on error
                return "no-recipient"
            end try
            return "ok"
        end tell
        end timeout
        """
    }

    /// Backslashes and double quotes are the two characters that would end an AppleScript string.
    static func escaped(_ text: String) -> String {
        text.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
    }
}

nonisolated enum GmailURLs {
    static let inbox = URL(string: "https://mail.google.com/mail/u/0/#inbox")!

    /// Gmail's compose window, pre-filled. The ASCII specials are percent-encoded by hand: a bare
    /// `+` is a space to Gmail, `&` and `#` would cut the query, a newline must travel as %0A
    /// (letters outside ASCII stay, and `URL(string:)` encodes those itself).
    static func compose(to recipient: String, subject: String, body: String) -> URL {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~")
        let encode = { (text: String) in text.addingPercentEncoding(withAllowedCharacters: allowed) ?? "" }
        return URL(string: "https://mail.google.com/mail/?view=cm&fs=1&to=\(encode(recipient))&su=\(encode(subject))&body=\(encode(body))")!
    }
}

/// Runs a Mail script on the app's one AppleScript queue and names the way it failed.
nonisolated enum MailAppleScriptRunner {
    private static var queue: DispatchQueue { AppleScriptQueue.shared }
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
                // Only the number: an AppleScript message can quote the expression that failed,
                // which here would be the body or the recipient.
                AppLog.append("mail script failed (\(errorNumber ?? 0))")
                switch errorNumber {
                case notPermittedError: continuation.resume(returning: .failure(.notAllowed))
                case timedOutError: continuation.resume(returning: .failure(.timedOut))
                default: continuation.resume(returning: .failure(.failed(message)))
                }
            }
        }
    }
}
