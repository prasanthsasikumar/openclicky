//
//  WindowControlLocatorTests.swift
//  OpenClickyTests
//
//  Which requests are about a window's close / minimize / zoom button. The Accessibility read of
//  the button's frame is not tested here.
//

import Testing
@testable import OpenClicky

struct WindowControlLocatorTests {
    @Test func theModelsLabelNamesTheButton() {
        // The request that went wrong on 2026-10-27: the model asked for the minimize button with no
        // text (it has none) and Claude put it in the menu bar.
        #expect(WindowControlLocator.requestedControl(label: "minimize button", text: "", userRequest: "I am talking about the terminal.") == .minimize)
        #expect(WindowControlLocator.requestedControl(label: "close button", text: "", userRequest: nil) == .close)
        #expect(WindowControlLocator.requestedControl(label: "zoom button", text: "", userRequest: nil) == .zoom)
        #expect(WindowControlLocator.requestedControl(label: "full screen button", text: "", userRequest: nil) == .zoom)
    }

    @Test func theUsersWordsNameTheButtonOrTheWindow() {
        #expect(WindowControlLocator.requestedControl(label: "here", text: "", userRequest: "how do I minimize this window") == .minimize)
        #expect(WindowControlLocator.requestedControl(label: "here", text: "", userRequest: "show me the yellow button") == .minimize)
        #expect(WindowControlLocator.requestedControl(label: "here", text: "", userRequest: "where is the red dot to close it") == .close)
        #expect(WindowControlLocator.requestedControl(label: "here", text: "", userRequest: "make the terminal full screen") == .zoom)
    }

    @Test func otherRequestsAreLeftToTheRestOfTheChain() {
        // A tab's ✕ is not the window's close button.
        #expect(WindowControlLocator.requestedControl(label: "close tab", text: "", userRequest: "how do I close this tab") == nil)
        // "close" in a menu, a caption, an unrelated question.
        #expect(WindowControlLocator.requestedControl(label: "Close menu item", text: "Close", userRequest: "where is close in the file menu") == nil)
        #expect(WindowControlLocator.requestedControl(label: "export button", text: "Export", userRequest: "how do I export this") == nil)
        // A colour on its own is not a button.
        #expect(WindowControlLocator.requestedControl(label: "yellow folder", text: "Yellow", userRequest: "point at the yellow folder") == nil)
    }
}
