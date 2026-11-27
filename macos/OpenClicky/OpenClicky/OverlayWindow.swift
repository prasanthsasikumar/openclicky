//
//  OverlayWindow.swift
//  OpenClicky
//
//  System-wide transparent overlay window for blue glowing cursor.
//  One OverlayWindow is created per screen so the cursor buddy
//  seamlessly follows the cursor across multiple monitors.
//
//  How it moves is in two other files: the animations are `Motion` tokens, and the flight arc,
//  the streak, the waveform shaping, the thinking dots and the idle bob are the pure functions
//  in `CursorBuddyMotion.swift`. Every loop here (bob, halo, dots, the glow while holding on a
//  target) runs only while it can be seen and has a still form under Reduce Motion.
//

import AppKit
import Combine
import QuartzCore
import SwiftUI

class OverlayWindow: NSWindow {
    init(screen: NSScreen) {
        // Create window covering entire screen
        super.init(
            contentRect: screen.frame,
            styleMask: .borderless,
            backing: .buffered,
            defer: false
        )

        // Make window transparent and non-interactive
        self.isOpaque = false
        self.backgroundColor = .clear
        self.level = .screenSaver  // Always on top, above submenus and popups
        self.ignoresMouseEvents = true  // Click-through
        self.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary]
        self.isReleasedWhenClosed = false
        self.hasShadow = false

        // Important: Allow the window to appear even when app is not active
        self.hidesOnDeactivate = false

        // Cover the entire screen
        self.setFrame(screen.frame, display: true)

        // Make sure it's on the right screen
        if let screenForWindow = NSScreen.screens.first(where: { $0.frame == screen.frame }) {
            self.setFrameOrigin(screenForWindow.frame.origin)
        }
    }

    // Prevent window from becoming key (no focus stealing)
    override var canBecomeKey: Bool {
        return false
    }

    override var canBecomeMain: Bool {
        return false
    }
}

// Cursor-like triangle shape (equilateral)
struct Triangle: Shape {
    func path(in rect: CGRect) -> Path {
        var path = Path()
        let size = min(rect.width, rect.height)
        let height = size * sqrt(3.0) / 2.0

        // Top vertex
        path.move(to: CGPoint(x: rect.midX, y: rect.midY - height / 1.5))
        // Bottom left vertex
        path.addLine(to: CGPoint(x: rect.midX - size / 2, y: rect.midY + height / 3))
        // Bottom right vertex
        path.addLine(to: CGPoint(x: rect.midX + size / 2, y: rect.midY + height / 3))
        path.closeSubpath()
        return path
    }
}

// PreferenceKey for tracking bubble size
struct SizePreferenceKey: PreferenceKey {
    static var defaultValue: CGSize = .zero
    static func reduce(value: inout CGSize, nextValue: () -> CGSize) {
        value = nextValue()
    }
}

struct GuideStopHintSizePreferenceKey: PreferenceKey {
    static var defaultValue: CGSize = .zero
    static func reduce(value: inout CGSize, nextValue: () -> CGSize) {
        value = nextValue()
    }
}

struct NavigationBubbleSizePreferenceKey: PreferenceKey {
    static var defaultValue: CGSize = .zero
    static func reduce(value: inout CGSize, nextValue: () -> CGSize) {
        value = nextValue()
    }
}

struct CaptionBubbleSizePreferenceKey: PreferenceKey {
    static var defaultValue: CGSize = .zero
    static func reduce(value: inout CGSize, nextValue: () -> CGSize) {
        value = nextValue()
    }
}

/// The buddy's behavioral mode. Controls whether it follows the cursor,
/// is flying toward a detected UI element, or is pointing at an element.
enum BuddyNavigationMode {
    /// Default — buddy follows the mouse cursor with spring animation
    case followingCursor
    /// Buddy is animating toward a detected UI element location
    case navigatingToTarget
    /// Buddy has arrived at the target and is pointing at it with a speech bubble
    case pointingAtTarget
}

// SwiftUI view for the blue glowing cursor pointer.
// Each screen gets its own BlueCursorView. The view checks whether
// the cursor is currently on THIS screen and only shows the buddy
// triangle when it is. During voice interaction, the triangle is
// replaced by a waveform (listening), thinking dots (processing), or
// streaming text bubble (responding).
struct BlueCursorView: View {
    let screenFrame: CGRect
    let isFirstAppearance: Bool
    @ObservedObject var companionManager: CompanionManager
    /// Observed for "reduce animation", so a loop stops the moment it is switched on.
    @ObservedObject private var dictationSettings: DictationSettings
    @Environment(\.accessibilityReduceMotion) private var systemReduceMotion

    /// Reduce Motion, from macOS or from OpenClicky's own setting: flights go straight and keep
    /// their size, and nothing loops.
    private var reduceMotion: Bool { systemReduceMotion || dictationSettings.reduceAnimation }

    @State private var cursorPosition: CGPoint
    @State private var isCursorOnThisScreen: Bool

    init(screenFrame: CGRect, isFirstAppearance: Bool, companionManager: CompanionManager) {
        self.screenFrame = screenFrame
        self.isFirstAppearance = isFirstAppearance
        self.companionManager = companionManager
        self.dictationSettings = companionManager.dictationSettings

        // Seed the cursor position from the current mouse location so the
        // buddy doesn't flash at (0,0) before onAppear fires.
        let mouseLocation = NSEvent.mouseLocation
        let localX = mouseLocation.x - screenFrame.origin.x
        let localY = screenFrame.height - (mouseLocation.y - screenFrame.origin.y)
        _cursorPosition = State(initialValue: CGPoint(x: localX + 35, y: localY + 25))
        _isCursorOnThisScreen = State(initialValue: screenFrame.contains(mouseLocation))
    }
    @State private var timer: Timer?
    @State private var welcomeText: String = ""
    @State private var showWelcome: Bool = true
    @State private var bubbleSize: CGSize = .zero
    /// Measured size of the caption bubble (it wraps), for placing it beside the buddy.
    @State private var captionBubbleSize: CGSize = .zero
    @State private var bubbleOpacity: Double = 1.0
    @State private var cursorOpacity: Double = 0.0

    // MARK: - Buddy Navigation State

    /// The buddy's current behavioral mode (following cursor, navigating, or pointing).
    @State private var buddyNavigationMode: BuddyNavigationMode = .followingCursor

