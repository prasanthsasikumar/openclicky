//
//  CleanUpSwitchReadersTests.swift
//  OpenClickyTests
//
//  "clean up my words with ai" is two stored settings (polishWithModel, polishOfflineTakes) read
//  in several places. Every reader must give the take's own answer,
//  `DictationEngineResolver.wantsModelPolish`, for every combination — an upgrader whose two old
//  settings disagree must not see the switch on while nothing is polished.
//

import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct CleanUpSwitchReadersTests {

    @Test func cleanUpWithAModelIsOnFromTheFirstTake() {
        let defaults = UserDefaults(suiteName: "openclicky-cleanup-fresh-\(UUID().uuidString)")!
        #expect(DictationSettings(defaults: defaults).polishWithModel)
        defaults.set(false, forKey: "dictation.polishWithModel")
        #expect(!DictationSettings(defaults: defaults).polishWithModel)
    }

    private func makeSettings(polishWithModel: Bool, polishOfflineTakes: Bool, engine: DictationEngineChoice) -> DictationSettings {
        let defaults = ScratchDefaults.make()
        let settings = DictationSettings(defaults: defaults)
        settings.engine = engine
        settings.polishWithModel = polishWithModel
        settings.polishOfflineTakes = polishOfflineTakes
        settings.incognito = false
        return settings
    }

    @Test(arguments: DictationEngineChoice.allCases)
    func theStylesSwitchShowsWhatTheTakeDoes(engine: DictationEngineChoice) {
        for polishWithModel in [false, true] {
            for polishOfflineTakes in [false, true] {
                let settings = makeSettings(polishWithModel: polishWithModel, polishOfflineTakes: polishOfflineTakes, engine: engine)
                #expect(DictationCleanUpRow.cleanUpSwitchShowsOn(settings: settings) == DictationEngineResolver.wantsModelPolish(settings: settings),
                        "polishWithModel \(polishWithModel), polishOfflineTakes \(polishOfflineTakes), \(engine)")
            }
        }
    }

    @Test func theRecordPageSaysAModelPolishesOnlyWhenOneDoes() {
        for polishWithModel in [false, true] {
            for polishOfflineTakes in [false, true] {
                let settings = makeSettings(polishWithModel: polishWithModel, polishOfflineTakes: polishOfflineTakes, engine: .offline)
                let sentence = WordsPageView.engineSentence(settings: settings, hasPolisher: true)
                #expect(sentence.contains("model for polish") == DictationEngineResolver.wantsModelPolish(settings: settings),
                        "polishWithModel \(polishWithModel), polishOfflineTakes \(polishOfflineTakes): \(sentence)")
            }
        }
    }

    /// The 0.8.0 upgrader: "polish with a model" was turned off, and a free account's default then
    /// turned "also polish offline takes" on. Nothing is polished, so nothing may say it is.
    @Test func anUpgraderWithTheOldSwitchOffSeesItOff() {
        let settings = makeSettings(polishWithModel: false, polishOfflineTakes: true, engine: .offline)
        #expect(!DictationEngineResolver.wantsModelPolish(settings: settings))
        #expect(!DictationCleanUpRow.cleanUpSwitchShowsOn(settings: settings))
        #expect(WordsPageView.engineSentence(settings: settings, hasPolisher: true) == "your voice and your history stay on this mac.")
    }

    /// The smoke run names its engine on the command line instead of the stored one.
    @Test func anEngineOtherThanTheChosenOneFollowsTheSameRule() {
        let settings = makeSettings(polishWithModel: true, polishOfflineTakes: false, engine: .sarvam)
        #expect(DictationEngineResolver.wantsModelPolish(settings: settings, engine: .sarvam))
        #expect(!DictationEngineResolver.wantsModelPolish(settings: settings, engine: .offline))
    }
}
