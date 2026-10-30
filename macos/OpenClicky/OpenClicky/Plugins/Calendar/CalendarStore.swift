//
//  CalendarStore.swift
//  OpenClicky
//
//  The Calendar plugin's one store: EventKit access, the week's events per day, the calendars and
//  which of them are hidden, and the quick add. It reads the calendar only while the plugin is on
//  and a view is watching (the island's column, the Settings page), and it never asks for access
//  on its own: macOS shows its prompt once, so it is spent only on the person's button press.
//

import AppKit
import Combine
import EventKit
import Foundation

@MainActor
final class CalendarStore: ObservableObject {
    static let shared = CalendarStore()

    static let enabledDefaultsKey = "openclicky.calendar.enabled"
    static let quickAddDefaultsKey = "openclicky.calendar.quickAdd"
    static let hiddenCalendarsDefaultsKey = "openclicky.calendar.hidden"

    /// Where macOS's Calendars privacy list lives, for when access was turned down.
    static let privacySettingsURL = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Calendars")

    /// What the app may do with the calendar. Only full access can read events; write-only access
    /// (granted to apps that asked for less) is treated like no access by the island.
    enum Access: Equatable {
        case notDetermined
        case allowed
        case writeOnly
        case denied
        case restricted

        init(status: EKAuthorizationStatus) {
            switch status {
            case .fullAccess: self = .allowed
            case .writeOnly: self = .writeOnly
            case .denied: self = .denied
            case .restricted: self = .restricted
            case .notDetermined: self = .notDetermined
            // `.authorized` is the pre-macOS 14 name for full access.
            case .authorized: self = .allowed
            @unknown default: self = .denied
            }
        }

        /// True when a press can still bring up macOS's own prompt; otherwise only System
        /// Settings can change the answer.
        var canPrompt: Bool { self == .notDetermined }
    }

    /// Why a quick add did not happen, in words the island can show.
    enum QuickAddError: LocalizedError, Equatable {
        case emptyTitle
        case noAccess
        case noWritableCalendar
        case saveFailed(String)

        var errorDescription: String? {
            switch self {
            case .emptyTitle: return "Give the event a name first."
            case .noAccess: return "OpenClicky needs calendar access to add events."
            case .noWritableCalendar: return "None of your calendars can take new events."
            case .saveFailed(let reason): return "Calendar did not save it: \(reason)"
            }
        }
    }

    @Published private(set) var access: Access
    /// Today and the six days after it, each with its visible events. Filled with empty days when
    /// there is no access, so the week strip still draws.
    @Published private(set) var days: [CalendarDay]
    /// Every event calendar, for Settings.
    @Published private(set) var calendars: [CalendarInfo] = []
    @Published private(set) var hiddenCalendarIDs: Set<String>

    /// Settings → Calendar → "Show calendar in the island".
    @Published var isEnabled: Bool {
        didSet {
            guard isEnabled != oldValue else { return }
            defaults.set(isEnabled, forKey: Self.enabledDefaultsKey)
            refetch()
        }
    }

    /// Settings → Calendar → "Add events from the island": shows the island's "+" button.
    @Published var isQuickAddEnabled: Bool {
        didSet {
            guard isQuickAddEnabled != oldValue else { return }
            defaults.set(isQuickAddEnabled, forKey: Self.quickAddDefaultsKey)
        }
    }

    private let eventStore = EKEventStore()
    private let defaults: UserDefaults
    private let systemCalendar: Calendar
    /// Every fetched event, hidden calendars included, so hiding or showing a calendar redraws
    /// without asking EventKit again.
    private var fetchedEvents: [CalendarEventItem] = []
    private var watcherCount = 0
    private var notificationObservers: [NSObjectProtocol] = []

    init(defaults: UserDefaults = .standard, systemCalendar: Calendar = .autoupdatingCurrent) {
        self.defaults = defaults
        self.systemCalendar = systemCalendar
        self.access = Access(status: EKEventStore.authorizationStatus(for: .event))
        self.isEnabled = defaults.object(forKey: Self.enabledDefaultsKey) as? Bool ?? true
        self.isQuickAddEnabled = defaults.object(forKey: Self.quickAddDefaultsKey) as? Bool ?? true
        self.hiddenCalendarIDs = Set(defaults.stringArray(forKey: Self.hiddenCalendarsDefaultsKey) ?? [])
        self.days = CalendarSchedule.dayStarts(from: Date(), calendar: systemCalendar).map { CalendarDay(start: $0, events: []) }
        observeChanges()
    }

    var isWatching: Bool { watcherCount > 0 }

    /// A view that shows the calendar appeared: the first one reads it now.
    func beginWatching() {
        watcherCount += 1
        guard watcherCount == 1 else { return }
        refreshAccess()
        refetch()
    }