    /// The rotation angle of the triangle in degrees. Default is -35° (cursor-like).
    /// Changes to face the direction of travel when navigating to a target.
    @State private var triangleRotationDegrees: Double = CursorFlightPath.restingRotationDegrees

    /// Speech bubble text shown when pointing at a detected element.
    @State private var navigationBubbleText: String = ""
    @State private var navigationBubbleOpacity: Double = 0.0
    @State private var navigationBubbleSize: CGSize = .zero
    @State private var guideStopHintSize: CGSize = .zero

    /// The cursor position at the moment navigation started, used to detect
    /// if the user moves the cursor enough to cancel the navigation.
    @State private var cursorPositionWhenNavigationStarted: CGPoint = .zero

    /// Timer driving the frame-by-frame bezier arc flight animation.
    /// Invalidated when the flight completes, is canceled, or the view disappears.
    @State private var navigationAnimationTimer: Timer?
    /// Bumped on every pointing start/retarget so the delayed fly-back of an earlier point
    /// (e.g. step 1 of a Realtime walkthrough) cannot pull the buddy off the current target.
    @State private var pointingGeneration = 0

    /// Scale factor applied to the buddy triangle during flight. Grows to ~1.3x
    /// at the midpoint of the arc and shrinks back to 1.0x on landing, creating
    /// an energetic "swooping" feel.
    @State private var buddyFlightScale: CGFloat = 1.0

    /// Scale factor for the navigation speech bubble's pop-in entrance.
    /// Starts at 0.5 and springs to 1.0 when the first character appears.
    @State private var navigationBubbleScale: CGFloat = 1.0

    /// True when the buddy is flying BACK to the cursor after pointing.
    /// Only during the return flight can cursor movement cancel the animation.
    @State private var isReturningToCursor: Bool = false
    /// The Settings tour's simulated click: a ring that grows and fades where the pointer's tip is.
    @State private var clickRippleScale: CGFloat = 0.3
    @State private var clickRippleOpacity: Double = 0
    /// The flight being drawn, for the streak behind the pointer; nil when it leaves none.
    @State private var flightStreak: FlightStreak?
    /// Bumped each time the pointer reaches what it points at; `ArrivalPulse` plays once per bump.
    @State private var arrivalToken = 0
    /// True for a few seconds once the mouse has been still: the pointer bobs (`CursorIdleBob`).
    @State private var isMouseSettled = false
    @State private var mouseRestClock = MouseRestClock()

    private struct FlightStreak: Equatable {
        let path: CursorFlightPath
        let progress: Double
    }

    private let fullWelcomeMessage = "Hi, I'm OpenClicky."

    private let navigationPointerPhrases = [
        "right here!",
        "this one!",
        "over here!",
        "click this!",
        "here it is!",
        "found it!"
    ]

    var body: some View {
        ZStack {
            // Nearly transparent background (helps with compositing)
            Color.black.opacity(0.001)

            // Welcome speech bubble (first launch only)
            if isCursorOnThisScreen && showWelcome && !welcomeText.isEmpty {
                Text(welcomeText)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundColor(.white)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 4)
                    .background(
                        RoundedRectangle(cornerRadius: 6, style: .continuous)
                            .fill(DS.Colors.overlayCursorColor)
                            .shadow(color: DS.Colors.overlayCursorColor.opacity(0.5), radius: 6, x: 0, y: 0)
                    )
                    .fixedSize()
                    .overlay(
                        GeometryReader { geo in
                            Color.clear
                                .preference(key: SizePreferenceKey.self, value: geo.size)
                        }
                    )
                    .opacity(bubbleOpacity)
                    .position(x: cursorPosition.x + 10 + (bubbleSize.width / 2), y: cursorPosition.y + 18)
                    .animation(Motion.pointerFollow(reduceMotion: reduceMotion), value: cursorPosition)
                    .animation(Motion.captionFade, value: bubbleOpacity)
                    .onPreferenceChange(SizePreferenceKey.self) { newSize in
                        bubbleSize = newSize
                    }
            }

            // Caption typed out next to the buddy: the onboarding prompt ("press control + option
            // and introduce yourself"), the open island's (i) explanation, hands-free feedback. Wraps
            // at 300 pt and flips to the buddy's left when it would run off the screen.
            if isCursorOnThisScreen && companionManager.isCursorCaptionVisible && !companionManager.cursorCaptionText.isEmpty {
                let fitsToTheRight = cursorPosition.x + 10 + captionBubbleSize.width <= screenFrame.width - 12
                Text(companionManager.cursorCaptionText)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundColor(.white)
                    .multilineTextAlignment(.leading)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 5)
                    .background(
                        RoundedRectangle(cornerRadius: 6, style: .continuous)
                            .fill(DS.Colors.overlayCursorColor)
                            .shadow(color: DS.Colors.overlayCursorColor.opacity(0.5), radius: 6, x: 0, y: 0)
                    )
                    .fixedSize(horizontal: false, vertical: true)
                    .overlay(
                        GeometryReader { geo in
                            Color.clear
                                .preference(key: CaptionBubbleSizePreferenceKey.self, value: geo.size)
                        }
                    )
                    .frame(width: 300, alignment: fitsToTheRight ? .leading : .trailing)
                    .opacity(companionManager.cursorCaptionOpacity)
                    .position(
                        x: fitsToTheRight ? cursorPosition.x + 10 + 150 : cursorPosition.x - 10 - 150,
                        y: cursorPosition.y + 12 + (captionBubbleSize.height / 2)
                    )
                    .animation(Motion.pointerFollow(reduceMotion: reduceMotion), value: cursorPosition)
                    .animation(Motion.captionFade, value: companionManager.cursorCaptionOpacity)
                    .onPreferenceChange(CaptionBubbleSizePreferenceKey.self) { newSize in
                        captionBubbleSize = newSize
                    }
            }

            // While a guide waits for the person to do a step, a quiet tag beside the pointer says how
            // to leave it: someone new to the guide had no idea it could be stopped. Left of the
            // pointer, so it never covers the answer caption on its right.
            if isCursorOnThisScreen && companionManager.guideStepUnderWay != nil && companionManager.voiceState == .idle {
                Text("esc to stop")
                    .font(.system(size: 10, weight: .medium))
                    .foregroundColor(.white.opacity(0.9))
                    .padding(.horizontal, 6)
                    .padding(.vertical, 3)
                    .background(
                        RoundedRectangle(cornerRadius: 5, style: .continuous)
                            .fill(Color.black.opacity(0.55))
                    )
                    .fixedSize()
                    .overlay(
                        GeometryReader { geo in
                            Color.clear
                                .preference(key: GuideStopHintSizePreferenceKey.self, value: geo.size)
                        }
                    )
                    .position(x: cursorPosition.x - 12 - (guideStopHintSize.width / 2), y: cursorPosition.y + 14)
                    .animation(Motion.pointerFollow(reduceMotion: reduceMotion), value: cursorPosition)
                    .transition(.opacity)
                    .onPreferenceChange(GuideStopHintSizePreferenceKey.self) { newSize in
                        guideStopHintSize = newSize
                    }
            }

