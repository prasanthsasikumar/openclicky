//
//  CalendarStoreTests.swift
//  OpenClickyTests
//
//  The Calendar plugin's pure parts: the week it shows, which events land on which day, the dots
//  under a day, hidden calendars, the calendars grouped by account, and the links into Calendar.
//

import Foundation
import Testing
@testable import OpenClicky

struct CalendarStoreTests {
    private static let calendar: Calendar = {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Pacific/Auckland")!
        return calendar
    }()

    private static func date(_ year: Int, _ month: Int, _ day: Int, _ hour: Int = 0, _ minute: Int = 0) -> Date {
        calendar.date(from: DateComponents(year: year, month: month, day: day, hour: hour, minute: minute))!
    }

    private static let red = CalendarColor(red: 1, green: 0, blue: 0)
    private static let green = CalendarColor(red: 0, green: 1, blue: 0)
    private static let blue = CalendarColor(red: 0, green: 0, blue: 1)
    private static let grey = CalendarColor(red: 0.5, green: 0.5, blue: 0.5)

    private static func event(
        _ title: String,
        start: Date,
        end: Date,
        calendarID: String = "work",
        color: CalendarColor = red,
        isAllDay: Bool = false,
        isRecurring: Bool = false
    ) -> CalendarEventItem {
        CalendarEventItem(
            id: "\(title)@\(start.timeIntervalSinceReferenceDate)", eventIdentifier: "\(title)-id",
            calendarItemIdentifier: "\(title)-item", title: title, start: start, end: end,
            isAllDay: isAllDay, isRecurring: isRecurring, calendarID: calendarID, color: color)
    }

    @Test func theWeekIsTodayAndTheSixDaysAfterItFromMidnight() {
        let starts = CalendarSchedule.dayStarts(from: Self.date(2026, 10, 30, 15, 42), calendar: Self.calendar)
        #expect(starts.count == 7)
        #expect(starts.first == Self.date(2026, 10, 30))
        #expect(starts.last == Self.date(2026, 11, 5))
        let interval = CalendarSchedule.fetchInterval(for: starts, calendar: Self.calendar)
        #expect(interval?.start == Self.date(2026, 10, 30))
        #expect(interval?.end == Self.date(2026, 11, 6))
    }

    @Test func theWeekKeepsWholeDaysAcrossADaylightSavingChange() {
        // New Zealand's clocks go back on 5 April 2026: that day is 25 hours long.
        let starts = CalendarSchedule.dayStarts(from: Self.date(2026, 4, 4, 10), calendar: Self.calendar)
        #expect(starts[1] == Self.date(2026, 4, 5))
        #expect(starts[2] == Self.date(2026, 4, 6))
    }

    @Test func eventsLandOnEveryDayTheyTouchInListOrder() {
        let starts = CalendarSchedule.dayStarts(from: Self.date(2026, 10, 30, 8), calendar: Self.calendar)
        let standup = Self.event("Standup", start: Self.date(2026, 10, 30, 9), end: Self.date(2026, 10, 30, 9, 15))
        let lunch = Self.event("Lunch", start: Self.date(2026, 10, 30, 12), end: Self.date(2026, 10, 30, 13))
        let holiday = Self.event("Holiday", start: Self.date(2026, 10, 30), end: Self.date(2026, 10, 31), isAllDay: true)
        let trip = Self.event("Trip", start: Self.date(2026, 10, 31, 18), end: Self.date(2026, 11, 2, 10))
        let endsAtMidnight = Self.event("Late", start: Self.date(2026, 10, 30, 23), end: Self.date(2026, 10, 31))
        let reminder = Self.event("Ping", start: Self.date(2026, 11, 1, 7), end: Self.date(2026, 11, 1, 7))

        let days = CalendarSchedule.days(
            events: [lunch, trip, standup, reminder, endsAtMidnight, holiday], dayStarts: starts,
            hiddenCalendarIDs: [], calendar: Self.calendar)

        #expect(days.count == 7)
        #expect(days[0].events.map(\.title) == ["Holiday", "Standup", "Lunch", "Late"])
        #expect(days[1].events.map(\.title) == ["Trip"])
        #expect(days[2].events.map(\.title) == ["Trip", "Ping"])
        #expect(days[3].events.map(\.title) == ["Trip"])
        #expect(days[4].events.isEmpty)
    }

    @Test func dotsAreOnePerEventInItsCalendarsColourAndAtMostThree() {
        let start = Self.date(2026, 10, 30, 9)
        let events = [
            Self.event("A", start: start, end: start.addingTimeInterval(600), color: Self.red),
            Self.event("B", start: start.addingTimeInterval(60), end: start.addingTimeInterval(600), color: Self.green),
            Self.event("C", start: start.addingTimeInterval(120), end: start.addingTimeInterval(600), color: Self.green),
            Self.event("D", start: start.addingTimeInterval(180), end: start.addingTimeInterval(600), color: Self.blue),
        ]
        #expect(CalendarSchedule.dotColors(for: events) == [Self.red, Self.green, Self.green])
        #expect(CalendarSchedule.dotColors(for: Array(events.prefix(1))) == [Self.red])
        #expect(CalendarSchedule.dotColors(for: []).isEmpty)

        let day = CalendarDay(start: Self.date(2026, 10, 30), events: events)
        #expect(day.dotColors.count == 3)
    }

