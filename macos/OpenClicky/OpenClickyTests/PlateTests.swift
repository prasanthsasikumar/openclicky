//
//  PlateTests.swift
//  OpenClickyTests
//
//  The day's offers: reading the model's reply, what it is asked, when the
//  day's cards are due, the digest, the store, where the dealt cards land, and the island's card.
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

struct PlatePromptTests {

    @Test func readsSuggestionsOutOfAFencedReply() {
        let reply = """
        Here you go:
        ```json
        {"suggestions":[
          {"title":"I'll draft the release notes","detail":"Twelve commits landed.\\nI'll write them up.","prompt":"Goal: write notes.","kind":"draft",
           "steps":["Read the commits"," ","Group by platform","Write the notes","Check the links","A fifth step"],"minutes":15,"result":"a draft of the notes"},
          {"title":"I'll compare 5 signing services","detail":"","prompt":"Goal: compare.","kind":"RESEARCH"}
        ]}
        ```
        """
        var counter = 0
        let suggestions = PlatePrompt.parse(reply) { counter += 1; return "id\(counter)" }
        #expect(suggestions.map(\.id) == ["id1", "id2"])
        #expect(suggestions[0].detail == "Twelve commits landed. I'll write them up.")
        #expect(suggestions[0].kind == .draft)
        #expect(suggestions[1].kind == .research)
        #expect(suggestions[0].steps == ["Read the commits", "Group by platform", "Write the notes", "Check the links"])
        #expect(suggestions[0].minutes == 15)
        #expect(suggestions[0].result == "a draft of the notes")
        #expect(suggestions[1].steps == nil && suggestions[1].minutes == nil && suggestions[1].result == nil)
        #expect(PlateSchedule.footnote(minutes: 15, result: "a draft of the notes") == "about 15 min · you get a draft of the notes")
        #expect(PlateSchedule.footnote(minutes: nil, result: " ") == nil)
        #expect(suggestions.allSatisfy { $0.status == .waiting })
    }

    @Test func dropsWhatCannotBeACard() {
        let reply = """
        {"suggestions":[
          {"title":"","prompt":"x"},
          {"title":"I'll do a thing","prompt":"  "},
          {"title":"I'll do a thing","prompt":"brief","kind":"mystery"},
          {"title":"i'll do a thing","prompt":"the same again"}
        ]}
        """
        let suggestions = PlatePrompt.parse(reply)
        #expect(suggestions.count == 1)
        #expect(suggestions[0].kind == .draft)   // an unknown kind is a draft
    }

    @Test func neverMoreThanSixAndNothingFromProse() {
        let items = (1...9).map { "{\"title\":\"I'll do \($0)\",\"prompt\":\"p\"}" }.joined(separator: ",")
        #expect(PlatePrompt.parse("{\"suggestions\":[\(items)]}").count == PlateSchedule.maximumCards)
        #expect(PlatePrompt.parse("I have nothing for you today.").isEmpty)
        #expect(PlatePrompt.parse("{\"suggestions\":[]}").isEmpty)
    }

    @Test func theNextRequestSaysWhatHappenedToTheLastCards() {
        var taken = PlateSuggestion(id: "a", title: "I'll draft notes", detail: "", prompt: "p", kind: .draft)
        taken.status = .taken
        var skipped = PlateSuggestion(id: "b", title: "I'll compare tools", detail: "", prompt: "p", kind: .research)
        skipped.status = .skipped
        let previous = PlateBatch(dayKey: "2026-11-21", suggestions: [taken, skipped], rating: .terrible, comment: "smaller jobs")
        let prompt = PlatePrompt.user(digest: "Today is Sunday.", previous: previous)
        #expect(prompt.contains("- I'll draft notes (they said go ahead)"))
        #expect(prompt.contains("they wrote: smaller jobs"))
        #expect(prompt.contains("- I'll compare tools (they skipped it)"))
        #expect(prompt.contains("not useful"))
        #expect(!PlatePrompt.user(digest: "d", previous: nil).contains("Last time"))
    }

