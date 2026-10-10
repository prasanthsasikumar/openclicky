//
//  EmailAccountForm.swift
//  OpenClicky
//
//  The one account form: an email, then — only if that email already has an account — a 6-digit
//  code from the inbox. A new email is signed in straight away and the confirmation link can be
//  clicked whenever. Used by onboarding and by the account sheet. The view owns the task doing the
//  work and cancels it when it goes away.
//

import SwiftUI

struct EmailAccountForm: View {
    let onSignedIn: () -> Void
    var onUseOwnKey: (() -> Void)? = nil
    @ObservedObject private var auth = OpenClickyAuthSession.shared
    @State private var email = ""
    @State private var code = ""
    @State private var work = AccountWorkSlot()
    /// The address a new code is being sent to, so the code step stays up while it goes.
    @State private var resendingCodeTo: String?

    static func canSubmitEmail(_ raw: String) -> Bool {
        let email = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let at = email.firstIndex(of: "@"), at != email.startIndex else { return false }
        return email[email.index(after: at)...].contains(".")
    }
    static func canSubmitCode(_ raw: String) -> Bool {
        let digits = raw.filter { !$0.isWhitespace }
        return digits.count == 6 && digits.allSatisfy(\.isNumber)
    }

    var body: some View {
        EmailAccountFormContent(state: auth.emailFlow, codeError: auth.lastErrorText, email: $email, code: $code,
                                onSubmitEmail: submitEmail, onSubmitCode: submitCode,
                                onDifferentEmail: { work.cancel(); auth.resetFlow(); code = "" }, onUseOwnKey: onUseOwnKey,
                                resendingCodeTo: resendingCodeTo, onSendNewCode: sendNewCode)
            .onAppear { auth.forgetSettledFlow() }
            .onDisappear { work.cancel() }
            .onChange(of: auth.emailFlow) { _, state in
                if case .signedIn = state {
                    ShellSettingsRevision.shared.noteChanged()
                    onSignedIn()
                }
            }
    }

    private func submitEmail() {
        guard Self.canSubmitEmail(email), !work.isRunning else { return }
        let address = email.trimmingCharacters(in: .whitespacesAndNewlines)
        work.start { await auth.start(email: address) }
    }
    /// Asks /auth/start again for the same address, which mails a fresh code.
    private func sendNewCode() {
        guard case .needsCode(let address) = auth.emailFlow, !work.isRunning else { return }
        code = ""
        resendingCodeTo = address
        work.start {
            await auth.start(email: address)
            resendingCodeTo = nil
        }
    }
    private func submitCode() {
        guard Self.canSubmitCode(code), !work.isRunning else { return }
        let digits = code
        work.start { await auth.submitCode(digits) }
    }
}

/// The form's look for one state, with no state of its own.
struct EmailAccountFormContent: View {
    let state: OpenClickyAuthSession.EmailFlowState
    /// Shown under the code field after a wrong code.
    var codeError: String? = nil
    @Binding var email: String
    @Binding var code: String
    let onSubmitEmail: () -> Void
    let onSubmitCode: () -> Void
    let onDifferentEmail: () -> Void
    var onUseOwnKey: (() -> Void)? = nil
    /// Set while a new code is on its way: the code step stays up for that address.
    var resendingCodeTo: String? = nil
    var onSendNewCode: () -> Void = {}

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            switch state {
            case .needsCode(let address), .checkingCode(let address): codeStep(address)
            case .sending where resendingCodeTo != nil: codeStep(resendingCodeTo ?? "")
            default: emailStep
            }
        }
    }

    private var isSending: Bool { state == .sending }
    private var isChecking: Bool {
        if case .checkingCode = state { return true }
        return false
    }

    private var emailStep: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                TextField("your email", text: $email)
                    .textFieldStyle(.roundedBorder).textContentType(.emailAddress)
                    .onSubmit(onSubmitEmail)
                Button(isSending ? "one moment…" : "continue", action: onSubmitEmail)
                    .buttonStyle(PaperPillButtonStyle(prominent: true))
                    .keyboardShortcut(.defaultAction)
                    .disabled(isSending || !EmailAccountForm.canSubmitEmail(email))
            }
            Text("no password — we'll send a link to confirm it's you.")
                .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
            if case .failed(let message) = state {
                Text(message).font(Paper.caption).foregroundStyle(Paper.danger).fixedSize(horizontal: false, vertical: true)
            }
            if let onUseOwnKey {
                Button("use my own key instead", action: onUseOwnKey)
                    .buttonStyle(.plain).font(Paper.caption).foregroundStyle(Paper.accentFill).pointerCursor()
            }
        }
    }

    private func codeStep(_ address: String) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("you already have an account — we sent a 6-digit code to \(address).")
                .font(Paper.body(13)).foregroundStyle(Paper.ink).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 8) {
                TextField("123456", text: $code)
                    .textFieldStyle(.roundedBorder).textContentType(.oneTimeCode).frame(width: 120)
                    .onSubmit(onSubmitCode)
                Button(isChecking ? "checking…" : "sign in", action: onSubmitCode)
                    .buttonStyle(PaperPillButtonStyle(prominent: true))
                    .keyboardShortcut(.defaultAction)
                    .disabled(isChecking || !EmailAccountForm.canSubmitCode(code))
            }
            if let codeError {
                Text(codeError).font(Paper.caption).foregroundStyle(Paper.danger).fixedSize(horizontal: false, vertical: true)
            }
            HStack(spacing: 14) {
                Button(isSending ? "sending…" : "send a new code", action: onSendNewCode)
                    .buttonStyle(.plain).font(Paper.caption).foregroundStyle(Paper.accentFill).pointerCursor()
                    .disabled(isSending || isChecking)
                Button("use a different email", action: onDifferentEmail)
                    .buttonStyle(.plain).font(Paper.caption).foregroundStyle(Paper.accentFill).pointerCursor()
            }
        }
    }
}
