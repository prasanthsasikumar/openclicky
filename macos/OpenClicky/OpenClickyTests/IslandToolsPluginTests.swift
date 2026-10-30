//
//  IslandToolsPluginTests.swift
//  OpenClickyTests
//
//  The Timer, Notes and Clipboard plugins: the focus cycle's phases and rounds, pausing with a
//  fixed clock, the notes file, and the clipboard list.
//

import AppKit
import Foundation
import Testing
@testable import OpenClicky

struct FocusTimerTests {
    let start = Date(timeIntervalSince1970: 1_000_000)
    let durations = FocusDurations(focus: 25 * 60, shortBreak: 5 * 60, longBreak: 15 * 60)

    @Test func runClockBanksTimeAcrossPauses() {
        var clock = RunClock()
        clock.start(at: start)
        #expect(clock.elapsed(at: start.addingTimeInterval(90)) == 90)
        clock.pause(at: start.addingTimeInterval(90))
        // Paused: time on the wall does not count.
        #expect(clock.elapsed(at: start.addingTimeInterval(1_000)) == 90)
        clock.start(at: start.addingTimeInterval(1_000))
        #expect(clock.elapsed(at: start.addingTimeInterval(1_030)) == 120)
        // Starting twice keeps the first start; pausing twice keeps the first pause.
        clock.start(at: start.addingTimeInterval(1_020))
        #expect(clock.elapsed(at: start.addingTimeInterval(1_030)) == 120)
        clock.pause(at: start.addingTimeInterval(1_030))
        clock.pause(at: start.addingTimeInterval(2_000))
        #expect(clock.elapsed(at: start.addingTimeInterval(5_000)) == 120 && !clock.isRunning)
        clock.reset()
        #expect(clock.elapsed(at: start) == 0)
    }

    @Test func focusGivesWayToAShortBreakThenFocusAgain() {
        var cycle = FocusCycle()
        cycle.start(at: start)
        #expect(cycle.advance(at: start.addingTimeInterval(24 * 60), durations: durations).isEmpty)
        #expect(cycle.remaining(at: start.addingTimeInterval(24 * 60), durations: durations) == 60)
        #expect(cycle.advance(at: start.addingTimeInterval(25 * 60), durations: durations) == [.shortBreak])
        #expect(cycle.completedFocusRounds == 1 && cycle.roundInSet == 2 && cycle.isRunning)
        #expect(cycle.remaining(at: start.addingTimeInterval(25 * 60), durations: durations) == 5 * 60)
        #expect(cycle.advance(at: start.addingTimeInterval(30 * 60), durations: durations) == [.focus])
        #expect(cycle.phase == .focus && cycle.completedFocusRounds == 1)
    }

    @Test func everyFourthFocusRoundEndsInALongBreak() {
        var cycle = FocusCycle()
        cycle.start(at: start)
        var entered: [FocusPhase] = []
        var time: TimeInterval = 0
        // Three focus + break pairs, then the fourth focus round.
        for _ in 0..<3 {
            time += durations.focus
            entered += cycle.advance(at: start.addingTimeInterval(time), durations: durations)
            time += durations.shortBreak
            entered += cycle.advance(at: start.addingTimeInterval(time), durations: durations)
        }
        time += durations.focus
        entered += cycle.advance(at: start.addingTimeInterval(time), durations: durations)
        #expect(entered == [.shortBreak, .focus, .shortBreak, .focus, .shortBreak, .focus, .longBreak])
        #expect(cycle.completedFocusRounds == 4 && cycle.roundInSet == 1)
        time += durations.longBreak
        #expect(cycle.advance(at: start.addingTimeInterval(time), durations: durations) == [.focus])
        // The next set of four starts over.
        time += durations.focus
        #expect(cycle.advance(at: start.addingTimeInterval(time), durations: durations) == [.shortBreak])
    }

    @Test func aLateTickCarriesTheOvershootIntoTheNextPhase() {
        var cycle = FocusCycle()
        cycle.start(at: start)
        // 25 min focus + 5 min break + 2 min: one sleep through two changes.
        let late = start.addingTimeInterval(32 * 60)
        #expect(cycle.advance(at: late, durations: durations) == [.shortBreak, .focus])
        #expect(cycle.remaining(at: late, durations: durations) == 23 * 60)
    }