    /// A view that showed the calendar went away: the last one lets go of the events.
    func endWatching() {
        watcherCount = max(0, watcherCount - 1)
        guard watcherCount == 0 else { return }
        fetchedEvents = []
        rebuildDays()
    }

    /// Reads the authorization again: it can change in System Settings while the app runs.
    func refreshAccess() {
        let current = Access(status: EKEventStore.authorizationStatus(for: .event))
        if current != access { access = current }
    }

    /// The "Allow Calendar Access" button: macOS's prompt when it has not been answered yet,
    /// System Settings' Calendars list when it was turned down.
    func requestAccess() async {
        refreshAccess()
        guard access.canPrompt else {
            if access != .allowed, let privacySettingsURL = Self.privacySettingsURL {
                NSWorkspace.shared.open(privacySettingsURL)
            }
            return
        }
        do {
            _ = try await eventStore.requestFullAccessToEvents()
        } catch {
            AppLog.append("calendar: access request failed: \(error.localizedDescription)")
        }
        refreshAccess()
        refetch()
    }

    // MARK: Hidden calendars

    func isCalendarVisible(_ calendarID: String) -> Bool {
        !hiddenCalendarIDs.contains(calendarID)
    }

    func setCalendar(_ calendarID: String, isVisible: Bool) {
        var nextHidden = hiddenCalendarIDs
        if isVisible { nextHidden.remove(calendarID) } else { nextHidden.insert(calendarID) }
        guard nextHidden != hiddenCalendarIDs else { return }
        hiddenCalendarIDs = nextHidden
        defaults.set(nextHidden.sorted(), forKey: Self.hiddenCalendarsDefaultsKey)
        rebuildDays()
    }

    /// The calendars as they are right now, without publishing anything: the Settings catalog is
    /// built while SwiftUI draws, where changing published state is not allowed.
    func currentCalendars() -> [CalendarInfo] {
        guard Access(status: EKEventStore.authorizationStatus(for: .event)) == .allowed else { return [] }
        return eventStore.calendars(for: .event).map(Self.calendarInfo)
    }

    // MARK: Quick add

    /// Saves a new event in the default calendar for new events (or, when that one is hidden or
    /// read-only, the first visible calendar that takes events) and reads the week again.
    func addEvent(title: String, start: Date, end: Date) throws {
        let trimmedTitle = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedTitle.isEmpty else { throw QuickAddError.emptyTitle }
        refreshAccess()
        guard access == .allowed else { throw QuickAddError.noAccess }
        guard let targetCalendar = calendarForNewEvents() else { throw QuickAddError.noWritableCalendar }

        let newEvent = EKEvent(eventStore: eventStore)
        newEvent.title = trimmedTitle
        newEvent.startDate = start
        newEvent.endDate = max(end, start)
        newEvent.calendar = targetCalendar
        do {
            try eventStore.save(newEvent, span: .thisEvent, commit: true)
        } catch {
            throw QuickAddError.saveFailed(error.localizedDescription)
        }
        refetch()
    }

    private func calendarForNewEvents() -> EKCalendar? {
        if let defaultCalendar = eventStore.defaultCalendarForNewEvents,
           defaultCalendar.allowsContentModifications,
           !hiddenCalendarIDs.contains(defaultCalendar.calendarIdentifier) {
            return defaultCalendar
        }
        let writableCalendars = eventStore.calendars(for: .event).filter(\.allowsContentModifications)
        return writableCalendars.first { !hiddenCalendarIDs.contains($0.calendarIdentifier) }
            ?? eventStore.defaultCalendarForNewEvents
            ?? writableCalendars.first
    }

    // MARK: Opening an event

    /// Shows the event in Calendar. Calendar opens `ical://ekevent/<identifier>`; a repeating
    /// event's occurrences share one identifier, so for those the occurrence's start (UTC) goes
    /// in front of the item identifier, which is the form Calendar uses to pick one occurrence.
    /// When there is no identifier to go on, Calendar simply comes forward.
    func open(_ event: CalendarEventItem) {
        if let eventURL = Self.calendarURL(for: event) {
            if NSWorkspace.shared.open(eventURL) { return }
        }
        openCalendarApp()
    }

