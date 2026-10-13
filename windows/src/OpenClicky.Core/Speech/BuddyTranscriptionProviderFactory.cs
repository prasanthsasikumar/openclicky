// Ports BuddyTranscriptionProviderFactory (BuddyTranscriptionProvider.swift): which backend the
// talk lane transcribes with. shell.json's `transcriptionProvider` wins; with an account or a key
// but no preference, upload transcription through the router ("openai"), since that needs nothing
// more. The Info.plist fallback of the Mac has no Windows twin (there is no bundle setting).

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Speech;

public static class BuddyTranscriptionProviderFactory
{
    public enum PreferredProvider
    {
        AssemblyAI,
        OpenAI,
        /// <summary>"apple" in shell.json: the offline engine, which on Windows is the Windows speech recogniser.</summary>
        OfflineSpeech,
    }

    /// <summary>shell.json's raw value ("assemblyai", "openai", "apple"; "windows" and "offline" also name the offline engine).</summary>
    public static PreferredProvider? ParsePreferredProvider(string? rawValue) => rawValue?.Trim().ToLowerInvariant() switch
    {
        "assemblyai" => PreferredProvider.AssemblyAI,
        "openai" => PreferredProvider.OpenAI,
        "apple" or "windows" or "offline" => PreferredProvider.OfflineSpeech,
        _ => null,
    };

    public static IBuddyTranscriptionProvider MakeDefaultProvider(IOfflineSpeechRecognizer offlineRecognizer)
    {
        var provider = ResolveProvider(
            OpenClickyConfiguration.Settings.TranscriptionProvider,
            OpenClickyConfiguration.IsConfigured,
            new AssemblyAIStreamingTranscriptionProvider(),
            new OpenAIAudioTranscriptionProvider(),
            () => new WindowsSpeechTranscriptionProvider(offlineRecognizer));
        AppLog.Write($"transcription: using {provider.DisplayName}");
        return provider;
    }

    /// <summary>
    /// The preferred provider when it is configured; a preferred cloud provider that is not falls
    /// back to the other cloud one, then to the offline engine. No preference: AssemblyAI, then
    /// upload, then offline.
    /// </summary>
    public static IBuddyTranscriptionProvider ResolveProvider(
        string? storedPreference,
        bool isConfigured,
        IBuddyTranscriptionProvider assemblyAIProvider,
        IBuddyTranscriptionProvider openAIProvider,
        Func<IBuddyTranscriptionProvider> makeOfflineProvider)
    {
        var preferredRaw = storedPreference?.ToLowerInvariant() ?? (isConfigured ? "openai" : null);
        var preferred = ParsePreferredProvider(preferredRaw);

        if (preferred == PreferredProvider.OfflineSpeech) return makeOfflineProvider();

        if (preferred == PreferredProvider.AssemblyAI)
        {
            if (assemblyAIProvider.IsConfigured) return assemblyAIProvider;
            AppLog.Write("transcription: AssemblyAI preferred but not configured, falling back");
            if (openAIProvider.IsConfigured) return openAIProvider;
            return makeOfflineProvider();
        }

        if (preferred == PreferredProvider.OpenAI)
        {
            if (openAIProvider.IsConfigured) return openAIProvider;
            AppLog.Write("transcription: OpenAI preferred but not configured, falling back");
            if (assemblyAIProvider.IsConfigured) return assemblyAIProvider;
            return makeOfflineProvider();
        }

        if (assemblyAIProvider.IsConfigured) return assemblyAIProvider;
        if (openAIProvider.IsConfigured) return openAIProvider;
        return makeOfflineProvider();
    }
}
