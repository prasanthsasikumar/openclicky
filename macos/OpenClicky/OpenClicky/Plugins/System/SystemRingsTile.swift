//
//  SystemRingsTile.swift
//  OpenClicky
//
//  The System widget tile on the open island's dashboard: CPU and memory as two rings side by
//  side, each with its figure and name under it. It shares the HUD manager's `SystemStatsStore`
//  with the System row, which samples only while one of them is on screen.
//

import AppKit
import SwiftUI

struct SystemRingsTile: View {
    @ObservedObject var store: SystemStatsStore

    private var memoryFraction: Double {
        let snapshot = store.snapshot
        return snapshot.memoryTotal > 0 ? Double(snapshot.memoryUsed) / Double(snapshot.memoryTotal) : 0
    }

    var body: some View {
        IslandTile {
            HStack(spacing: 6) {
                SystemRingGauge(fraction: store.snapshot.cpu, symbolName: "cpu", label: "CPU", ringColor: .green)
                SystemRingGauge(fraction: memoryFraction, symbolName: "memorychip", label: "Memory", ringColor: Color(nsColor: .systemBlue))
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .onAppear { store.beginWatching() }
        .onDisappear { store.endWatching() }
    }
}

/// One ring with its symbol inside and the figure and name under it.
private struct SystemRingGauge: View {
    let fraction: Double
    let symbolName: String
    let label: String
    let ringColor: Color

    /// A sliver always shows, so an idle Mac still reads as a ring that is measuring, not an empty track.
    private var visibleFill: CGFloat { CGFloat(max(min(fraction, 1), 0.01)) }

    var body: some View {
        VStack(spacing: 4) {
            ZStack {
                Circle()
                    .stroke(ringColor.opacity(0.22), lineWidth: 4.5)
                Circle()
                    .trim(from: 0, to: visibleFill)
                    .stroke(ringColor, style: StrokeStyle(lineWidth: 4.5, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                    .animation(.easeOut(duration: 0.5), value: visibleFill)
                Image(systemName: symbolName)
                    .font(.system(size: 13, weight: .medium))
                    .foregroundColor(.white)
            }
            .frame(width: 40, height: 40)
            Text(SystemStatsFormatter.percent(fraction))
                .font(.system(size: 13, weight: .semibold))
                .monospacedDigit()
                .foregroundColor(.white)
                .lineLimit(1)
            Text(label)
                .font(.system(size: 9, weight: .semibold))
                .foregroundColor(.white.opacity(0.55))
                .lineLimit(1)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(label) \(SystemStatsFormatter.percent(fraction))")
    }
}