    static func calendarURL(for event: CalendarEventItem) -> URL? {
        let allowedCharacters = CharacterSet.urlPathAllowed.subtracting(CharacterSet(charactersIn: "/"))
        if event.isRecurring {
            let formatter = DateFormatter()
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.timeZone = TimeZone(identifier: "UTC")
            formatter.dateFormat = event.isAllDay ? "yyyyMMdd" : "yyyyMMdd'T'HHmmss'Z'"
            let occurrence = formatter.string(from: event.start)
            guard let itemIdentifier = event.calendarItemIdentifier.addingPercentEncoding(withAllowedCharacters: allowedCharacters) else { return nil }
            return URL(string: "ical://ekevent/\(occurrence)/\(itemIdentifier)?method=show&options=more")
        }
        guard let eventIdentifier = event.eventIdentifier,
              let encodedIdentifier = eventIdentifier.addingPercentEncoding(withAllowedCharacters: allowedCharacters) else { return nil }
        return URL(string: "ical://ekevent/\(encodedIdentifier)?method=show&options=more")
    }

    func openCalendarApp() {
        guard let calendarAppURL = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.apple.iCal") else { return }
        NSWorkspace.shared.openApplication(at: calendarAppURL, configuration: NSWorkspace.OpenConfiguration())
    }

    // MARK: Fetching

    /// Reads the week again, but only while the plugin is on, a view is watching and access is
    /// granted; otherwise the days are left empty.
    private func refetch() {
        let dayStarts = CalendarSchedule.dayStarts(from: Date(), calendar: systemCalendar)
        guard isWatching, access == .allowed else {
            fetchedEvents = []
            calendars = []
            rebuildDays(dayStarts: dayStarts)
            return
        }
        let eventCalendars = eventStore.calendars(for: .event)
        let nextCalendars = eventCalendars.map(Self.calendarInfo)
        if nextCalendars != calendars { calendars = nextCalendars }

        if isEnabled, let interval = CalendarSchedule.fetchInterval(for: dayStarts, calendar: systemCalendar) {
            let predicate = eventStore.predicateForEvents(withStart: interval.start, end: interval.end, calendars: eventCalendars)
            fetchedEvents = eventStore.events(matching: predicate).map(Self.eventItem)
        } else {
            fetchedEvents = []
        }
        rebuildDays(dayStarts: dayStarts)
    }

    private func rebuildDays(dayStarts: [Date]? = nil) {
        let starts = dayStarts ?? CalendarSchedule.dayStarts(from: Date(), calendar: systemCalendar)
        let nextDays = CalendarSchedule.days(events: fetchedEvents, dayStarts: starts, hiddenCalendarIDs: hiddenCalendarIDs, calendar: systemCalendar)
        if nextDays != days { days = nextDays }
    }

    /// Registered once for the app's lifetime; each handler does nothing unless a view is watching.
    /// The observers are delivered on the main queue, so they run on the main actor already.
    private func observeChanges() {
        let center = NotificationCenter.default
        // Another app (or a sync) changed the calendar database.
        notificationObservers.append(center.addObserver(forName: .EKEventStoreChanged, object: eventStore, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.calendarDatabaseChanged() }
        })
        // Midnight passed (or the clock or time zone jumped): the week starts on another day.
        notificationObservers.append(center.addObserver(forName: .NSCalendarDayChanged, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.calendarDatabaseChanged() }
        })
        // Access is changed in System Settings, so it is read again whenever OpenClicky comes forward.
        notificationObservers.append(center.addObserver(forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.applicationBecameActive() }
        })
    }

    private func calendarDatabaseChanged() {
        guard isWatching else { return }
        refetch()
    }

    private func applicationBecameActive() {
        guard isWatching else { return }
        let accessBefore = access
        refreshAccess()
        if access != accessBefore { refetch() }
    }

    private static func calendarInfo(_ calendar: EKCalendar) -> CalendarInfo {
        CalendarInfo(
            id: calendar.calendarIdentifier,
            title: calendar.title,
            color: CalendarColor(nsColor: calendar.color ?? .systemGray),
            sourceTitle: calendar.source?.title ?? "Other",
            allowsNewEvents: calendar.allowsContentModifications)
    }

    private static func eventItem(_ event: EKEvent) -> CalendarEventItem {
        let start: Date = event.startDate
        let end: Date = event.endDate ?? start
        let itemIdentifier: String = event.calendarItemIdentifier
        let eventIdentifier: String? = event.eventIdentifier
        return CalendarEventItem(
            id: "\(eventIdentifier ?? itemIdentifier)@\(start.timeIntervalSinceReferenceDate)",
            eventIdentifier: eventIdentifier,
            calendarItemIdentifier: itemIdentifier,
            title: (event.title?.isEmpty == false ? event.title : nil) ?? "Untitled",
            start: start,
            end: end,
            isAllDay: event.isAllDay,
            isRecurring: event.hasRecurrenceRules,
            calendarID: event.calendar?.calendarIdentifier ?? "",
            color: CalendarColor(nsColor: event.calendar?.color ?? .systemGray))
    }
}
