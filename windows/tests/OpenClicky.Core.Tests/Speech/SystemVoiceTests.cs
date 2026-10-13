// Ports MacVoiceTests.swift as this PC's voice: when it is chosen (shell.json `speechVoice`), how
// its voices are labelled, the rate and the picked voice it renders with, and that the setting
// survives a round trip through shell.json's encoding.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class SystemVoiceTests
{
    private sealed class FakeSystemVoice : ISystemVoice
    {
        public (string Text, string Language, string? Picked, double? Rate)? LastRender;

        public IReadOnlyList<SystemVoiceInfo> Voices(string? languageCode = null) =>
        [
            new("aria", "Microsoft Aria", "en-US", "Female"),
            new("libby", "Microsoft Libby", "en-GB", "Female"),
        ];

        public SystemVoiceInfo? CurrentVoice(string languageCode, string? pickedVoiceId = null) =>
            Voices(languageCode).FirstOrDefault(voice => voice.Id == pickedVoiceId) ?? Voices(languageCode)[0];

        public double DefaultSpeakingRate => 0.92;

        public Task<byte[]> RenderWavAsync(string text, string languageCode, string? pickedVoiceId = null, double? speakingRate = null, CancellationToken cancellationToken = default)
        {
            LastRender = (text, languageCode, pickedVoiceId, speakingRate);
            return Task.FromResult(new byte[] { 1, 2, 3 });
        }
    }

    [Fact]
    public void OnlyMacMeansThisPCsVoice()
    {
        var settings = new OpenClickyShellSettings();
        Assert.False(SystemVoice.IsChosen(settings));
        settings.SpeechVoice = "cloud";
        Assert.False(SystemVoice.IsChosen(settings));
        settings.SpeechVoice = "mac";
        Assert.True(SystemVoice.IsChosen(settings));
    }

    [Fact]
    public void ThisPCSpeaksBelowTheNormalRate()
    {
        var voice = new SystemVoice(new FakeSystemVoice());
        Assert.True(voice.SpeakingRate < 1.0);
    }

    [Fact]
    public void VoicesSayTheirRegion()
    {
        Assert.Equal("Eddy, United Kingdom", new SystemVoice.Choice("a", "Eddy", "United Kingdom").Label);
        Assert.Equal("Zoe", new SystemVoice.Choice("c", "Zoe", null).Label);
        var choices = new SystemVoice(new FakeSystemVoice()).Choices("en");
        Assert.Equal(["aria", "libby"], choices.Select(choice => choice.Id));
        Assert.All(choices, choice => Assert.NotNull(choice.Region));
        Assert.Null(SystemVoice.RegionName("en"));
    }

    [Fact]
    public async Task AudioIsRenderedWithThePickedVoiceAndTheRate()
    {
        var fake = new FakeSystemVoice();
        var settings = new OpenClickyShellSettings { MacVoiceIdentifier = "libby" };
        var audio = await new SystemVoice(fake).AudioAsync("hello", settings, "en");
        Assert.Equal([1, 2, 3], audio);
        Assert.Equal(("hello", "en", "libby", 0.92), fake.LastRender);
        Assert.Equal("libby", new SystemVoice(fake).CurrentVoice(settings, "en")?.Id);
    }

    [Fact]
    public void TheVoiceChoiceRoundTripsThroughShellJSON()
    {
        var settings = new OpenClickyShellSettings { SpeechVoice = "mac", MacVoiceIdentifier = "MSTTS_V110_enUS_AriaM" };
        var decoded = OpenClickyShellSettings.Decode(settings.Encode());
        Assert.Equal("mac", decoded.SpeechVoice);
        Assert.Equal("MSTTS_V110_enUS_AriaM", decoded.MacVoiceIdentifier);
    }
}
