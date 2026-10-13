using System.Globalization;
using System.IO;
using OpenClicky.Core.Platform;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;
using Windows.Media.SpeechSynthesis;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// The offline engine on Windows: Windows.Media.SpeechRecognition continuous dictation.
/// <para>
/// Two Windows limits the Mac's Apple Speech did not have: (1) the WinRT recogniser always listens
/// to the system default capture device — it takes no audio stream, so OpenClicky's own capture
/// cannot feed it and a microphone chosen in Settings is ignored by this engine; (2) the dictation
/// grammar needs "Online speech recognition" switched on (Settings → Privacy &amp; security → Speech),
/// and needs the language's speech pack installed.
/// </para>
/// </summary>
public sealed class WindowsSpeechRecognizer : IOfflineSpeechRecognizer
{
    private const int PrivacyPolicyNotAcceptedHResult = unchecked((int)0x80045509);

    public bool UsesDefaultMicrophoneOnly => true;

    public IReadOnlyList<string> SupportedLanguages() =>
        SpeechRecognizer.SupportedTopicLanguages.Select(language => language.LanguageTag).ToList();

    public async Task<IOfflineSpeechSession> StartAsync(OfflineSpeechOptions options, CancellationToken cancellationToken = default)
    {
        var requested = options.LanguageTag ?? CultureInfo.CurrentUICulture.Name;
        var candidates = new[] { options.LanguageTag }
            .Concat(options.FallbackLanguageTags)
            .Append(CultureInfo.CurrentUICulture.Name)
            .Append("en-US")
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Cast<string>();
        var supported = SupportedLanguages();
        var languageTag = candidates.Select(tag => BestSupported(tag, supported)).FirstOrDefault(tag => tag is not null)
            ?? throw new OfflineSpeechException(OfflineSpeechFailure.LanguageNotInstalled,
                $"dictation cannot listen in {requested} on this pc. add its speech pack in settings, time & language, speech.");

        SpeechRecognizer recognizer;
        try
        {
            recognizer = new SpeechRecognizer(new Language(languageTag));
        }
        catch (Exception exception) when (exception.HResult == PrivacyPolicyNotAcceptedHResult)
        {
            throw PrivacyFailure(exception);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            throw Unavailable(exception);
        }
        try
        {
            recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
            var compilation = await recognizer.CompileConstraintsAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (compilation.Status != SpeechRecognitionResultStatus.Success)
            {
                throw Failure(compilation.Status, languageTag);
            }
            // A take can be long and quiet in places; Clicky decides when it ends, not the recogniser.
            recognizer.ContinuousRecognitionSession.AutoStopSilenceTimeout = TimeSpan.FromMinutes(10);
            var session = new WindowsSpeechSession(recognizer, languageTag);
            await recognizer.ContinuousRecognitionSession.StartAsync(SpeechContinuousRecognitionMode.Default).AsTask(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch (Exception exception) when (exception.HResult == PrivacyPolicyNotAcceptedHResult)
        {
            recognizer.Dispose();
            throw PrivacyFailure(exception);
        }
        catch (Exception exception) when (exception is not OfflineSpeechException and not OperationCanceledException)
        {
            recognizer.Dispose();
            throw Unavailable(exception);
        }
        catch
        {
            recognizer.Dispose();
            throw;
        }
    }

    private static OfflineSpeechException PrivacyFailure(Exception exception) =>
        new(OfflineSpeechFailure.SpeechPrivacyPolicyNotAccepted,
            "windows dictation needs online speech recognition switched on in settings, privacy & security, speech.", exception);

    /// <summary>With no capture device at all the recogniser fails with an "internal speech error" COMException.</summary>
    private static OfflineSpeechException Unavailable(Exception exception) =>
        HasCaptureDevice()
            ? new OfflineSpeechException(OfflineSpeechFailure.Other, $"dictation is not available on this pc (0x{exception.HResult:X8}).", exception)
            : new OfflineSpeechException(OfflineSpeechFailure.MicrophoneUnavailable, "dictation can't hear the default microphone.", exception);

    private static bool HasCaptureDevice()
    {
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            return enumerator.HasDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Console);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return true;
        }
    }

