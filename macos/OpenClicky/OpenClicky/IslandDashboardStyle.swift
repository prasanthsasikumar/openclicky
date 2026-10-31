//
//  IslandDashboardStyle.swift
//  OpenClicky
//
//  The open island's dashboard look, shared by every column and tile on it: white at low opacity
//  on the black or glass island, continuous corners, small semibold type, monospaced digits.
//

import SwiftUI

enum IslandDash {
    /// Every row of the open island is inset this far from its sides.
    static let sidePadding: CGFloat = 20
    /// Columns sit this far apart, a hairline divider between them.
    static let columnSpacing: CGFloat = 16
    static let divider = Color.white.opacity(0.08)
    /// The dashboard row's height under the band.
    static let contentHeight: CGFloat = 130

    /// The now-playing column.
    static let mediaWidth: CGFloat = 300
    static let artworkSize: CGFloat = 84
    static let artworkRadius: CGFloat = 12

    /// A widget tile: a square with soft corners, a faint fill and a hairline border.
    static let tileSize: CGFloat = 118
    static let tileSpacing: CGFloat = 8
    static let tileRadius: CGFloat = 20
    static let tileFill = Color(white: 0.11)
    static let tileBorder = Color.white.opacity(0.08)

    /// Round icon buttons (the toolbar, transport) are white 0.12 under the pointer.
    static let hoverFill = Color.white.opacity(0.12)
    static let pillFill = Color.white.opacity(0.16)
    static let track = Color.white.opacity(0.18)
    static let secondaryText = Color.white.opacity(0.55)
    static let today = Color(nsColor: .systemRed)

    /// Layout springs; Reduce motion makes each a short ease.
    static func columnSpring(reduceMotion: Bool) -> Animation {
        reduceMotion ? .easeInOut(duration: 0.16) : .spring(response: 0.32, dampingFraction: 0.88)
    }
}

/// The chrome every widget tile shares: the faint fill (or a background of its own, the weather's
/// sky), the hairline border, the soft corners.
struct IslandTile<Content: View, Background: View>: View {
    var padding: CGFloat = 10
    @ViewBuilder var background: Background
    @ViewBuilder var content: Content

    var body: some View {
        content
            .padding(padding)
            .frame(width: IslandDash.tileSize, height: IslandDash.tileSize, alignment: .topLeading)
            .background(background)
            .overlay(RoundedRectangle(cornerRadius: IslandDash.tileRadius, style: .continuous).strokeBorder(IslandDash.tileBorder, lineWidth: 1))
            .clipShape(RoundedRectangle(cornerRadius: IslandDash.tileRadius, style: .continuous))
    }
}

extension IslandTile where Background == Color {
    init(padding: CGFloat = 10, @ViewBuilder content: () -> Content) {
        self.padding = padding
        self.background = IslandDash.tileFill
        self.content = content()
    }
}

/// A round icon button on the island: the symbol in a circle that lights up under the pointer and
/// shrinks a touch while pressed.
struct IslandIconButtonStyle: ButtonStyle {
    var size: CGFloat = 11
    var frameScale: CGFloat = 2
    var isActive = false

    func makeBody(configuration: Configuration) -> some View {
        IslandIconButtonBody(configuration: configuration, size: size, frameScale: frameScale, isActive: isActive)
    }
}

private struct IslandIconButtonBody: View {
    let configuration: ButtonStyle.Configuration
    let size: CGFloat
    let frameScale: CGFloat
    let isActive: Bool
    @State private var isHovered = false

    var body: some View {
        configuration.label
            .font(.system(size: size, weight: .semibold))
            .foregroundColor(.white.opacity(isActive || isHovered ? 1 : 0.85))
            .frame(width: size * frameScale, height: size * frameScale)
            .background(Circle().fill(isActive || isHovered || configuration.isPressed ? IslandDash.hoverFill : Color.clear))
            .contentShape(Circle())
            .scaleEffect(configuration.isPressed ? 0.88 : 1)
            .animation(.easeOut(duration: 0.12), value: isHovered)
            .onHover { isHovered = $0 }
            .pointerCursor()
    }
}

/// A small pill button on the island ("Today", "Allow").
struct IslandPillButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 11, weight: .semibold))
            .foregroundColor(.white)
            .padding(.horizontal, 12)
            .padding(.vertical, 5)
            .background(Capsule().fill(Color.white.opacity(configuration.isPressed ? 0.25 : 0.16)))
            .pointerCursor()
    }
}
