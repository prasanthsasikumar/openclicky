//
//  HandheldWindowSwitcherTests.swift
//  OpenClickyTests
//
//  The handheld's wheel: its button opens a stack of the open windows, the wheel moves through
//  it, and the button again brings the one in front forward.
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct HandheldWindowSwitcherTests {
    private func bytes(_ hex: String) -> [UInt8] { hex.split(separator: " ").map { UInt8($0, radix: 16)! } }

    private func window(_ number: Int, app: String, title: String, pid: pid_t = 100, layer: Int = 0,
                        width: CGFloat = 800, height: CGFloat = 600) -> SwitcherWindowList.RawWindow {
        SwitcherWindowList.RawWindow(windowNumber: number, ownerPID: pid, ownerName: app, title: title,
                                     bounds: CGRect(x: 0, y: 0, width: width, height: height), layer: layer)
    }

    // MARK: the device

    @Test func theWheelsButtonIsAControlOfItsOwn() {
        let whisprompt = WhisPromptHandheld.device
        // Recorded off the device: key code 03, a single press.
        #expect(whisprompt.decode(4, bytes("04 02 03 01 83 00 00 00 3f 1d a3 00 00"))
                == HandheldEvent(controlID: "wheelButton", gesture: .single, sequence: 0x83))
        #expect(whisprompt.control(withID: "wheelButton")?.name == "Wheel button")
        #expect(whisprompt.defaultBindings.action(controlID: "wheelButton", gesture: .single) == .switchWindows)
        #expect(HandheldAction.switchWindows.title == "Switch windows")
    }

    @Test func theButtonTogglesTheSwitcherThroughTheHost() {
        let host = RecordingHandheldActionHost()
        let runner = HandheldActionRunner(host: host, schedule: ManualHandheldScheduler().schedule)
        runner.handle(HandheldEvent(controlID: "wheelButton", gesture: .single, sequence: 1), bindings: WhisPromptHandheld.device.defaultBindings)
        #expect(host.windowSwitcherToggleCount == 1)
        #expect(host.shortcutEvents.isEmpty)
    }

    // MARK: which windows

    @Test func onlyOrdinaryWindowsOfOtherAppsAreListedFrontFirst() {
        let listed = SwitcherWindowList.windows(from: [
            window(1, app: "Safari", title: "OpenClicky"),
            window(2, app: "Dock", title: "", layer: 20),
            window(3, app: "OpenClicky", title: "Settings", pid: 7),
            window(4, app: "Terminal", title: "zsh", pid: 200),
            window(5, app: "Safari", title: "", width: 30, height: 30),
        ], ownProcessID: 7)
        #expect(listed.map(\.windowNumber) == [1, 4])
        #expect(listed.first?.title == "OpenClicky")
        #expect(listed.first?.appName == "Safari")
    }

    @Test func aWindowWithNoTitleIsListedUnderItsAppsName() {
        let listed = SwitcherWindowList.windows(from: [window(1, app: "Finder", title: "")], ownProcessID: 7)
        #expect(listed.first?.title == "Finder")
    }

    // MARK: moving through them

    private func makeModel(count: Int) -> WindowSwitcherModel {
        let model = WindowSwitcherModel()
        model.open(with: (0..<count).map { SwitcherWindow(windowNumber: $0, ownerPID: pid_t(100 + $0), appName: "App \($0)", title: "Window \($0)") })
        return model
    }

    @Test func itOpensOnTheWindowBehindTheFrontOne() {
        // The front window is where the person already is; one turn less to reach the last one used.
        #expect(makeModel(count: 4).selectedIndex == 1)
        #expect(makeModel(count: 1).selectedIndex == 0)
    }

    @Test func turningMovesAndWrapsBothWays() {
        let model = makeModel(count: 3)
        model.move(by: 1)
        #expect(model.selectedIndex == 2)
        model.move(by: 1)
        #expect(model.selectedIndex == 0)
        model.move(by: -1)
        #expect(model.selectedIndex == 2)
    }

    @Test func aScrollIsOneStepInItsDirectionHoweverLarge() {
        #expect(WindowSwitcherModel.steps(forScrollDeltaY: -5) == 1)
        #expect(WindowSwitcherModel.steps(forScrollDeltaY: 5) == -1)
        #expect(WindowSwitcherModel.steps(forScrollDeltaY: -0.4) == 1)
        #expect(WindowSwitcherModel.steps(forScrollDeltaY: 0) == 0)
    }

    @Test func confirmingGivesTheSelectedWindowAndCloses() {
        let model = makeModel(count: 3)
        model.move(by: 1)
        #expect(model.confirm()?.windowNumber == 2)
        #expect(!model.isOpen)
        #expect(model.confirm() == nil)
    }

    @Test func dismissingClosesWithoutChoosing() {
        let model = makeModel(count: 3)
        model.dismiss()
        #expect(!model.isOpen)
        #expect(model.confirm() == nil)
    }

    @Test func withNoWindowsItDoesNotOpen() {
        let model = WindowSwitcherModel()
        model.open(with: [])
        #expect(!model.isOpen)
    }

    @Test func cardsAreLaidOutAroundTheSelectedOne() {
        // The selected card sits in the middle at full size; neighbours step away and shrink,
        // and cards past the visible range are not drawn.
        #expect(WindowSwitcherLayout.offset(of: 2, selectedIndex: 2, count: 5) == 0)
        #expect(WindowSwitcherLayout.offset(of: 3, selectedIndex: 2, count: 5) == 1)
        // The stack is a loop: the last card sits just above the first.
        #expect(WindowSwitcherLayout.offset(of: 4, selectedIndex: 0, count: 5) == -1)
        #expect(WindowSwitcherLayout.scale(forOffset: 0) == 1)
        #expect(WindowSwitcherLayout.scale(forOffset: 1) < 1)
        #expect(WindowSwitcherLayout.opacity(forOffset: 0) == 1)
        #expect(WindowSwitcherLayout.opacity(forOffset: 4) == 0)
    }
}
