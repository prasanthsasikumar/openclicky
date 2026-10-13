// The rules the Settings rows read: the account form, the work slot, the keys list, the plan rows,
// the account rows, and the Your words page's sentences (the Mac's CleanUpSwitchReadersTests /
// RecordPage sentence tests for what moved out of the views).

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Settings;

namespace OpenClicky.Core.Tests.Settings;

public sealed class SettingsSentencesTests
{
    [Theory]
    [InlineData("me@example.com", true)]
    [InlineData("  me@example.com ", true)]
    [InlineData("@example.com", false)]
    [InlineData("me@example", false)]
    [InlineData("me", false)]
    public void AnEmailNeedsANameAnAtAndADot(string email, bool canSubmit) =>
        Assert.Equal(canSubmit, EmailAccountFormRules.CanSubmitEmail(email));

    [Theory]
    [InlineData("123456", true)]
    [InlineData("123 456", true)]
    [InlineData("12345", false)]
    [InlineData("12345a", false)]
    [InlineData("١٢٣٤٥٦", false)]
    public void ACodeIsSixDigits(string code, bool canSubmit) =>
        Assert.Equal(canSubmit, EmailAccountFormRules.CanSubmitCode(code));

    [Fact]
    public async Task TheWorkSlotRunsOneThingAtATime()
    {
        var slot = new AccountWorkSlot();
        var release = new TaskCompletionSource();
        Assert.True(slot.Start(_ => release.Task));
        Assert.True(slot.IsRunning);
        Assert.False(slot.Start(_ => Task.CompletedTask));
        release.SetResult();
        for (var attempt = 0; attempt < 100 && slot.IsRunning; attempt++) await Task.Delay(5);
        Assert.False(slot.IsRunning);
        Assert.True(slot.Start(_ => Task.CompletedTask));
    }

    [Fact]
    public void KeysAreMaskedAndSarvamIsOfferedOnlyInIndia()
    {
        Assert.Equal("••••wxyz", ProviderKeys.Masked("sk-abcdwxyz"));
        var shell = new OpenClickyShellSettings { OpenaiApiKey = " sk-1 ", AnthropicApiKey = "  " };
        Assert.Equal([ProviderKey.OpenAI], ProviderKeys.Saved(shell));
        Assert.DoesNotContain(ProviderKey.Sarvam, ProviderKeys.Unsaved(shell, AppRegion.World));
        Assert.Contains(ProviderKey.Sarvam, ProviderKeys.Unsaved(shell, AppRegion.India));
        Assert.Contains(ProviderKey.Anthropic, ProviderKeys.Unsaved(shell, AppRegion.World));
        ProviderKey.AmazonPolly.Store(shell, "AKIA:secret");
        Assert.Equal("AKIA:secret", ProviderKey.AmazonPolly.SavedKey(shell));
        Assert.Equal("polly", ProviderKey.AmazonPolly.SpeechVoiceId());
        Assert.Null(ProviderKey.AmazonPolly.AnswerSource());
        Assert.Equal(AnswerSource.Xai, ProviderKey.Xai.AnswerSource());
    }

    [Fact]
    public void PlanRowsSayWhatIsMissing()
    {
        Assert.Equal("Checking…", PlanSubscription.Detail(AnswerSource.CodexPlan, null, false));
        Assert.Contains("isn’t installed on this PC", PlanSubscription.Detail(AnswerSource.ClaudeCodePlan, PlanCliStatus.NotInstalled, false), StringComparison.Ordinal);
        Assert.Contains("“codex login”", PlanSubscription.Detail(AnswerSource.CodexPlan, PlanCliStatus.InstalledSignedOut, false), StringComparison.Ordinal);
        Assert.StartsWith("Signed in. Switch on", PlanSubscription.Detail(AnswerSource.CodexPlan, PlanCliStatus.Ready, false), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAllowanceRowNeverWaitsForeverWithoutASignIn()
    {
        Assert.Equal("Sign in to see usage", AccountRows.PlanSummary(null, null, usesOwnKeys: false, hasAccountToken: false).Trailing);
        Assert.Equal("Your keys", AccountRows.PlanSummary(null, null, usesOwnKeys: true, hasAccountToken: false).Trailing);
        Assert.Equal("Loading…", AccountRows.PlanSummary(null, null, usesOwnKeys: false, hasAccountToken: true).Trailing);
        Assert.Equal("Needs account", AccountRows.BackendEngineUsage(false, false).Badge);
    }

    [Fact]
    public void TheSidebarFootNamesThePerson()
    {
        Assert.Equal("prasanth", AccountRows.AccountName("prasanth@ahlab.org", "Prasanth Sasikumar"));
        Assert.Equal("Prasanth S.", AccountRows.AccountName(null, "Prasanth Sasikumar"));
        Assert.Equal("You", AccountRows.AccountName(null, ""));
        Assert.Equal("P", AccountRows.AccountInitial("prasanth"));
        Assert.Equal("Personal · no account", AccountRows.AccountStanding(false, false));
    }

    [Fact]
    public void TheEngineSentenceSaysWhereTheVoiceGoes()
    {
        var settings = new DictationSettings(Preferences.InMemory()) { Engine = DictationEngineChoice.Offline, Incognito = false };
        Assert.Equal("your voice and your history stay on this pc.", WordsPageSentences.EngineSentence(settings, hasPolisher: false));
        settings.Incognito = true;
        Assert.Equal("Your voice stays on this PC, and nothing is saved.", WordsPageSentences.EngineSentenceInSentenceCase(settings, hasPolisher: false));
        settings.Engine = DictationEngineChoice.Sarvam;
        Assert.Equal("Audio goes to Sarvam with your key. Incognito: nothing is saved.", WordsPageSentences.EngineSentenceInSentenceCase(settings, hasPolisher: true));
    }

    [Fact]
    public void ReasonsBecomeSentencesWithTheirCapitals()
    {
        Assert.Equal("Needs your Sarvam key", WordsPageSentences.SentenceCased("needs your sarvam key"));
        Assert.Equal("Sign in to OpenClicky or add an OpenAI key", WordsPageSentences.SentenceCased("sign in to openclicky or add an openai key"));
    }

    [Fact]
    public void TakesGroupByDayNewestFirst()
    {
        var today = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Local);
        TakeRecord Take(DateTime when) => new() { CreatedAt = new DateTimeOffset(when) };
        var groups = WordsPageSentences.GroupedByDay(
            [Take(today.AddHours(-1)), Take(today.AddDays(-1)), Take(today.AddDays(-3)), Take(today.AddHours(-2))],
            today, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(["Today", "Yesterday", "Saturday, 10 Oct"], groups.Select(group => group.Title));
        Assert.Equal(2, groups[0].Takes.Count);
    }

    [Fact]
    public void TheCleanUpSwitchSetsBothSettings()
    {
        var settings = new DictationSettings(Preferences.InMemory()) { Engine = DictationEngineChoice.Offline };
        DictationCleanUp.SetSwitch(settings, true);
        Assert.True(settings.PolishWithModel);
        Assert.True(settings.PolishOfflineTakes);
        Assert.True(DictationCleanUp.SwitchShowsOn(settings));
        DictationCleanUp.SetSwitch(settings, false);
        Assert.False(DictationCleanUp.SwitchShowsOn(settings));
        Assert.EndsWith("free OpenClicky account.", DictationCleanUp.Detail(hasModel: false), StringComparison.Ordinal);
    }
}
