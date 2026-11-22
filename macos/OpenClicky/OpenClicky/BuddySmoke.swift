//
//  BuddySmoke.swift
//  OpenClicky
//
//  Debug only. `OpenClicky --openclicky-buddy-smoke [stay|exit]` makes the pointer play its states
//  by itself, for a screen recording: four flights between fixed places on the main display (a
//  long one, a medium one, a short hop), the hold on a target, the flight back to the mouse, then
//  listening (with a made-up loudness), thinking, speaking and idle. Nothing is heard, asked or
//  clicked and no window takes the keyboard. Each step is written to the log as "buddy smoke: …".
//

#if DEBUG
import AppKit

@MainActor
enum BuddySmoke {
    /// Seconds from `play` to the last step, for whoever records it.
    static let length: TimeInterval = 31

    private static var loudnessTimer: Timer?

    static func play(on companionManager: CompanionManager, quitsWhenDone: Bool) {
        guard let screen = NSScreen.screens.first else {
            AppLog.append("buddy smoke: no display, nothing played")
            return
        }
        let frame = screen.frame
        let reduceMotion = companionManager.dictationSettings.reduceAnimation || NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        AppLog.append("buddy smoke: start on \(Int(frame.width))x\(Int(frame.height)), reduce motion \(reduceMotion), presence \(companionManager.pointerPresence.rawValue), about \(Int(length)) s")

        /// A place on the main display by its share of the width and of the height from the top.
        func place(_ x: CGFloat, _ fromTop: CGFloat) -> CGPoint {
            CGPoint(x: frame.minX + frame.width * x, y: frame.maxY - frame.height * fromTop)
        }
        var clock: TimeInterval = 0
        func at(_ delay: TimeInterval, _ step: String, _ work: @escaping @MainActor () -> Void) {
            clock += delay
            DispatchQueue.main.asyncAfter(deadline: .now() + clock) {
                AppLog.append("buddy smoke: \(step)")
                work()
            }
        }
        func fly(to point: CGPoint, saying bubble: String) {
            companionManager.pointForSettingsTour(at: point, bubble: bubble)
        }

        companionManager.debugShowPointerOverlay()
        companionManager.wakePointer()
        // The pointer stays on each place until it is sent to the next one.
        companionManager.beginSettingsTourPointing()

        let first = place(0.25, 0.30)
        let second = place(0.78, 0.38)
        let third = place(0.70, 0.70)
        let fourth = CGPoint(x: third.x + 90, y: third.y)

        at(1.0, "flight 1 of 4, from the mouse to the upper left") { fly(to: first, saying: "one") }
        at(2.6, "flight 2 of 4, a long one to the upper right") { fly(to: second, saying: "two") }
        at(2.6, "flight 3 of 4, down the right side") { fly(to: third, saying: "three") }
        at(2.2, "flight 4 of 4, a short hop (no streak)") { fly(to: fourth, saying: "four") }
        at(2.2, "holding on the target for about 4 s, then flying back to the mouse") {
            companionManager.endSettingsTourPointing()
        }
        at(6.5, "listening for 3.5 s (made-up loudness)") {
            companionManager.debugSetVoiceState(.listening)
            let startedAt = ProcessInfo.processInfo.systemUptime
            loudnessTimer?.invalidate()
            loudnessTimer = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { _ in
                let elapsed = ProcessInfo.processInfo.systemUptime - startedAt
                // Speech-like: syllables a few times a second, with a pause in the middle.
                let syllables = abs(sin(elapsed * 7.5)) * (0.6 + 0.4 * sin(elapsed * 2.1))
                let level = (1.4...2.0).contains(elapsed) ? 0.0 : 0.02 + 0.26 * max(0, syllables)
                Task { @MainActor in companionManager.debugSetAudioPowerLevel(CGFloat(level)) }
            }
        }
        at(3.5, "thinking for 3 s") {
            loudnessTimer?.invalidate()
            loudnessTimer = nil
            companionManager.debugSetAudioPowerLevel(0)
            companionManager.debugSetVoiceState(.processing)
        }
        at(3.0, "speaking for 3 s (no sound)") {
            companionManager.debugSetVoiceState(.responding)
        }
        at(3.0, "idle beside the mouse for 4.5 s (it bobs once the mouse has been still 0.6 s)") {
            companionManager.debugSetVoiceState(.idle)
            companionManager.wakePointer()
        }
        at(4.5, quitsWhenDone ? "done, quitting" : "done, staying idle") {
            if quitsWhenDone { NSApp.terminate(nil) }
        }
    }
}
#endif
