namespace OpenClicky.Core.Platform;

public sealed record OfflineSpeechOptions
{
    /// <summary>BCP-47 tag ("en-US", "hi-IN"); null = the reply language, then en-US.</summary>
    public string? LanguageTag { get; init; }
    /// <summary>Further tags to try in order when the first is not installed.</summary>
    public IReadOnlyList<string> FallbackLanguageTags { get; init; } = [];
}

public enum OfflineSpeechFailure
{
    LanguageNotInstalled,
    /// <summary>Windows dictation needs "Online speech recognition" (Settings → Privacy → Speech) switched on.</summary>
    SpeechPrivacyPolicyNotAccepted,
    MicrophoneUnavailable,
    Other,
}

public sealed class OfflineSpeechException(OfflineSpeechFailure failure, string userFacingSentence, Exception? inner = null)
    : Exception(userFacingSentence, inner)
{
    public OfflineSpeechFailure Failure { get; } = failure;
    /// <summary>What to show the person, lower case as the Mac's sentences.</summary>
    public string UserFacingSentence { get; } = userFacingSentence;
}

/// <summary>A running take of the Windows speech recogniser (continuous dictation).</summary>
public interface IOfflineSpeechSession : IAsyncDisposable
{
    /// <summary>The whole take so far (finished stretches + the current hypothesis), on every update.</summary>
    event Action<string>? TranscriptUpdated;

    /// <summary>A recognition error mid-take (when nothing was recognised yet it is the take's error).</summary>
    event Action<Exception>? Error;

    /// <summary>The language actually used.</summary>
    string LanguageTag { get; }

    /// <summary>Stops listening and returns the final take text.</summary>
    Task<string> StopAsync();

    /// <summary>Stops without a result.</summary>
    void Cancel();
}

/// <summary>
/// The Windows speech recogniser (Apple Speech on the Mac). Windows.Media.SpeechRecognition only
/// listens to the system default capture device: it cannot be fed our own audio, so a microphone
/// chosen in Settings that is not the default is not honoured by this engine (<see cref="UsesDefaultMicrophoneOnly"/>).
/// </summary>
public interface IOfflineSpeechRecognizer
{
    bool UsesDefaultMicrophoneOnly { get; }

    /// <summary>Installed dictation languages (BCP-47).</summary>
    IReadOnlyList<string> SupportedLanguages();

    /// <summary>Starts a continuous dictation session; throws <see cref="OfflineSpeechException"/> with the sentence to show.</summary>
    Task<IOfflineSpeechSession> StartAsync(OfflineSpeechOptions options, CancellationToken cancellationToken = default);
}

/// <summary>An installed voice (OneCore / WinRT).</summary>
public sealed record SystemVoiceInfo(string Id, string Name, string Language, string Gender);

/// <summary>"This PC" voice for spoken answers (MacVoice): rendered to WAV for the SpeechQueue, not spoken straight to the speakers.</summary>
public interface ISystemVoice
{
    /// <summary>Voices for a language ("en", "en-GB"…; null = all), best first (exact region match, then name).</summary>
    IReadOnlyList<SystemVoiceInfo> Voices(string? languageCode = null);

    /// <summary>The voice picked in Settings if still installed, else the best one for the language, else the system default.</summary>
    SystemVoiceInfo? CurrentVoice(string languageCode, string? pickedVoiceId = null);

    /// <summary>The speaking rate used when none is given (the Mac slows basic voices a little; 1.0 = normal).</summary>
    double DefaultSpeakingRate { get; }

    /// <summary>The text spoken by the current voice as WAV bytes. Throws <see cref="SystemVoiceException"/> when nothing was rendered.</summary>
    Task<byte[]> RenderWavAsync(string text, string languageCode, string? pickedVoiceId = null, double? speakingRate = null, CancellationToken cancellationToken = default);
}

public sealed class SystemVoiceException(string message, Exception? inner = null) : Exception(message, inner)
{
    public const string NothingRenderedSentence = "this pc's voice didn't say anything";
}