    @Test func theAgentBriefCarriesTheChangeAndTheLimits() {
        var suggestion = PlateSuggestion(id: "a", title: "I'll draft notes", detail: "", prompt: "Goal: write the notes.", kind: .draft)
        #expect(!PlatePrompt.agentBrief(for: suggestion).contains("asked for this change"))
        suggestion.adjustment = "  top 3 only "
        let brief = PlatePrompt.agentBrief(for: suggestion)
        #expect(brief.hasPrefix("Goal: write the notes."))
        #expect(brief.contains("overrides the brief where they differ: top 3 only"))
        #expect(brief.contains("Do not send, post, publish, buy, delete"))
    }
}

struct PlateScheduleTests {
    private var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Asia/Singapore")!
        return calendar
    }

    private func date(_ day: Int, _ hour: Int) -> Date {
        calendar.date(from: DateComponents(year: 2026, month: 11, day: day, hour: hour))!
    }

    @Test func dueOncePerDayAndNotBeforeSix() {
        #expect(PlateSchedule.dayKey(for: date(22, 9), calendar: calendar) == "2026-11-22")
        #expect(PlateSchedule.isDue(now: date(22, 9), lastBatchDayKey: nil, calendar: calendar))
        #expect(PlateSchedule.isDue(now: date(22, 9), lastBatchDayKey: "2026-11-21", calendar: calendar))
        #expect(!PlateSchedule.isDue(now: date(22, 9), lastBatchDayKey: "2026-11-22", calendar: calendar))
        #expect(!PlateSchedule.isDue(now: date(22, 5), lastBatchDayKey: "2026-11-21", calendar: calendar))
    }

    @Test func theWordsCount() {
        #expect(PlateSchedule.headline(count: 1) == "1 job I could take on today")
        #expect(PlateSchedule.headline(count: 6) == "6 jobs I could take on today")
        #expect(PlateMenu.title(waiting: 0) == "What Could You Take On Today?")
        #expect(PlateMenu.title(waiting: 4) == "Today's Offers (4)…")
        #expect(PlateSchedule.dayPart(for: date(22, 9), calendar: calendar, locale: Locale(identifier: "en_US")) == "Sunday morning")
        #expect(PlateSchedule.dayPart(for: date(22, 19), calendar: calendar, locale: Locale(identifier: "en_US")) == "Sunday evening")
    }
}

struct PlateContextTests {
    private var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        return calendar
    }
    private let now = Date(timeIntervalSince1970: 1_795_000_000)

    @Test func anEmptyMacHasNothingToGoOn() {
        #expect(PlateContext().isEmpty)
        #expect(PlateContext(notes: "  \n").isEmpty)
        #expect(!PlateContext(notes: "call the bank").isEmpty)
    }

    @Test func theDigestGroupsSessionsByProjectNewestFirst() {
        let context = PlateContext(
            sessions: [
                .init(agent: "claude", project: "site", title: "Fix the footer", lastEventAt: now.addingTimeInterval(-7200)),
                .init(agent: "claude", project: "openclicky", title: "Release 0.12.2", lastEventAt: now.addingTimeInterval(-60)),
                .init(agent: "codex", project: "openclicky", title: "Release 0.12.2", lastEventAt: now.addingTimeInterval(-600)),
                .init(agent: "codex", project: "openclicky", title: "Windows dictation", lastEventAt: now.addingTimeInterval(-900)),
                .init(agent: "claude", project: "scratch", title: nil, lastEventAt: now.addingTimeInterval(-9000)),
            ],
            events: [.init(title: "Design review", start: now.addingTimeInterval(86_400), isAllDay: false)],
            notes: String(repeating: "n", count: 900))
        let digest = context.digest(now: now, calendar: calendar, locale: Locale(identifier: "en_US"))
        #expect(digest.contains("- openclicky: Release 0.12.2; Windows dictation\n- site: Fix the footer\n- scratch"))
        #expect(digest.contains(": Design review"))
        #expect(digest.contains(String(repeating: "n", count: PlateContext.maximumNoteCharacters)))
        #expect(!digest.contains(String(repeating: "n", count: PlateContext.maximumNoteCharacters + 1)))
    }

    @Test func theScanReadsOnlyRecentSessionsAndTheirTitles() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("plate-scan-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let project = root.appendingPathComponent("claude/projects/-Users-me-code-site", isDirectory: true)
        try FileManager.default.createDirectory(at: project, withIntermediateDirectories: true)
        let recent = project.appendingPathComponent("a.jsonl")
        try #"{"type":"user","cwd":"/Users/me/code/site"}"#.appending("\n").appending(#"{"type":"ai-title","aiTitle":"Fix the footer","cwd":"/Users/me/code/site"}"#).write(to: recent, atomically: true, encoding: .utf8)
        let old = project.appendingPathComponent("b.jsonl")
        try #"{"type":"user","cwd":"/Users/me/code/old"}"#.write(to: old, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.modificationDate: Date().addingTimeInterval(-10 * 86_400)], ofItemAtPath: old.path)

        let roots = AgentLogRoots(claudeProjects: root.appendingPathComponent("claude/projects"), codexSessions: root.appendingPathComponent("codex/sessions"))
        let sessions = PlateSessionScan.recentSessions(roots: roots, now: Date())
        #expect(sessions.map(\.project) == ["site"])
        #expect(sessions.first?.agent == "claude")
    }
}

