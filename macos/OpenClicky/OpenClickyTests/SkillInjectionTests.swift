//
//  SkillInjectionTests.swift
//  OpenClickyTests
//
//  The pure part of skill injection: how the skills block is joined onto a base prompt.
//

import Testing
@testable import OpenClicky

struct SkillInjectionTests {

    @Test func emptyBlockLeavesTheBasePromptUntouched() {
        #expect(CompanionManager.composeTalkInstructions(base: "base", skillsBlock: "") == "base")
        #expect(CompanionManager.composeTalkInstructions(base: "base", skillsBlock: "  \n\n ") == "base")
    }

    @Test func blockIsAppendedAfterABlankLine() {
        let out = CompanionManager.composeTalkInstructions(base: "base", skillsBlock: "\n## Skill: X\nbody\n")
        #expect(out == "base\n\n## Skill: X\nbody")
    }

    @Test func realtimeDefaultInstructionsGrowWithAnAppSkill() {
        let skill = SkillFile.parse("---\nname: Safari\ndescription: d\napps: [com.apple.Safari]\nsurfaces: [talk]\n---\n## Layout\nTabs live at the top.\n", id: "safari")!
        let front = FrontAppContext(bundleIdentifier: "com.apple.Safari", appName: "Safari", url: nil, windowTitle: nil)
        let block = SkillPromptBuilder.build(activeSkills: [], appSkill: AppSkillMatcher.match(front, in: [skill]), front: front)
        let out = CompanionManager.composeTalkInstructions(base: RealtimeVoiceClient.defaultInstructions, skillsBlock: block)
        #expect(out.count > RealtimeVoiceClient.defaultInstructions.count)
        #expect(out.contains("Tabs live at the top."))
    }
}
