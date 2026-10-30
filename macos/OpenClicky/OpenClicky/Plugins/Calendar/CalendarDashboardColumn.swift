//
//  CalendarDashboardColumn.swift
//  OpenClicky
//
//  The calendar column on the open island's dashboard: the month and a week strip (today and the
//  six days after it, a dot per event under each number), and under it the chosen day's events.
//  Clicking a day chooses it, clicking an event opens it in Calendar, and the "+" adds an event
//  right there. Without calendar access the column says so and offers the one button that asks.
//

import AppKit
import SwiftUI

struct CalendarDashboardColumn: View {
    /// The column's width on the dashboard row.
    static let width: CGFloat = 268
    static let headerHeight: CGFloat = 40

    @ObservedObject var store: CalendarStore
    @State private var selectedDayStart: Date?
    @State private var isQuickAddOpen = false

    /// Nil uses the app's one store.
    init(store: CalendarStore? = nil) {
        self.store = store ?? .shared
    }

    private var systemCalendar: Calendar { .autoupdatingCurrent }

    /// The chosen day, or today when nothing is chosen or the chosen day has left the week.
    private var selectedDay: CalendarDay? {
        store.days.first { $0.start == selectedDayStart } ?? store.days.first
    }

    private var canQuickAdd: Bool {
        store.isQuickAddEnabled && store.access == .allowed
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            header
                .frame(height: Self.headerHeight)
            content
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
        .frame(width: Self.width, height: IslandDash.contentHeight, alignment: .topLeading)
        .onAppear { store.beginWatching() }
        .onDisappear { store.endWatching() }
        .onChange(of: canQuickAdd) { isAllowed in
            if !isAllowed { isQuickAddOpen = false }
        }
    }

    // MARK: Header

    private var header: some View {
        HStack(spacing: 4) {
            Text(monthAbbreviation)
                .font(.system(size: 21, weight: .bold))
                .foregroundColor(.white)
                .lineLimit(1)
                .minimumScaleFactor(0.7)
                .frame(width: 46, alignment: .leading)
            HStack(spacing: 6) {
                ForEach(store.days) { day in
                    CalendarDayCell(
                        day: day,
                        isToday: systemCalendar.isDateInToday(day.start),
                        isSelected: day.start == selectedDay?.start,
                        showsDots: store.access == .allowed
                    ) {
                        withAnimation(.spring(response: 0.3, dampingFraction: 0.8)) {
                            selectedDayStart = day.start
                        }
                    }
                }
            }
            .frame(maxWidth: .infinity)
            if canQuickAdd {
                Button {
                    withAnimation(.spring(response: 0.3, dampingFraction: 0.8)) { isQuickAddOpen.toggle() }
                } label: {
                    Image(systemName: isQuickAddOpen ? "xmark" : "plus")
                }
                .buttonStyle(IslandIconButtonStyle(size: 10, isActive: isQuickAddOpen))
                .help(isQuickAddOpen ? "Close" : "Add an event")
                .accessibilityLabel(isQuickAddOpen ? "Close new event" : "Add an event")
            }
        }
    }

    /// The chosen day's month, so choosing a day across a month boundary updates it.
    private var monthAbbreviation: String {
        let formatter = DateFormatter()
        formatter.calendar = systemCalendar
        formatter.setLocalizedDateFormatFromTemplate("MMM")
        return formatter.string(from: selectedDay?.start ?? Date())
    }

    // MARK: Body

