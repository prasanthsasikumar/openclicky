//
//  IslandHome.swift
//  OpenClicky
//
//  The open island's home: what it shows when nothing else is going on. The columns that are on
//  sit side by side under the band (now playing, the calendar, then the widget tiles), each a
//  hairline apart, and the island grows to fit them. With none on, home is the shortcuts row.
//

import Combine
import Foundation
import SwiftUI

/// One column of the home row.
enum IslandHomeColumn: String, Equatable, CaseIterable {
    case media
    case calendar
    case weather
    case system

    /// Tiles share one column, packed closer than columns are.
    var isTile: Bool { self == .weather || self == .system }
}

/// The home row's columns and sizes, from the switches. Pure, so the sizes are tested.
struct IslandHomeLayout: Equatable {
    static let calendarEnabledKey = "openclicky.calendar.enabled"
    static let weatherEnabledKey = "openclicky.weather.enabled"
    static let calendarWidth: CGFloat = 268

    let columns: [IslandHomeColumn]

    /// From the plugin switches (Now playing, System) and the calendar and weather switches.
    static func current(defaults: UserDefaults = .standard) -> IslandHomeLayout {
        let plugins = IslandComponentSettings(defaults: defaults).enabledPlugins
        let calendarOn = defaults.object(forKey: calendarEnabledKey) as? Bool ?? true
        let weatherOn = defaults.object(forKey: weatherEnabledKey) as? Bool ?? false
        return IslandHomeLayout(
            mediaOn: plugins.contains(IslandPluginRegistry.mediaID),
            calendarOn: calendarOn,
            weatherOn: weatherOn,
            systemOn: plugins.contains(IslandPluginRegistry.systemID))
    }

    init(mediaOn: Bool, calendarOn: Bool, weatherOn: Bool, systemOn: Bool) {
        var columns: [IslandHomeColumn] = []
        if mediaOn { columns.append(.media) }
        if calendarOn { columns.append(.calendar) }
        if weatherOn { columns.append(.weather) }
        if systemOn { columns.append(.system) }
        self.columns = columns
    }

    var isEmpty: Bool { columns.isEmpty }

    /// The groups drawn with a divider between them: each column on its own, the tiles together.
    var groups: [[IslandHomeColumn]] {
        var groups: [[IslandHomeColumn]] = []
        for column in columns {
            if column.isTile, let last = groups.last, last.first?.isTile == true {
                groups[groups.count - 1].append(column)
            } else {
                groups.append([column])
            }
        }
        return groups
    }

    static func width(of group: [IslandHomeColumn]) -> CGFloat {
        if group.first?.isTile == true {
            let count = CGFloat(group.count)
            return count * IslandDash.tileSize + (count - 1) * IslandDash.tileSpacing
        }
        switch group.first {
        case .media: return IslandDash.mediaWidth
        case .calendar: return calendarWidth
        default: return 0
        }
    }

    /// The island's width for this row: the groups, a divider and two gaps between each, and the
    /// side padding. Nil when nothing is on (home falls back to the shortcuts).
    var width: CGFloat? {
        guard !isEmpty else { return nil }
        let groups = self.groups
        let content = groups.map(Self.width(of:)).reduce(0, +)
        let gutters = CGFloat(groups.count - 1) * (IslandDash.columnSpacing * 2 + 1)
        return content + gutters + IslandDash.sidePadding * 2
    }

    /// The tile height, a little air above and more below.
    static let rowTopPadding: CGFloat = 2
    static let rowBottomPadding: CGFloat = 14
    var height: CGFloat {
        isEmpty ? NotchPanelBody.shortcutsHeight : IslandDash.tileSize + Self.rowTopPadding + Self.rowBottomPadding
    }
}

/// Home in the island's slot.
struct HomeIslandComponent: IslandComponent {
    let id = IslandComponentRegistry.homeID
    let title = "Home"
    let symbolName = "square.grid.2x2"
    var preferredHeight: CGFloat { IslandHomeLayout.current().height }
    var preferredWidth: CGFloat? { IslandHomeLayout.current().width }
    func isAvailable(in context: IslandContext) -> Bool { true }
    @MainActor func makeView(companionManager: CompanionManager, model: NotchHUDModel) -> AnyView {
        AnyView(IslandHomeView(model: model, companionManager: companionManager))
    }
}

struct IslandHomeView: View {
    @ObservedObject var model: NotchHUDModel
    @ObservedObject var companionManager: CompanionManager
    @State private var layout = IslandHomeLayout.current()

    var body: some View {
        Group {
            if layout.isEmpty || model.placement.isSideDrawer {
                NotchPanelBody(model: model, companionManager: companionManager)
            } else {
                HStack(alignment: .top, spacing: IslandDash.columnSpacing) {
                    ForEach(Array(layout.groups.enumerated()), id: \.offset) { index, group in
                        if index > 0 {
                            IslandDash.divider.frame(width: 1).padding(.vertical, 6)
                        }
                        groupView(group)
                    }
                }
                .padding(.top, IslandHomeLayout.rowTopPadding)
                // The slot already insets by the band's side padding; home wants a little more.
                .padding(.horizontal, IslandDash.sidePadding - DS.HUD.bodySidePadding)
                .frame(maxWidth: .infinity, alignment: .topLeading)
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: UserDefaults.didChangeNotification).receive(on: DispatchQueue.main)) { _ in
            let next = IslandHomeLayout.current()
            if next != layout {
                withAnimation(IslandDash.columnSpring(reduceMotion: model.reduceMotion)) { layout = next }
            }
        }
    }

    @ViewBuilder private func groupView(_ group: [IslandHomeColumn]) -> some View {
        if group.first?.isTile == true {
            HStack(spacing: IslandDash.tileSpacing) {
                ForEach(group, id: \.self) { tile($0) }
            }
        } else if let column = group.first {
            switch column {
            case .media:
                MediaDashboardColumn(media: companionManager.notchHUDManager.media)
            case .calendar:
                IslandHomeCalendarColumn()
                    .frame(width: IslandHomeLayout.calendarWidth, alignment: .topLeading)
            case .weather, .system:
                EmptyView()
            }
        }
    }

    @ViewBuilder private func tile(_ column: IslandHomeColumn) -> some View {
        switch column {
        case .weather: IslandHomeWeatherTile()
        case .system: IslandHomeSystemTile(store: companionManager.notchHUDManager.systemStats)
        case .media, .calendar: EmptyView()
        }
    }
}

// The calendar, weather and system pieces live in their plugin folders; these name what home
// embeds, so the row above reads the same whatever each one draws.
struct IslandHomeCalendarColumn: View {
    var body: some View { Color.clear }
}

struct IslandHomeWeatherTile: View {
    var body: some View { WeatherTile() }
}

struct IslandHomeSystemTile: View {
    @ObservedObject var store: SystemStatsStore
    var body: some View { SystemRingsTile(store: store) }
}