            // Navigation pointer bubble — shown when buddy arrives at a detected element.
            // Pops in with a scale-bounce (0.5x → 1.0x spring) and a bright initial
            // glow that settles, creating a "materializing" effect.
            if buddyNavigationMode == .pointingAtTarget && !navigationBubbleText.isEmpty {
                Text(navigationBubbleText)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundColor(.white)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 4)
                    .background(
                        RoundedRectangle(cornerRadius: 6, style: .continuous)
                            .fill(DS.Colors.overlayCursorColor)
                            .shadow(
                                color: DS.Colors.overlayCursorColor.opacity(0.5 + (1.0 - navigationBubbleScale) * 1.0),
                                radius: 6 + (1.0 - navigationBubbleScale) * 16,
                                x: 0, y: 0
                            )
                    )
                    .fixedSize()
                    .overlay(
                        GeometryReader { geo in
                            Color.clear
                                .preference(key: NavigationBubbleSizePreferenceKey.self, value: geo.size)
                        }
                    )
                    .scaleEffect(navigationBubbleScale)
                    .opacity(navigationBubbleOpacity)
                    .position(x: cursorPosition.x + 10 + (navigationBubbleSize.width / 2), y: cursorPosition.y + 18)
                    .animation(Motion.pointerFollow(reduceMotion: reduceMotion), value: cursorPosition)
                    .animation(Motion.pointerArrive(reduceMotion: reduceMotion), value: navigationBubbleScale)
                    .animation(Motion.captionFade, value: navigationBubbleOpacity)
                    .onPreferenceChange(NavigationBubbleSizePreferenceKey.self) { newSize in
                        navigationBubbleSize = newSize
                    }
            }

            // The streak the pointer leaves behind it on a long flight.
            if let flightStreak {
                TravelStreakView(
                    points: CursorTravelStreak.points(on: flightStreak.path, atProgress: flightStreak.progress),
                    opacity: CursorTravelStreak.opacity(atProgress: flightStreak.progress)
                )
                .allowsHitTesting(false)
            }

            // Blue triangle cursor — shown when idle or while TTS is playing (responding).
            // All three states (triangle, waveform, dots) stay in the view tree
            // permanently and cross-fade via opacity so SwiftUI doesn't remove/re-insert
            // them (which caused a visible cursor "pop").
            //
            // During cursor following: spring animation for snappy tracking.
            // During navigation: NO implicit animation — the flight timer sets position, turn and
            // size directly each frame. Each animation sits right after what it animates, because
            // the innermost one wins: the landing settle and the turn back to rest must not be
            // cancelled by the "no animation" that position has outside following.
            LoopingPhase(isRunning: holdGlowIsOn, beat: Motion.haloBeat) { breath in
                LoopingPhase(isRunning: bobIsOn, beat: Motion.bobBeat) { bob in
                    Triangle()
                        .fill(DS.Colors.overlayCursorColor)
                        .frame(width: 16, height: 16)
                        .rotationEffect(.degrees(triangleRotationDegrees))
                        .animation(
                            buddyNavigationMode == .navigatingToTarget ? nil : Motion.structural(reduceMotion: reduceMotion),
                            value: triangleRotationDegrees
                        )
                        .shadow(
                            color: DS.Colors.overlayCursorColor,
                            radius: CursorFlightPath.glowRadius(forScale: buddyFlightScale) + holdGlowExtra + breath * 6,
                            x: 0, y: 0
                        )
                        .scaleEffect(buddyFlightScale * (presenceAllowsPointer ? 1 : 0.7))
                        .animation(
                            buddyNavigationMode == .navigatingToTarget ? nil : Motion.pointerLanding(reduceMotion: reduceMotion),
                            value: buddyFlightScale
                        )
                        .animation(Motion.pointerScale(reduceMotion: reduceMotion), value: presenceAllowsPointer)
                        .scaleEffect(1 + breath * 0.033)
                        .offset(y: -CursorIdleBob.rise * bob)
                }
            }
            .opacity(triangleIsShown ? cursorOpacity : 0)
            .position(cursorPosition)
            .animation(
                buddyNavigationMode == .followingCursor
                    ? Motion.pointerFollow(reduceMotion: reduceMotion)
                    : nil,
                value: cursorPosition
            )
            .animation(Motion.swap, value: companionManager.voiceState)
            .animation(companionManager.isPointerAwake ? Motion.swap : Motion.slowExit, value: companionManager.isPointerAwake)

            // A ring where the pointer's tip is, once, when it reaches what it points at. The
            // pointer sits 8 pt right and 12 pt below that place.
            ArrivalPulse(token: arrivalToken, reduceMotion: reduceMotion)
                .position(x: cursorPosition.x - 8, y: cursorPosition.y - 12)
                .allowsHitTesting(false)

            // The Settings tour's "click": the pointer sits 8 pt right and 12 pt below what it points at.
            Circle()
                .stroke(DS.Colors.overlayCursorColor, lineWidth: 2.5)
                .frame(width: 34, height: 34)
                .scaleEffect(clickRippleScale)
                .opacity(buddyIsVisibleOnThisScreen ? clickRippleOpacity : 0)
                .position(x: cursorPosition.x - 8, y: cursorPosition.y - 12)
                .allowsHitTesting(false)

            // Blue waveform in a soft halo — replaces the triangle while listening. The halo
            // breathes; under Reduce Motion it is a still disc.
            ZStack {
                LoopingPhase(isRunning: isListeningHere && !reduceMotion, beat: Motion.haloBeat) { beat in
                    Circle()
                        .fill(DS.Colors.overlayCursorColor.opacity(0.22))
                        .frame(width: 28, height: 28)
                        .scaleEffect(1 + beat * 0.16)
                        .opacity(reduceMotion ? 0.5 : 0.72 - beat * 0.44)
                }
                BlueCursorWaveformView(
                    audioPowerLevel: companionManager.currentAudioPowerLevel,
                    isActive: isListeningHere,
                    reduceMotion: reduceMotion
                )
            }
            .opacity(isListeningHere ? cursorOpacity : 0)
            .position(cursorPosition)
            .animation(Motion.pointerFollow(reduceMotion: reduceMotion), value: cursorPosition)
            .animation(Motion.swap, value: companionManager.voiceState)

            // Three dots taking turns — shown while the AI is processing (transcription, the
            // answer, waiting for the voice). They draw together into the pointer when it is done.
            CursorThinkingDotsView(isActive: isThinkingHere && !isSketchingHere, reduceMotion: reduceMotion)
                .opacity(isThinkingHere && !isSketchingHere ? cursorOpacity : 0)
                .position(cursorPosition)
                .animation(Motion.pointerFollow(reduceMotion: reduceMotion), value: cursorPosition)
                .animation(Motion.swap, value: companionManager.voiceState)

            // A pencil sketching — in place of the dots while a drawing is on its way, so the
            // wait for its first line reads as "it is being drawn" and not as nothing happening.
            CursorSketchingView(isActive: isSketchingHere, reduceMotion: reduceMotion)
                .opacity(isSketchingHere ? cursorOpacity : 0)
                .position(cursorPosition)
                .animation(Motion.pointerFollow(reduceMotion: reduceMotion), value: cursorPosition)
                .animation(Motion.swap, value: isSketchingHere)

        }
        .frame(width: screenFrame.width, height: screenFrame.height)
        .ignoresSafeArea()
        .onAppear {
            // Set initial cursor position immediately before starting animation
            let mouseLocation = NSEvent.mouseLocation
            isCursorOnThisScreen = screenFrame.contains(mouseLocation)

            let swiftUIPosition = convertScreenPointToSwiftUICoordinates(mouseLocation)
            self.cursorPosition = CGPoint(x: swiftUIPosition.x + 35, y: swiftUIPosition.y + 25)

            // OpenClicky: when the buddy is introduced from the notch (the first-run tour), start
            // at the notch and fly out instead of popping in at the mouse.
            if let launchOrigin = companionManager.cursorLaunchOriginScreenLocation, screenFrame.contains(launchOrigin) {
                self.cursorPosition = convertScreenPointToSwiftUICoordinates(launchOrigin)
                self.cursorOpacity = 1.0
                companionManager.cursorLaunchOriginScreenLocation = nil
                startTrackingCursor()
                if companionManager.detectedElementScreenLocation == nil {
                    startFlyingBackToCursor()
                }
                return
            }

            startTrackingCursor()

            // Only show welcome message on first appearance (app start)
            // and only if the cursor starts on this screen
            if isFirstAppearance && isCursorOnThisScreen {
                withAnimation(Motion.firstAppearance) {
                    self.cursorOpacity = 1.0
                }
                DispatchQueue.main.asyncAfter(deadline: .now() + 2.0) {
                    self.bubbleOpacity = 0.0
                    startWelcomeAnimation()
                }
            } else {
                self.cursorOpacity = 1.0
            }
        }
        .onDisappear {
            timer?.invalidate()
            navigationAnimationTimer?.invalidate()
        }
        .onChange(of: companionManager.detectedElementPointToken) { _ in
            // Every pointAt bumps the token, so repeated coordinates re-fly too. The location
            // is read from the manager (it is set before the token is bumped).
            guard let screenLocation = companionManager.detectedElementScreenLocation,
                  let displayFrame = companionManager.detectedElementDisplayFrame else {
                return
            }

            // Only navigate if the target is on THIS screen
            guard screenFrame.contains(CGPoint(x: displayFrame.midX, y: displayFrame.midY))
                  || displayFrame == screenFrame else {
                return
            }

            startNavigatingToElement(screenLocation: screenLocation)
        }
        .onChange(of: companionManager.pointerClickToken) { _ in
            clickRippleScale = 0.3
            clickRippleOpacity = 0.9
            withAnimation(Motion.ripple) {
                clickRippleScale = 1.4
                clickRippleOpacity = 0
            }
        }
        .onChange(of: companionManager.cursorFlightLeg) { flightLeg in
            guard let flightLeg, flightLeg.screenFrame == screenFrame else { return }
            performFlightLeg(flightLeg)
        }
    }

    private var triangleIsShown: Bool {
        buddyIsVisibleOnThisScreen && presenceAllowsPointer
            && (companionManager.voiceState == .idle || companionManager.voiceState == .responding)
    }

    private var isListeningHere: Bool {
        buddyIsVisibleOnThisScreen && companionManager.voiceState == .listening
    }

    private var isThinkingHere: Bool {
        buddyIsVisibleOnThisScreen && companionManager.voiceState == .processing
    }

    private var isSketchingHere: Bool {
        isThinkingHere && companionManager.isPreparingDrawing
    }

    /// The pointer bobs beside a mouse that has just come to rest, and all the while Clicky
    /// speaks. Never in flight, never while it rests out of sight, never under Reduce Motion.
    private var bobIsOn: Bool {
        guard !reduceMotion, triangleIsShown, cursorOpacity > 0, buddyNavigationMode == .followingCursor else { return false }
        return companionManager.voiceState == .responding || isMouseSettled
    }

    /// While the pointer holds on what it points at, its glow breathes.
    private var holdGlowIsOn: Bool {
        !reduceMotion && buddyNavigationMode == .pointingAtTarget
    }

    /// The still form of that glow under Reduce Motion: a little wider, not moving.
    private var holdGlowExtra: CGFloat {
        reduceMotion && buddyNavigationMode == .pointingAtTarget ? 4 : 0
    }

    /// The presence setting only governs plain following: a pointer that is flying, pointing at
    /// something, or in a conversation is always shown.
    private var presenceAllowsPointer: Bool {
        buddyNavigationMode != .followingCursor
            || companionManager.voiceState != .idle
            || companionManager.isPointerAwake
    }

    /// Whether the buddy triangle should be visible on this screen.
    /// True when cursor is on this screen during normal following, or
    /// when navigating/pointing at a target on this screen. When another
    /// screen is navigating (detectedElementScreenLocation is set but this
    /// screen isn't the one animating), hide the cursor so only one buddy
    /// is ever visible at a time.
    private var buddyIsVisibleOnThisScreen: Bool {
        switch buddyNavigationMode {
        case .followingCursor:
            // If another screen's BlueCursorView is navigating to an element,
            // hide the cursor on this screen to prevent a duplicate buddy
            if companionManager.detectedElementScreenLocation != nil {
                return false
            }
            // Likewise while another screen's overlay is flying the buddy back to a mouse that
            // moved screens.
            if let flightLeg = companionManager.cursorFlightLeg, flightLeg.screenFrame != screenFrame {
                return false
            }
            return isCursorOnThisScreen
        case .navigatingToTarget, .pointingAtTarget:
            return true
        }
    }

    // MARK: - Cursor Tracking

    private func startTrackingCursor() {
        timer = Timer.scheduledTimer(withTimeInterval: 0.016, repeats: true) { _ in
            let mouseLocation = NSEvent.mouseLocation
            self.isCursorOnThisScreen = self.screenFrame.contains(mouseLocation)

            let settled = self.mouseRestClock.note(mouseLocation, at: ProcessInfo.processInfo.systemUptime)
            if settled != self.isMouseSettled { self.isMouseSettled = settled }

            // During forward flight or pointing, the buddy is NOT interrupted by
            // mouse movement — it completes its full animation and return flight.
            // Only during the RETURN flight do we allow cursor movement to cancel
            // (so the buddy snaps to following if the user moves while it's flying back).
            if self.buddyNavigationMode == .navigatingToTarget && self.isReturningToCursor {
                let currentMouseInSwiftUI = self.convertScreenPointToSwiftUICoordinates(mouseLocation)
                let distanceFromNavigationStart = hypot(
                    currentMouseInSwiftUI.x - self.cursorPositionWhenNavigationStarted.x,
                    currentMouseInSwiftUI.y - self.cursorPositionWhenNavigationStarted.y
                )
                if distanceFromNavigationStart > 100 {
                    cancelNavigationAndResumeFollowing()
                }
                return
            }

            // During forward navigation or pointing, just skip cursor tracking
            if self.buddyNavigationMode != .followingCursor {
                return
            }

            // Normal cursor following
            let swiftUIPosition = self.convertScreenPointToSwiftUICoordinates(mouseLocation)
            let buddyX = swiftUIPosition.x + 35
            let buddyY = swiftUIPosition.y + 25
            self.cursorPosition = CGPoint(x: buddyX, y: buddyY)
        }
    }

    /// Converts a macOS screen point (AppKit, bottom-left origin) to SwiftUI
    /// coordinates (top-left origin) relative to this screen's overlay window.
    private func convertScreenPointToSwiftUICoordinates(_ screenPoint: CGPoint) -> CGPoint {
        let x = screenPoint.x - screenFrame.origin.x
        let y = (screenFrame.origin.y + screenFrame.height) - screenPoint.y
        return CGPoint(x: x, y: y)
    }

    // MARK: - Element Navigation

    /// Starts animating the buddy toward a detected UI element location.
    private func startNavigatingToElement(screenLocation: CGPoint) {
        // Don't interrupt welcome animation
        guard !showWelcome || welcomeText.isEmpty else { return }

        // Convert the AppKit screen location to SwiftUI coordinates for this screen
        let targetInSwiftUI = convertScreenPointToSwiftUICoordinates(screenLocation)

        // Offset the target so the buddy sits beside the element rather than
        // directly on top of it — 8px to the right, 12px below.
        let offsetTarget = CGPoint(
            x: targetInSwiftUI.x + 8,
            y: targetInSwiftUI.y + 12
        )

        // Clamp target to screen bounds with padding
        let clampedTarget = CGPoint(
            x: max(20, min(offsetTarget.x, screenFrame.width - 20)),
            y: max(20, min(offsetTarget.y, screenFrame.height - 20))
        )

        // Record the current cursor position so we can detect if the user
        // moves the mouse enough to cancel the return flight
        let mouseLocation = NSEvent.mouseLocation
        cursorPositionWhenNavigationStarted = convertScreenPointToSwiftUICoordinates(mouseLocation)

        // Retarget while already pointing (the next step of a walkthrough): drop the bubble and
        // invalidate the pending fly-back, then fly on from where the buddy is now.
        if buddyNavigationMode == .pointingAtTarget {
            pointingGeneration += 1
            navigationBubbleText = ""
            navigationBubbleOpacity = 0.0
            navigationBubbleScale = 1.0
        }

        // Enter navigation mode — stop cursor following
        buddyNavigationMode = .navigatingToTarget
        isReturningToCursor = false

        animateBezierFlightArc(to: clampedTarget) {
            guard self.buddyNavigationMode == .navigatingToTarget else { return }
            self.startPointingAtElement()
        }
    }

    /// Animates the buddy along its arc (`CursorFlightPath`) from where it is to `destination`:
    /// it turns to face where it is going, swells in the middle, leaves a streak on a long
    /// flight and lands slightly small, filling out as the flight ends. Under Reduce Motion it
    /// glides straight there at its normal size and lean. Progress is read from the clock, so a
    /// busy main thread drops frames rather than slowing the flight.
    private func animateBezierFlightArc(
        to destination: CGPoint,
        onComplete: @escaping () -> Void
    ) {
        navigationAnimationTimer?.invalidate()

        let calm = reduceMotion
        let path = CursorFlightPath(from: cursorPosition, to: destination, straight: calm)
        let leavesStreak = !calm && CursorTravelStreak.isShown(forDistance: path.distance)
        let startedAt = CACurrentMediaTime()

        navigationAnimationTimer = Timer.scheduledTimer(withTimeInterval: 1.0 / 60.0, repeats: true) { _ in
            let progress = path.progress(afterSeconds: CACurrentMediaTime() - startedAt)

            if progress >= 1 {
                self.navigationAnimationTimer?.invalidate()
                self.navigationAnimationTimer = nil
                self.flightStreak = nil
                self.cursorPosition = path.end
                // From the landing size back to full; animated because the mode changes below.
                self.buddyFlightScale = 1.0
                onComplete()
                return
            }

            let pose = path.pose(atProgress: progress, calm: calm)
            self.cursorPosition = pose.position
            self.triangleRotationDegrees = pose.rotationDegrees
            self.buddyFlightScale = pose.scale
            self.flightStreak = leavesStreak ? FlightStreak(path: path, progress: progress) : nil
        }
    }

    /// Transitions to pointing mode — shows a speech bubble with a bouncy
    /// scale-in entrance and variable-speed character streaming.
    private func startPointingAtElement() {
        buddyNavigationMode = .pointingAtTarget
        pointingGeneration += 1
        let generation = pointingGeneration

        // Rotate back to default pointer angle now that we've arrived
        triangleRotationDegrees = CursorFlightPath.restingRotationDegrees
        arrivalToken &+= 1

        // Reset navigation bubble state — start small for the scale-bounce entrance
        navigationBubbleText = ""
        navigationBubbleOpacity = 1.0
        navigationBubbleSize = .zero
        navigationBubbleScale = 0.5

        // Use custom bubble text from the companion manager (e.g. onboarding demo)
        // if available, otherwise fall back to a random pointer phrase
        let pointerPhrase = companionManager.detectedElementBubbleText
            ?? navigationPointerPhrases.randomElement()
            ?? "right here!"

        streamNavigationBubbleCharacter(phrase: pointerPhrase, characterIndex: 0) {
            // All characters streamed — hold for 3 seconds, then fly back (unless retargeted since)
            DispatchQueue.main.asyncAfter(deadline: .now() + 3.0) {
                guard self.buddyNavigationMode == .pointingAtTarget, self.pointingGeneration == generation else { return }
                // The Settings tour keeps the pointer on each row while its line is said.
                guard !self.companionManager.pointerStaysOnTarget else { return }
                self.navigationBubbleOpacity = 0.0
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                    guard self.buddyNavigationMode == .pointingAtTarget, self.pointingGeneration == generation else { return }
                    self.startFlyingBackToCursor()
                }
            }
        }
    }

    /// Streams the navigation bubble text one character at a time with variable
    /// delays (30–60ms) for a natural "speaking" rhythm.
    private func streamNavigationBubbleCharacter(
        phrase: String,
        characterIndex: Int,
        onComplete: @escaping () -> Void
    ) {
        guard buddyNavigationMode == .pointingAtTarget else { return }
        guard characterIndex < phrase.count else {
            onComplete()
            return
        }

        let charIndex = phrase.index(phrase.startIndex, offsetBy: characterIndex)
        navigationBubbleText.append(phrase[charIndex])

        // On the first character, trigger the scale-bounce entrance
        if characterIndex == 0 {
            navigationBubbleScale = 1.0
        }

        let characterDelay = Double.random(in: 0.03...0.06)
        DispatchQueue.main.asyncAfter(deadline: .now() + characterDelay) {
            self.streamNavigationBubbleCharacter(
                phrase: phrase,
                characterIndex: characterIndex + 1,
                onComplete: onComplete
            )
        }
    }

    /// Flies the buddy back to the current cursor position after pointing is done.
    private func startFlyingBackToCursor() {
        let mouseLocation = NSEvent.mouseLocation
        if !screenFrame.contains(mouseLocation) {
            // The mouse is on another display: fly to this display's edge, then across that one.
            navigationBubbleText = ""
            navigationBubbleOpacity = 0.0
            companionManager.beginCursorFlight(.toMouse, fromScreenFrame: screenFrame)
            return
        }
        let cursorInSwiftUI = convertScreenPointToSwiftUICoordinates(mouseLocation)
        let cursorWithTrackingOffset = CGPoint(x: cursorInSwiftUI.x + 35, y: cursorInSwiftUI.y + 25)

        cursorPositionWhenNavigationStarted = cursorInSwiftUI

        buddyNavigationMode = .navigatingToTarget
        isReturningToCursor = true

        animateBezierFlightArc(to: cursorWithTrackingOffset) {
            self.finishNavigationAndResumeFollowing()
        }
    }

    // MARK: - Flights between displays (OpenClicky)

    /// Animates one leg of a buddy flight on this display (see `CursorFlightPlanner`): back to a
    /// mouse that is on another display. A non-final leg ends at this display's edge and hands
    /// over to the destination display's overlay.
    private func performFlightLeg(_ flightLeg: CursorFlightLeg) {
        guard !showWelcome || welcomeText.isEmpty else { return }
        navigationAnimationTimer?.invalidate()
        pointingGeneration += 1
        navigationBubbleText = ""
        navigationBubbleOpacity = 0.0
        navigationBubbleScale = 1.0

        if let startScreenLocation = flightLeg.startScreenLocation {
            // Continuing a flight from another display: appear at the edge it left from.
            cursorPosition = convertScreenPointToSwiftUICoordinates(startScreenLocation)
        }
        cursorOpacity = 1.0

        var destination = convertScreenPointToSwiftUICoordinates(flightLeg.endScreenLocation)
        if flightLeg.isFinalLeg {
            // Land where cursor following would put the buddy; a big mouse move cancels it.
            destination = CGPoint(x: destination.x + 35, y: destination.y + 25)
            cursorPositionWhenNavigationStarted = convertScreenPointToSwiftUICoordinates(NSEvent.mouseLocation)
        }
        buddyNavigationMode = .navigatingToTarget
        isReturningToCursor = flightLeg.isFinalLeg

        animateBezierFlightArc(to: destination) {
            guard self.companionManager.cursorFlightLeg == flightLeg else { return }
            if !flightLeg.isFinalLeg {
                // Slip off this display's edge; the destination display's overlay continues.
                self.buddyNavigationMode = .followingCursor
                self.isReturningToCursor = false
                self.triangleRotationDegrees = CursorFlightPath.restingRotationDegrees
                self.companionManager.continueCursorFlight(afterLeg: flightLeg)
                return
            }
            self.finishNavigationAndResumeFollowing()
        }
    }

    /// Cancels an in-progress navigation because the user moved the cursor.
    private func cancelNavigationAndResumeFollowing() {
        navigationAnimationTimer?.invalidate()
        navigationAnimationTimer = nil
        navigationBubbleText = ""
        navigationBubbleOpacity = 0.0
        navigationBubbleScale = 1.0
        buddyFlightScale = 1.0
        finishNavigationAndResumeFollowing()
    }

    /// Returns the buddy to normal cursor-following mode after navigation completes.
    private func finishNavigationAndResumeFollowing() {
        navigationAnimationTimer?.invalidate()
        navigationAnimationTimer = nil
        flightStreak = nil
        buddyNavigationMode = .followingCursor
        isReturningToCursor = false
        triangleRotationDegrees = CursorFlightPath.restingRotationDegrees
        buddyFlightScale = 1.0
        // Back beside the mouse: it may bob for a moment, as after any pause.
        mouseRestClock.restart(at: ProcessInfo.processInfo.systemUptime)
        navigationBubbleText = ""
        navigationBubbleOpacity = 0.0
        navigationBubbleScale = 1.0
        companionManager.clearDetectedElementLocation()
        companionManager.finishCursorFlight(onScreenFrame: screenFrame)
        // Back at the mouse: stay a moment so the landing is seen, then rest away as usual.
        companionManager.wakePointer()
    }

    // MARK: - Welcome Animation

    private func startWelcomeAnimation() {
        withAnimation(Motion.captionFade) {
            self.bubbleOpacity = 1.0
        }

        var currentIndex = 0
        Timer.scheduledTimer(withTimeInterval: 0.03, repeats: true) { timer in
            guard currentIndex < self.fullWelcomeMessage.count else {
                timer.invalidate()
                // Hold the text for 2 seconds, then fade it out
                DispatchQueue.main.asyncAfter(deadline: .now() + 2.0) {
                    self.bubbleOpacity = 0.0
                }
                DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) {
                    self.showWelcome = false
                    // Start the onboarding video right after the welcome text disappears
                    self.companionManager.setupOnboardingVideo()
                }
                return
            }

            let index = self.fullWelcomeMessage.index(self.fullWelcomeMessage.startIndex, offsetBy: currentIndex)
            self.welcomeText.append(self.fullWelcomeMessage[index])
            currentIndex += 1
        }
    }
}

