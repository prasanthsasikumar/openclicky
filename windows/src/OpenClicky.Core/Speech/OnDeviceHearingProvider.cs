// The ask lane's "hear it on this PC" engine. On the Mac that is Apple Speech, which every Mac has;
// Windows' recogniser is missing on many PCs (no dictation language pack, or a VM: 0x800455A0) and
// asks for Windows' online speech recognition, so Parakeet answers when it is downloaded and hears
// the language, else the built-in Moonshine when it hears it (English), and Windows speech only
// otherwise. Decided per take, because Parakeet usually finishes downloading after the provider is built.

using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Speech;

public sealed class OnDeviceHearingProvider(IOfflineSpeechRecognizer offlineRecognizer) : IBuddyTranscriptionProvider
{
    private readonly ParakeetTranscriptionProvider parakeetProvider = new();
    private readonly MoonshineTranscriptionProvider moonshineProvider = new();
    private readonly WindowsSpeechTranscriptionProvider windowsSpeechProvider = new(offlineRecognizer);

    private IBuddyTranscriptionProvider Current =>
        parakeetProvider.IsConfigured ? parakeetProvider
        : moonshineProvider.IsConfigured ? moonshineProvider
        : windowsSpeechProvider;

    public string DisplayName => Current.DisplayName;
    public bool RequiresSpeechRecognitionPermission => Current.RequiresSpeechRecognitionPermission;
    public bool IsConfigured => Current.IsConfigured;
    public string? UnavailableExplanation => Current.UnavailableExplanation;

    public Task<IBuddyStreamingTranscriptionSession> StartStreamingSessionAsync(
        IReadOnlyList<string> keyterms,
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError,
        CancellationToken cancellationToken = default) =>
        Current.StartStreamingSessionAsync(keyterms, onTranscriptUpdate, onFinalTranscriptReady, onError, cancellationToken);
}
