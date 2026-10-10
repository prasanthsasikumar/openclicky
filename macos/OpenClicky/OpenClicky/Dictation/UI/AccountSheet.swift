//
//  AccountSheet.swift
//  OpenClicky
//
//  Create a free account or sign in, in the app: email and password, then "check your email" while
//  the session waits for the confirmation link and signs in on its own. The sheet owns the task
//  doing that work and cancels it when it closes, so a closed sheet never leaves a wait running.
//  Also the usage bar the settings page shows for a signed-in account.
//

import SwiftUI

struct AccountSheet: View {
    enum Mode: Identifiable {
        case create, signIn
        var id: Self { self }
    }

    @State private var mode: Mode
    let onDone: () -> Void
    /// Offered on the "full" screen; nil leaves the button out.
    let onUseOwnKey: (() -> Void)?
    @ObservedObject private var auth = OpenClickyAuthSession.shared
    @State private var email = ""
    @State private var password = ""
    @State private var isSigningIn = false
    @State private var signInFailure: String?
    @State private var info: String?
    /// The sign-up or sign-in in flight; cancelled when the sheet closes.
    @State private var accountWork = AccountWorkSlot()
    /// The reset-link request; cancelled when the sheet closes or the mode switches.
    @State private var resetLinkTask: Task<Void, Never>?

    init(startIn mode: Mode = .create, onDone: @escaping () -> Void, onUseOwnKey: (() -> Void)? = nil) {
        _mode = State(initialValue: mode)
        self.onDone = onDone
        self.onUseOwnKey = onUseOwnKey
    }

    var body: some View {
        AccountSheetContent(
            mode: mode,
            signUpState: auth.signUpState,
            email: $email,
            password: $password,
            isSigningIn: isSigningIn,
            signInFailure: signInFailure,
            info: info,
            onSubmit: submit,
            onForgotPassword: sendResetLink,
            onSwitchMode: switchMode,
            onClose: onDone,
            onUseOwnKey: onUseOwnKey
        )
        .onAppear { auth.forgetSettledSignUp() }
        .onDisappear {
            accountWork.cancel()
            resetLinkTask?.cancel()
        }
        .onChange(of: auth.signUpState) { _, state in
            if state == .signedIn { finishSignedIn() }
        }
    }

    private func submit() {
        let trimmedEmail = email.trimmingCharacters(in: .whitespacesAndNewlines)
        guard Self.canSubmit(mode: mode, email: trimmedEmail, password: password), !isSigningIn else { return }
        // Return can fire both the field's onSubmit and the default button before any state has
        // changed; the slot refuses the second start synchronously, so the task the sheet cancels
        // on close is always the one doing the work.
        guard !accountWork.isRunning else { return }
        info = nil
        signInFailure = nil
        switch mode {
        case .create:
            accountWork.start { await auth.signUp(email: trimmedEmail, password: password) }
        case .signIn:
            isSigningIn = true
            accountWork.start {
                let didSignIn = await auth.signIn(email: trimmedEmail, password: password)
                isSigningIn = false
                if didSignIn {
                    finishSignedIn()
                } else if !Task.isCancelled {
                    signInFailure = Self.friendlySignInMessage(auth.lastErrorText)
                }
            }
        }
    }

    private func sendResetLink() {
        let trimmedEmail = email.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmedEmail.contains("@") else {
            info = "type your email above first."
            return
        }
        signInFailure = nil
        info = "sending a reset link…"
        resetLinkTask?.cancel()
        resetLinkTask = Task {
            let didSend = await auth.recover(email: trimmedEmail)
            guard !Task.isCancelled else { return }
            info = didSend
                ? "we sent a reset link to \(trimmedEmail)."
                : "couldn't send a reset link — check the email and try again."
        }
    }

    private func switchMode() {
        resetLinkTask?.cancel()
        mode = mode == .create ? .signIn : .create
        signInFailure = nil
        info = nil
        auth.forgetSettledSignUp()
    }