// MARK: - Loops

/// Runs a value between 0 and 1 and back for as long as `isRunning` is true, and eases it back
/// to 0 when it is not. The one place the pointer's loops start and stop, so none is left running
/// out of sight: pass `false` under Reduce Motion and draw the still form from phase 0.
struct LoopingPhase<Content: View>: View {
    let isRunning: Bool
    /// Seconds for one direction.
    let beat: Double
    @ViewBuilder let content: (CGFloat) -> Content

    @State private var phase: CGFloat = 0
    /// Counts starts and stops, so a start that was put off a turn is dropped if a stop came first.
    @State private var change = 0

    var body: some View {
        content(phase)
            .onAppear { if isRunning { startSoon() } }
            .onChange(of: isRunning) { _, running in
                if running { startSoon() } else { stop() }
            }
    }

    /// A turn later, in a transaction of its own: started together with whatever switched it on
    /// (a landing, a first layout), the loop would take that change with it and repeat it too.
    private func startSoon() {
        change += 1
        let expected = change
        DispatchQueue.main.async {
            guard change == expected else { return }
            withAnimation(Motion.loop(beat: beat)) { phase = 1 }
        }
    }

    private func stop() {
        change += 1
        withAnimation(Motion.swap) { phase = 0 }
    }
}

// MARK: - Travel streak

