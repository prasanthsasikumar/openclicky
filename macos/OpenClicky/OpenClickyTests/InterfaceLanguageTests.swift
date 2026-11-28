//
//  InterfaceLanguageTests.swift
//  OpenClickyTests
//
//  Chinese interface strings, and the English fallback when Chinese is off.
//

import Testing
@testable import OpenClicky

struct InterfaceLanguageTests {
    @Test func chineseCoversTheSettingsSidebarAndEnglishStaysWhenItIsOff() {
        InterfaceLanguage.forcedChinese = true
        #expect(InterfaceLanguage.t("Dictation") == "听写")
        #expect(InterfaceLanguage.t("Language") == "语言")
        #expect(InterfaceLanguage.t("12 settings") == "12 项设置")
        #expect(InterfaceLanguage.t("Not a known line") == "Not a known line")
        InterfaceLanguage.forcedChinese = false
        #expect(InterfaceLanguage.t("Dictation") == "Dictation")
        #expect(InterfaceLanguage.t("12 settings") == "12 settings")
        InterfaceLanguage.forcedChinese = nil
    }
}
