//
//  DrawStageBubble.swift
//  OpenClicky
//
//  A finished drawing shrinks into a bubble beside the island: a small thumbnail of the card on
//  paper. A click brings the drawing back full size (the page still holds it); ✕ on hover throws
//  it away. Only the last drawing is kept: a new one replaces the bubble.
//

import AppKit
import Combine
import SwiftUI

/// Where the bubble goes for the island's placement, as pure geometry.
enum DrawStageBubblePlacement {
    static let width: CGFloat = 96
    static let gap: CGFloat = 12
    static let screenInset: CGFloat = 16
    static let shortestHeight: CGFloat = 54
    static let tallestHeight: CGFloat = 120

    /// `islandFrame` and `screenVisibleFrame` are AppKit rects (origin bottom-left). A left drawer
    /// gets the bubble just outside its right edge, a right drawer just outside its left edge,
    /// the notch island below-right of itself; with the island hidden or absent, the screen's
    /// top-right corner. The bubble never leaves the visible frame.
    static func frame(islandFrame: CGRect?, placement: HUDPlacement, screenVisibleFrame: CGRect, thumbnailAspect: CGFloat) -> CGRect {
        let height = thumbnailAspect > 0 ? min(tallestHeight, max(shortestHeight, (width / thumbnailAspect).rounded())) : 64
        var frame: CGRect
        switch (placement, islandFrame) {
        case (.left, .some(let island)):
            frame = CGRect(x: island.maxX + gap, y: island.maxY - height, width: width, height: height)
        case (.right, .some(let island)):
            frame = CGRect(x: island.minX - gap - width, y: island.maxY - height, width: width, height: height)
        case (.notch, .some(let island)):
            frame = CGRect(x: island.maxX + gap, y: island.minY - 8 - height, width: width, height: height)
        default:
            frame = CGRect(x: screenVisibleFrame.maxX - screenInset - width, y: screenVisibleFrame.maxY - screenInset - height, width: width, height: height)
        }
        frame.origin.x = min(max(frame.minX, screenVisibleFrame.minX + screenInset), screenVisibleFrame.maxX - screenInset - width)
        frame.origin.y = min(max(frame.minY, screenVisibleFrame.minY + screenInset), screenVisibleFrame.maxY - screenInset - height)
        return frame
    }
}

@MainActor
final class DrawStageBubble {
    static let shared = DrawStageBubble()

    /// A click on the thumbnail: the drawing comes back.
    var onRestore: (() -> Void)?
    /// The ✕: the drawing is thrown away.
    var onDiscard: (() -> Void)?

    private var panel: NSPanel?
    private let model = DrawStageBubbleModel()
    private(set) var isShowing = false

    func show(thumbnail: NSImage, islandFrame: CGRect?, placement: HUDPlacement, screen: NSScreen) {
        let aspect = thumbnail.size.height > 0 ? thumbnail.size.width / thumbnail.size.height : 1.5
        let frame = DrawStageBubblePlacement.frame(islandFrame: islandFrame, placement: placement, screenVisibleFrame: screen.visibleFrame, thumbnailAspect: aspect)
        model.thumbnail = thumbnail
        let panel = self.panel ?? makePanel()
        self.panel = panel
        panel.setFrame(frame, display: true)
        panel.alphaValue = 0
        panel.orderFrontRegardless()
        isShowing = true
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.18
            panel.animator().alphaValue = 1
        }
    }

    func dismiss() {
        guard isShowing, let panel else { return }
        isShowing = false
        NSAnimationContext.runAnimationGroup({ context in
            context.duration = 0.15
            panel.animator().alphaValue = 0
        }, completionHandler: { [weak panel] in panel?.orderOut(nil) })
    }

    private func makePanel() -> NSPanel {
        let panel = NSPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        // The island's level, so the bubble beside it is never under it.
        panel.level = .popUpMenu
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.contentView = NSHostingView(rootView: DrawStageBubbleView(
            model: model,
            onRestore: { [weak self] in self?.onRestore?() },
            onDiscard: { [weak self] in self?.onDiscard?() }
        ))
        return panel
    }
}

@MainActor
final class DrawStageBubbleModel: ObservableObject {
    @Published var thumbnail: NSImage?
}

/// The thumbnail on a paper card with a soft shadow; ✕ at the top-right on hover.
struct DrawStageBubbleView: View {
    @ObservedObject var model: DrawStageBubbleModel
    let onRestore: () -> Void
    let onDiscard: () -> Void
    @State private var isHovering = false

    var body: some View {
        ZStack(alignment: .topTrailing) {
            Group {
                if let thumbnail = model.thumbnail {
                    Image(nsImage: thumbnail).resizable().aspectRatio(contentMode: .fill)
                } else {
                    Color(red: 0.99, green: 0.97, blue: 0.92)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Color(red: 0.99, green: 0.97, blue: 0.92))
            .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(Color.black.opacity(0.12), lineWidth: 1))
            .shadow(color: .black.opacity(0.25), radius: 8, y: 3)
            .contentShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
            .onTapGesture { onRestore() }
            .pointerCursor()
            .help("Bring the drawing back")

            if isHovering {
                Button(action: onDiscard) {
                    Image(systemName: "xmark")
                        .font(.system(size: 9, weight: .bold))
                        .foregroundStyle(.white)
                        .frame(width: 18, height: 18)
                        .background(Circle().fill(Color.black.opacity(0.7)))
                }
                .buttonStyle(.plain)
                .pointerCursor()
                .padding(4)
                .help("Throw the drawing away")
            }
        }
        .padding(2)
        .onHover { isHovering = $0 }
    }
}
