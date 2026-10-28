// The real IDictationProviderFactory (Core/Dictation's seam): the provider each dictation engine is
// built as, the Mac's DictationEngineResolver.makeProvider. The offline engine needs the Platform's
// Windows speech recogniser, so the factory is made with it at startup.

using OpenClicky.Core.Dictation;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Speech;

public sealed class DictationProviderFactory(IOfflineSpeechRecognizer offlineRecognizer) : IDictationProviderFactory
{
    /// <summary>Windows speech recognition (continuous dictation), pinned to the dictation language when one is chosen.</summary>
    public IBuddyTranscriptionProvider MakeOffline(string? preferredLocale) =>
        new WindowsSpeechTranscriptionProvider(offlineRecognizer, () => preferredLocale);

    /// <summary>The built-in Moonshine, whose unavailable reason is the resolver's (in this build, and English or auto-detect).</summary>
    public IBuddyTranscriptionProvider MakeMoonshine(Func<string?> bareLanguageCode) =>
        new MoonshineTranscriptionProvider(() => DictationEngineResolver.MoonshineUnavailableReason(MoonshineFacts.IsInstalled, bareLanguageCode()));

    /// <summary>Parakeet, whose unavailable reason is the resolver's (downloaded, and a language it hears).</summary>
    public IBuddyTranscriptionProvider MakeParakeet(Func<string?> bareLanguageCode) =>
        new ParakeetTranscriptionProvider(() => DictationEngineResolver.ParakeetUnavailableReason(ParakeetFacts.IsInstalled, bareLanguageCode()));

    public IBuddyTranscriptionProvider MakeSarvam(Func<DictationLanguage> language) =>
        new SarvamTranscriptionProvider(() => language().Code, unavailableReason: DictationEngineResolver.SarvamUnavailableReason);

    /// <summary>OpenAI's transcription: on the person's own OpenAI key, else the backend's /agent/transcribe (the router decides).</summary>
    public IBuddyTranscriptionProvider MakeOpenClicky(Func<string?> bareLanguageCode) => new OpenAIAudioTranscriptionProvider(bareLanguageCode);

    public IBuddyTranscriptionProvider MakeAssemblyAI(Func<string?> bareLanguageCode) => new AssemblyAIStreamingTranscriptionProvider(bareLanguageCode);
}
