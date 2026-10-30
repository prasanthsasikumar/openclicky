//
//  MediaDashboardColumn.swift
//  OpenClicky
//
//  The now-playing column of the open island's dashboard: big artwork with the player's icon on
//  its corner, the title and artist, a bar tinted by the artwork that runs with the track (drag or
//  click to seek), the times, and the transport with the output picker beside it.
//

import AppKit
import AVKit
import SwiftUI

struct MediaDashboardColumn: View {
    @ObservedObject var media: MediaStore

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            artwork
            if let snapshot = media.snapshot {
                trackInfo(snapshot)
            } else {
                emptyInfo
            }
        }
        .frame(width: IslandDash.mediaWidth, alignment: .topLeading)
        .onAppear { media.beginWatching() }
        .onDisappear { media.endWatching() }
    }

    // MARK: Artwork

    private var artwork: some View {
        ZStack {
            if let image = media.artwork {
                Image(nsImage: image)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
                    .transition(.opacity)
            } else {
                LinearGradient(colors: [Color(white: 0.5), Color(white: 0.2)], startPoint: .top, endPoint: .bottom)
                Image(systemName: "music.note")
                    .font(.system(size: IslandDash.artworkSize * 0.4))
                    .foregroundColor(.white.opacity(0.7))
            }
        }
        .frame(width: IslandDash.artworkSize, height: IslandDash.artworkSize)
        .clipShape(RoundedRectangle(cornerRadius: IslandDash.artworkRadius, style: .continuous))
        .shadow(color: accent.opacity(media.artwork == nil ? 0 : 0.35), radius: 10)
        .overlay(alignment: .bottomTrailing) {
            if let player = media.snapshot?.player, let icon = Self.appIcon(for: player) {
                Image(nsImage: icon)
                    .resizable()
                    .frame(width: 26, height: 26)
                    .shadow(color: .black.opacity(0.35), radius: 1.5)
                    .offset(x: 6, y: 6)
            }
        }
        .animation(.easeOut(duration: 0.3), value: media.artwork)
        .accessibilityHidden(true)
    }

    /// The player's own icon for the artwork's corner, fetched once per player.
    @MainActor private static var iconCache: [MediaPlayer: NSImage] = [:]
    @MainActor static func appIcon(for player: MediaPlayer) -> NSImage? {
        if let cached = iconCache[player] { return cached }
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: player.bundleIdentifier) else { return nil }
        let icon = NSWorkspace.shared.icon(forFile: url.path)
        iconCache[player] = icon
        return icon
    }

    /// The bar's tint: the artwork's average colour, lifted so a dark cover still shows; white
    /// without artwork.
    private var accent: Color {
        guard let image = media.artwork, let color = ArtworkAccent.color(of: image) else { return .white }
        return Color(nsColor: color)
    }

    // MARK: Track

    private func trackInfo(_ snapshot: MediaSnapshot) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(snapshot.title)
                .font(.system(size: 13, weight: .bold))
                .foregroundColor(.white)
                .lineLimit(1)
            Text(snapshot.artist.isEmpty ? snapshot.player.displayName.capitalized : snapshot.artist)
                .font(.system(size: 12))
                .foregroundColor(.white.opacity(0.6))
                .lineLimit(1)
            DashboardScrubber(media: media, snapshot: snapshot, tint: accent)
                .padding(.top, 4)
            HStack(spacing: 0) {
                Spacer(minLength: 0)
                Button(action: { Task { await media.previous() } }) { Image(systemName: "backward.fill") }
                    .buttonStyle(IslandIconButtonStyle(size: 15, frameScale: 2))
                    .help("Previous")
                    .accessibilityLabel("Previous")
                Button(action: { Task { await media.playPause() } }) {
                    Image(systemName: snapshot.isPlaying ? "pause.fill" : "play.fill").contentTransition(.identity)
                }
                .buttonStyle(IslandIconButtonStyle(size: 20, frameScale: 1.7))
                .help(snapshot.isPlaying ? "Pause" : "Play")
                .accessibilityLabel(snapshot.isPlaying ? "Pause" : "Play")
                Button(action: { Task { await media.next() } }) { Image(systemName: "forward.fill") }
                    .buttonStyle(IslandIconButtonStyle(size: 15, frameScale: 2))
                    .help("Next")
                    .accessibilityLabel("Next")
                Spacer(minLength: 0)
                AudioRoutePicker()
                    .frame(width: 24, height: 24)
                    .help("Play on another speaker")
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.top, 2)
    }

    private var emptyInfo: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Nothing playing")
                .font(.system(size: 14, weight: .semibold))
                .foregroundColor(.white)
            Text(media.refusedPlayer.map(MediaIslandView.hint(refused:)) ?? "Play something in Music or Spotify and it shows up here.")
                .font(.system(size: 12))
                .foregroundColor(.white.opacity(0.55))
                .lineLimit(3)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.top, 8)
    }
}

