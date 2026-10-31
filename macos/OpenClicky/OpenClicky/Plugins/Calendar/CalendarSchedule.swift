//
//  CalendarSchedule.swift
//  OpenClicky
//
//  The Calendar plugin's pure parts: the week the island shows (today and the six days after it),
//  which events land on which day, the dots under a day's number, the calendars grouped by the
//  account they belong to, and how an event's time reads. No EventKit here, so all of it is
//  unit-tested with plain values; `CalendarStore` turns EventKit's objects into these.
//

import AppKit
import SwiftUI

/// A calendar's colour as plain sRGB components, so events and calendars compare as values.
nonisolated struct CalendarColor: Hashable {
    var red: Double
    var green: Double
    var blue: Double

    /// The system's calendar colours come in various colour spaces; sRGB is what the island draws in.
    init(nsColor: NSColor) {
        let srgbColor = nsColor.usingColorSpace(.sRGB) ?? NSColor.systemGray
        self.red = Double(srgbColor.redComponent)
        self.green = Double(srgbColor.greenComponent)
        self.blue = Double(srgbColor.blueComponent)
    }

    init(red: Double, green: Double, blue: Double) {
        self.red = red
        self.green = green
        self.blue = blue
    }

    var color: Color { Color(.sRGB, red: red, green: green, blue: blue, opacity: 1) }
}

/// One occurrence of an event. A repeating event has one item per occurrence, all sharing the
/// event's identifiers, so `id` adds the start time.
nonisolated struct CalendarEventItem: Identifiable, Equatable {
    let id: String
    /// EventKit's identifier for the event, used to open it in Calendar; nil for an event that was
    /// never saved, which the island never sees in practice.
    let eventIdentifier: String?
    let calendarItemIdentifier: String
    let title: String
    let start: Date
    let end: Date
    let isAllDay: Bool
    let isRecurring: Bool
    let calendarID: String
    let color: CalendarColor
}

/// One day of the week strip: its midnight and the visible events that touch it.
nonisolated struct CalendarDay: Identifiable, Equatable {
    let start: Date
    let events: [CalendarEventItem]

    var id: Date { start }
    var dotColors: [CalendarColor] { CalendarSchedule.dotColors(for: events) }
}

/// One calendar as Settings lists it.
nonisolated struct CalendarInfo: Identifiable, Equatable {
    let id: String
    let title: String
    let color: CalendarColor
    /// The account it belongs to ("iCloud", a Google address, "On My Mac").
    let sourceTitle: String
    /// False for subscribed and birthday calendars, which cannot take a new event.
    let allowsNewEvents: Bool
}

/// The calendars of one account, as one Settings section.
nonisolated struct CalendarSourceGroup: Identifiable, Equatable {
    let title: String
    let calendars: [CalendarInfo]

    var id: String { title }
}