    @ViewBuilder
    private var content: some View {
        if !store.isEnabled {
            Text("The calendar is off in Settings → Calendar.")
                .font(.system(size: 11))
                .foregroundColor(.white.opacity(0.6))
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if store.access != .allowed {
            noAccessView
        } else if isQuickAddOpen, let selectedDay {
            CalendarQuickAddField(store: store, dayStart: selectedDay.start) {
                withAnimation(.spring(response: 0.3, dampingFraction: 0.8)) { isQuickAddOpen = false }
            }
        } else if let selectedDay, !selectedDay.events.isEmpty {
            eventList(for: selectedDay)
        } else {
            emptyView
        }
    }

    private var noAccessView: some View {
        VStack(spacing: 8) {
            Text(noAccessExplanation)
                .font(.system(size: 11))
                .foregroundColor(.white.opacity(0.6))
                .multilineTextAlignment(.center)
                .lineLimit(2)
                .fixedSize(horizontal: false, vertical: true)
            Button("Allow Calendar Access") {
                Task { await store.requestAccess() }
            }
            .buttonStyle(IslandPillButtonStyle())
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var noAccessExplanation: String {
        switch store.access {
        case .notDetermined: return "See your week here once OpenClicky can read your calendar."
        case .writeOnly: return "OpenClicky can add events but not read them. Allow full access in System Settings."
        case .denied, .restricted, .allowed: return "Calendar access is off for OpenClicky. Turn it on in System Settings."
        }
    }

    private var emptyView: some View {
        let isToday = selectedDay.map { systemCalendar.isDateInToday($0.start) } ?? true
        return HStack(spacing: 8) {
            Image(systemName: "calendar")
                .font(.system(size: 15))
            Text(isToday ? "No events today" : "Nothing planned")
                .font(.system(size: 15, weight: .semibold))
        }
        .foregroundColor(.white.opacity(0.6))
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func eventList(for day: CalendarDay) -> some View {
        // Redrawn every minute so an event that just ended dims without anything else changing.
        TimelineView(.periodic(from: .now, by: 60)) { timeline in
            ScrollView(.vertical, showsIndicators: false) {
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(day.events) { event in
                        CalendarEventRow(
                            event: event,
                            timeText: CalendarSchedule.timeText(for: event, on: day.start, calendar: systemCalendar),
                            isPast: CalendarSchedule.isPast(event, now: timeline.date)
                        ) {
                            store.open(event)
                        }
                    }
                }
            }
        }
    }
}

// MARK: - A day in the strip

private struct CalendarDayCell: View {
    let day: CalendarDay
    let isToday: Bool
    let isSelected: Bool
    let showsDots: Bool
    let onSelect: () -> Void
    @State private var isHovered = false

    private static let weekdayFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.setLocalizedDateFormatFromTemplate("EEE")
        return formatter
    }()

    private var numberColor: Color {
        isToday ? IslandDash.today : .white.opacity(0.85)
    }

    private var numberWeight: Font.Weight {
        if isSelected { return .bold }
        return isToday ? .semibold : .medium
    }

    var body: some View {
        Button(action: onSelect) {
            VStack(spacing: 2) {
                Text(Self.weekdayFormatter.string(from: day.start).uppercased())
                    .font(.system(size: 8, weight: .semibold))
                    .foregroundColor(.white.opacity(0.75))
                    .lineLimit(1)
                    .minimumScaleFactor(0.7)
                Text("\(Calendar.autoupdatingCurrent.component(.day, from: day.start))")
                    .font(.system(size: 13, weight: numberWeight).monospacedDigit())
                    .foregroundColor(numberColor)
                HStack(spacing: 2) {
                    if showsDots {
                        ForEach(Array(day.dotColors.enumerated()), id: \.offset) { _, dotColor in
                            Circle()
                                .fill(dotColor.color)
                                .frame(width: 3, height: 3)
                        }
                    }
                }
                .frame(height: 3)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 3)
            .background(
                RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .fill(Color.white.opacity(isSelected ? 0.1 : (isHovered ? 0.06 : 0)))
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { isHovered = $0 }
        .pointerCursor()
        .accessibilityLabel(Self.accessibilityDateFormatter.string(from: day.start))
        .accessibilityValue(day.events.isEmpty ? "No events" : "\(day.events.count) events")
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }

    private static let accessibilityDateFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateStyle = .full
        formatter.timeStyle = .none
        return formatter
    }()
}

// MARK: - An event in the list

private struct CalendarEventRow: View {
    let event: CalendarEventItem
    let timeText: String
    let isPast: Bool
    let onOpen: () -> Void
    @State private var isHovered = false

    var body: some View {
        Button(action: onOpen) {
            HStack(alignment: .top, spacing: 7) {
                RoundedRectangle(cornerRadius: 1.5, style: .continuous)
                    .fill(event.color.color)
                    .frame(width: 3)
                VStack(alignment: .leading, spacing: 1) {
                    Text(event.title)
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundColor(.white)
                        .lineLimit(1)
                    Text(timeText)
                        .font(.system(size: 10).monospacedDigit())
                        .foregroundColor(.white.opacity(0.55))
                        .lineLimit(1)
                }
                Spacer(minLength: 0)
            }
            .fixedSize(horizontal: false, vertical: true)
            .padding(.vertical, 3)
            .padding(.horizontal, 4)
            .background(
                RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .fill(Color.white.opacity(isHovered ? 0.07 : 0))
            )
            .contentShape(Rectangle())
            .opacity(isPast ? 0.5 : 1)
        }
        .buttonStyle(.plain)
        .onHover { isHovered = $0 }
        .pointerCursor()
        .help("Open in Calendar")
        .accessibilityLabel("\(event.title), \(timeText)")
    }
}