    private static OfflineSpeechException Failure(SpeechRecognitionResultStatus status, string languageTag) => status switch
    {
        SpeechRecognitionResultStatus.TopicLanguageNotSupported or SpeechRecognitionResultStatus.GrammarLanguageMismatch =>
            new OfflineSpeechException(OfflineSpeechFailure.LanguageNotInstalled,
                $"dictation cannot listen in {languageTag} on this pc. add its speech pack in settings, time & language, speech."),
        SpeechRecognitionResultStatus.MicrophoneUnavailable =>
            new OfflineSpeechException(OfflineSpeechFailure.MicrophoneUnavailable, "dictation can't hear the default microphone."),
        SpeechRecognitionResultStatus.NetworkFailure =>
            new OfflineSpeechException(OfflineSpeechFailure.Other, "dictation lost its connection to windows speech."),
        _ => new OfflineSpeechException(OfflineSpeechFailure.Other, $"dictation is not available on this pc ({status})."),
    };

    /// <summary>The installed tag equal to <paramref name="tag"/>, else the first with the same language ("en" → "en-US").</summary>
    internal static string? BestSupported(string tag, IReadOnlyList<string> supported)
    {
        var exact = supported.FirstOrDefault(candidate => string.Equals(candidate, tag, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var primary = tag.Split('-')[0];
        return supported.FirstOrDefault(candidate => string.Equals(candidate.Split('-')[0], primary, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class WindowsSpeechSession : IOfflineSpeechSession
    {
        private readonly SpeechRecognizer recognizer;
        private readonly object textLock = new();
        private string finishedStretchesText = string.Empty;
        private string latestText = string.Empty;
        private bool stopped;

        public WindowsSpeechSession(SpeechRecognizer recognizer, string languageTag)
        {
            this.recognizer = recognizer;
            LanguageTag = languageTag;
            recognizer.HypothesisGenerated += (_, args) => Update(args.Hypothesis.Text, isFinal: false);
            recognizer.ContinuousRecognitionSession.ResultGenerated += (_, args) =>
            {
                if (args.Result.Status == SpeechRecognitionResultStatus.Success) Update(args.Result.Text, isFinal: true);
            };
            recognizer.ContinuousRecognitionSession.Completed += (_, args) =>
            {
                if (args.Status is SpeechRecognitionResultStatus.Success or SpeechRecognitionResultStatus.UserCanceled) return;
                bool hasText;
                lock (textLock) hasText = latestText.Trim().Length > 0;
                if (!hasText && !stopped) Error?.Invoke(new OfflineSpeechException(OfflineSpeechFailure.Other, $"dictation stopped ({args.Status})."));
            };
        }

        public event Action<string>? TranscriptUpdated;
        public event Action<Exception>? Error;

        public string LanguageTag { get; }

        /// <summary>A result closes a stretch; the next stretch starts from nothing, so the closed ones are kept (as on the Mac).</summary>
        private void Update(string stretchText, bool isFinal)
        {
            string text;
            lock (textLock)
            {
                latestText = Joined(finishedStretchesText, stretchText);
                if (isFinal) finishedStretchesText = latestText;
                text = latestText;
            }
            TranscriptUpdated?.Invoke(text);
        }

        public async Task<string> StopAsync()
        {
            stopped = true;
            try
            {
                await recognizer.ContinuousRecognitionSession.StopAsync().AsTask().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Already stopped (silence timeout or an error).
            }
            lock (textLock) return latestText;
        }

        public void Cancel()
        {
            stopped = true;
            try
            {
                _ = recognizer.ContinuousRecognitionSession.CancelAsync();
            }
            catch (InvalidOperationException)
            {
            }
        }

        public ValueTask DisposeAsync()
        {
            Cancel();
            recognizer.Dispose();
            return ValueTask.CompletedTask;
        }

        /// <summary>SpokenStretches.joined: a full stop goes in when the next stretch starts with a capital and the last did not end a sentence.</summary>
        private static string Joined(string finished, string current)
        {
            var finishedTrimmed = finished.Trim();
            var currentTrimmed = current.Trim();
            if (finishedTrimmed.Length == 0) return currentTrimmed;
            if (currentTrimmed.Length == 0) return finishedTrimmed;
            var finishedEndsASentence = ".!?,;:…".Contains(finishedTrimmed[^1]);
            var currentStartsASentence = char.IsUpper(currentTrimmed[0]);
            var separator = !finishedEndsASentence && currentStartsASentence ? ". " : " ";
            return finishedTrimmed + separator + currentTrimmed;
        }
    }
}

/// <summary>"This PC" voice (MacVoice): OneCore voices through Windows.Media.SpeechSynthesis, rendered to WAV.</summary>
public sealed class WindowsSystemVoice : ISystemVoice
{
    /// <summary>OneCore voices are the "basic" kind the Mac slows to 0.8×; they are clearer than those, so a little less.</summary>
    public double DefaultSpeakingRate => 0.92;

    public IReadOnlyList<SystemVoiceInfo> Voices(string? languageCode = null)
    {
        var voices = SpeechSynthesizer.AllVoices
            .Select(voice => new SystemVoiceInfo(voice.Id, voice.DisplayName, voice.Language, voice.Gender.ToString()));
        if (string.IsNullOrEmpty(languageCode)) return voices.OrderBy(voice => voice.Language).ThenBy(voice => voice.Name).ToList();
        var primary = languageCode.Split('-')[0];
        return voices
            .Where(voice => string.Equals(voice.Language.Split('-')[0], primary, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(voice => string.Equals(voice.Language, languageCode, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(voice => string.Equals(voice.Language, CultureInfo.CurrentCulture.Name, StringComparison.OrdinalIgnoreCase))
            .ThenBy(voice => voice.Name)
            .ToList();
    }

    public SystemVoiceInfo? CurrentVoice(string languageCode, string? pickedVoiceId = null)
    {
        if (!string.IsNullOrEmpty(pickedVoiceId))
        {
            var picked = Voices().FirstOrDefault(voice => voice.Id == pickedVoiceId);
            if (picked is not null) return picked;
        }
        var best = Voices(languageCode).FirstOrDefault();
        if (best is not null) return best;
        var fallback = SpeechSynthesizer.DefaultVoice;
        return fallback is null ? null : new SystemVoiceInfo(fallback.Id, fallback.DisplayName, fallback.Language, fallback.Gender.ToString());
    }

    public async Task<byte[]> RenderWavAsync(string text, string languageCode, string? pickedVoiceId = null, double? speakingRate = null, CancellationToken cancellationToken = default)
    {
        using var synthesizer = new SpeechSynthesizer();
        var voice = CurrentVoice(languageCode, pickedVoiceId);
        if (voice is not null)
        {
            var information = SpeechSynthesizer.AllVoices.FirstOrDefault(candidate => candidate.Id == voice.Id);
            if (information is not null) synthesizer.Voice = information;
        }
        synthesizer.Options.SpeakingRate = Math.Clamp(speakingRate ?? DefaultSpeakingRate, 0.5, 6.0);
        try
        {
            using var stream = await synthesizer.SynthesizeTextToStreamAsync(text).AsTask(cancellationToken).ConfigureAwait(false);
            using var readable = stream.AsStreamForRead();
            using var memory = new MemoryStream();
            await readable.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            // A WAV header alone (44 bytes) is nothing said.
            if (memory.Length <= 64) throw new SystemVoiceException(SystemVoiceException.NothingRenderedSentence);
            return memory.ToArray();
        }
        catch (Exception exception) when (exception is not SystemVoiceException and not OperationCanceledException)
        {
            throw new SystemVoiceException(SystemVoiceException.NothingRenderedSentence, exception);
        }
    }
}
