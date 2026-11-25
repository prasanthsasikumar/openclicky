//
//  PlateCardView.swift
//  OpenClicky
//
//  One offer as a paper card: a grey strip, the sentence, a pen doodle for its kind, and three
//  buttons. Under the pointer it straightens and a slip with the plan slides out from beneath
//  it; "change it" swaps the buttons for a line to type what should be different; saying yes
//  stamps it.
//

import AppKit
import SwiftUI

enum PlatePaper {
    static let paper = Color(hex: "#F5F4F1")
    static let strip = Color(hex: "#E4E3DF")
    static let slip = Color(hex: "#FFF6D6")
    static let ink = Color(hex: "#161616")
    static let ink2 = Color(hex: "#5C5C5A")
    static let ink3 = Color(hex: "#8E8E8A")
    static let blue = Color(hex: "#2F55D4")
    static let blueFill = Color(hex: "#C9D8FF")
    static let stamp = Color(hex: "#1F8A4C")
    static let radius: CGFloat = 12
}

struct PlateCardView: View {
    let suggestion: PlateSuggestion
    let width: CGFloat
    let isOpen: Bool
    let isAdjusting: Bool
    let isStamped: Bool
    let reduceMotion: Bool
    @Binding var adjustment: String
    let onDoIt: () -> Void
    let onAdjust: () -> Void
    let onCancelAdjust: () -> Void
    let onSkip: () -> Void

    var body: some View {
        card
            .background(alignment: .bottom) {
                PlatePlanSlip(suggestion: suggestion, width: width - 30)
                    // Closed, the slip lies behind the card; open, it hangs from under its bottom edge.
                    .alignmentGuide(.bottom) { isOpen ? $0[.top] + 14 : $0[.bottom] + 6 }
                    .rotationEffect(.degrees(isOpen ? 1.2 : 0))
                    .opacity(isOpen ? 1 : 0)
                    .accessibilityHidden(!isOpen)
            }
            .overlay {
                if isStamped {
                    Text("GOT IT")
                        .font(.system(size: 30, weight: .black, design: .rounded))
                        .tracking(2)
                        .foregroundColor(PlatePaper.stamp)
                        .padding(.horizontal, 14)
                        .padding(.vertical, 6)
                        .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).stroke(PlatePaper.stamp, lineWidth: 4))
                        .rotationEffect(.degrees(-11))
                        .opacity(0.9)
                        .transition(reduceMotion ? .opacity : .scale(scale: 1.9).combined(with: .opacity))
                        .accessibilityLabel("taken on")
                }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel(suggestion.title)
    }

    private var card: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 6) {
                Circle().fill(DS.HUD.pointer).frame(width: 8, height: 8)
                Text(stripLabel)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundColor(PlatePaper.ink2)
                Spacer()
                if let minutes = suggestion.minutes {
                    Text("~\(minutes) min")
                        .font(.system(size: 11, weight: .medium))
                        .foregroundColor(PlatePaper.ink3)
                }
            }
            .padding(.horizontal, 14)
            .frame(height: 30)
            .background(PlatePaper.strip)

            HStack(alignment: .top, spacing: 10) {
                Text(suggestion.title)
                    .font(.system(size: 16, weight: .bold))
                    .foregroundColor(PlatePaper.ink)
                    .lineSpacing(1.5)
                    .lineLimit(5)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
                PlateDoodle(kind: suggestion.kind, seed: suggestion.id)
                    .frame(width: 58, height: 58)
                    .accessibilityHidden(true)
            }
            .padding(.horizontal, 16)
            .padding(.top, 14)

            Spacer(minLength: 14)

            Group {
                if isAdjusting {
                    adjustRow
                } else {
                    buttonRow
                }
            }
            .padding(.horizontal, 16)
            .padding(.bottom, 14)
            .opacity(isStamped ? 0.25 : 1)
        }
        .frame(width: width)
        .frame(minHeight: PlateDeckLayout.cardHeight, alignment: .top)
        .fixedSize(horizontal: false, vertical: true)
        .background(PlatePaper.paper)
        .clipShape(RoundedRectangle(cornerRadius: PlatePaper.radius, style: .continuous))
        .shadow(color: .black.opacity(isOpen ? 0.5 : 0.35), radius: isOpen ? 26 : 14, y: isOpen ? 14 : 8)
    }

    private var stripLabel: String {
        switch suggestion.kind {
        case .draft: return "a draft"
        case .research: return "some reading"
        case .watch: return "a check"
        case .list: return "a list"
        case .numbers: return "some numbers"
        case .message: return "a message"
        case .schedule: return "some dates"
        case .code: return "some code"
        }
    }

    private var buttonRow: some View {
        HStack(spacing: 8) {
            Button("Go ahead", action: onDoIt)
                .buttonStyle(PlatePillStyle(isPrimary: true))
                .accessibilityHint("hands this job to the agent")
            Button("Change it", action: onAdjust)
                .buttonStyle(PlatePillStyle(isPrimary: false))
            Button(action: onSkip) {
                Text("Bin it")
                    .font(.system(size: 12.5, weight: .medium))
                    .foregroundColor(PlatePaper.ink3)
                    .padding(.horizontal, 8)
                    .frame(height: 26)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .pointerCursor()
            Spacer(minLength: 0)
        }
    }

    private var adjustRow: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("What should be different?")
                    .font(.system(size: 12.5, weight: .semibold))
                    .foregroundColor(PlatePaper.ink)
                Spacer()
                Button(action: onCancelAdjust) {
                    Image(systemName: "xmark")
                        .font(.system(size: 9, weight: .bold))
                        .foregroundColor(PlatePaper.ink2)
                        .frame(width: 20, height: 20)
                        .background(Circle().stroke(PlatePaper.ink3.opacity(0.6), lineWidth: 1))
                }
                .buttonStyle(.plain)
                .pointerCursor()
                .accessibilityLabel("cancel the change")
            }
            PlateTextField(text: $adjustment, placeholder: "Type it here", onSubmit: onDoIt, onEscape: onCancelAdjust)
                .frame(height: 30)
                .padding(.horizontal, 9)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.white))
                .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).stroke(PlatePaper.blue, lineWidth: 1.5))
            Text("return hands it over that way")
                .font(.system(size: 11))
                .foregroundColor(PlatePaper.ink3)
        }
    }
}

