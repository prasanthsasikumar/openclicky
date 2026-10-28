//
//  EmailActionsTests.swift
//  OpenClickyTests
//
//  "Check my email" and "draft an email": which app, the Mail scripts as strings, the Gmail URLs,
//  the caps on what the model may hand over. The AppleScript runs are not tested here.
//

import Foundation
import Testing
@testable import OpenClicky

struct EmailActionsTests {
    @Test func theNamedAppWinsThenTheSettingThenWhetherMailIsSetUp() {
        #expect(EmailApp.choose(requested: "gmail", setting: "mail", mailHasAccounts: true) == .gmail)
        #expect(EmailApp.choose(requested: nil, setting: "gmail", mailHasAccounts: true) == .gmail)
        #expect(EmailApp.choose(requested: nil, setting: nil, mailHasAccounts: true) == .mail)
        #expect(EmailApp.choose(requested: nil, setting: nil, mailHasAccounts: false) == .gmail)
        #expect(EmailApp.choose(requested: "outlook", setting: nil, mailHasAccounts: false) == .gmail)
        #expect(EmailApp.choose(requested: "Mail", setting: nil, mailHasAccounts: false) == .mail)
    }

    @Test func mailCountsAsSetUpWhenItHasAnAccountsFile() throws {
        let home = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("email-home-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: home) }
        #expect(!EmailApp.mailHasAccounts(home: home))
        let mailData = home.appendingPathComponent("Library/Mail/V10/MailData")
        try FileManager.default.createDirectory(at: mailData, withIntermediateDirectories: true)
        #expect(!EmailApp.mailHasAccounts(home: home))
        try Data().write(to: mailData.appendingPathComponent("Accounts.plist"))
        #expect(EmailApp.mailHasAccounts(home: home))
    }

    @Test func theDraftScriptEscapesQuotesAndUsesAnAddressOrAName() {
        let withAddress = MailScripts.draftSource(to: "ana@example.com", subject: "Hi \"there\"", body: "back\\slash")
        #expect(withAddress.contains("subject:\"Hi \\\"there\\\"\""))
        #expect(withAddress.contains("content:\"back\\\\slash\""))
        #expect(withAddress.contains("{address:\"ana@example.com\"}"))
        #expect(withAddress.contains("visible:true"))
        #expect(!withAddress.lowercased().contains("send"))
        let withName = MailScripts.draftSource(to: "Ana", subject: "", body: "x")
        #expect(withName.contains("{name:\"Ana\"}"))
    }

    @Test func theUnreadReplyParsesIntoSenderAndSubject() {
        let reply = "Ana <ana@x.com>\u{1E}Invoice\u{1D}GitHub\u{1E}[repo] PR #4"
        #expect(MailScripts.parseUnread(reply) == [UnreadMail(sender: "Ana <ana@x.com>", subject: "Invoice"), UnreadMail(sender: "GitHub", subject: "[repo] PR #4")])
        #expect(MailScripts.parseUnread("") == [])
        #expect(MailScripts.unreadInboxSource(limit: 10).contains("read status is false"))
        #expect(MailScripts.unreadInboxSource(limit: 10).contains("with timeout of 5 seconds"))
    }

    @Test func gmailComposeEncodesEverythingGmailWouldOtherwiseMisread() {
        let url = GmailURLs.compose(to: "ana+work@example.com", subject: "Q&A #2", body: "line one\nline two")
        #expect(url.absoluteString == "https://mail.google.com/mail/?view=cm&fs=1&to=ana%2Bwork%40example.com&su=Q%26A%20%232&body=line%20one%0Aline%20two")
        #expect(GmailURLs.inbox.absoluteString == "https://mail.google.com/mail/u/0/#inbox")
    }

    @Test func recipientsSubjectsAndBodiesAreCapped() {
        #expect(MacActionValidation.recipient("<script>") == nil)
        #expect(MacActionValidation.recipient(" Ana ") == "Ana")
        #expect(MacActionValidation.recipient("") == nil)
        #expect(MacActionValidation.recipient(String(repeating: "a", count: 81)) == nil)
        #expect(MacActionValidation.emailBody(String(repeating: "a", count: 2_500)).count == 2_000)
        #expect(MacActionValidation.emailSubject(String(repeating: "a", count: 250)).count == 200)
    }

    @Test func theUnreadSentenceNamesAtMostThreeAndTheLogNamesNobody() {
        let four = (1...4).map { UnreadMail(sender: "S\($0)", subject: "T\($0)") }
        #expect(MacActionOutcome.unreadMail(four).spokenSentence == "4 unread: S1 about T1, S2 about T2, S3 about T3, and 1 more.")
        #expect(MacActionOutcome.unreadMail([UnreadMail(sender: "Ana", subject: "Hi")]).spokenSentence == "1 unread: Ana about Hi.")
        #expect(MacActionOutcome.unreadMail([]).spokenSentence == "No unread email in Mail.")
        #expect(MacActionOutcome.unreadMail(four).toolOutput.contains("S4 — T4"))
        #expect(MacActionOutcome.unreadMail(four).logDescription == "check_email: 4 unread in Mail")
        let draft = MacActionOutcome.draftedEmail(app: .mail, to: "ana@example.com", bodyLength: 240)
        #expect(!draft.logDescription.contains("ana@"))
        #expect(draft.logDescription == "draft_email: mail, 240-character body, address given")
        #expect(draft.spokenSentence.contains("ana@example.com"))
        #expect(draft.toolOutput.contains("never send"))
        #expect(!draft.thenLooksAtScreen)
        #expect(MacActionOutcome.draftedEmail(app: .gmail, to: "Ana", bodyLength: 5).thenLooksAtScreen)
        #expect(MacActionOutcome.mailUnavailable(.notAllowed).spokenSentence.contains("Automation"))
        #expect(MacActionOutcome.mailUnavailable(.timedOut).thenLooksAtScreen)
    }
}
