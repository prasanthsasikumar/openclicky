// Ports the settings-and-engine parts of CleanUpSwitchReadersTests.swift, DictationPipelineTests.swift,
// HearingSettingsTests.swift (the language every engine receives) and AppRegionTests.swift (the
// dictation languages each region offers). The views' readers (the styles switch, the record page's
// sentence) and the island's warnings are UI and are tested with the Windows views.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Speech;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Shortcuts;

namespace OpenClicky.Core.Tests.Dictation;

public sealed class DictationSettingsTests
{
    private static DictationSettings MakeSettings(bool polishWithModel, bool polishOfflineTakes, DictationEngineChoice engine)
    {
        var settings = new DictationSettings(Preferences.InMemory())
        {
            Engine = engine,
            PolishWithModel = polishWithModel,
            PolishOfflineTakes = polishOfflineTakes,
            Incognito = false,
        };
        return settings;
    }

    [Fact]
    public void AFreshInstallHasTheMacsDefaults()
    {
        var preferences = Preferences.InMemory();
        var settings = new DictationSettings(preferences);
        Assert.Equal(DictationEngineChoice.Offline, settings.Engine);
        Assert.Equal(DictationLanguage.Auto, settings.Language);
        Assert.Equal(DictationScript.Native, settings.Script);
        Assert.False(settings.PolishWithModel);
        Assert.False(settings.PolishOfflineTakes);
        Assert.Equal(DictationHotkeys.Default, settings.DictationKey);
        Assert.True(settings.OrbVisible);
        Assert.Equal(OrbLook.Pill, settings.OrbLook);
        Assert.True(settings.OrbHidesWhenIdle);
        Assert.True(settings.OrbIsDraggable);
        Assert.True(settings.Sounds);
        Assert.Equal(3, settings.InactivityTimeoutMinutes);
        Assert.True(settings.RetainFailedTakeAudio);
        Assert.True(settings.ReadNearbyText);
        Assert.Null(settings.OrbPosition);
        Assert.Equal(DictationAppearance.System, settings.Appearance);
        Assert.True(preferences.GetBool(DictationSettings.Keys.OrbHidesWhenIdleDefaultApplied));
    }

    [Fact]
    public void EverySettingIsStoredUnderTheMacsKeyAndRawValue()
    {
        var preferences = Preferences.InMemory();
        var settings = new DictationSettings(preferences)
        {
            Engine = DictationEngineChoice.AssemblyAI,
            LanguageCode = "hi-IN",
            Script = DictationScript.Roman,
            OrbTheme = OrbTheme.Coral,
            OrbPosition = new OrbPoint(100, 250.5),
            InactivityTimeoutMinutes = 0,
            Region = AppRegion.World,
            DictationKey = DictationHotkey.RightAlt,
            KeepMemoryOnThisPC = true,
        };
        Assert.Equal("assemblyai", preferences.GetString("dictation.engine"));
        Assert.Equal("hi-IN", preferences.GetString("dictation.language"));
        Assert.Equal("roman", preferences.GetString("dictation.script"));
        Assert.Equal("coral", preferences.GetString("dictation.orb.theme"));
        Assert.Equal("100,250.5", preferences.GetString("dictation.orb.position"));
        Assert.Equal(0, preferences.GetInt("dictation.inactivityTimeoutMinutes", -1));
        Assert.Equal("world", preferences.GetString("openclicky.region"));
        Assert.Equal("rightAlt", preferences.GetString("dictation.key"));
        Assert.True(preferences.GetBool("dictation.keepMemoryOnThisMac"));

        var reread = new DictationSettings(preferences);
        Assert.Equal(DictationEngineChoice.AssemblyAI, reread.Engine);
        Assert.Equal(new OrbPoint(100, 250.5), reread.OrbPosition);
        Assert.Equal(0, reread.InactivityTimeoutMinutes);
        Assert.Equal(AppRegion.World, reread.Region);
        Assert.Equal("hi", reread.Language.BareCode);
    }

    [Fact]
    public void AMacOnlyDictationKeyReadsAsTheWindowsDefault()
    {
        var preferences = Preferences.InMemory();
        preferences.Set("dictation.key", "fn");
        Assert.Equal(DictationHotkeys.Default, new DictationSettings(preferences).DictationKey);
    }

    [Fact]
    public void TheOrbResetsToAFreshInstall()
    {
        var settings = new DictationSettings(Preferences.InMemory()) { OrbLook = OrbLook.Pixel, OrbPosition = new OrbPoint(1, 2), Sounds = false };
        settings.ResetOrbToDefaults();
        Assert.Equal(OrbLook.Pill, settings.OrbLook);
        Assert.Null(settings.OrbPosition);
        Assert.True(settings.Sounds);
    }

    // MARK: the clean-up switch (CleanUpSwitchReadersTests)

