//
//  AccountSheet.swift
//  OpenClicky
//
//  Create a free account or sign in, in the app: one email field (Task 8 redesigns this). A new
//  email is signed in at once; an existing account is mailed a code, typed on the second step. The
//  sheet owns the task doing that work and cancels it when it closes.
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
    @State private var code = ""
    @State private var info: String?
    /// The send in flight; cancelled when the sheet closes.
    @State private var accountWork = AccountWorkSlot()

    init(startIn mode: Mode = .create, onDone: @escaping () -> Void, onUseOwnKey: (() -> Void)? = nil) {
        _mode = State(initialValue: mode)
        self.onDone = onDone
        self.onUseOwnKey = onUseOwnKey
    }

    var body: some View {
        AccountSheetContent(
            mode: mode,
            emailFlow: auth.emailFlow,
            email: $email,
            code: $code,
            info: info,
            onSubmit: submit,
            onSubmitCode: submitCode,
            onSwitchMode: switchMode,
            onClose: onDone,
            onUseOwnKey: onUseOwnKey
        )
        .onAppear { auth.forgetSettledFlow() }
        .onDisappear { accountWork.cancel() }
        .onChange(of: auth.emailFlow) { _, state in
            if case .signedIn = state { finishSignedIn() }
        }
    }

    private func submit() {
        let trimmedEmail = email.trimmingCharacters(in: .whitespacesAndNewlines)
        guard Self.canSubmit(email: trimmedEmail) else { return }
        // Return can fire both the field's onSubmit and the default button before any state has
        // changed; the slot refuses the second start synchronously.
        guard !accountWork.isRunning else { return }
        info = nil
        accountWork.start { await auth.start(email: trimmedEmail) }
    }

    private func submitCode() {
        guard !accountWork.isRunning else { return }
        accountWork.start { await auth.submitCode(code) }
    }

    private func switchMode() {
        mode = mode == .create ? .signIn : .create
        info = nil
        auth.forgetSettledFlow()
    }

    private func finishSignedIn() {
        code = ""
        ShellSettingsRevision.shared.noteChanged()
        onDone()
    }

    static func canSubmit(email: String) -> Bool { email.contains("@") }
}

/// The sheet's look for one state, with no state of its own, so each state can be drawn on its own.
struct AccountSheetContent: View {
    let mode: AccountSheet.Mode
    let emailFlow: OpenClickyAuthSession.EmailFlowState
    @Binding var email: String
    @Binding var code: String
    let info: String?
    let onSubmit: () -> Void
    let onSubmitCode: () -> Void
    let onSwitchMode: () -> Void
    let onClose: () -> Void
    var onUseOwnKey: (() -> Void)? = nil

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            switch screen {
            case .code(let address): codeStep(address: address)
            case .full: full
            case .form: form
            }
        }
        .padding(28)
        .frame(width: 440, alignment: .leading)
        .background(Paper.background)
    }

    private enum Screen { case form, code(String), full }

    private var screen: Screen {
        switch emailFlow {
        case .needsCode(let address): return .code(address)
        case .failed(let message) where mode == .create && message == AccountLimitError.accountsFull.message: return .full
        default: return .form
        }
    }

    private var isBusy: Bool { emailFlow == .sending }

    private var form: some View {
        VStack(alignment: .leading, spacing: 16) {
            PaperHeading(text: mode == .create ? "create a free account" : "sign in", size: 26)
            Text("polished dictation and spoken answers, on us — up to $10 of use a month. just your email, no password.")
                .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                .fixedSize(horizontal: false, vertical: true)
            TextField("email", text: $email)
                .textFieldStyle(.roundedBorder)
                .textContentType(.emailAddress)
                .onSubmit(onSubmit)
            if case .failed(let message) = emailFlow {
                Text(message).font(Paper.caption).foregroundStyle(Paper.danger).fixedSize(horizontal: false, vertical: true)
            }
            if let info {
                Text(info).font(Paper.caption).foregroundStyle(Paper.inkSecondary).fixedSize(horizontal: false, vertical: true)
            }
            HStack(spacing: 8) {
                Button(isBusy ? "sending…" : "continue", action: onSubmit)
                    .buttonStyle(PaperPillButtonStyle(prominent: true))
                    .keyboardShortcut(.defaultAction)
                    .disabled(isBusy || !AccountSheet.canSubmit(email: email.trimmingCharacters(in: .whitespacesAndNewlines)))
                Button("not now", action: onClose)
                    .buttonStyle(PaperPillButtonStyle(quiet: true))
                    .keyboardShortcut(.cancelAction)
                Spacer()
            }
        }
    }

    private func codeStep(address: String) -> some View {
        VStack(alignment: .leading, spacing: 16) {
            PaperHeading(text: "check your email", size: 26)
            Text("we sent a 6-digit code to \(address).")
                .font(Paper.rowTitle).foregroundStyle(Paper.ink)
            TextField("code", text: $code)
                .textFieldStyle(.roundedBorder)
                .onSubmit(onSubmitCode)
            HStack(spacing: 8) {
                Button(isBusy ? "checking…" : "sign in", action: onSubmitCode)
                    .buttonStyle(PaperPillButtonStyle(prominent: true))
                    .keyboardShortcut(.defaultAction)
                    .disabled(isBusy || code.filter(\.isNumber).count < 6)
                Button("close", action: onClose)
                    .buttonStyle(PaperPillButtonStyle(quiet: true))
                    .keyboardShortcut(.cancelAction)
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
