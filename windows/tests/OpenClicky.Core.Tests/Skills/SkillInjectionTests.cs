// Port of OpenClickyTests/SkillInjectionTests.swift — the pure part of skill injection: how the
// skills block is joined onto a base prompt. The Swift tests call
// CompanionManager.composeTalkInstructions; on Windows it is SkillPromptBuilder.ComposeTalkInstructions.

using OpenClicky.Core.Skills;

namespace OpenClicky.Core.Tests.Skills;

public class SkillInjectionTests
{
    [Fact]
    public void EmptyBlockLeavesTheBasePromptUntouched()
    {
        Assert.Equal("base", SkillPromptBuilder.ComposeTalkInstructions("base", ""));
        Assert.Equal("base", SkillPromptBuilder.ComposeTalkInstructions("base", "  \n\n "));
    }

    [Fact]
    public void BlockIsAppendedAfterABlankLine()
    {
        var output = SkillPromptBuilder.ComposeTalkInstructions("base", "\n## Skill: X\nbody\n");
        Assert.Equal("base\n\n## Skill: X\nbody", output);
    }

    [Fact]
    public void BaseInstructionsGrowWithAnAppSkill()
    {
        // The Swift test uses RealtimeVoiceClient.defaultInstructions as the base; that type belongs to the
        // Speech port, so any non-empty base stands in for it here.
        const string baseInstructions = "You are OpenClicky, a voice companion.";
        var skill = SkillFile.Parse("---\nname: Safari\ndescription: d\napps: [com.apple.Safari, msedge.exe]\nsurfaces: [talk]\n---\n## Layout\nTabs live at the top.\n", "safari")!;
        var front = new FrontAppContext { ProcessName = "msedge.exe", AppName = "Microsoft Edge" };
        var block = SkillPromptBuilder.Build(Array.Empty<SkillFile>(), AppSkillMatcher.Match(front, new[] { skill }), front);
        var output = SkillPromptBuilder.ComposeTalkInstructions(baseInstructions, block);
        Assert.True(output.Length > baseInstructions.Length);
        Assert.Contains("Tabs live at the top.", output);
    }
}