/// The plan, on a slip of lined note paper: why, the steps, how long and what comes back.
struct PlatePlanSlip: View {
    let suggestion: PlateSuggestion
    let width: CGFloat

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            if !suggestion.detail.isEmpty {
                Text(suggestion.detail)
                    .font(.system(size: 11.5))
                    .foregroundColor(PlatePaper.ink2)
                    .lineLimit(3)
                    .fixedSize(horizontal: false, vertical: true)
            }
            ForEach(Array((suggestion.steps ?? []).enumerated()), id: \.offset) { index, step in
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text("\(index + 1)")
                        .font(.system(size: 10.5, weight: .bold, design: .rounded))
                        .foregroundColor(PlatePaper.blue)
                        .frame(width: 10, alignment: .leading)
                    Text(step)
                        .font(.system(size: 12, weight: .medium))
                        .foregroundColor(PlatePaper.ink)
                        .lineLimit(1)
                }
            }
            if let footnote = PlateSchedule.footnote(minutes: suggestion.minutes, result: suggestion.result) {
                Text(footnote)
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundColor(PlatePaper.stamp)
                    .lineLimit(1)
                    .padding(.top, 1)
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 24)
        .padding(.bottom, 12)
        .frame(width: width, alignment: .leading)
        .background(PlatePaper.slip)
        .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
        .shadow(color: .black.opacity(0.3), radius: 8, y: 5)
    }
}

struct PlatePillStyle: ButtonStyle {
    let isPrimary: Bool

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 12.5, weight: .semibold))
            .foregroundColor(PlatePaper.ink)
            .padding(.horizontal, 13)
            .frame(height: 26)
            .background(Capsule().fill(isPrimary ? PlatePaper.blueFill : Color.white))
            .overlay(Capsule().stroke(isPrimary ? PlatePaper.blue : PlatePaper.ink.opacity(0.75), lineWidth: 1.2))
            .scaleEffect(configuration.isPressed ? 0.95 : 1)
            .animation(Motion.hover, value: configuration.isPressed)
            .pointerCursor()
    }
}

/// A one-line AppKit field that takes the keyboard as soon as it is in a window: the panels it
/// lives in never activate the app, and SwiftUI's focus needs an active app.
struct PlateTextField: NSViewRepresentable {
    @Binding var text: String
    let placeholder: String
    let onSubmit: () -> Void
    let onEscape: () -> Void

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> NSTextField {
        let textField = FocusingTextField()
        textField.isBordered = false
        textField.isBezeled = false
        textField.drawsBackground = false
        textField.focusRingType = .none
        textField.font = .systemFont(ofSize: 13)
        textField.textColor = NSColor(white: 0.09, alpha: 1)
        textField.placeholderAttributedString = NSAttributedString(
            string: placeholder,
            attributes: [.foregroundColor: NSColor(white: 0.55, alpha: 1), .font: NSFont.systemFont(ofSize: 13)])
        textField.cell?.usesSingleLineMode = true
        textField.cell?.isScrollable = true
        textField.delegate = context.coordinator
        textField.setContentHuggingPriority(.defaultLow, for: .horizontal)
        textField.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        textField.setAccessibilityLabel(placeholder)
        return textField
    }

    func updateNSView(_ textField: NSTextField, context: Context) {
        context.coordinator.parent = self
        if textField.stringValue != text { textField.stringValue = text }
    }

    final class FocusingTextField: NSTextField {
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            guard window != nil else { return }
            for delay in [0.0, 0.08, 0.3] {
                DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
                    guard let self, let window = self.window, window.firstResponder !== self.currentEditor() else { return }
                    window.makeKey()
                    window.makeFirstResponder(self)
                }
            }
        }
    }

    final class Coordinator: NSObject, NSTextFieldDelegate {
        var parent: PlateTextField
        init(_ parent: PlateTextField) { self.parent = parent }

        func controlTextDidChange(_ notification: Notification) {
            guard let textField = notification.object as? NSTextField else { return }
            parent.text = textField.stringValue
        }

        func control(_ control: NSControl, textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
            if commandSelector == #selector(NSResponder.insertNewline(_:)) {
                parent.onSubmit()
                return true
            }
            if commandSelector == #selector(NSResponder.cancelOperation(_:)) || commandSelector == #selector(NSTextView.complete(_:)) {
                parent.onEscape()
                return true
            }
            return false
        }
    }
}