    [Fact]
    public void TheOfflineEnginesNeedTheExplicitSwitch()
    {
        foreach (var engine in DictationEngineChoices.AllCases)
        {
            foreach (var polishWithModel in new[] { false, true })
            {
                foreach (var polishOfflineTakes in new[] { false, true })
                {
                    var settings = MakeSettings(polishWithModel, polishOfflineTakes, engine);
                    var expected = polishWithModel && (!engine.IsOnDevice() || polishOfflineTakes);
                    Assert.Equal(expected, DictationEngineResolver.WantsModelPolish(settings));
                }
            }
        }
    }

    /// <summary>
    /// The 0.8.0 upgrader: "polish with a model" was turned off, and a free account's default then
    /// turned "also polish offline takes" on. Nothing is polished, so nothing may say it is.
    /// </summary>
    [Fact]
    public void AnUpgraderWithTheOldSwitchOffSeesItOff() =>
        Assert.False(DictationEngineResolver.WantsModelPolish(MakeSettings(polishWithModel: false, polishOfflineTakes: true, DictationEngineChoice.Offline)));

    /// <summary>The smoke run names its engine on the command line instead of the stored one.</summary>
    [Fact]
    public void AnEngineOtherThanTheChosenOneFollowsTheSameRule()
    {
        var settings = MakeSettings(polishWithModel: true, polishOfflineTakes: false, DictationEngineChoice.Sarvam);
        Assert.True(DictationEngineResolver.WantsModelPolish(settings, DictationEngineChoice.Sarvam));
        Assert.False(DictationEngineResolver.WantsModelPolish(settings, DictationEngineChoice.Offline));
    }

    [Fact]
    public void TheAccountDefaultFollowsTheAccountUntilThePersonChooses()
    {
        var preferences = Preferences.InMemory();
        var settings = new DictationSettings(preferences);
        settings.ApplyAccountPolishDefault(AccountKind.Account);
        Assert.True(settings.PolishOfflineTakes);
        // The default is not stored, so it follows the account as it changes.
        Assert.False(preferences.Contains(DictationSettings.Keys.PolishOfflineTakes));
        settings.ApplyAccountPolishDefault(AccountKind.SignedOut);
        Assert.False(settings.PolishOfflineTakes);

        settings.ChoosePolishOfflineTakes(false);
        settings.ApplyAccountPolishDefault(AccountKind.Account);
        Assert.False(settings.PolishOfflineTakes);
    }

    [Fact]
    public void AStoredValueFromBeforeTheChoiceFlagCountsAsChosen()
    {
        var preferences = Preferences.InMemory();
        preferences.Set(DictationSettings.Keys.PolishOfflineTakes, false);
        var settings = new DictationSettings(preferences);
        Assert.True(preferences.GetBool(DictationSettings.Keys.PolishOfflineTakesChosen));
        settings.ApplyAccountPolishDefault(AccountKind.Account);
        Assert.False(settings.PolishOfflineTakes);
    }

    // MARK: engines on this PC (DictationPipelineTests)

    [Fact]
    public void OnlyTheOfflineEnginesKeepTheVoiceOnThisPC()
    {
        Assert.Equal([DictationEngineChoice.Offline, DictationEngineChoice.Parakeet], DictationEngineChoices.AllCases.Where(engine => engine.IsOnDevice()));
        // On Windows only Parakeet is private: Windows speech recognition may use Microsoft's
        // online speech service, so it is never tagged or described as keeping the voice here.
        Assert.Equal([DictationEngineChoice.Parakeet], DictationEngineChoices.AllCases.Where(engine => engine.KeepsVoiceOnThisPC()));
        foreach (var engine in DictationEngineChoices.AllCases)
        {
            Assert.Equal(engine.KeepsVoiceOnThisPC(), engine.AudioDestinationTag() == "Private · offline");
            Assert.Equal(engine.KeepsVoiceOnThisPC(), engine.AudioDestinationSentence() == "Your voice stays on this PC.");
        }
    }

    [Fact]
    public void WindowsSpeechSaysItMayUseMicrosoftsOnlineService()
    {
        var offline = DictationEngineChoice.Offline;
        Assert.Contains("Microsoft’s online speech service", offline.SettingsDetail());
        Assert.Contains("online speech recognition", offline.SettingsDetail());
        Assert.Contains("Microsoft’s online speech service", offline.AudioDestinationSentence());
        Assert.DoesNotContain("Nothing leaves", offline.SettingsDetail());
        Assert.DoesNotContain("private", offline.SearchKeywords());
        Assert.DoesNotContain("offline", offline.SearchKeywords());
        Assert.Contains("nothing leaves this PC", DictationEngineChoice.Parakeet.SettingsDetail());
        Assert.True(ParakeetFacts.IsRecommendedForThisPC);
    }

