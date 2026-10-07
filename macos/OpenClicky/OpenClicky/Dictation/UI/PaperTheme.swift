//
//  PaperTheme.swift
//  OpenClicky
//
//  The dictation window's look: warm paper, ink, a coral highlighter under the serif headings,
//  lowercase copy. Tokens and the handful of small views every page is built from. Light and dark
//  both come from here; the orb follows the same appearance setting.
//

import AppKit
import Combine
import SwiftUI

enum Paper {
    // Colours adapt to the window's appearance through NSColor's dynamic providers. Names in comments are the
    // design's token names (docs: OpenClicky Refinement, section D).
    /// paper
    static let background = Color(nsColor: dynamic(light: NSColor(srgbRed: 0.957, green: 0.945, blue: 0.918, alpha: 1), dark: NSColor(srgbRed: 0.106, green: 0.102, blue: 0.094, alpha: 1)))
    /// rail
    static let rail = Color(nsColor: dynamic(light: NSColor(srgbRed: 0.933, green: 0.918, blue: 0.882, alpha: 1), dark: NSColor(srgbRed: 0.133, green: 0.125, blue: 0.114, alpha: 1)))
    /// card
    static let card = Color(nsColor: dynamic(light: NSColor(srgbRed: 0.984, green: 0.976, blue: 0.957, alpha: 1), dark: NSColor(srgbRed: 0.157, green: 0.149, blue: 0.137, alpha: 1)))
    /// line
    static let hairline = Color(nsColor: dynamic(light: NSColor(srgbRed: 0.886, green: 0.863, blue: 0.812, alpha: 1), dark: NSColor(srgbRed: 0.227, green: 0.216, blue: 0.200, alpha: 1)))
    /// lineSoft — row dividers inside a card
    static let lineSoft = Color(nsColor: dynamic(light: NSColor(srgbRed: 0.925, green: 0.902, blue: 0.855, alpha: 1), dark: NSColor(srgbRed: 0.200, green: 0.188, blue: 0.169, alpha: 1)))
    /// ink
    static let ink = Color(nsColor: dynamic(light: NSColor(srgbRed: 0.133, green: 0.125, blue: 0.110, alpha: 1), dark: NSColor(srgbRed: 0.929, green: 0.914, blue: 0.882, alpha: 1)))
    /// ink2
    static let inkSecondary = Color(nsColor: dynamic(light: NSColor(srgbRed: 0.369, green: 0.353, blue: 0.322, alpha: 1), dark: NSColor(srgbRed: 0.690, green: 0.667, blue: 0.624, alpha: 1)))
    /// ink3 — the smallest text still meets 4.5:1
    static let inkTertiary = Color(nsColor: dynamic(light: NSColor(srgbRed: 0.451, green: 0.431, blue: 0.392, alpha: 1), dark: NSColor(srgbRed: 0.604, green: 0.580, blue: 0.541, alpha: 1)))
    /// accent — the pointer's coral, for marks and highlights
    static let accent = Color(nsColor: dynamic(light: NSColor(srgbRed: 0.851, green: 0.318, blue: 0.173, alpha: 1), dark: NSColor(srgbRed: 0.941, green: 0.439, blue: 0.310, alpha: 1)))
    /// accentFill — filled buttons, white text on it meets 4.5:1
    static let accentFill = Color(nsColor: dynamic(light: NSColor(srgbRed: 0.769, green: 0.275, blue: 0.165, alpha: 1), dark: NSColor(srgbRed: 0.878, green: 0.376, blue: 0.247, alpha: 1)))
    /// selection — the chosen rail row
    static let selection = Color(nsColor: dynamic(light: NSColor(srgbRed: 0.953, green: 0.863, blue: 0.788, alpha: 1), dark: NSColor(srgbRed: 0.271, green: 0.161, blue: 0.122, alpha: 1)))
    /// ok
    static let success = Color(nsColor: dynamic(light: NSColor(srgbRed: 0.239, green: 0.478, blue: 0.306, alpha: 1), dark: NSColor(srgbRed: 0.424, green: 0.761, blue: 0.541, alpha: 1)))
    /// danger
    static let danger = Color(nsColor: dynamic(light: NSColor(srgbRed: 0.698, green: 0.227, blue: 0.133, alpha: 1), dark: NSColor(srgbRed: 0.941, green: 0.541, blue: 0.439, alpha: 1)))
    static let cardRaised = Color(nsColor: dynamic(light: NSColor.white, dark: NSColor(srgbRed: 0.196, green: 0.188, blue: 0.173, alpha: 1)))
    static let accentSoft = Color(nsColor: dynamic(light: NSColor(red: 0.97, green: 0.80, blue: 0.72, alpha: 1), dark: NSColor(red: 0.45, green: 0.22, blue: 0.16, alpha: 1)))
    static let highlighter = Color(nsColor: dynamic(light: NSColor(red: 0.99, green: 0.84, blue: 0.70, alpha: 1), dark: NSColor(red: 0.42, green: 0.25, blue: 0.16, alpha: 1)))