    @Test func pausingStopsTheCountdownAndResumingPicksUpWhereItWas() {
        var cycle = FocusCycle()
        cycle.start(at: start)
        cycle.pause(at: start.addingTimeInterval(10 * 60))
        let muchLater = start.addingTimeInterval(3 * 3600)
        #expect(cycle.advance(at: muchLater, durations: durations).isEmpty)
        #expect(cycle.remaining(at: muchLater, durations: durations) == 15 * 60 && !cycle.isRunning)
        cycle.start(at: muchLater)
        #expect(cycle.remaining(at: muchLater.addingTimeInterval(5 * 60), durations: durations) == 10 * 60)
        #expect(cycle.advance(at: muchLater.addingTimeInterval(15 * 60), durations: durations) == [.shortBreak])
    }

    @Test func resetGoesBackToTheFirstFocusRound() {
        var cycle = FocusCycle()
        #expect(!cycle.hasProgress)
        cycle.start(at: start)
        _ = cycle.advance(at: start.addingTimeInterval(26 * 60), durations: durations)
        #expect(cycle.hasProgress)
        cycle.reset()
        #expect(cycle.phase == .focus && cycle.completedFocusRounds == 0 && !cycle.isRunning && !cycle.hasProgress)
        #expect(cycle.remaining(at: start, durations: durations) == 25 * 60)
    }

    @Test func theClockReadsLikeATimer() {
        #expect(TimerFormatter.countdown(25 * 60) == "25:00")
        #expect(TimerFormatter.countdown(0.2) == "0:01")
        #expect(TimerFormatter.countdown(0) == "0:00")
        #expect(TimerFormatter.countdown(12 * 60 + 3.5) == "12:04")
        #expect(TimerFormatter.clock(3723.9) == "1:02:03")
    }

    @Test func minutesComeFromSettingsWithDefaults() throws {
        let defaults = try #require(UserDefaults(suiteName: "timer-tests-\(UUID().uuidString)"))
        #expect(TimerSettings.durations(from: defaults) == FocusDurations(focus: 25 * 60, shortBreak: 5 * 60, longBreak: 15 * 60))
        defaults.set(50, forKey: TimerSettings.focusMinutesKey)
        defaults.set(10, forKey: TimerSettings.breakMinutesKey)
        defaults.set(-3, forKey: TimerSettings.longBreakMinutesKey)
        #expect(TimerSettings.durations(from: defaults) == FocusDurations(focus: 50 * 60, shortBreak: 10 * 60, longBreak: 15 * 60))
    }

    @MainActor @Test func theStoreChimesOnAPhaseChangeAndWritesItsPeekLine() throws {
        let defaults = try #require(UserDefaults(suiteName: "timer-tests-\(UUID().uuidString)"))
        var now = start
        var chimes: [FocusPhase] = []
        let store = TimerStore(clock: { now }, defaults: defaults, chime: { chimes.append($0) })
        #expect(store.peekLine == nil && !store.isRunning)
        store.toggleRunning()
        now = start.addingTimeInterval(12 * 60 + 56)
        store.tick()
        #expect(store.isRunning && store.peekLine == "Focus 12:04" && chimes.isEmpty)
        now = start.addingTimeInterval(25 * 60 + 10)
        store.tick()
        #expect(chimes == [.shortBreak] && store.peekLine == "Break 4:50")
        store.toggleRunning()
        #expect(!store.isRunning && store.peekLine == nil)
        store.mode = .stopwatch
        store.toggleRunning()
        now = now.addingTimeInterval(65)
        store.tick()
        #expect(store.peekLine == "Stopwatch 1:05")
        store.resetShownMode()
        #expect(!store.isRunning && store.stopwatchElapsed == 0)
    }
}

struct ClipboardHistoryTests {
    @Test func theSameCopyAgainMovesToTheTopKeepingItsRow() {
        var history = ClipboardHistory()
        history.add(.text("one"))
        history.add(.text("two"))
        let firstID = history.entries[1].id
        history.add(.text("one"))
        #expect(history.entries.map(\.content) == [.text("one"), .text("two")])
        #expect(history.entries[0].id == firstID)
    }