/// The track's bar: a 5 pt track filled to the position in the artwork's tint, elapsed time on
/// the left and what is left on the right. A click or a drag seeks.
private struct DashboardScrubber: View {
    @ObservedObject var media: MediaStore
    let snapshot: MediaSnapshot
    let tint: Color
    /// While dragging, where the thumb is (0…1); the track follows the finger, not the player.
    @State private var dragFraction: Double?

    var body: some View {
        TimelineView(.periodic(from: .now, by: snapshot.isPlaying ? 0.5 : 3600)) { timeline in
            let position = snapshot.position(at: timeline.date)
            let playing = snapshot.duration > 0 ? position / snapshot.duration : 0
            let fraction = min(max(dragFraction ?? playing, 0), 1)
            VStack(spacing: 3) {
                GeometryReader { proxy in
                    ZStack(alignment: .leading) {
                        Capsule().fill(IslandDash.track)
                        Capsule().fill(tint).frame(width: max(5, proxy.size.width * CGFloat(fraction)))
                    }
                    .frame(height: 5)
                    .frame(maxHeight: .infinity)
                    .contentShape(Rectangle())
                    .gesture(
                        DragGesture(minimumDistance: 0)
                            .onChanged { value in dragFraction = Double(value.location.x / max(proxy.size.width, 1)) }
                            .onEnded { value in
                                let target = min(max(Double(value.location.x / max(proxy.size.width, 1)), 0), 1)
                                dragFraction = nil
                                Task { await media.seek(to: target) }
                            }
                    )
                    .pointerCursor()
                }
                .frame(height: 10)
                HStack {
                    Text(MediaFormatter.clock(snapshot.duration * fraction))
                    Spacer()
                    Text("-" + MediaFormatter.clock(max(snapshot.duration - snapshot.duration * fraction, 0)))
                }
                .font(.system(size: 10, weight: .medium).monospacedDigit())
                .foregroundColor(.white.opacity(0.55))
            }
            .animation(.easeOut(duration: 0.12), value: dragFraction)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(MediaFormatter.clock(snapshot.position)) of \(MediaFormatter.clock(snapshot.duration))")
    }
}

/// The system's speaker picker, borderless and white so it sits with the transport.
private struct AudioRoutePicker: NSViewRepresentable {
    func makeNSView(context: Context) -> AVRoutePickerView {
        let picker = AVRoutePickerView()
        picker.isRoutePickerButtonBordered = false
        picker.setRoutePickerButtonColor(.white.withAlphaComponent(0.85), for: .normal)
        picker.setRoutePickerButtonColor(.white, for: .normalHighlighted)
        return picker
    }

    func updateNSView(_ nsView: AVRoutePickerView, context: Context) {}
}

/// The average colour of a cover, brightened so the bar shows on black.
enum ArtworkAccent {
    @MainActor private static var cache: (image: ObjectIdentifier, color: NSColor)?

    @MainActor static func color(of image: NSImage) -> NSColor? {
        if let cache, cache.image == ObjectIdentifier(image) { return cache.color }
        guard let cgImage = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return nil }
        var pixel = [UInt8](repeating: 0, count: 4)
        guard let context = CGContext(data: &pixel, width: 1, height: 1, bitsPerComponent: 8, bytesPerRow: 4,
                                      space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        context.interpolationQuality = .medium
        context.draw(cgImage, in: CGRect(x: 0, y: 0, width: 1, height: 1))
        let average = NSColor(srgbRed: CGFloat(pixel[0]) / 255, green: CGFloat(pixel[1]) / 255, blue: CGFloat(pixel[2]) / 255, alpha: 1)
        let lifted = lift(average)
        cache = (ObjectIdentifier(image), lifted)
        return lifted
    }

    /// Keeps the hue, softens the saturation and raises the brightness to at least 0.75.
    static func lift(_ color: NSColor) -> NSColor {
        var hue: CGFloat = 0, saturation: CGFloat = 0, brightness: CGFloat = 0, alpha: CGFloat = 0
        (color.usingColorSpace(.sRGB) ?? color).getHue(&hue, saturation: &saturation, brightness: &brightness, alpha: &alpha)
        return NSColor(hue: hue, saturation: min(saturation, 0.45), brightness: max(brightness, 0.75), alpha: 1)
    }
}