    // Type scale. Serif sizes are the design's Newsreader sizes, set in the system serif.
    static let display = heading(36)
    static let pageTitle = heading(30)
    static let cardTitle = heading(21)
    static let rowTitle = body(13, weight: .semibold)
    static let caption = body(12)
    static let micro = body(11)
    static let key = mono(11)

    // Metrics, in points.
    enum Metric {
        static let windowTop: CGFloat = 36
        static let windowSides: CGFloat = 40
        static let rowVertical: CGFloat = 12
        static let rowHorizontal: CGFloat = 16
        static let mainRail: CGFloat = 220
        static let settingsRail: CGFloat = 240
        static let railItemHeight: CGFloat = 32
        static let keyRadius: CGFloat = 5
        static let controlRadius: CGFloat = 7
        static let railItemRadius: CGFloat = 8
        static let cardRadius: CGFloat = 12
        static let buttonHeight: CGFloat = 28
        static let fieldHeight: CGFloat = 30
    }

    static func heading(_ size: CGFloat = 30) -> Font { .system(size: size, weight: .regular, design: .serif) }
    static func title(_ size: CGFloat = 20) -> Font { .system(size: size, weight: .medium, design: .serif) }
    static func body(_ size: CGFloat = 13, weight: Font.Weight = .regular) -> Font { .system(size: size, weight: weight) }
    static func label(_ size: CGFloat = 11) -> Font { .system(size: size, weight: .medium) }
    static func mono(_ size: CGFloat = 11) -> Font { .system(size: size, weight: .regular, design: .monospaced) }

    private static func dynamic(light: NSColor, dark: NSColor) -> NSColor {
        NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? dark : light
        }
    }
}

// MARK: - Small views

/// A lowercase serif heading with the highlighter sweep underneath, like the pages' titles.
struct PaperHeading: View {
    let text: String
    var size: CGFloat = 30

    var body: some View {
        Text(text)
            .font(Paper.heading(size))
            .foregroundStyle(Paper.ink)
            .padding(.horizontal, 4)
            .background(alignment: .bottom) {
                RoundedRectangle(cornerRadius: 3)
                    .fill(Paper.highlighter)
                    .frame(height: size * 0.3)
                    .offset(y: -size * 0.05)
                    .rotationEffect(.degrees(-0.6))
            }
            .padding(.horizontal, -4)
    }
}

struct PaperCard<Content: View>: View {
    var padding: CGFloat = 0
    @ViewBuilder var content: Content

    var body: some View {
        content
            .padding(padding)
            .background(RoundedRectangle(cornerRadius: Paper.Metric.cardRadius, style: .continuous).fill(Paper.card))
            .overlay(RoundedRectangle(cornerRadius: Paper.Metric.cardRadius, style: .continuous).strokeBorder(Paper.hairline, lineWidth: 1))
    }
}

/// A settings row: title, a line of copy, and whatever sits on the right.
struct PaperRow<Trailing: View>: View {
    let title: String
    var detail: String?
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(alignment: .center, spacing: 16) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(Paper.rowTitle).foregroundStyle(Paper.ink)
                if let detail { Text(detail).font(Paper.caption).foregroundStyle(Paper.inkSecondary).fixedSize(horizontal: false, vertical: true) }
            }
            Spacer(minLength: 12)
            trailing
        }
        .padding(.horizontal, Paper.Metric.rowHorizontal)
        .padding(.vertical, Paper.Metric.rowVertical)
    }
}

struct PaperDivider: View {
    var body: some View { Rectangle().fill(Paper.lineSoft).frame(height: 1) }
}

/// The small section label above a group of rows ("appearance", "behavior").
struct PaperSectionLabel: View {
    let text: String
    var body: some View {
        Text(text).font(Paper.body(13, weight: .medium)).foregroundStyle(Paper.ink).padding(.top, 10)
    }
}

struct PaperToggle: View {
    @Binding var isOn: Bool
    var body: some View {
        Toggle("", isOn: $isOn).toggleStyle(.switch).labelsHidden().tint(Paper.success).controlSize(.small)
    }
}

/// An outlined button, 28 pt high ("reset", "replay", "dock it"). `quiet` drops the outline and
/// fill for a secondary action beside it ("what can it do?"); `compact` is the small in-row
/// version ("copy" on a take).
struct PaperPillButtonStyle: ButtonStyle {
    var prominent = false
    var destructive = false
    var quiet = false
    var compact = false

