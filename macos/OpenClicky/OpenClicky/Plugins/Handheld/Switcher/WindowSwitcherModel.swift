//
//  WindowSwitcherModel.swift
//  OpenClicky
//
//  The handheld's window switcher, as plain data: which windows it lists, which one is in front
//  of the stack, and where each card sits around it. Press the wheel's button and the open
//  windows come up as a stack of cards; turn the wheel and the stack moves past; press again and
//  the card in front is the window you go to.
//

import AppKit
import Combine
import Foundation

struct SwitcherWindow: Equatable, Identifiable {
    let windowNumber: Int
    let ownerPID: pid_t
    let appName: String
    let title: String
    var id: Int { windowNumber }
}

enum SwitcherWindowList {
    /// One window as the window server lists it.
    struct RawWindow {
        let windowNumber: Int
        let ownerPID: pid_t
        let ownerName: String
        let title: String
        let bounds: CGRect
        let layer: Int
    }

    /// Smaller than this a side and it is a tool tip, a badge or a helper, not a window to go to.
    private static let smallestWindowSide: CGFloat = 80

    /// The windows worth switching to, front first: ordinary windows (layer 0) of other apps, big
    /// enough to be real ones. A window with no title goes by its app's name.
    static func windows(from rawWindows: [RawWindow], ownProcessID: pid_t) -> [SwitcherWindow] {
        rawWindows.compactMap { rawWindow in
            guard rawWindow.layer == 0, rawWindow.ownerPID != ownProcessID,
                  rawWindow.bounds.width >= smallestWindowSide, rawWindow.bounds.height >= smallestWindowSide else { return nil }
            return SwitcherWindow(windowNumber: rawWindow.windowNumber, ownerPID: rawWindow.ownerPID, appName: rawWindow.ownerName,
                                  title: rawWindow.title.isEmpty ? rawWindow.ownerName : rawWindow.title)
        }
    }

    /// The windows on screen now, front to back. Titles need Screen Recording, which OpenClicky
    /// has for its questions; without it every window goes by its app's name.
    static func current() -> [SwitcherWindow] {
        let options: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
        guard let windowInfoList = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else { return [] }
        let rawWindows: [RawWindow] = windowInfoList.compactMap { windowInfo in
            guard let windowNumber = windowInfo[kCGWindowNumber as String] as? Int,
                  let ownerPID = windowInfo[kCGWindowOwnerPID as String] as? pid_t,
                  let layer = windowInfo[kCGWindowLayer as String] as? Int,
                  let boundsDictionary = windowInfo[kCGWindowBounds as String] as? NSDictionary,
                  let bounds = CGRect(dictionaryRepresentation: boundsDictionary) else { return nil }
            return RawWindow(windowNumber: windowNumber, ownerPID: ownerPID,
                             ownerName: windowInfo[kCGWindowOwnerName as String] as? String ?? "",
                             title: windowInfo[kCGWindowName as String] as? String ?? "",
                             bounds: bounds, layer: layer)
        }
        return windows(from: rawWindows, ownProcessID: ProcessInfo.processInfo.processIdentifier)
    }
}

@MainActor
final class WindowSwitcherModel: ObservableObject {
    @Published private(set) var windows: [SwitcherWindow] = []
    @Published private(set) var selectedIndex = 0
    @Published private(set) var isOpen = false

    /// Opens on the window behind the front one: the front one is where the person already is,
    /// so a press, press goes back to the window they used last.
    func open(with windows: [SwitcherWindow]) {
        guard !windows.isEmpty else { return }
        self.windows = windows
        selectedIndex = windows.count > 1 ? 1 : 0
        isOpen = true
    }

    /// The stack is a loop: past the last card comes the first.
    func move(by steps: Int) {
        guard isOpen, !windows.isEmpty, steps != 0 else { return }
        let count = windows.count
        selectedIndex = ((selectedIndex + steps) % count + count) % count
    }

    func select(windowNumber: Int) {
        guard let index = windows.firstIndex(where: { $0.windowNumber == windowNumber }) else { return }
        selectedIndex = index
    }

    /// The window in front of the stack; the switcher closes.
    func confirm() -> SwitcherWindow? {
        guard isOpen, windows.indices.contains(selectedIndex) else { return nil }
        let chosenWindow = windows[selectedIndex]
        isOpen = false
        return chosenWindow
    }

    func dismiss() {
        isOpen = false
    }

    /// One click of the wheel is one card, whatever amount the click scrolls by: down is the next
    /// card, up the one before.
    static func steps(forScrollDeltaY scrollDeltaY: CGFloat) -> Int {
        if scrollDeltaY < 0 { return 1 }
        if scrollDeltaY > 0 { return -1 }
        return 0
    }
}

/// Where a card sits around the one in front, as pure numbers.
enum WindowSwitcherLayout {
    static let cardWidth: CGFloat = 420
    static let cardHeight: CGFloat = 64
    static let cardSpacing: CGFloat = 76
    /// Cards this far from the front one, and farther, are not seen.
    static let farthestVisibleOffset = 4

    /// How many places a card is from the front one, the short way round the loop (-1 is just above).
    static func offset(of index: Int, selectedIndex: Int, count: Int) -> Int {
        guard count > 0 else { return 0 }
        var offset = (index - selectedIndex) % count
        if offset > count / 2 { offset -= count }
        if offset < -(count - 1) / 2 { offset += count }
        return offset
    }

    static func scale(forOffset offset: Int) -> CGFloat {
        max(0.72, 1 - 0.07 * CGFloat(abs(offset)))
    }

    static func opacity(forOffset offset: Int) -> Double {
        max(0, 1 - Double(abs(offset)) / Double(farthestVisibleOffset))
    }
}
