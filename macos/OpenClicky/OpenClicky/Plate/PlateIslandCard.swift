//
//  PlateIslandCard.swift
//  OpenClicky
//
//  What the island shows for the day's cards: the announcement (the weekday, how many, a fan of
//  three cards, not now / see all), the header over the open deck, a line while the cards are
//  being written or when there are none, and the one question afterwards.
//

import Combine
import SwiftUI

enum PlateIslandCard: Equatable {
    case announce(dayPart: String, count: Int, titles: [String])
    case deck(count: Int)
    case note(String, isWorking: Bool)
    case feedback
    /// After a rating that was not a plain yes: one line on what would have been better.
    case tell
    case thanks(String)

    /// The card's height under the island's top inset.
    var contentHeight: CGFloat {
        switch self {
        case .announce: return 214
        case .deck: return 56
        case .note: return 46
        case .feedback: return 86
        case .tell: return 96
        case .thanks: return 92
        }
    }
}

struct PlateIslandCardView: View {
    let card: PlateIslandCard
    let controller: PlateController
    var topInset: CGFloat = 0
    var reduceMotion = false

    var body: some View {
        Group {
            switch card {
            case .announce(let dayPart, let count, let titles):
                announce(dayPart: dayPart, count: count, titles: titles)
            case .deck(let count):
                deckHeader(count: count)
            case .note(let text, let isWorking):
                HStack(spacing: 8) {
                    if isWorking { ProgressView().controlSize(.small).colorScheme(.dark) }
                    Text(text).font(.system(size: 13, weight: .medium)).foregroundColor(DS.HUD.text)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            case .feedback:
                feedback
            case .tell:
                PlateTellView(controller: controller)
            case .thanks(let line):
                thanks(line)
            }
        }
        .padding(.top, topInset)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
    }

    private func announce(dayPart: String, count: Int, titles: [String]) -> some View {
        VStack(spacing: 0) {
            Text(dayPart.uppercased())
                .font(.system(size: 10, weight: .semibold))
                .tracking(1.6)
                .foregroundColor(DS.HUD.text3)
                .padding(.top, 8)
            Text(PlateSchedule.headline(count: count))
                .font(.system(size: 16, weight: .bold))
                .foregroundColor(DS.HUD.text)
                .padding(.top, 3)
            PlateFan(titles: titles, reduceMotion: reduceMotion)
                .frame(height: 112)
                .padding(.top, 8)
            HStack(spacing: 10) {
                Button(action: { controller.notNow() }) {
                    Text("Later")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundColor(DS.HUD.text)
                        .frame(width: 132, height: 32)
                        .background(Capsule().fill(DS.HUD.bandControl))
                        .overlay(Capsule().stroke(Color.white.opacity(0.14), lineWidth: 1))
                }
                .buttonStyle(.plain)
                .pointerCursor()
                Button(action: { controller.openDeck() }) {
                    Text(count == 1 ? "Show me" : "Show me all \(count)")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundColor(PlatePaper.ink)
                        .frame(width: 132, height: 32)
                        .background(Capsule().fill(PlatePaper.blueFill))
                        .overlay(Capsule().stroke(PlatePaper.blue, lineWidth: 1.2))
                }
                .buttonStyle(.plain)
                .pointerCursor()
            }
            .padding(.top, 8)
        }
    }

    private func deckHeader(count: Int) -> some View {
        ZStack {
            VStack(spacing: 2) {
                Text(PlateSchedule.headline(count: count))
                    .font(.system(size: 14, weight: .bold))
                    .foregroundColor(DS.HUD.text)
                Text("Rest on a card for its plan. Go ahead, change it, or bin it.")
                    .font(.system(size: 12))
                    .foregroundColor(DS.HUD.text2)
            }
            HStack {
                Spacer()
                Button(action: { controller.closeDeck() }) {
                    Image(systemName: "xmark")
                        .font(.system(size: 10, weight: .bold))
                        .foregroundColor(DS.HUD.text)
                        .frame(width: 24, height: 24)
                        .background(Circle().fill(DS.HUD.surfaceRaised))
                }
                .buttonStyle(.plain)
                .pointerCursor()
                .accessibilityLabel("close the offers")
                .padding(.trailing, 16)
            }
        }
        .frame(maxHeight: .infinity)
    }

    private var feedback: some View {
        ZStack(alignment: .topTrailing) {
            VStack(spacing: 10) {
                Text("Were these worth offering?")
                    .font(.system(size: 14, weight: .bold))
                    .foregroundColor(DS.HUD.text)
                HStack(spacing: 8) {
                    ForEach(Array(PlateRating.allCases.enumerated()), id: \.element) { index, rating in
                        Button(rating.buttonTitle) { controller.rate(rating) }
                            .buttonStyle(PlateFeedbackPillStyle())
                            .modifier(PlateRiseIn(order: index, reduceMotion: reduceMotion))
                    }
                }
            }
            .frame(maxWidth: .infinity)
            .padding(.top, 8)
            // The question goes away by itself; the ring is also "not now".
            PlateCountdownRing(seconds: 25, onDone: { controller.dismissFeedback() })
                .padding(.trailing, 16)
                .padding(.top, 6)
        }
    }

    private func thanks(_ line: String) -> some View {
        VStack(spacing: 8) {
            PlateTick(reduceMotion: reduceMotion)
            Text(line)
                .font(.system(size: 14, weight: .bold))
                .foregroundColor(DS.HUD.text)
        }
        .padding(.top, 6)
    }
}

private struct PlateFeedbackPillStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 12.5, weight: .semibold))
            .foregroundColor(PlatePaper.ink)
            .padding(.horizontal, 14)
            .frame(height: 28)
            .background(Capsule().fill(Color.white.opacity(configuration.isPressed ? 0.8 : 0.95)))
            .pointerCursor()
    }
}

