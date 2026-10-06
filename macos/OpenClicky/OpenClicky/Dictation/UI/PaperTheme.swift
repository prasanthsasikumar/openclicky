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
    // Colours adapt to the window's appearance through NSColor's dynamic providers.
    static let background = Color(nsColor: dynamic(light: NSColor(red: 0.965, green: 0.953, blue: 0.918, alpha: 1), dark: NSColor(red: 0.11, green: 0.11, blue: 0.10, alpha: 1)))
    static let rail = Color(nsColor: dynamic(light: NSColor(red: 0.945, green: 0.933, blue: 0.898, alpha: 1), dark: NSColor(red: 0.085, green: 0.085, blue: 0.08, alpha: 1)))
    static let card = Color(nsColor: dynamic(light: NSColor(red: 0.985, green: 0.978, blue: 0.955, alpha: 1), dark: NSColor(red: 0.15, green: 0.15, blue: 0.14, alpha: 1)))
    static let cardRaised = Color(nsColor: dynamic(light: NSColor.white, dark: NSColor(red: 0.19, green: 0.19, blue: 0.18, alpha: 1)))
    static let hairline = Color(nsColor: dynamic(light: NSColor(red: 0.85, green: 0.83, blue: 0.78, alpha: 1), dark: NSColor(white: 0.24, alpha: 1)))
    static let ink = Color(nsColor: dynamic(light: NSColor(red: 0.12, green: 0.13, blue: 0.11, alpha: 1), dark: NSColor(red: 0.93, green: 0.92, blue: 0.89, alpha: 1)))
    static let inkSecondary = Color(nsColor: dynamic(light: NSColor(red: 0.40, green: 0.41, blue: 0.37, alpha: 1), dark: NSColor(white: 0.66, alpha: 1)))
    static let inkTertiary = Color(nsColor: dynamic(light: NSColor(red: 0.58, green: 0.59, blue: 0.54, alpha: 1), dark: NSColor(white: 0.48, alpha: 1)))
    /// The accent: OpenClicky's coral, the buddy's colour.
    static let accent = Color(nsColor: dynamic(light: NSColor(red: 0.86, green: 0.33, blue: 0.21, alpha: 1), dark: NSColor(red: 0.96, green: 0.45, blue: 0.33, alpha: 1)))
    static let accentSoft = Color(nsColor: dynamic(light: NSColor(red: 0.97, green: 0.80, blue: 0.72, alpha: 1), dark: NSColor(red: 0.45, green: 0.22, blue: 0.16, alpha: 1)))
    static let highlighter = Color(nsColor: dynamic(light: NSColor(red: 0.99, green: 0.84, blue: 0.70, alpha: 1), dark: NSColor(red: 0.42, green: 0.25, blue: 0.16, alpha: 1)))
    static let success = Color(nsColor: dynamic(light: NSColor(red: 0.25, green: 0.50, blue: 0.30, alpha: 1), dark: NSColor(red: 0.45, green: 0.72, blue: 0.50, alpha: 1)))
    static let danger = Color(nsColor: dynamic(light: NSColor(red: 0.70, green: 0.25, blue: 0.20, alpha: 1), dark: NSColor(red: 0.95, green: 0.45, blue: 0.40, alpha: 1)))
    static let selection = Color(nsColor: dynamic(light: NSColor(red: 0.95, green: 0.88, blue: 0.80, alpha: 1), dark: NSColor(white: 0.2, alpha: 1)))

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
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Paper.card))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(Paper.hairline, lineWidth: 1))
    }
}

/// A settings row: title, a line of copy, and whatever sits on the right.
struct PaperRow<Trailing: View>: View {
    let title: String
    var detail: String?
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(alignment: .center, spacing: 16) {
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(Paper.body(13, weight: .medium)).foregroundStyle(Paper.ink)
                if let detail { Text(detail).font(Paper.body(11)).foregroundStyle(Paper.inkSecondary).fixedSize(horizontal: false, vertical: true) }
            }
            Spacer(minLength: 12)
            trailing
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 13)
    }
}

struct PaperDivider: View {
    var body: some View { Rectangle().fill(Paper.hairline).frame(height: 1).padding(.horizontal, 18) }
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

/// An outlined pill button ("reset", "replay", "copy").
struct PaperPillButtonStyle: ButtonStyle {
    var prominent = false
    var destructive = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(Paper.body(12, weight: .medium))
            .foregroundStyle(destructive ? Paper.danger : (prominent ? Color.white : Paper.ink))
            .padding(.horizontal, 12)
            .padding(.vertical, 6)
            .background(
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .fill(prominent ? Paper.accent : Paper.cardRaised.opacity(configuration.isPressed ? 0.6 : 1)))
            .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(destructive ? Paper.danger.opacity(0.5) : (prominent ? Color.clear : Paper.hairline)))
            .opacity(configuration.isPressed ? 0.8 : 1)
            .pointerCursor()
    }
}

/// A keycap, like "fn".
struct Keycap: View {
    let text: String
    var body: some View {
        Text(text)
            .font(Paper.mono(10))
            .foregroundStyle(Paper.inkSecondary)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(RoundedRectangle(cornerRadius: 4).fill(Paper.cardRaised))
            .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(Paper.hairline))
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
                        .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(selection == option ? Paper.accent : Color.clear))
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
            OrbMarkShape().fill(Paper.accent).frame(width: 22, height: 22)
            Text("openclicky").font(.system(size: 26, weight: .medium, design: .serif)).foregroundStyle(Paper.ink)
        }
    }
}

extension View {
    /// Bottom rule under a list row, inset like the window's.
    func paperRowRule() -> some View {
        overlay(alignment: .bottom) { Rectangle().fill(Paper.hairline).frame(height: 1) }
    }
}