/// The stretch of arc just behind a flying pointer, drawn as short pieces that thin and fade
/// towards the old end.
struct TravelStreakView: View {
    let points: [CGPoint]
    let opacity: Double

    var body: some View {
        ZStack {
            ForEach(0..<max(points.count - 1, 0), id: \.self) { index in
                let share = Double(index + 1) / Double(max(points.count - 1, 1))
                Path { path in
                    path.move(to: points[index])
                    path.addLine(to: points[index + 1])
                }
                .stroke(
                    DS.Colors.overlayCursorColor.opacity(opacity * share),
                    style: StrokeStyle(lineWidth: 1.5 + 3.5 * share, lineCap: .round)
                )
            }
        }
        // No shadow here: this view is as large as the display, and a blur that size on every
        // frame of a flight costs far more than the streak is worth.
    }
}

// MARK: - Arrival pulse

/// A thin ring that opens and fades once each time `token` changes. Under Reduce Motion the ring
/// only fades.
struct ArrivalPulse: View {
    let token: Int
    let reduceMotion: Bool

    @State private var scale: CGFloat = 0.5
    @State private var opacity: Double = 0

    var body: some View {
        Circle()
            .stroke(DS.Colors.overlayCursorColor, lineWidth: 1.5)
            .frame(width: 26, height: 26)
            .scaleEffect(scale)
            .opacity(opacity)
            .onChange(of: token) { _, _ in
                // The starting look first, the animation a turn later: set together they cancel out.
                scale = reduceMotion ? 1 : 0.5
                opacity = 0.75
                DispatchQueue.main.async {
                    withAnimation(Motion.pointerArrive(reduceMotion: reduceMotion)) { scale = reduceMotion ? 1 : 1.6 }
                    withAnimation(Motion.captionFade) { opacity = 0 }
                }
            }
    }
}