/// A tick that lands with a small bounce.
private struct PlateTick: View {
    let reduceMotion: Bool
    @State private var hasLanded = false

    var body: some View {
        Image(systemName: "checkmark")
            .font(.system(size: 13, weight: .bold))
            .foregroundColor(DS.HUD.live)
            .frame(width: 34, height: 34)
            .background(Circle().fill(DS.HUD.live.opacity(0.16)))
            .overlay(Circle().stroke(DS.HUD.live.opacity(0.7), lineWidth: 1.5))
            .scaleEffect(hasLanded ? 1 : 0.4)
            .opacity(hasLanded ? 1 : 0)
            .onAppear { withAnimation(Motion.bounce(reduceMotion: reduceMotion)) { hasLanded = true } }
    }
}

/// Content that rises into place, each item a moment after the one before.
private struct PlateRiseIn: ViewModifier {
    let order: Int
    let reduceMotion: Bool
    @State private var isIn = false

    func body(content: Content) -> some View {
        content
            .offset(y: isIn || reduceMotion ? 0 : 8)
            .opacity(isIn ? 1 : 0)
            .onAppear { withAnimation(.easeOut(duration: 0.4).delay(0.1 + Double(order) * 0.06)) { isIn = true } }
    }
}

/// A ring that empties over `seconds` and then calls `onDone`; the pointer over it holds the
/// clock, and a click ends it at once.
struct PlateCountdownRing: View {
    let seconds: Double
    let onDone: () -> Void
    @State private var remaining: Double = 1
    @State private var isPaused = false
    private let tick = Timer.publish(every: 0.1, on: .main, in: .common).autoconnect()

    var body: some View {
        Button(action: onDone) {
            ZStack {
                Circle().stroke(Color.white.opacity(0.16), lineWidth: 1.8)
                Circle().trim(from: 0, to: remaining)
                    .stroke(Color.white.opacity(0.8), style: StrokeStyle(lineWidth: 1.8, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                Image(systemName: "xmark").font(.system(size: 7.5, weight: .bold)).foregroundColor(DS.HUD.text2)
            }
            .frame(width: 20, height: 20)
            .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .onHover { isPaused = $0 }
        .onReceive(tick) { _ in
            guard !isPaused, remaining > 0 else { return }
            withAnimation(.linear(duration: 0.1)) { remaining = max(0, remaining - 0.1 / seconds) }
            if remaining <= 0 { onDone() }
        }
        .accessibilityLabel("not now")
    }
}

/// "What would have been better?": one line, sent with return, skipped with esc or the button.
private struct PlateTellView: View {
    let controller: PlateController
    @State private var comment = ""

    var body: some View {
        VStack(spacing: 8) {
            Text("What would have been better?")
                .font(.system(size: 14, weight: .bold))
                .foregroundColor(DS.HUD.text)
            HStack(spacing: 8) {
                PlateTextField(text: $comment, placeholder: "Smaller jobs, more about one project…", onSubmit: { controller.tell(comment) }, onEscape: { controller.tell("") })
                    .frame(height: 28)
                    .padding(.horizontal, 10)
                    .background(Capsule().fill(Color.white.opacity(0.95)))
                Button(comment.trimmingCharacters(in: .whitespaces).isEmpty ? "Skip" : "Send") { controller.tell(comment) }
                    .buttonStyle(IslandPillButtonStyle())
            }
            .padding(.horizontal, 28)
        }
        .padding(.top, 8)
    }
}

/// Three small cards fanned under the headline, the first suggestion on top. They drop in from
/// the notch one after another.
private struct PlateFan: View {
    let titles: [String]
    let reduceMotion: Bool
    @State private var isDealt = false

    var body: some View {
        ZStack {
            if titles.count > 1 { miniCard(titles[1], order: 1).rotationEffect(.degrees(isDealt ? -8 : 0)).offset(x: isDealt ? -118 : 0, y: isDealt ? 8 : -60) }
            if titles.count > 2 { miniCard(titles[2], order: 2).rotationEffect(.degrees(isDealt ? 8 : 0)).offset(x: isDealt ? 118 : 0, y: isDealt ? 8 : -60) }
            if let first = titles.first { miniCard(first, order: 0).offset(y: isDealt ? 0 : -60).zIndex(1) }
        }
        .onAppear {
            guard !isDealt else { return }
            withAnimation(Motion.spread(reduceMotion: reduceMotion).delay(0.16)) { isDealt = true }
        }
    }

    private func miniCard(_ title: String, order: Int) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 5) {
                Circle().fill(DS.HUD.pointer).frame(width: 6, height: 6)
                Spacer()
            }
            .padding(.horizontal, 9)
            .frame(height: 18)
            .background(PlatePaper.strip)
            Text(title)
                .font(.system(size: order == 0 ? 12 : 10.5, weight: .bold))
                .foregroundColor(PlatePaper.ink)
                .lineLimit(5)
                .multilineTextAlignment(.leading)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                .padding(.horizontal, 10)
                .padding(.top, 7)
        }
        .frame(width: order == 0 ? 190 : 150, height: order == 0 ? 108 : 92)
        .background(PlatePaper.paper)
        .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
        .shadow(color: .black.opacity(0.4), radius: 6, y: 3)
        .opacity(isDealt ? 1 : 0)
        .accessibilityHidden(order != 0)
    }
}
