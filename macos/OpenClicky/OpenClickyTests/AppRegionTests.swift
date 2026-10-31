//
//  AppRegionTests.swift
//  OpenClickyTests
//
//  India or the rest of the world: which languages each offers, never dropping the one in use.
//  The settings always use the world now; India's set is kept for what is already in use.
//

import Testing
@testable import OpenClicky

struct AppRegionTests {
    @Test func theWorldIsOfferedNoIndianDictationLanguages() {
        let world = DictationLanguage.pinnableChoices(region: .world, current: .auto)
        #expect(!world.isEmpty)
        #expect(!world.contains { $0.code.hasSuffix("-IN") })
        #expect(world.contains(DictationLanguage.named("en-US")))
        #expect(world.contains(DictationLanguage.named("fr-FR")))
    }

    @Test func indiaKeepsItsLanguagesAndEnglishAsWrittenThere() {
        let india = DictationLanguage.pinnableChoices(region: .india, current: .auto)
        #expect(india.first == DictationLanguage.named("en-IN"))
        #expect(india.contains(DictationLanguage.named("hi-IN")))
        #expect(!india.contains(DictationLanguage.named("en-US")))
    }

    @Test func aLanguageInUseStaysOfferedWhateverTheRegion() {
        let hindi = DictationLanguage.named("hi-IN")
        #expect(DictationLanguage.pinnableChoices(region: .world, current: hindi).last == hindi)
        #expect(ReplyLanguage.choices(region: .world, currentCode: "ta").contains { $0.code == "ta" })
        #expect(!ReplyLanguage.choices(region: .world, currentCode: "en").contains { $0.code == "hi" })
        #expect(ReplyLanguage.choices(region: .india, currentCode: "en").count == ReplyLanguage.choices.count)
    }

    @Test func pinningStartsOnTheRegionsEnglish() {
        #expect(DictationLanguage.defaultPinned(region: .india).code == "en-IN")
        #expect(DictationLanguage.defaultPinned(region: .world).code == "en-US")
    }

    @Test func onlyTheIndianLanguagesCountAsIndian() {
        #expect(DictationLanguage.named("ta-IN").isIndian)
        #expect(!DictationLanguage.named("en-IN").isIndian)
        #expect(!DictationLanguage.named("de-DE").isIndian)
        #expect(!DictationLanguage.auto.isIndian)
    }
}
