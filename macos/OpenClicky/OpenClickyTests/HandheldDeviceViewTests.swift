//
//  HandheldDeviceViewTests.swift
//  OpenClickyTests
//
//  The handheld's 3D picture on its settings page: where its files come from, and that with a
//  picture the page shows one button's choices at a time (the one clicked), and without one the
//  whole list as before.
//

import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct HandheldDeviceViewTests {
    private let scratchSuiteName = "HandheldDeviceViewTests-\(UUID().uuidString)"
    private let host = RecordingHandheldActionHost()

    private func makePluginWithTheDeviceConnected() -> (plugin: HandheldPlugin, source: FakeHandheldReportSource) {
        let defaults = UserDefaults(suiteName: scratchSuiteName)!
        defaults.removePersistentDomain(forName: scratchSuiteName)
        defaults.set(true, forKey: HandheldPlugin.enabledKey)
        let source = FakeHandheldReportSource()
        let plugin = HandheldPlugin(defaults: defaults, makeSource: { source }, makerAppIsRunning: { false })
        plugin.attach(to: host)
        source.connectWhisPrompt()
        return (plugin, source)
    }

    private func relevantIDs(_ items: [SettingsItem]) -> [String] {
        items.filter { $0.isRelevant() }.map(\.id)
    }

    // MARK: where the page's files come from

    @Test func thePagesFilesAreInTheAppAndTheModelIsThePersonsOwn() {
        let bundleFolder = URL(fileURLWithPath: "/App/Contents/Resources")
        let modelFile = URL(fileURLWithPath: "/Users/me/.openclicky/handheld/whisprompt.glb")
        func resolve(_ path: String) -> String? {
            HandheldViewerScheme.fileURL(forRequestPath: path, bundleResources: bundleFolder, modelFile: modelFile)?.path
        }
        #expect(resolve("/index.html") == "/App/Contents/Resources/handheld-viewer.html")
        #expect(resolve("/viewer.js") == "/App/Contents/Resources/handheld-viewer.js")
        #expect(resolve("/three/three.module.min.js") == "/App/Contents/Resources/draw-stage/vendor/three/three.module.min.js")
        #expect(resolve("/three/three.core.min.js") == "/App/Contents/Resources/draw-stage/vendor/three/three.core.min.js")
        #expect(resolve("/model.glb") == "/Users/me/.openclicky/handheld/whisprompt.glb")
    }

    @Test func nothingElseIsServed() {
        let bundleFolder = URL(fileURLWithPath: "/App/Contents/Resources")
        let modelFile = URL(fileURLWithPath: "/Users/me/.openclicky/handheld/whisprompt.glb")
        for path in ["/three/../../Info.plist", "/../shell.json", "/three/other.js", "/shell.json", "", "//index.html", "/index.html/extra"] {
            #expect(HandheldViewerScheme.fileURL(forRequestPath: path, bundleResources: bundleFolder, modelFile: modelFile) == nil, "\(path) was served")
        }
    }

    @Test func theModelIsLookedForUnderTheDevicesNameInThePersonsFolder() {
        let home = URL(fileURLWithPath: "/Users/me")
        #expect(HandheldDeviceModelFile.url(for: WhisPromptHandheld.device, homeDirectory: home).path == "/Users/me/.openclicky/handheld/whisprompt.glb")
    }

    @Test func theDeviceSaysWhichPartOfItsModelIsWhichControl() {
        let parts = WhisPromptHandheld.device.modelPartNameByControlID
        #expect(parts["keyA"] == "按键1")
        #expect(parts["keyB"] == "按键2")
        #expect(parts["keyC"] == "按键3")
        #expect(parts["wheelButton"] == "金属滑轮")
    }

    // MARK: the page with and without a picture

    @Test func withAPictureOnlyTheChosenButtonsPickersShow() {
        let (plugin, _) = makePluginWithTheDeviceConnected()
        let items = HandheldSettings.items(plugin: plugin, deviceModelIsAvailable: { _ in true })
        #expect(relevantIDs(items).contains("handheld.device"))
        // Key A is chosen to begin with.
        #expect(relevantIDs(items).filter { $0.hasPrefix("handheld.binding.") } == [
            "handheld.binding.whisprompt.keyA.single", "handheld.binding.whisprompt.keyA.double", "handheld.binding.whisprompt.keyA.longStart",
        ])
        plugin.selectControl(withID: "wheelButton")
        #expect(relevantIDs(items).filter { $0.hasPrefix("handheld.binding.") } == [
            "handheld.binding.whisprompt.wheelButton.single", "handheld.binding.whisprompt.wheelButton.double", "handheld.binding.whisprompt.wheelButton.longStart",
        ])
    }

    @Test func withoutAPictureTheWholeListShowsAsBefore() {
        let (plugin, _) = makePluginWithTheDeviceConnected()
        let items = HandheldSettings.items(plugin: plugin, deviceModelIsAvailable: { _ in false })
        #expect(!relevantIDs(items).contains("handheld.device"))
        #expect(relevantIDs(items).filter { $0.hasPrefix("handheld.binding.") }.count == 12)
    }

    @Test func thePictureHasAPlaceOnThePage() {
        let (plugin, _) = makePluginWithTheDeviceConnected()
        let arranged = SettingsLayout.arrange(HandheldSettings.items(plugin: plugin, deviceModelIsAvailable: { _ in true }))
        #expect(arranged.first { $0.id == "handheld.device" }?.page == .handheld)
    }

    @Test func choosingAControlThatIsNotTheDevicesIsIgnored() {
        let (plugin, _) = makePluginWithTheDeviceConnected()
        plugin.selectControl(withID: "nonsense")
        #expect(plugin.selectedControlID == "keyA")
    }

    @Test func aPressOnTheDeviceIsShownOnThePicture() {
        let (plugin, source) = makePluginWithTheDeviceConnected()
        var pressedControlIDs: [String] = []
        plugin.onControlPressed = { controlID in pressedControlIDs.append(controlID) }
        source.sendFromWhisPrompt("04 02 01 01 28 00 00 00 d3 31 43 00 00")
        #expect(pressedControlIDs == ["keyB"])
    }

    @Test func theMessageFromThePageNamesAControl() {
        #expect(HandheldDeviceViewMessage.selectedControlID(fromMessageBody: ["type": "select", "control": "keyC"]) == "keyC")
        #expect(HandheldDeviceViewMessage.selectedControlID(fromMessageBody: ["type": "hover", "control": "keyC"]) == nil)
        #expect(HandheldDeviceViewMessage.selectedControlID(fromMessageBody: "keyC") == nil)
        let script = HandheldDeviceViewMessage.configurationScript(
            for: WhisPromptHandheld.device, bindings: WhisPromptHandheld.device.defaultBindings, selectedControlID: "keyA", reduceMotion: false)
        #expect(script.contains("\"name\":\"Key A\",\"part\":\"按键1\""))
        // The labels beside the picture: what each gesture does, and nothing for one that does nothing.
        #expect(script.contains("{\"action\":\"Dictate (hold)\",\"mark\":\"hold\"}"))
        #expect(script.contains("{\"action\":\"Switch windows\",\"mark\":\"•\"}"))
        let keyBGestures = HandheldDeviceViewMessage.gesturesByControlID(for: WhisPromptHandheld.device, bindings: WhisPromptHandheld.device.defaultBindings)["keyB"]
        #expect(keyBGestures?.map { $0["mark"] } == ["•", "••"])
        #expect(HandheldDeviceViewMessage.gesturesScript(for: WhisPromptHandheld.device, bindings: WhisPromptHandheld.device.defaultBindings)
            .hasPrefix("window.handheldViewer && window.handheldViewer.setGestures({"))
    }

    @Test func thePageSpendsItsSpaceOnTheDevice() {
        // No header card on this page, the device first, and the line about which device is
        // connected at the bottom.
        #expect(!DictationSettingsPage.handheld.showsHeaderCard)
        #expect(DictationSettingsPage.island.showsHeaderCard)
        let (plugin, _) = makePluginWithTheDeviceConnected()
        let pageIDs = SettingsLayout.arrange(HandheldSettings.items(plugin: plugin, deviceModelIsAvailable: { _ in true }))
            .filter { $0.page == .handheld }.map(\.id)
        #expect(pageIDs.first == "handheld.device")
        #expect(pageIDs.suffix(2) == ["handheld.status", "handheld.makerAppNote"])
    }
}