    @Test func itKeepsTheLastThirty() {
        var history = ClipboardHistory()
        for index in 0..<40 { history.add(.text("copy \(index)")) }
        #expect(history.entries.count == ClipboardHistory.capacity)
        #expect(history.entries.first?.content == .text("copy 39") && history.entries.last?.content == .text("copy 10"))
    }

    @Test func passwordManagerCopiesAreSkipped() {
        var history = ClipboardHistory()
        for type in ClipboardHistory.transientTypes {
            let kept = history.add(.text("secret"), pasteboardTypes: [.string, type])
            #expect(!kept)
        }
        #expect(history.entries.isEmpty)
        let kept = history.add(.text("fine"), pasteboardTypes: [.string])
        #expect(kept && history.entries.count == 1)
    }

    @Test func rowsReadAsOneShortLine() throws {
        #expect(ClipboardContent.text("  first   line\nsecond").label == "first line")
        #expect(ClipboardContent.link(try #require(URL(string: "https://example.com/a?b=1"))).label == "example.com/a?b=1")
        let files = [URL(fileURLWithPath: "/tmp/a.pdf"), URL(fileURLWithPath: "/tmp/b.pdf")]
        #expect(ClipboardContent.files(files).label == "a.pdf and 1 more")
        #expect(ClipboardContent.image(Data(), type: .png, pixelSize: CGSize(width: 1200, height: 800)).label == "Image 1200 × 800")
    }

    @Test func onlyASingleWebOrMailAddressCountsAsALink() {
        #expect(ClipboardPasteboard.link(in: "https://example.com") != nil)
        #expect(ClipboardPasteboard.link(in: "mailto:someone@example.com") != nil)
        #expect(ClipboardPasteboard.link(in: "see https://example.com") == nil)
        #expect(ClipboardPasteboard.link(in: "file:///etc/hosts") == nil)
        #expect(ClipboardPasteboard.link(in: "hello") == nil)
    }

    @MainActor @Test func theStoreReadsAPasteboardSkipsTransientCopiesAndForgetsWhenSwitchedOff() throws {
        let pasteboard = NSPasteboard(name: NSPasteboard.Name("clipboard-tests-\(UUID().uuidString)"))
        defer { pasteboard.releaseGlobally() }
        let defaults = try #require(UserDefaults(suiteName: "clipboard-tests-\(UUID().uuidString)"))
        let store = ClipboardStore(pasteboard: pasteboard, defaults: defaults)
        store.setEnabled(true)
        pasteboard.clearContents()
        pasteboard.setString("hello", forType: .string)
        store.poll()
        pasteboard.clearContents()
        pasteboard.setString("hunter2", forType: .string)
        pasteboard.setData(Data(), forType: NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType"))
        store.poll()
        pasteboard.clearContents()
        pasteboard.setString("https://example.com", forType: .string)
        store.poll()
        #expect(store.history.entries.map(\.content.label) == ["example.com", "hello"])
        store.copyBack(store.history.entries[1])
        #expect(pasteboard.string(forType: .string) == "hello")
        store.poll()
        #expect(store.history.entries.map(\.content.label) == ["hello", "example.com"])
        store.setEnabled(false)
        #expect(store.history.entries.isEmpty && !store.isPolling)
    }
}

struct NotesStoreTests {
    @MainActor @Test func typingIsWrittenOnceItPausesAndReadBackNextTime() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("notes-tests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let fileURL = directory.appendingPathComponent("Notes/notes.txt")
        let store = NotesStore(fileURL: fileURL, saveDelay: .milliseconds(50))
        #expect(store.text.isEmpty)
        store.update("milk")
        store.update("milk, eggs")
        #expect(store.hasPendingSave && !FileManager.default.fileExists(atPath: fileURL.path))
        for _ in 0..<40 where store.hasPendingSave { try await Task.sleep(for: .milliseconds(25)) }
        #expect(try String(contentsOf: fileURL, encoding: .utf8) == "milk, eggs")
        store.update("milk, eggs, bread")
        store.saveNow()
        #expect(NotesStore(fileURL: fileURL).text == "milk, eggs, bread")
    }
}