struct PlateStoreTests {
    @Test func aNewDayKeepsTheOldBatchAsPrevious() {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("plate-store-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let store = PlateStore(folder: folder)
        #expect(store.loadCurrent() == nil)
        var saturday = PlateBatch(dayKey: "2026-11-21", suggestions: [PlateSuggestion(id: "a", title: "I'll a", detail: "", prompt: "p", kind: .watch)])
        store.save(saturday)
        saturday.rating = .okay
        saturday.suggestions[0].status = .skipped
        store.save(saturday)
        #expect(store.loadCurrent() == saturday)
        #expect(store.loadPrevious() == nil)
        let sunday = PlateBatch(dayKey: "2026-11-22", suggestions: [])
        store.save(sunday)
        #expect(store.loadCurrent() == sunday)
        #expect(store.loadPrevious() == saturday)
        #expect(saturday.waiting.isEmpty && saturday.hasDecision)
    }
}

struct PlateDeckLayoutTests {
    private let area = CGRect(x: 0, y: 96, width: 1512, height: 776)

    @Test func upToFourInARowThenTwoRows() {
        #expect(PlateDeckLayout.rows(for: 0) == [])
        #expect(PlateDeckLayout.rows(for: 3) == [3])
        #expect(PlateDeckLayout.rows(for: 4) == [4])
        #expect(PlateDeckLayout.rows(for: 5) == [3, 2])
        #expect(PlateDeckLayout.rows(for: 6) == [3, 3])
        #expect(PlateDeckLayout.rows(for: 7) == [4, 3])
    }

    @Test func everyCardLandsOnTheScreenAndOnlyAFewDegreesAskew() {
        for count in 1...7 {
            let slots = PlateDeckLayout.slots(count: count, in: area)
            let width = PlateDeckLayout.cardWidth(for: count, areaWidth: area.width)
            #expect(slots.count == count)
            for slot in slots {
                #expect(slot.center.x - width / 2 > 0 && slot.center.x + width / 2 < area.width)
                #expect(slot.center.y - PlateDeckLayout.cardHeight / 2 > area.minY - 20 && slot.center.y + PlateDeckLayout.cardHeight / 2 < area.maxY + 20)
                #expect(abs(slot.degrees) <= 7)
            }
        }
        #expect(PlateDeckLayout.slots(count: 6, in: area) == PlateDeckLayout.slots(count: 6, in: area))
    }

    @Test func aNarrowScreenShrinksTheCards() {
        #expect(PlateDeckLayout.cardWidth(for: 4, areaWidth: 1512) == PlateDeckLayout.cardWidth)
        let narrow = PlateDeckLayout.cardWidth(for: 4, areaWidth: 1180)
        #expect(narrow < PlateDeckLayout.cardWidth && narrow >= PlateDeckLayout.minimumCardWidth)
    }