// MARK: - Blue Cursor Waveform

/// A small blue waveform that replaces the triangle cursor while
/// the user is holding the push-to-talk shortcut and speaking. `isActive` false stops its clock
/// (it stays in the view tree while hidden); under Reduce Motion the bars follow the voice but do
/// not sway by themselves.
struct BlueCursorWaveformView: View {
    let audioPowerLevel: CGFloat
    var isActive = true
    var reduceMotion = false

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 36.0, paused: !isActive || reduceMotion)) { timelineContext in
            HStack(alignment: .center, spacing: 2) {
                ForEach(0..<CursorWaveform.barProfile.count, id: \.self) { barIndex in
                    RoundedRectangle(cornerRadius: 1.5, style: .continuous)
                        .fill(DS.Colors.overlayCursorColor)
                        .frame(
                            width: 2,
                            height: CursorWaveform.barHeight(
                                bar: barIndex,
                                level: audioPowerLevel,
                                time: timelineContext.date.timeIntervalSinceReferenceDate,
                                sways: !reduceMotion
                            )
                        )
                }
            }
            .shadow(color: DS.Colors.overlayCursorColor.opacity(0.6), radius: 6, x: 0, y: 0)
            .animation(Motion.levelEase, value: audioPowerLevel)
        }
    }
}

