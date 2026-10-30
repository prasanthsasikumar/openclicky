//
//  TimerSettings.swift
//  OpenClicky
//
//  The Timer plugin's rows in Settings: its switch, then how long focus and each break last.
//

import SwiftUI

extension SettingsCatalog {
    func timerSettingsItems() -> [SettingsItem] {
        let section = "Timer"
        let minuteRows: [SettingsItem] = [
            minutesItem(id: "timer.focusMinutes", section: section, title: "Focus", detail: "How long each focus round lasts.",
                        key: TimerSettings.focusMinutesKey, fallback: TimerSettings.defaultFocusMinutes),
            minutesItem(id: "timer.breakMinutes", section: section, title: "Break", detail: "The short break after each focus round.",
                        key: TimerSettings.breakMinutesKey, fallback: TimerSettings.defaultBreakMinutes),
            minutesItem(id: "timer.longBreakMinutes", section: section, title: "Long break", detail: "The longer break after every fourth round.",
                        key: TimerSettings.longBreakMinutesKey, fallback: TimerSettings.defaultLongBreakMinutes),
        ]
        return [pluginItem(IslandPluginRegistry.timerID, page: .tools, section: section, keywords: ["focus", "break", "stopwatch", "countdown", "rounds"])].compactMap { $0 }
            + minuteRows
    }

    private func minutesItem(id: String, section: String, title: String, detail: String, key: String, fallback: Int) -> SettingsItem {
        SettingsItem(
            id: id, page: .tools, section: section, title: title, detail: detail,
            keywords: ["timer", "minutes", "focus", "break"],
            view: AnyView(TimerMinutesRow(title: title, detail: detail, key: key, fallback: fallback)))
    }
}

/// One phase's length: a menu of whole minutes, stored as an integer under `key`.
private struct TimerMinutesRow: View {
    let title: String
    let detail: String
    let key: String
    let fallback: Int
    @AppStorage private var minutes: Int

    init(title: String, detail: String, key: String, fallback: Int) {
        self.title = title
        self.detail = detail
        self.key = key
        self.fallback = fallback
        self._minutes = AppStorage(wrappedValue: fallback, key)
    }

    var body: some View {
        SettingsRow(title: title, detail: detail) {
            Picker("", selection: $minutes) {
                // A value stored outside the menu's steps still shows, rather than a blank menu.
                ForEach(choices, id: \.self) { value in Text("\(value) min").tag(value) }
            }
            .labelsHidden()
            .fixedSize()
            .pointerCursor()
            .accessibilityLabel(title)
        }
    }

    private var choices: [Int] {
        TimerSettings.minuteChoices.contains(minutes) ? TimerSettings.minuteChoices : (TimerSettings.minuteChoices + [minutes]).sorted()
    }
}