    [Fact]
    public void TheEnginesKeepTheMacsRawValues()
    {
        Assert.Equal(["offline", "parakeet", "sarvam", "openclicky", "assemblyai"], DictationEngineChoices.AllCases.Select(engine => engine.Id()));
        Assert.Equal(DictationEngineChoice.OpenClicky, DictationEngineChoices.FromRawValue("openclicky"));
        Assert.Equal("Windows speech recognition", DictationEngineChoice.Offline.SettingsTitle());
        Assert.Equal("windows", DictationEngineChoice.Offline.ListenerName());
        Assert.Equal("this pc", DictationEngineChoice.Parakeet.ListenerName());
        Assert.Contains("windows", DictationEngineChoice.Offline.SearchKeywords());
        Assert.Contains("cloud", DictationEngineChoice.OpenClicky.SearchKeywords());
        Assert.Contains("hindi", DictationEngineChoice.Sarvam.SearchKeywords());
        Assert.Equal("", DictationEngineChoice.Offline.UnavailableBadge());
    }

    [Fact]
    public void TheStoredLanguageIsTheOneTheSettingsWrote()
    {
        var preferences = Preferences.InMemory();
        Assert.Equal(DictationLanguage.Auto, DictationSettings.StoredLanguage(preferences));
        _ = new DictationSettings(preferences) { LanguageCode = "hi-IN" };
        Assert.Equal("hi", DictationSettings.StoredLanguage(preferences).BareCode);
    }

    // MARK: why an engine can't run

    [Fact]
    public void EachEngineSaysWhatItIsMissing()
    {
        string? Reason(DictationEngineChoice engine, AccountKind kind = AccountKind.OwnKeys, bool isConfigured = true, bool signedIn = true,
                       string? sarvamKey = "k", bool parakeetInstalled = true, string? bareLanguage = null) =>
            DictationEngineResolver.UnavailableReason(engine, kind, isConfigured, signedIn, sarvamKey, parakeetInstalled, () => bareLanguage);

        Assert.Null(Reason(DictationEngineChoice.Offline, isConfigured: false));
        Assert.Equal("needs its 465 MB download", Reason(DictationEngineChoice.Parakeet, parakeetInstalled: false));
        Assert.Equal(DictationEngineResolver.ParakeetLanguageNotHeardReason, Reason(DictationEngineChoice.Parakeet, bareLanguage: "hi"));
        Assert.Null(Reason(DictationEngineChoice.Parakeet, bareLanguage: "de"));
        Assert.Equal("needs your sarvam key", Reason(DictationEngineChoice.Sarvam, sarvamKey: " \n"));
        Assert.Null(Reason(DictationEngineChoice.Sarvam));
        Assert.Equal("needs an openclicky account or your openai key", Reason(DictationEngineChoice.OpenClicky, isConfigured: false));
        Assert.Equal("needs your own key", Reason(DictationEngineChoice.AssemblyAI, kind: AccountKind.Account));
        Assert.Equal("needs an openclicky account: assemblyai runs on openclicky's key", Reason(DictationEngineChoice.AssemblyAI, signedIn: false));
        Assert.Null(Reason(DictationEngineChoice.OpenClicky, signedIn: false));
    }

    [Fact]
    public void ThePolisherFollowsTheKeys()
    {
        Assert.IsType<SarvamTakePolisher>(DictationEngineResolver.MakePolisher(" s ", new ProviderCredentials("o", "a", "t"), AccountKind.OwnKeys));
        Assert.IsType<AnthropicTakePolisher>(DictationEngineResolver.MakePolisher(null, new ProviderCredentials(null, "a", null), AccountKind.SignedOut));
        Assert.IsType<AnswerKeyTakePolisher>(DictationEngineResolver.MakePolisher(null, new ProviderCredentials(null, null, null, groqKey: "q"), AccountKind.SignedOut));
        // An OpenAI key polishes on OpenAI (BackendTakePolisher routes it there), ahead of the other keys.
        Assert.IsType<BackendTakePolisher>(DictationEngineResolver.MakePolisher(null, new ProviderCredentials("o", "a", null), AccountKind.OwnKeys));
        Assert.IsType<AccountTakePolisher>(DictationEngineResolver.MakePolisher(null, new ProviderCredentials(null, null, "t"), AccountKind.Account));
        Assert.Null(DictationEngineResolver.MakePolisher("", new ProviderCredentials(null, null, null), AccountKind.SignedOut));
    }

    // MARK: the language every engine hears (HearingSettingsTests, F-16)

    private sealed class RecordingFactory : IDictationProviderFactory
    {
        public string? OfflineLocale { get; private set; }
        public Func<string?>? ParakeetLanguage { get; private set; }
        public Func<DictationLanguage>? SarvamLanguage { get; private set; }
        public Func<string?>? OpenClickyLanguage { get; private set; }
        public Func<string?>? AssemblyAILanguage { get; private set; }