// MARK: - Thinking dots

/// Three dots beside the mouse while an answer is on its way, lighting one after another. When
/// the thinking ends they draw together as they fade, into where the pointer comes back. Under
/// Reduce Motion they are three still dots.
struct CursorThinkingDotsView: View {
    let isActive: Bool
    let reduceMotion: Bool

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30.0, paused: !isActive || reduceMotion)) { timelineContext in
            let time = timelineContext.date.timeIntervalSinceReferenceDate
            HStack(spacing: isActive ? 3 : 0) {
                ForEach(0..<CursorThinkingDots.count, id: \.self) { dot in
                    let brightness = reduceMotion ? 1 : CursorThinkingDots.brightness(dot: dot, time: time)
                    Circle()
                        .fill(DS.Colors.overlayCursorColor)
                        .frame(width: 4.5, height: 4.5)
                        .scaleEffect(0.82 + 0.18 * brightness)
                        .opacity(reduceMotion ? 1 : CursorThinkingDots.opacity(dot: dot, time: time))
                }
            }
            .shadow(color: DS.Colors.overlayCursorColor.opacity(0.6), radius: 6, x: 0, y: 0)
            .animation(Motion.swap, value: isActive)
        }
    }
}

// MARK: - Sketching pencil

/// A pencil drawing a short squiggle beside the mouse while a drawing is on its way, over and
/// over. It stops while it is not shown (a hidden animation still costs power). Under Reduce
/// Motion it is a still pencil at the end of its squiggle.
struct CursorSketchingView: View {
    let isActive: Bool
    let reduceMotion: Bool

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30.0, paused: !isActive || reduceMotion)) { timelineContext in
            let time = timelineContext.date.timeIntervalSinceReferenceDate
            let progress = reduceMotion ? 1 : CursorSketching.progress(time: time)
            let tip = CursorSketching.point(at: progress)
            ZStack(alignment: .topLeading) {
                Path { path in
                    let steps = 36
                    path.move(to: CursorSketching.point(at: 0))
                    for step in 1...steps { path.addLine(to: CursorSketching.point(at: Double(step) / Double(steps))) }
                }
                .trim(from: 0, to: progress)
                .stroke(DS.Colors.overlayCursorColor, style: StrokeStyle(lineWidth: 2.2, lineCap: .round, lineJoin: .round))
                .opacity(reduceMotion ? 1 : CursorSketching.inkOpacity(time: time))
                // The pencil symbol's point is at its lower left: that corner rides the stroke's end.
                Image(systemName: "pencil")
                    .font(.system(size: 17, weight: .semibold))
                    .foregroundStyle(DS.Colors.overlayCursorColor)
                    .frame(width: 18, height: 18)
                    .offset(x: tip.x, y: tip.y - 18)
            }
            .frame(width: CursorSketching.size.width, height: CursorSketching.size.height, alignment: .topLeading)
            .shadow(color: DS.Colors.overlayCursorColor.opacity(0.6), radius: 6, x: 0, y: 0)
        }
        .accessibilityHidden(true)
    }
}

