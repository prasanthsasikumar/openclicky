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
#endif