        public IBuddyTranscriptionProvider MakeOffline(string? preferredLocale) { OfflineLocale = preferredLocale; return new NamedTranscriptionProvider("offline"); }
        public IBuddyTranscriptionProvider MakeParakeet(Func<string?> bareLanguageCode) { ParakeetLanguage = bareLanguageCode; return new NamedTranscriptionProvider("parakeet"); }
        public IBuddyTranscriptionProvider MakeSarvam(Func<DictationLanguage> language) { SarvamLanguage = language; return new NamedTranscriptionProvider("sarvam"); }
        public IBuddyTranscriptionProvider MakeOpenClicky(Func<string?> bareLanguageCode) { OpenClickyLanguage = bareLanguageCode; return new NamedTranscriptionProvider("openclicky"); }
        public IBuddyTranscriptionProvider MakeAssemblyAI(Func<string?> bareLanguageCode) { AssemblyAILanguage = bareLanguageCode; return new NamedTranscriptionProvider("assemblyai"); }
    }

    [Fact]
    public void EveryDictationEngineReceivesTheStylesPageLanguage()
    {
        var settings = new DictationSettings(Preferences.InMemory()) { LanguageCode = "hi-IN" };
        var factory = new RecordingFactory();
        foreach (var engine in DictationEngineChoices.AllCases)
        {
            Assert.Equal(engine.Id(), DictationEngineResolver.MakeProvider(engine, settings, factory).DisplayName);
        }
        Assert.Equal("hi-IN", factory.OfflineLocale);
        Assert.Equal("hi", factory.ParakeetLanguage!());
        Assert.Equal("hi", factory.OpenClickyLanguage!());
        Assert.Equal("hi", factory.AssemblyAILanguage!());
        Assert.Equal("hi-IN", factory.SarvamLanguage!().Code);

        // Read when a take starts: auto sends none, so the model detects it.
        settings.LanguageCode = "auto";
        Assert.Null(factory.OpenClickyLanguage());
        Assert.Null(factory.AssemblyAILanguage());
        Assert.Equal(DictationLanguage.Auto, factory.SarvamLanguage());
        DictationEngineResolver.MakeProvider(DictationEngineChoice.Offline, settings, factory);
        Assert.Null(factory.OfflineLocale);
    }

    // MARK: the region's languages (AppRegionTests)

    [Fact]
    public void TheWorldIsOfferedNoIndianDictationLanguages()
    {
        var world = DictationLanguage.PinnableChoices(AppRegion.World, DictationLanguage.Auto);
        Assert.NotEmpty(world);
        Assert.DoesNotContain(world, language => language.Code.EndsWith("-IN", StringComparison.Ordinal));
        Assert.Contains(DictationLanguage.Named("en-US"), world);
        Assert.Contains(DictationLanguage.Named("fr-FR"), world);
    }

    [Fact]
    public void IndiaKeepsItsLanguagesAndEnglishAsWrittenThere()
    {
        var india = DictationLanguage.PinnableChoices(AppRegion.India, DictationLanguage.Auto);
        Assert.Equal(DictationLanguage.Named("en-IN"), india[0]);
        Assert.Contains(DictationLanguage.Named("hi-IN"), india);
        Assert.DoesNotContain(DictationLanguage.Named("en-US"), india);
    }

    [Fact]
    public void ALanguageInUseStaysOfferedWhateverTheRegion()
    {
        var hindi = DictationLanguage.Named("hi-IN");
        Assert.Equal(hindi, DictationLanguage.PinnableChoices(AppRegion.World, hindi)[^1]);
    }

    [Fact]
    public void PinningStartsOnTheRegionsEnglish()
    {
        Assert.Equal("en-IN", DictationLanguage.DefaultPinned(AppRegion.India).Code);
        Assert.Equal("en-US", DictationLanguage.DefaultPinned(AppRegion.World).Code);
    }

    [Fact]
    public void OnlyTheIndianLanguagesCountAsIndian()
    {
        Assert.True(DictationLanguage.Named("ta-IN").IsIndian);
        Assert.False(DictationLanguage.Named("en-IN").IsIndian);
        Assert.False(DictationLanguage.Named("de-DE").IsIndian);
        Assert.False(DictationLanguage.Auto.IsIndian);
    }

    [Fact]
    public void AnUnknownLanguageIsAutoDetect()
    {
        Assert.Equal(DictationLanguage.Auto, DictationLanguage.Named("xx-YY"));
        Assert.Null(DictationLanguage.Auto.BareCode);
        Assert.Equal("pt", DictationLanguage.Named("pt-BR").BareCode);
    }
}