    @Test func thePointerOpensTheCardUnderItAndTheOpenCardKeepsIt() {
        let slots = PlateDeckLayout.slots(count: 3, in: area)
        let width = PlateDeckLayout.cardWidth(for: 3, areaWidth: area.width)
        #expect(PlateDeckLayout.hoveredIndex(at: slots[1].center, slots: slots, cardWidth: width, openIndex: nil) == 1)
        #expect(PlateDeckLayout.hoveredIndex(at: CGPoint(x: 4, y: 100), slots: slots, cardWidth: width, openIndex: nil) == nil)
        // Under a resting card's bottom edge there is nothing; under the open one hangs its plan slip.
        let below = CGPoint(x: slots[1].center.x, y: slots[1].center.y + PlateDeckLayout.cardHeight / 2 + 60)
        #expect(PlateDeckLayout.hoveredIndex(at: below, slots: slots, cardWidth: width, openIndex: nil) == nil)
        #expect(PlateDeckLayout.hoveredIndex(at: below, slots: slots, cardWidth: width, openIndex: 1) == 1)
    }

    @Test func aThrownCardRisesThenLandsInTheBin() {
        let size = CGSize(width: 1512, height: 982)
        let bin = PlateDeckLayout.binCenter(in: size)
        #expect(bin.x == 756 && bin.y < size.height - 100)
        let start = CGPoint(x: 300, y: 300)
        #expect(Motion.arcPoint(from: start, to: bin, lift: 100, progress: 0) == start)
        #expect(Motion.arcPoint(from: start, to: bin, lift: 100, progress: 1) == bin)
        let middle = Motion.arcPoint(from: start, to: bin, lift: 100, progress: 0.5)
        #expect(middle.y == (start.y + bin.y) / 2 - 100)
        #expect(PlateMotion.throwLift(distance: 50) == 40)
        #expect(PlateMotion.throwLift(distance: 2000) == 140)
    }

    @Test func theLastCardLandsWithinASecondAndAHalf() {
        #expect(PlateMotion.dealTime(count: 6) < 1.5)
        #expect(PlateMotion.gatherTime(count: 6) < 0.6)
    }
}

@MainActor
struct PlateIslandModelTests {
    private func notchModel() -> NotchHUDModel {
        NotchHUDModel(geometry: NotchGeometry(screenFrame: CGRect(x: 0, y: 0, width: 1512, height: 982), hasHardwareNotch: true, notchWidth: 190, notchHeight: 33, visibleFrame: CGRect(x: 0, y: 0, width: 1512, height: 949)))
    }

    @Test func theCardOpensTheIslandAtItsOwnHeightAndClears() {
        let model = notchModel()
        model.setPlateCard(.announce(dayPart: "Sunday evening", count: 6, titles: ["a", "b", "c"]))
        #expect(model.expansion == .plate)
        #expect(model.width == 512)
        #expect(model.height == model.cardTopInset + 214)
        model.setPlateCard(.deck(count: 6))
        #expect(model.height == model.cardTopInset + 56)
        model.islandClicked()                     // the card owns its clicks
        #expect(model.expansion == .plate)
        model.setPlateCard(nil)
        #expect(model.expansion == .collapsed)
    }

    @Test func permissionCardsOutrankIt() {
        let model = notchModel()
        model.setPlateCard(.feedback)
        model.setPermissionPrompt(PermissionPrompt(step: .accessibility, status: PermissionStatus()))
        #expect(model.expansion == .permission)
    }
}

struct MotionTokenTests {
    @Test func theSpringsStayInTheirFamilies() {
        #expect((0.32...0.45).contains(Motion.settle.response) && (0.82...0.90).contains(Motion.settle.damping))
        #expect((0.28...0.38).contains(Motion.pop.response) && (0.70...0.80).contains(Motion.pop.damping))
        #expect((0.22...0.34).contains(Motion.bounce.response) && (0.50...0.62).contains(Motion.bounce.damping))
        #expect(Motion.structural.duration == 0.34 && Motion.structuralSlow.duration == 0.40)
        #expect(Motion.exitDuration <= 0.4 && Motion.hoverDuration <= 0.15)
    }

    @Test func aSpringConvertsForCoreAnimation() {
        let spring = Motion.SpringSpec(response: 0.5, damping: 0.74)
        #expect(abs(spring.stiffness - 157.91) < 0.01)
        #expect(abs(spring.dampingCoefficient - 18.6) < 0.01)
    }

