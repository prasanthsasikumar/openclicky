//
//  IslandSmoke.swift
//  OpenClicky
//
//  `--openclicky-island-smoke`: the island's states played by the app itself, for a recording:
//  listening, thinking, an agent working (two milestones), speaking, a finished-job card, then
//  the pointer "resting" on the notch until the panel opens. No pointer or keyboard is used.
//

#if DEBUG
import AppKit
import Foundation
import SwiftUI

@MainActor
enum IslandSmoke {
    static func run(companionManager: CompanionManager) {
        var clock = 0.0
        func at(_ delay: TimeInterval, _ label: String, _ work: @escaping @MainActor () -> Void) {
            clock += delay
            DispatchQueue.main.asyncAfter(deadline: .now() + clock) {
                AppLog.append("island smoke: \(label)")
                work()
            }
        }
        let hud = companionManager.notchHUDManager
        at(0.5, "listening") { companionManager.voiceState = .listening }
        at(2.2, "thinking") { companionManager.voiceState = .processing }
        at(2.0, "agent working") { companionManager.debugSetAgentActivity("reading the page…") }
        at(1.8, "second milestone") { companionManager.debugSetAgentActivity("writing the summary…") }
        at(2.0, "speaking") {
            companionManager.debugSetAgentActivity(nil)
            companionManager.voiceState = .responding
        }
        at(2.0, "idle, job-done card") {
            companionManager.voiceState = .idle
            companionManager.islandNotices.post(IslandNoticeCenter.jobFinishedNotice(OpenClickyAgentResultSummary(
                threadId: "smoke", title: "Compare 6 code signing services", text: "The comparison table is ready, with a recommendation.",
                artifacts: ["signing.md"], status: "completed", finishedAt: Date())))
        }
        at(4.0, "card dismissed, pointer rests") {
            companionManager.islandNotices.dismiss()
            hud.debugRestOnPrimary()
        }
        at(3.0, "pointer leaves") { hud.debugCloseAll() }
        at(1.5, "done") {}
    }
}

/// `--openclicky-render-cards <folder>`: the island's cards drawn to PNG files without a screen,
/// to check their layout (the views that are AppKit or Core Animation underneath come out blank).
@MainActor
enum IslandCardRender {
    static func run(folder: String, companionManager: CompanionManager) {
        let directory = URL(fileURLWithPath: folder, isDirectory: true)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        var typed = IslandReplyState(question: "what does the sweep along the island's edge mean?", showsAnswer: true)
        save("reply-1-waiting", reply(typed, companionManager), to: directory)
        typed.text = "It means an agent is working on something for you. The light under the corners tells you which state Clicky is in:"
        save("reply-2-streaming", reply(typed, companionManager), to: directory)
        typed.text += " orange while it listens, amber while it thinks, green while it speaks and blue while an agent works. When the job is finished the sweep stops and a card says so."
        typed.isFinished = true
        typed.fraction = 0.7
        typed.nextQuestions = ["Can I switch the light off?", "What does the agent do while it works?", "How do I stop a job?"]
        save("reply-3-finished", reply(typed, companionManager), to: directory)
        var job = IslandReplyState(question: "compare 6 code signing services and tell me which fits", showsAnswer: true)
        job.addStep("starting agent…")
        job.addStep("reading the pricing pages")
        job.addStep("writing the comparison")
        save("reply-4-job-steps", reply(job, companionManager), to: directory)
        job.text = "The comparison is ready. For an open source project the foundation programme fits best: it is free and takes about two weeks."
        job.isFinished = true
        job.threadId = "smoke"
        save("reply-5-job-done", reply(job, companionManager), to: directory)
        let spoken = IslandReplyState(question: "", nextQuestions: ["Why is the sky red at sunset?", "Is it the same on Mars?"], showsAnswer: false)
        save("reply-6-next-only", reply(spoken, companionManager), to: directory)
        let notice = IslandNoticeState(notice: IslandNoticeCenter.jobFinishedNotice(OpenClickyAgentResultSummary(
            threadId: "smoke", title: "Compare 6 code signing services", text: "The comparison table is ready, with a recommendation.",
            artifacts: ["signing.md"], status: "completed", finishedAt: Date())), fraction: 0.6)
        save("notice-job-done", IslandNoticeView(state: notice, center: companionManager.islandNotices)
            .frame(width: 512, height: IslandNoticeState.contentHeight).background(Color.black), to: directory)
        let sample = PlateController.sampleSuggestions[1]
        save("plate-card-open", PlateCardView(suggestion: sample, width: 300, isOpen: true, isAdjusting: false, isStamped: false, reduceMotion: true,
                                              adjustment: .constant(""), onDoIt: {}, onAdjust: {}, onCancelAdjust: {}, onSkip: {})
            .padding(.bottom, 150).padding(30).background(Color(white: 0.25)), to: directory)
        save("plate-card-stamped", PlateCardView(suggestion: sample, width: 300, isOpen: false, isAdjusting: false, isStamped: true, reduceMotion: true,
                                                 adjustment: .constant(""), onDoIt: {}, onAdjust: {}, onCancelAdjust: {}, onSkip: {})
            .padding(30).background(Color(white: 0.25)), to: directory)
        AppLog.append("render cards: written to \(directory.path)")
    }

    private static func reply(_ state: IslandReplyState, _ companionManager: CompanionManager) -> some View {
        IslandReplyView(state: state, controller: companionManager.islandReply, reduceMotion: true)
            .frame(width: IslandReplyLayout.width, height: IslandReplyLayout.contentHeight(for: state))
            .background(Color.black)
    }

    private static func save(_ name: String, _ view: some View, to directory: URL) {
        let renderer = ImageRenderer(content: view)
        renderer.scale = 2
        guard let image = renderer.nsImage, let tiff = image.tiffRepresentation, let bitmap = NSBitmapImageRep(data: tiff),
              let png = bitmap.representation(using: .png, properties: [:]) else {
            AppLog.append("render cards: \(name) could not be drawn")
            return
        }
        try? png.write(to: directory.appendingPathComponent(name + ".png"))
    }
}
#endif