    func makeBody(configuration: Configuration) -> some View {
        let shape = RoundedRectangle(cornerRadius: compact ? 6 : Paper.Metric.controlRadius, style: .continuous)
        return configuration.label
            .font(compact ? Paper.caption : Paper.body(13, weight: .medium))
            .foregroundStyle(foreground)
            .padding(.horizontal, compact ? 8 : 12)
            .frame(minHeight: compact ? 22 : Paper.Metric.buttonHeight)
            .background(shape.fill(fill.opacity(configuration.isPressed ? 0.7 : 1)))
            .overlay(shape.strokeBorder(stroke))
            .opacity(configuration.isPressed ? 0.8 : 1)
            .contentShape(shape)
            .pointerCursor()
    }

    private var foreground: Color {
        if destructive { return Paper.danger }
        if prominent { return Color.white }
        return quiet || compact ? Paper.inkSecondary : Paper.ink
    }

    private var fill: Color {
        if prominent { return Paper.accentFill }
        return quiet ? Color.clear : Paper.cardRaised
    }

    private var stroke: Color {
        if destructive { return Paper.danger.opacity(0.5) }
        return prominent || quiet ? Color.clear : Paper.hairline
    }
}

/// A keycap, like "fn".
struct Keycap: View {
    let text: String
    var body: some View {
        Text(text)
            .font(Paper.key)
            .foregroundStyle(Paper.inkSecondary)
            .padding(.horizontal, 5)
            .padding(.vertical, 1)
            .background(RoundedRectangle(cornerRadius: Paper.Metric.keyRadius).fill(Paper.cardRaised))
            .overlay(RoundedRectangle(cornerRadius: Paper.Metric.keyRadius).strokeBorder(Paper.hairline))
    }
}

/// A two-way or three-way segmented choice drawn as pills ("native / roman", "full / mini").
struct PaperSegments<Option: Hashable>: View {
    let options: [(Option, String)]
    @Binding var selection: Option

    var body: some View {
        HStack(spacing: 4) {
            ForEach(options, id: \.0) { option, label in
                Button(action: { selection = option }) {
                    Text(label)
                        .font(Paper.body(12, weight: .medium))
                        .foregroundStyle(selection == option ? Color.white : Paper.ink)
                        .padding(.horizontal, 12)
                        .padding(.vertical, 5)
                        .background(RoundedRectangle(cornerRadius: Paper.Metric.controlRadius, style: .continuous).fill(selection == option ? Paper.accentFill : Color.clear))
                }
                .buttonStyle(.plain)
                .pointerCursor()
            }
        }
        .padding(3)
        .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(Paper.cardRaised))
        .overlay(RoundedRectangle(cornerRadius: 9, style: .continuous).strokeBorder(Paper.hairline))
    }
}

/// The icon of an app, by bundle id, from the system.
struct AppIconView: View {
    let bundleID: String?
    var size: CGFloat = 18

    var body: some View {
        Group {
            if let image = AppIconCache.shared.icon(for: bundleID) {
                Image(nsImage: image).resizable().interpolation(.high)
            } else {
                RoundedRectangle(cornerRadius: size * 0.22).fill(Paper.hairline)
            }
        }
        .frame(width: size, height: size)
    }
}

final class AppIconCache {
    static let shared = AppIconCache()
    private var icons: [String: NSImage] = [:]
    private var names: [String: String] = [:]

    func icon(for bundleID: String?) -> NSImage? {
        guard let bundleID else { return nil }
        if let cached = icons[bundleID] { return cached }
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) else { return nil }
        let image = NSWorkspace.shared.icon(forFile: url.path)
        icons[bundleID] = image
        return image
    }

    func name(for bundleID: String) -> String? {
        if let cached = names[bundleID] { return cached }
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) else { return nil }
        let name = (Bundle(url: url)?.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String)
            ?? (Bundle(url: url)?.object(forInfoDictionaryKey: "CFBundleName") as? String)
            ?? url.deletingPathExtension().lastPathComponent
        names[bundleID] = name
        return name
    }
}

/// The wordmark in the rail: the mark and the name, lowercase serif.
struct OpenClickyWordmark: View {
    var body: some View {
        HStack(spacing: 8) {
            OpenClickyMarkShape().fill(Paper.accent, style: FillStyle(eoFill: true)).frame(width: 20, height: 20)
            // One line always: the 220 pt rail leaves the name about 150 pt beside the mark and the sidebar button.
            Text("openclicky").font(.system(size: 22, weight: .medium, design: .serif)).foregroundStyle(Paper.ink)
                .lineLimit(1).fixedSize()
        }
    }
}

extension View {
    /// Bottom rule under a list row, inset like the window's.
    func paperRowRule() -> some View {
        overlay(alignment: .bottom) { Rectangle().fill(Paper.hairline).frame(height: 1) }
    }
}