    @Test func theStructuralCurveLeavesFastAndLandsSoftly() {
        let curve = Motion.structural
        #expect(curve.value(at: 0) == 0)
        #expect(abs(curve.value(at: 1) - 1) < 0.001)
        #expect(curve.value(at: 0.25) > 0.7)      // most of the way in the first quarter
        var last = -0.001
        for step in 0...20 {
            let value = curve.value(at: Double(step) / 20)
            #expect(value >= last - 0.002 && value <= 1.001)
            last = value
        }
    }
}

struct PlateSoundTests {
    @Test func everySoundIsShortAndEndsInSilence() {
        for sound in PlateSound.allCases {
            let samples = PlateSoundSynth.samples(for: sound)
            #expect(samples.count > 2_000 && samples.count < 22_050)          // under half a second
            #expect(samples.allSatisfy { abs($0) <= 1.5 })
            #expect(samples.contains { abs($0) > 0.05 })
            let tail = samples.suffix(200).map { abs($0) }.max() ?? 1
            #expect(tail < 0.12)
            #expect(PlateSoundSynth.samples(for: sound) == samples)            // the same every time
            #expect(PlateSoundSynth.wav(for: sound).count > samples.count * 2)
        }
    }

    @Test func theSamePenWobblesTheSameWay() {
        var first = PlateWobble(seed: "card-1"), again = PlateWobble(seed: "card-1"), other = PlateWobble(seed: "card-2")
        let a = (0..<6).map { _ in first.next() }, b = (0..<6).map { _ in again.next() }, c = (0..<6).map { _ in other.next() }
        #expect(a == b && a != c)
        #expect(a.allSatisfy { (-1...1).contains($0) })
        for kind in PlateSuggestion.Kind.allCases {
            #expect(!PlateDoodleGeometry.strokes(for: kind).isEmpty)
        }
    }
}

struct IslandNoticeTests {
    private func notice(_ id: String, seconds: Double = 8) -> IslandNotice { IslandNotice(id: id, title: id, seconds: seconds) }

    @Test func noticesShowOneAtATimeAndRunOut() {
        var queue = IslandNoticeQueue()
        #expect(queue.current == nil && queue.fraction == 0)
        queue.enqueue(notice("a", seconds: 2))
        queue.enqueue(notice("b", seconds: 4))
        #expect(queue.current?.id == "a" && queue.fraction == 1)
        let first = queue.advance(by: 1, isHeld: false)
        #expect(!first && queue.fraction == 0.5)
        let held = queue.advance(by: 5, isHeld: true)          // the pointer holds the clock
        #expect(!held && queue.fraction == 0.5)
        let ranOut = queue.advance(by: 1, isHeld: false)
        #expect(ranOut)
        #expect(queue.current?.id == "b" && queue.fraction == 1)
        queue.dismissCurrent()
        #expect(queue.current == nil)
    }

    @Test func theSameNoticeAgainReplacesItAndTheQueueIsBounded() {
        var queue = IslandNoticeQueue()
        queue.enqueue(notice("a", seconds: 2))
        queue.advance(by: 1.5, isHeld: false)
        queue.enqueue(IslandNotice(id: "a", title: "again", seconds: 6))
        #expect(queue.waiting.count == 1 && queue.current?.title == "again" && queue.fraction == 1)
        for index in 0..<10 { queue.enqueue(notice("n\(index)")) }
        #expect(queue.waiting.count == IslandNoticeQueue.maximumWaiting)
        #expect(queue.current?.id == "a" && queue.waiting.last?.id == "n9")
    }