    @Test func hiddenCalendarsLeaveTheListAndTheDots() {
        let starts = CalendarSchedule.dayStarts(from: Self.date(2026, 10, 30, 8), calendar: Self.calendar)
        let work = Self.event("Review", start: Self.date(2026, 10, 30, 10), end: Self.date(2026, 10, 30, 11), calendarID: "work", color: Self.red)
        let birthday = Self.event("Birthday", start: Self.date(2026, 10, 30), end: Self.date(2026, 10, 31), calendarID: "birthdays", color: Self.grey, isAllDay: true)

        let allVisible = CalendarSchedule.days(events: [work, birthday], dayStarts: starts, hiddenCalendarIDs: [], calendar: Self.calendar)
        #expect(allVisible[0].events.map(\.title) == ["Birthday", "Review"])

        let birthdaysHidden = CalendarSchedule.days(events: [work, birthday], dayStarts: starts, hiddenCalendarIDs: ["birthdays"], calendar: Self.calendar)
        #expect(birthdaysHidden[0].events.map(\.title) == ["Review"])
        #expect(birthdaysHidden[0].dotColors == [Self.red])

        let everythingHidden = CalendarSchedule.days(events: [work, birthday], dayStarts: starts, hiddenCalendarIDs: ["work", "birthdays"], calendar: Self.calendar)
        #expect(everythingHidden.allSatisfy { $0.events.isEmpty })
        #expect(everythingHidden.count == 7)
    }

    @Test func calendarsAreGroupedByAccountInAlphabeticalOrder() {
        let calendars = [
            CalendarInfo(id: "1", title: "Work", color: Self.red, sourceTitle: "iCloud", allowsNewEvents: true),
            CalendarInfo(id: "2", title: "family", color: Self.green, sourceTitle: "iCloud", allowsNewEvents: true),
            CalendarInfo(id: "3", title: "Team", color: Self.blue, sourceTitle: "me@example.com", allowsNewEvents: true),
            CalendarInfo(id: "4", title: "Birthdays", color: Self.grey, sourceTitle: "Other", allowsNewEvents: false),
        ]
        let groups = CalendarSchedule.sourceGroups(calendars)
        #expect(groups.map(\.title) == ["iCloud", "me@example.com", "Other"])
        #expect(groups[0].calendars.map(\.title) == ["family", "Work"])
        #expect(groups[1].calendars.map(\.id) == ["3"])
        #expect(CalendarSchedule.sourceGroups([]).isEmpty)
    }

    @Test func timesReadAsAllDayOrAStartAndEnd() {
        let dayStart = Self.date(2026, 10, 30)
        let english = Locale(identifier: "en_NZ")
        let holiday = Self.event("Holiday", start: dayStart, end: Self.date(2026, 10, 31), isAllDay: true)
        #expect(CalendarSchedule.timeText(for: holiday, on: dayStart, calendar: Self.calendar, locale: english) == "All day")

        let trip = Self.event("Trip", start: Self.date(2026, 10, 29, 18), end: Self.date(2026, 11, 1, 10))
        #expect(CalendarSchedule.timeText(for: trip, on: dayStart, calendar: Self.calendar, locale: english) == "All day")

        let review = Self.event("Review", start: Self.date(2026, 10, 30, 9, 30), end: Self.date(2026, 10, 30, 10))
        let reviewText = CalendarSchedule.timeText(for: review, on: dayStart, calendar: Self.calendar, locale: Locale(identifier: "en_GB"))
        #expect(reviewText == "09:30 – 10:00")
    }

    @Test func pastEventsAreTheOnesThatHaveEnded() {
        let review = Self.event("Review", start: Self.date(2026, 10, 30, 9), end: Self.date(2026, 10, 30, 10))
        #expect(!CalendarSchedule.isPast(review, now: Self.date(2026, 10, 30, 9, 30)))
        #expect(CalendarSchedule.isPast(review, now: Self.date(2026, 10, 30, 10)))
    }

    @Test func aQuickAddStartsAtTheNextHalfHourTodayAndAtNineOnLaterDays() {
        let now = Self.date(2026, 10, 30, 14, 10)
        #expect(CalendarSchedule.suggestedQuickAddStart(on: Self.date(2026, 10, 30), now: now, calendar: Self.calendar) == Self.date(2026, 10, 30, 14, 30))
        #expect(CalendarSchedule.suggestedQuickAddStart(on: Self.date(2026, 10, 30), now: Self.date(2026, 10, 30, 14, 30), calendar: Self.calendar) == Self.date(2026, 10, 30, 15))
        #expect(CalendarSchedule.suggestedQuickAddStart(on: Self.date(2026, 11, 2), now: now, calendar: Self.calendar) == Self.date(2026, 11, 2, 9))
        // At 23:50 the next half hour is tomorrow, so the event starts now instead.
        #expect(CalendarSchedule.suggestedQuickAddStart(on: Self.date(2026, 10, 30), now: Self.date(2026, 10, 30, 23, 50), calendar: Self.calendar) == Self.date(2026, 10, 30, 23, 50))
    }

    @MainActor @Test func eventsOpenInCalendarByIdentifierAndRepeatsByOccurrence() {
        let single = Self.event("Review", start: Self.date(2026, 10, 30, 9), end: Self.date(2026, 10, 30, 10))
        #expect(CalendarStore.calendarURL(for: single)?.absoluteString == "ical://ekevent/Review-id?method=show&options=more")

        // 9:00 in Auckland on 30 October is 20:00 UTC the day before.
        let weekly = Self.event("Standup", start: Self.date(2026, 10, 30, 9), end: Self.date(2026, 10, 30, 9, 15), isRecurring: true)
        #expect(CalendarStore.calendarURL(for: weekly)?.absoluteString == "ical://ekevent/20261029T200000Z/Standup-item?method=show&options=more")
    }
}