    private func finishSignedIn() {
        password = ""
        ShellSettingsRevision.shared.noteChanged()
        onDone()
    }

    /// Creating an account needs a password the backend accepts (8 or more characters); signing
    /// in takes whatever password the account already has.
    static func canSubmit(mode: Mode, email: String, password: String) -> Bool {
        guard email.contains("@") else { return false }
        switch mode {
        case .create: return password.count >= 8
        case .signIn: return !password.isEmpty
        }
    }

    /// Supabase answers sign-in failures in its own words ("Invalid login credentials"); the
    /// person reads one plain sentence instead.
    static func friendlySignInMessage(_ raw: String?) -> String {
        let lowered = (raw ?? "").lowercased()
        if lowered.contains("invalid login credentials") {
            return "that email and password don't match — try again, or reset the password."
        }
        if lowered.contains("email not confirmed") {
            return "this email isn't confirmed yet — tap the link we sent you first."
        }
        return "couldn't sign in right now — try again in a minute."
    }
}

/// The sheet's look for one state, with no state of its own, so each state can be drawn on its own.
struct AccountSheetContent: View {
    let mode: AccountSheet.Mode
    let signUpState: OpenClickyAuthSession.SignUpState
    @Binding var email: String
    @Binding var password: String
    let isSigningIn: Bool
    let signInFailure: String?
    let info: String?
    let onSubmit: () -> Void
    let onForgotPassword: () -> Void
    let onSwitchMode: () -> Void
    let onClose: () -> Void
    var onUseOwnKey: (() -> Void)? = nil

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            switch screen {
            case .waiting(let address): waiting(address: address)
            case .full: full
            case .form: form
            }
        }
        .padding(28)
        .frame(width: 440, alignment: .leading)
        .background(Paper.background)
    }

    private enum Screen { case form, waiting(String), full }

    private var screen: Screen {
        switch signUpState {
        case .awaitingConfirmation(let address): return .waiting(address)
        case .full where mode == .create: return .full
        default: return .form
        }
    }

    private var isBusy: Bool { isSigningIn || signUpState == .sending }

    private var form: some View {
        VStack(alignment: .leading, spacing: 16) {
            PaperHeading(text: mode == .create ? "create a free account" : "sign in", size: 26)
            Text(mode == .create
                 ? "polished dictation and spoken answers, on us — up to $10 of use a month."
                 : "use the email and password you signed up with.")
                .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                .fixedSize(horizontal: false, vertical: true)
            VStack(alignment: .leading, spacing: 8) {
                TextField("email", text: $email)
                    .textFieldStyle(.roundedBorder)
                    .textContentType(.emailAddress)
                SecureField(mode == .create ? "password — at least 8 characters" : "password", text: $password)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit(onSubmit)
            }
            if let message = failureMessage {
                Text(message).font(Paper.caption).foregroundStyle(Paper.danger).fixedSize(horizontal: false, vertical: true)
            }
            if let info {
                Text(info).font(Paper.caption).foregroundStyle(Paper.inkSecondary).fixedSize(horizontal: false, vertical: true)
            }
            HStack(spacing: 8) {
                Button(submitTitle, action: onSubmit)
                    .buttonStyle(PaperPillButtonStyle(prominent: true))
                    .keyboardShortcut(.defaultAction)
                    .disabled(isBusy || !AccountSheet.canSubmit(mode: mode, email: email.trimmingCharacters(in: .whitespacesAndNewlines), password: password))
                Button("not now", action: onClose)
                    .buttonStyle(PaperPillButtonStyle(quiet: true))
                    .keyboardShortcut(.cancelAction)
                Spacer()
            }
            HStack(spacing: 14) {
                Button(mode == .create ? "i already have an account" : "create one instead", action: onSwitchMode)
                    .buttonStyle(.plain).font(Paper.caption).foregroundStyle(Paper.accentFill).pointerCursor()
                if mode == .signIn {
                    Button("forgot password?", action: onForgotPassword)
                        .buttonStyle(.plain).font(Paper.caption).foregroundStyle(Paper.inkSecondary).pointerCursor()
                }
            }
        }
    }

    private var submitTitle: String {
        switch mode {
        case .create: return signUpState == .sending ? "creating…" : "create account"
        case .signIn: return isSigningIn ? "signing in…" : "sign in"
        }
    }

    /// A sign-up's failure belongs to the create form, a sign-in's to the sign-in form.
    private var failureMessage: String? {
        switch mode {
        case .create:
            if case .failed(let message) = signUpState { return message }
            return nil
        case .signIn:
            return signInFailure
        }
    }

    private func waiting(address: String) -> some View {
        VStack(alignment: .leading, spacing: 16) {
            PaperHeading(text: "check your email", size: 26)
            PaperCard(padding: 16) {
                HStack(alignment: .top, spacing: 12) {
                    Image(systemName: "envelope").font(.system(size: 18)).foregroundStyle(Paper.accent)
                    VStack(alignment: .leading, spacing: 6) {
                        Text("we sent a link to \(address).").font(Paper.rowTitle).foregroundStyle(Paper.ink)
                        Text("tap it, then come back — openclicky signs you in on its own.")
                            .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer(minLength: 0)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            HStack(spacing: 10) {
                ProgressView().controlSize(.small)
                Text("waiting for the link…").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
            }
            HStack {
                Button("close", action: onClose)
                    .buttonStyle(PaperPillButtonStyle())
                    .keyboardShortcut(.cancelAction)
                Text("you can sign in later with the same email and password.")
                    .font(Paper.caption).foregroundStyle(Paper.inkTertiary)
            }
        }
    }

    private var full: some View {
        VStack(alignment: .leading, spacing: 16) {
            PaperHeading(text: "no room just now", size: 26)
            Text(AccountLimitError.accountsFull.message)
                .font(Paper.body(13)).foregroundStyle(Paper.ink)
                .fixedSize(horizontal: false, vertical: true)
            Text("dictation on this mac works without an account.")
                .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
            HStack(spacing: 8) {
                if let onUseOwnKey {
                    Button("use my own key", action: onUseOwnKey)
                        .buttonStyle(PaperPillButtonStyle(prominent: true))
                        .keyboardShortcut(.defaultAction)
                    Button("close", action: onClose)
                        .buttonStyle(PaperPillButtonStyle())
                        .keyboardShortcut(.cancelAction)
                } else {
                    Button("close", action: onClose)
                        .buttonStyle(PaperPillButtonStyle(prominent: true))
                        .keyboardShortcut(.defaultAction)
                }
                Button("i already have an account", action: onSwitchMode)
                    .buttonStyle(PaperPillButtonStyle(quiet: true))
            }
        }
    }
}

/// Holds the one sign-up or sign-in a sheet runs at a time. `start` refuses while one is running
/// and says so synchronously, before the task has had a chance to run a single line.
@MainActor
final class AccountWorkSlot {
    private(set) var isRunning = false
    private var task: Task<Void, Never>?

    @discardableResult
    func start(_ work: @escaping @MainActor () async -> Void) -> Bool {
        guard !isRunning else { return false }
        isRunning = true
        task = Task { @MainActor [weak self] in
            await work()
            self?.isRunning = false
        }
        return true
    }

    func cancel() { task?.cancel() }
}

/// "plenty left this month · resets on nov 1", over a thin bar of the month used.
struct AccountUsageBar: View {
    let summary: BillingSummary

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            GeometryReader { geometry in
                ZStack(alignment: .leading) {
                    Capsule().fill(Paper.lineSoft)
                    Capsule().fill(summary.allowanceStanding == .plenty ? Paper.success : Paper.accent)
                        .frame(width: max(4, geometry.size.width * summary.fractionUsed))
                }
            }
            .frame(height: 6)
            .accessibilityHidden(true)
            Text(summary.allowanceSentence(resetDay: summary.monthResetDay()))
                .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .accessibilityElement(children: .combine)
    }
}
