//
//  AccountSheet.swift
//  OpenClicky
//
//  The account sheet: a heading, the one email form (EmailAccountForm), and "not now".
//  Also the work slot the form runs its requests in, and the usage bar the settings page shows for a signed-in account.
//

import SwiftUI

struct AccountSheet: View {
    let onDone: () -> Void
    /// Offered under the email field; nil leaves the link out.
    var onUseOwnKey: (() -> Void)? = nil

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            PaperHeading(text: "your free openclicky account", size: 26)
            Text("polished dictation and spoken answers, on us — up to $10 of use a month once your email is confirmed.")
                .font(Paper.caption).foregroundStyle(Paper.inkSecondary).fixedSize(horizontal: false, vertical: true)
            EmailAccountForm(onSignedIn: onDone, onUseOwnKey: onUseOwnKey)
            Button("not now", action: onDone)
                .buttonStyle(PaperPillButtonStyle(quiet: true)).keyboardShortcut(.cancelAction)
        }
        .padding(28)
        .frame(width: 440, alignment: .leading)
        .background(Paper.background)
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
