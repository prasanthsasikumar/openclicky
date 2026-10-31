//
//  CalendarSettings.swift
//  OpenClicky
//
//  The Calendar plugin's rows in Settings: the island switch, the quick add switch, calendar
//  access, and a checkbox per calendar under a section per account.
//

import AppKit
import SwiftUI

extension SettingsCatalog {
    func calendarSettingsItems() -> [SettingsItem] {
        let store = CalendarStore.shared
        var items: [SettingsItem] = [
            SettingsItem(
                id: "calendar.enabled",
                title: "Calendar", detail: "Your week and today's events on the open island.",
                keywords: ["calendar", "events", "island", "week", "agenda", "schedule"],
                view: AnyView(CalendarSwitchRow(
                    store: store, keyPath: \.isEnabled,
                    title: "Calendar", detail: "Your week and today's events on the open island."))),
            SettingsItem(
                id: "calendar.quickAdd",
                title: "Add events from the island", detail: "A + beside the week to add an event without opening Calendar.",
                keywords: ["calendar", "add", "new event", "quick add", "create"],
                view: AnyView(CalendarSwitchRow(
                    store: store, keyPath: \.isQuickAddEnabled,
                    title: "Add events from the island", detail: "A + beside the week to add an event without opening Calendar."))),
            SettingsItem(
                id: "calendar.access",
                title: "Calendar access", detail: "OpenClicky reads your events only to show them on the island.",
                keywords: ["calendar", "permission", "privacy", "allow", "access"],
                view: AnyView(CalendarAccessRow(store: store))),
        ]

        // One section per account, from the calendars as they are now. When none can be read yet
        // (no access, or access just granted on this page), one section lists them all as they
        // arrive, since the page's sections are fixed until it is drawn again.
        let sourceGroups = CalendarSchedule.sourceGroups(store.currentCalendars())
        if sourceGroups.isEmpty {
            items.append(SettingsItem(
                id: "calendar.calendars",
                title: "Calendars", detail: "Choose which calendars the island shows.",
                keywords: ["calendar", "calendars", "hide", "show", "account"],
                view: AnyView(CalendarAllCalendarsList(store: store))))
        } else {
            for sourceGroup in sourceGroups {
                for calendarInfo in sourceGroup.calendars {
                    items.append(SettingsItem(
                        id: "calendar.calendar.\(calendarInfo.id)", section: sourceGroup.title,
                        title: calendarInfo.title, detail: "",
                        keywords: ["calendar", "calendars", "hide", "show", "account", sourceGroup.title],
                        view: AnyView(CalendarVisibilityRow(store: store, calendarInfo: calendarInfo))))
                }
            }
        }
        return items
    }
}

/// A switch bound to one of the store's settings, observing the store so it stays live wherever
/// it is drawn (the page or a search result).
private struct CalendarSwitchRow: View {
    @ObservedObject var store: CalendarStore
    let keyPath: ReferenceWritableKeyPath<CalendarStore, Bool>
    let title: String
    let detail: String

    var body: some View {
        SettingsRow(title: title, detail: detail) {
            SettingsSwitch(isOn: Binding(get: { store[keyPath: keyPath] }, set: { store[keyPath: keyPath] = $0 }))
                .accessibilityLabel(title)
        }
    }
}

/// "Allowed" with a green check, or the button that asks (or opens System Settings once asked).
private struct CalendarAccessRow: View {
    @ObservedObject var store: CalendarStore

    private var detail: String {
        switch store.access {
        case .allowed: return "OpenClicky reads your events only to show them on the island."
        case .notDetermined: return "macOS asks once; the island shows your events after you allow it."
        case .writeOnly: return "OpenClicky may add events but not read them. Allow full access in System Settings."
        case .denied, .restricted: return "Turned off for OpenClicky. Turn it on under Privacy & Security → Calendars."
        }
    }

    var body: some View {
        SettingsRow(title: "Calendar access", detail: detail) {
            if store.access == .allowed {
                HStack(spacing: 4) {
                    Image(systemName: "checkmark.circle.fill")
                        .foregroundStyle(SettingsLook.success)
                    Text("Allowed")
                        .font(SettingsLook.caption)
                        .foregroundStyle(.secondary)
                }
            } else {
                Button(store.access.canPrompt ? "Allow" : "Open System Settings") {
                    Task { await store.requestAccess() }
                }
                .pointerCursor()
            }
        }
        .onAppear { store.refreshAccess() }
    }
}

/// A calendar's checkbox, its colour and its name.
private struct CalendarVisibilityRow: View {
    @ObservedObject var store: CalendarStore
    let calendarInfo: CalendarInfo
    @Environment(\.settingsSearchQuery) private var searchQuery

    var body: some View {
        HStack(spacing: 8) {
            Toggle("", isOn: Binding(
                get: { store.isCalendarVisible(calendarInfo.id) },
                set: { store.setCalendar(calendarInfo.id, isVisible: $0) }))
                .toggleStyle(.checkbox)
                .labelsHidden()
                .pointerCursor()
                .accessibilityLabel(calendarInfo.title)
            Circle()
                .fill(calendarInfo.color.color)
                .frame(width: 8, height: 8)
            Text(settingsHighlighted(calendarInfo.title, query: searchQuery))
                .font(SettingsLook.rowTitle)
            Spacer(minLength: 0)
        }
        .padding(.vertical, SettingsLook.Metric.rowVertical)
    }
}

/// Every calendar under its account's name, for when the page was built before any could be read.
private struct CalendarAllCalendarsList: View {
    @ObservedObject var store: CalendarStore

    var body: some View {
        Group {
            if store.access != .allowed {
                SettingsRow(title: "Calendars", detail: "Allow calendar access to choose which calendars the island shows.") {
                    EmptyView()
                }
            } else if store.calendars.isEmpty {
                SettingsRow(title: "Calendars", detail: "No calendars on this Mac yet.") { EmptyView() }
            } else {
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(CalendarSchedule.sourceGroups(store.calendars)) { sourceGroup in
                        Text(sourceGroup.title)
                            .font(SettingsLook.caption.weight(.semibold))
                            .foregroundStyle(.secondary)
                            .padding(.top, 4)
                        ForEach(sourceGroup.calendars) { calendarInfo in
                            CalendarVisibilityRow(store: store, calendarInfo: calendarInfo)
                        }
                    }
                }
            }
        }
        // The calendars are read only while a view watches; this list is one.
        .onAppear { store.beginWatching() }
        .onDisappear { store.endWatching() }
    }
}
