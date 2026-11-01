// The assistant's name: what a typed name is kept as, and how the prompts that introduce the assistant use it.

using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Tests.Configuration;

public class AssistantNameTests
{
    [Theory]
    [InlineData("Dyna", "Dyna")]
    [InlineData("  Professor   Pip ", "Professor Pip")]
    [InlineData("Dyna!!! <script>", "Dyna script")]
    [InlineData("", null)]
    [InlineData("???", null)]
    [InlineData(null, null)]
    public void ATypedNameIsKeptClean(string? typed, string? kept) => Assert.Equal(kept, AssistantName.Cleaned(typed));

    [Fact]
    public void ALongNameIsCut() => Assert.Equal(AssistantName.MaximumLength, AssistantName.Cleaned(new string('a', 80))!.Length);

    [Fact]
    public void TheDefaultNameLeavesPromptsAsTheyAre()
    {
        const string prompt = "you're openclicky, a friendly companion.";
        Assert.Equal(prompt, AssistantName.Introduce(prompt, "OpenClicky"));
        Assert.Equal(prompt, AssistantName.Introduce(prompt, "openclicky"));
    }

    [Fact]
    public void AChosenNameIsWhoTheModelIs()
    {
        var prompt = AssistantName.Introduce("you're openclicky, a friendly companion. You are OpenClicky, fast.", "Dyna");
        Assert.StartsWith("you're Dyna, a friendly companion. You are Dyna, fast.", prompt);
        Assert.Contains("your name is Dyna", prompt);

        var asked = AssistantName.Introduce(AskLanePrompts.CompanionVoiceResponseSystemPrompt, "Dyna");
        Assert.Contains("you're Dyna", asked);
        Assert.DoesNotContain("you're openclicky", asked);
    }
}
