// Ports MacVoice.swift as "this PC": the system's own voice for spoken answers, free, offline, no
// account. The Windows speech synthesiser renders each piece of an answer to WAV (not straight to
// the speakers), so it goes through the same SpeechQueue and player as the cloud voice: the next
// sentence renders while this one plays, and the island's stop button stops it the same way. The
// best installed voice for the reply language is used unless one is picked in Settings → Clicky
// (`macVoiceIdentifier` in shell.json, kept under the Mac's key so the file stays interchangeable).

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Speech;

public sealed class SystemVoice(ISystemVoice voices)
{
    /// <summary>A voice in Settings → Clicky's list. Windows voices have no Premium/Enhanced tiers, so the label is name and region.</summary>
    /// <param name="Region">"United Kingdom" for en-GB: several voices share a name across regions.</param>
    public sealed record Choice(string Id, string Name, string? Region)
    {
        public string Label => Region is { } region ? $"{Name}, {region}" : Name;
    }

    public ISystemVoice Voices { get; } = voices;

    /// <summary>The voices installed for <paramref name="languageCode"/> ("en", "hi", …), best first.</summary>
    public IReadOnlyList<Choice> Choices(string? languageCode = null) =>
        Voices.Voices(languageCode ?? ReplyLanguage.CurrentCode)
            .Select(voice => new Choice(voice.Id, voice.Name, RegionName(voice.Language)))
            .ToList();

    /// <summary>The voice answers are spoken in: the one picked, if still installed, else the best for the language.</summary>
    public SystemVoiceInfo? CurrentVoice(OpenClickyShellSettings? settings = null, string? languageCode = null) =>
        Voices.CurrentVoice(languageCode ?? ReplyLanguage.CurrentCode, (settings ?? OpenClickyConfiguration.Settings).MacVoiceIdentifier);

    /// <summary>How fast this PC speaks: a little below normal, as the Mac slows its voices (the platform's default rate).</summary>
    public double SpeakingRate => Voices.DefaultSpeakingRate;

    /// <summary>Whether answers are spoken by this PC (shell.json `speechVoice` = "mac").</summary>
    public static bool IsChosen(OpenClickyShellSettings? settings = null) =>
        (settings ?? OpenClickyConfiguration.Settings).SpeechVoice == "mac";

    /// <summary><paramref name="text"/> spoken by the current voice, as WAV bytes the player can play.</summary>
    public Task<byte[]> AudioAsync(string text, OpenClickyShellSettings? settings = null, string? languageCode = null, CancellationToken cancellationToken = default) =>
        Voices.RenderWavAsync(text, languageCode ?? ReplyLanguage.CurrentCode,
            (settings ?? OpenClickyConfiguration.Settings).MacVoiceIdentifier, SpeakingRate, cancellationToken);

    /// <summary>"en-GB" → "United Kingdom" (in this PC's display language); null without a region.</summary>
    public static string? RegionName(string languageTag)
    {
        var parts = languageTag.Split('-', '_');
        if (parts.Length < 2) return null;
        try
        {
            return new System.Globalization.RegionInfo(parts[^1]).DisplayName;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