nonisolated enum CalendarSchedule {
    /// Today and the six days after it.
    static let dayCount = 7
    /// A day shows at most this many dots under its number, however busy it is.
    static let maximumDotCount = 3

    /// Midnight of today and of each of the following days, in `calendar`'s time zone.
    static func dayStarts(from now: Date, calendar: Calendar, count: Int = dayCount) -> [Date] {
        let todayStart = calendar.startOfDay(for: now)
        return (0..<count).compactMap { offset in
            calendar.date(byAdding: .day, value: offset, to: todayStart)
        }
    }

    /// The span EventKit is asked about: from the first day's midnight to the midnight after the last day.
    static func fetchInterval(for dayStarts: [Date], calendar: Calendar) -> DateInterval? {
        guard let firstDayStart = dayStarts.first,
              let lastDayStart = dayStarts.last,
              let endOfLastDay = calendar.date(byAdding: .day, value: 1, to: lastDayStart) else { return nil }
        return DateInterval(start: firstDayStart, end: endOfLastDay)
    }

    /// The events of each day, without the hidden calendars' events. An event that runs over
    /// several days (a trip, a conference) is listed on every day it touches. All-day events come
    /// first, then by start time, then by title so the order never flickers between fetches.
    static func days(
        events: [CalendarEventItem],
        dayStarts: [Date],
        hiddenCalendarIDs: Set<String>,
        calendar: Calendar
    ) -> [CalendarDay] {
        let visibleEvents = events.filter { !hiddenCalendarIDs.contains($0.calendarID) }
        return dayStarts.map { dayStart in
            let dayEnd = calendar.date(byAdding: .day, value: 1, to: dayStart) ?? dayStart.addingTimeInterval(86_400)
            let eventsOnThisDay = visibleEvents
                .filter { event in eventTouchesDay(event, dayStart: dayStart, dayEnd: dayEnd) }
                .sorted(by: listOrder)
            return CalendarDay(start: dayStart, events: eventsOnThisDay)
        }
    }

    /// An event touches a day when it overlaps it. An event with no length (a reminder-like
    /// 10:00–10:00) touches the day its start falls on.
    static func eventTouchesDay(_ event: CalendarEventItem, dayStart: Date, dayEnd: Date) -> Bool {
        if event.end <= event.start {
            return event.start >= dayStart && event.start < dayEnd
        }
        return event.start < dayEnd && event.end > dayStart
    }

    private static func listOrder(_ first: CalendarEventItem, _ second: CalendarEventItem) -> Bool {
        if first.isAllDay != second.isAllDay { return first.isAllDay }
        if first.start != second.start { return first.start < second.start }
        return first.title.localizedStandardCompare(second.title) == .orderedAscending
    }

    /// One dot per event in its calendar's colour, at most three: the dots say "busy", not how busy.
    static func dotColors(for events: [CalendarEventItem]) -> [CalendarColor] {
        Array(events.prefix(maximumDotCount).map(\.color))
    }

    /// The calendars grouped by account, accounts and calendars in alphabetical order, so Settings
    /// shows one section per account.
    static func sourceGroups(_ calendars: [CalendarInfo]) -> [CalendarSourceGroup] {
        let calendarsBySource = Dictionary(grouping: calendars, by: \.sourceTitle)
        return calendarsBySource.keys
            .sorted { $0.localizedStandardCompare($1) == .orderedAscending }
            .map { sourceTitle in
                let sortedCalendars = (calendarsBySource[sourceTitle] ?? [])
                    .sorted { $0.title.localizedStandardCompare($1.title) == .orderedAscending }
                return CalendarSourceGroup(title: sourceTitle, calendars: sortedCalendars)
            }
    }

    /// True once the event is over; the list dims it.
    static func isPast(_ event: CalendarEventItem, now: Date) -> Bool {
        event.end <= now
    }

    /// How the event's time reads under its title: "All day", or "9:30 – 10:00" in the person's
    /// own clock style. A timed event that covers the whole of this day (the middle of a trip)
    /// reads as all day too.
    static func timeText(for event: CalendarEventItem, on dayStart: Date, calendar: Calendar, locale: Locale = .current) -> String {
        let dayEnd = calendar.date(byAdding: .day, value: 1, to: dayStart) ?? dayStart.addingTimeInterval(86_400)
        if event.isAllDay || (event.start <= dayStart && event.end >= dayEnd) {
            return "All day"
        }
        let formatter = DateFormatter()
        formatter.locale = locale
        formatter.calendar = calendar
        formatter.timeZone = calendar.timeZone
        formatter.dateStyle = .none
        formatter.timeStyle = .short
        if event.end <= event.start {
            return formatter.string(from: event.start)
        }
        return "\(formatter.string(from: event.start)) – \(formatter.string(from: event.end))"
    }

    /// Where a quick-added event starts unless the person picks another time: on today, the next
    /// half hour; on a later day, 9 in the morning.
    static func suggestedQuickAddStart(on dayStart: Date, now: Date, calendar: Calendar) -> Date {
        if calendar.isDate(dayStart, inSameDayAs: now) {
            let minute = calendar.component(.minute, from: now)
            let minutesToNextHalfHour = 30 - minute % 30
            let shifted = calendar.date(byAdding: .minute, value: minutesToNextHalfHour, to: now) ?? now
            let nextHalfHour = calendar.dateInterval(of: .minute, for: shifted)?.start ?? shifted
            // Late in the evening the next half hour is tomorrow; the event is meant for today.
            if !calendar.isDate(nextHalfHour, inSameDayAs: now) {
                return calendar.dateInterval(of: .minute, for: now)?.start ?? now
            }
            return nextHalfHour
        }
        return calendar.date(bySettingHour: 9, minute: 0, second: 0, of: dayStart) ?? dayStart
    }
}
