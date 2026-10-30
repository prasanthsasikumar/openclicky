//
//  CalendarQuickAdd.swift
//  OpenClicky
//
//  The island's quick add: a name and a start time for the chosen day, Return saves an hour-long
//  event, esc or the "x" closes it. The island's panel never activates the app, so typing would
//  go to the app in front; like the island's text composer, OpenClicky activates itself and makes
//  the panel key while the field is open, then hands the keyboard back to that app.
//

import AppKit
import Combine
import SwiftUI

struct CalendarQuickAddField: View {
    @ObservedObject var store: CalendarStore
    let dayStart: Date
    let onClose: () -> Void

    @State private var title = ""
    @State private var start = Date()
    @State private var errorText: String?
    @StateObject private var keyboardLoan = CalendarKeyboardLoan()
    @FocusState private var isTitleFocused: Bool

    /// A quick-added event lasts an hour; Calendar is where to change that.
    private static let eventDuration: TimeInterval = 3_600

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                TextField("", text: $title, prompt: Text("New event").foregroundColor(.white.opacity(0.4)))
                    .textFieldStyle(.plain)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundColor(.white)
                    .focused($isTitleFocused)
                    .onSubmit(addEvent)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 5)
                    .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(Color.white.opacity(0.1)))
                DatePicker("", selection: $start, displayedComponents: .hourAndMinute)
                    .labelsHidden()
                    .datePickerStyle(.field)
                    .controlSize(.small)
                    .environment(\.colorScheme, .dark)
                    .fixedSize()
                    .pointerCursor()
            }
            HStack(spacing: 6) {
                Text(errorText ?? "Return adds it · esc closes")
                    .font(.system(size: 10))
                    .foregroundColor(errorText == nil ? .white.opacity(0.45) : DS.Colors.warning)
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
                Button("Add", action: addEvent)
                    .buttonStyle(IslandPillButtonStyle())
                    .disabled(title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(.top, 2)
        .background(CalendarWindowReader { window in keyboardLoan.window = window })
        .onExitCommand(perform: onClose)
        .onAppear {
            start = CalendarSchedule.suggestedQuickAddStart(on: dayStart, now: Date(), calendar: .autoupdatingCurrent)
            takeKeyboard()
        }
        .onDisappear { keyboardLoan.giveBack() }
        .onChange(of: dayStart) { newDayStart in
            start = CalendarSchedule.suggestedQuickAddStart(on: newDayStart, now: Date(), calendar: .autoupdatingCurrent)
        }
        .onChange(of: title) { _ in errorText = nil }
    }

    /// The panel becomes key once the app is active, which macOS 14 can grant a moment late, so
    /// the field is focused again a few times until it takes (the composer does the same).
    private func takeKeyboard() {
        keyboardLoan.borrow()
        for delay in [0.05, 0.25, 0.6] {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
                keyboardLoan.makeWindowKey()
                isTitleFocused = true
            }
        }
    }

    private func addEvent() {
        // The picker only sets the time; the day is the one chosen in the strip.
        let calendar = Calendar.autoupdatingCurrent
        let timeOfDay = calendar.dateComponents([.hour, .minute], from: start)
        let eventStart = calendar.date(bySettingHour: timeOfDay.hour ?? 9, minute: timeOfDay.minute ?? 0, second: 0, of: dayStart) ?? start
        do {
            try store.addEvent(title: title, start: eventStart, end: eventStart.addingTimeInterval(Self.eventDuration))
            title = ""
            onClose()
        } catch {
            errorText = error.localizedDescription
        }
    }
}

/// Lends the island's panel the keyboard and gives it back to the app that had it.
@MainActor
final class CalendarKeyboardLoan: ObservableObject {
    weak var window: NSWindow?
    private var applicationActiveBefore: NSRunningApplication?
    private var isBorrowed = false

    func borrow() {
        guard !isBorrowed else { return }
        isBorrowed = true
        let frontApplication = NSWorkspace.shared.frontmostApplication
        if frontApplication?.processIdentifier != ProcessInfo.processInfo.processIdentifier {
            applicationActiveBefore = frontApplication
        }
        NSApp.activate(ignoringOtherApps: true)
        makeWindowKey()
    }

    func makeWindowKey() {
        guard isBorrowed, let window, !window.isKeyWindow else { return }
        window.makeKeyAndOrderFront(nil)
    }

    func giveBack() {
        guard isBorrowed else { return }
        isBorrowed = false
        window?.makeFirstResponder(nil)
        if let applicationActiveBefore, !applicationActiveBefore.isTerminated {
            applicationActiveBefore.activate()
        }
        applicationActiveBefore = nil
    }
}

/// Hands the hosting window to SwiftUI once the view is in one.
struct CalendarWindowReader: NSViewRepresentable {
    let onWindow: (NSWindow?) -> Void

    func makeNSView(context: Context) -> WindowReportingView {
        let view = WindowReportingView()
        view.onWindow = onWindow
        return view
    }

    func updateNSView(_ nsView: WindowReportingView, context: Context) {
        nsView.onWindow = onWindow
    }

    final class WindowReportingView: NSView {
        var onWindow: ((NSWindow?) -> Void)?

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            onWindow?(window)
        }
    }
}