    @MainActor @Test func aFinishedJobSaysSoWithItsFirstLine() {
        let done = IslandNoticeCenter.jobFinishedNotice(OpenClickyAgentResultSummary(
            threadId: "t1", title: "Compare tools", text: "The table is ready.\nSecond line.", artifacts: ["a.md", "b.md"], status: "completed", finishedAt: Date()))
        #expect(done.title == "A job is done" && done.tone == .done && done.actionTitle == "Open")
        #expect(done.detail == "The table is ready. \u{B7} 2 files")
        let stopped = IslandNoticeCenter.jobFinishedNotice(OpenClickyAgentResultSummary(
            threadId: "", title: "Compare tools", text: "", artifacts: [], status: "failed", finishedAt: Date()))
        #expect(stopped.title == "A job stopped early" && stopped.tone == .trouble && stopped.actionTitle == nil && stopped.detail == "Compare tools")
    }
}

struct IslandGlowTests {
    @Test func theGlowExistsOnlyWhileSomethingIsGoingOn() {
        #expect(IslandGlowTone.tone(voiceState: .idle, isAgentWorking: false) == nil)
        #expect(IslandGlowTone.tone(voiceState: .listening, isAgentWorking: false) == .listening)
        #expect(IslandGlowTone.tone(voiceState: .processing, isAgentWorking: false) == .thinking)
        #expect(IslandGlowTone.tone(voiceState: .processing, isAgentWorking: true) == .working)
        #expect(IslandGlowTone.tone(voiceState: .idle, isAgentWorking: true) == .working)
        #expect(IslandGlowTone.tone(voiceState: .responding, isAgentWorking: true) == .speaking)
        #expect(IslandGlowMetrics.sweepSeconds < IslandGlowMetrics.sweepCycleSeconds)
    }

    /// The light is drawn inside the island and scales down with a short one.
    @Test func theGlowIsSizedFromTheIslandsHeight() {
        #expect(IslandGlowMetrics.blobRadius(islandHeight: 32) <= 38)
        #expect(IslandGlowMetrics.blobRadius(islandHeight: 232) == 38)
        #expect(IslandGlowMetrics.blobRadius(islandHeight: 0) == 0)
        #expect(IslandGlowMetrics.bandHeight(islandHeight: 32) < 32)
    }
}

@MainActor
struct IslandRestAndNoticeModelTests {
    private func notchModel() -> NotchHUDModel {
        NotchHUDModel(geometry: NotchGeometry(screenFrame: CGRect(x: 0, y: 0, width: 1512, height: 982), hasHardwareNotch: true, notchWidth: 190, notchHeight: 33, visibleFrame: CGRect(x: 0, y: 0, width: 1512, height: 949)))
    }

    private func waitFor(_ model: NotchHUDModel, _ expansion: NotchHUDExpansion) async throws {
        for _ in 0..<200 where model.expansion != expansion { try await Task.sleep(nanoseconds: 20_000_000) }
    }

    @Test func restingThePointerOpensThePanelAndLeavingClosesIt() async throws {
        let model = notchModel()
        model.opensOnRest = true
        var taps = 0
        model.onOpenedByRest = { taps += 1 }
        model.setHovering(true)
        #expect(model.expansion == .peek)
        try await waitFor(model, .full)
        #expect(model.expansion == .full && model.isDwellOpen && taps == 1)
        model.setHovering(false)
        try await waitFor(model, .collapsed)
        #expect(model.expansion == .collapsed && !model.isDwellOpen)
    }

    @Test func withTheSwitchOffHoveringOnlyPeeks() async throws {
        let model = notchModel()
        model.opensOnRest = false
        model.setHovering(true)
        try await Task.sleep(nanoseconds: UInt64((IslandMotion.dwellSeconds + 0.3) * 1_000_000_000))
        #expect(model.expansion == .peek)
    }

    @Test func aNoticeShowsBelowOtherCardsAndHoldsUnderThePointer() {
        let model = notchModel()
        model.opensOnRest = false
        let state = IslandNoticeState(notice: IslandNotice(id: "a", title: "A job is done"), fraction: 1)
        model.setNotice(state)
        #expect(model.expansion == .notice)
        #expect(model.height == model.cardTopInset + IslandNoticeState.contentHeight)
        model.setHovering(true)                          // hovering holds the card, it does not peek
        #expect(model.expansion == .notice)
        model.setPlateCard(.feedback)
        #expect(model.expansion == .plate)
        model.setPlateCard(nil)
        model.setNotice(nil)
        model.setHovering(false)
        #expect(model.expansion != .notice)
    }
}