// MARK: - Blue Cursor Spinner

/// A small blue spinning indicator, used by the island's badge while the AI is processing a
/// voice input. (Beside the mouse this state is `CursorThinkingDotsView`.)
struct BlueCursorSpinnerView: View {
    @State private var isSpinning = false

    var body: some View {
        Circle()
            .trim(from: 0.15, to: 0.85)
            .stroke(
                AngularGradient(
                    colors: [
                        DS.Colors.overlayCursorColor.opacity(0.0),
                        DS.Colors.overlayCursorColor
                    ],
                    center: .center
                ),
                style: StrokeStyle(lineWidth: 2.5, lineCap: .round)
            )
            .frame(width: 14, height: 14)
            .rotationEffect(.degrees(isSpinning ? 360 : 0))
            .shadow(color: DS.Colors.overlayCursorColor.opacity(0.6), radius: 6, x: 0, y: 0)
            .onAppear {
                withAnimation(Motion.spin) {
                    isSpinning = true
                }
            }
    }
}

// Manager for overlay windows — creates one per screen so the cursor
// buddy seamlessly follows the cursor across multiple monitors.
@MainActor
class OverlayWindowManager {
    private var overlayWindows: [OverlayWindow] = []
    var hasShownOverlayBefore = false
    /// The screen frames the overlays were built for, so a display change can be told apart from
    /// the many notifications that change nothing.
    private var shownScreenFrames: [CGRect] = []
    private weak var companionManager: CompanionManager?
    private var screenChangeObserver: NSObjectProtocol?

    init() {
        // A display plugged in, unplugged, or moved in the arrangement: the overlays were sized
        // and placed for the old layout, so the buddy would be missing or misplaced on the new
        // one. Rebuild them for the screens as they are now.
        screenChangeObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.rebuildIfScreensChanged() }
        }
    }

    /// Whether the overlays built for `from` fit the screens `to`; order does not matter.
    nonisolated static func screensChanged(from: [CGRect], to: [CGRect]) -> Bool {
        func sorted(_ frames: [CGRect]) -> [CGRect] {
            frames.sorted { ($0.minX, $0.minY, $0.width, $0.height) < ($1.minX, $1.minY, $1.width, $1.height) }
        }
        return sorted(from) != sorted(to)
    }

    private func rebuildIfScreensChanged() {
        guard !overlayWindows.isEmpty, let companionManager else { return }
        let frames = NSScreen.screens.map(\.frame)
        guard Self.screensChanged(from: shownScreenFrames, to: frames) else { return }
        AppLog.append("displays changed; rebuilding the pointer overlay for \(frames.count) screen(s)")
        hasShownOverlayBefore = true
        showOverlay(onScreens: NSScreen.screens, companionManager: companionManager)
    }

    func showOverlay(onScreens screens: [NSScreen], companionManager: CompanionManager) {
        // Hide any existing overlays
        hideOverlay()
        self.companionManager = companionManager
        shownScreenFrames = screens.map(\.frame)

        // Track if this is the first time showing overlay (welcome message)
        let isFirstAppearance = !hasShownOverlayBefore
        hasShownOverlayBefore = true

        // Create one overlay window per screen
        for screen in screens {
            let window = OverlayWindow(screen: screen)

            let contentView = BlueCursorView(
                screenFrame: screen.frame,
                isFirstAppearance: isFirstAppearance,
                companionManager: companionManager
            )

            let hostingView = NSHostingView(rootView: contentView)
            hostingView.frame = screen.frame
            window.contentView = hostingView

            overlayWindows.append(window)
            window.orderFrontRegardless()
        }
    }

    func hideOverlay() {
        for window in overlayWindows {
            window.orderOut(nil)
            window.contentView = nil
        }
        overlayWindows.removeAll()
    }

    /// Fades out overlay windows over `duration` seconds, then removes them.
    func fadeOutAndHideOverlay(duration: TimeInterval = 0.4) {
        let windowsToFade = overlayWindows
        overlayWindows.removeAll()

        NSAnimationContext.runAnimationGroup({ context in
            context.duration = duration
            context.timingFunction = CAMediaTimingFunction(name: .easeIn)
            for window in windowsToFade {
                window.animator().alphaValue = 0
            }
        }, completionHandler: {
            for window in windowsToFade {
                window.orderOut(nil)
                window.contentView = nil
            }
        })
    }
}
