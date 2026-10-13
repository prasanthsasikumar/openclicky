// The window's answers-and-voices seam (ISettingsServices) over the real Core objects: AnswerSourceCheck
// and PlanCLIs for the Test buttons and the plan rows, SpeechVoiceChoice / CloudVoiceSpeaker for the
// voice picker and its preview, KokoroVoice and ParakeetDictation for the download rows. The
// narration of the Settings tour (RecordedVoice) is here too.

using System.ComponentModel;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Settings;

/// <summary>One downloadable model's row over KokoroVoice or ParakeetDictation.</summary>
public sealed class ModelDownloadRow : IModelDownload
{
    private readonly Func<ModelInstallState> readState;
    private readonly Action download;
    private readonly Action cancel;
    private readonly Func<Task> remove;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ModelDownloadRow(INotifyPropertyChanged source, Func<ModelInstallState> readState, string downloadSizeText,
                            Action download, Action cancel, Func<Task> remove, IMainThread? mainThread = null)
    {
        this.readState = readState;
        this.download = download;
        this.cancel = cancel;
        this.remove = remove;
        DownloadSizeText = downloadSizeText;
        source.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName != "State") return;
            void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
            if (mainThread is null || mainThread.IsMainThread) Raise();
            else mainThread.Post(Raise);
        };
    }

    public ModelDownloadState State => readState() switch
    {
        ModelInstallState.DownloadingState downloading => ModelDownloadState.Downloading(downloading.Fraction),
        ModelInstallState.UnpackingState => ModelDownloadState.Unpacking,
        ModelInstallState.InstalledState => ModelDownloadState.Installed,
        ModelInstallState.FailedState failed => ModelDownloadState.Failed(failed.Message),
        _ => ModelDownloadState.NotInstalled,
    };

    public string DownloadSizeText { get; }

    public void Download() => download();

    public void CancelDownload() => cancel();

    public void Remove() => _ = remove();

    public static ModelDownloadRow For(KokoroVoice kokoro, IMainThread? mainThread = null) =>
        new(kokoro, () => kokoro.State, KokoroVoice.DownloadSizeText, () => kokoro.Download(), kokoro.CancelDownload, kokoro.RemoveAsync, mainThread);

    public static ModelDownloadRow For(ParakeetDictation parakeet, IMainThread? mainThread = null) =>
        new(parakeet, () => parakeet.State, ParakeetDictation.DownloadSizeText, () => parakeet.Download(), parakeet.CancelDownload, parakeet.RemoveAsync, mainThread);
}

public sealed class LiveSettingsServices(IModelDownload kokoro, IModelDownload parakeet, IAudioPlayer previewPlayer) : ISettingsServices
{
    public const string PreviewSentence = "hi, i'm clicky. this is how i sound.";

    public IModelDownload Kokoro { get; } = kokoro;

    public IModelDownload Parakeet { get; } = parakeet;

    public async Task<string> TestAnswerSourceAsync(AnswerSource source, CancellationToken cancellationToken = default)
    {
        var outcome = await AnswerSourceCheck.RunAsync(source, ProviderRouter.CurrentCredentials(),
            OpenClickyConfiguration.Settings.AnswerModels ?? new Dictionary<string, string>(), cancellationToken: cancellationToken);
        return outcome.Sentence;
    }

    public async Task<string> TestVoiceKeyAsync(string voiceId, CancellationToken cancellationToken = default)
    {
        try
        {
            await CloudVoiceSpeaker.AudioAsync("ok", SpeechVoiceChoices.FromStored(voiceId), cancellationToken: cancellationToken);
            return "✓ Works. It can speak your answers.";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return error.Message;
        }
    }

    public async Task<PlanCliStatus> PlanStatusAsync(AnswerSource plan, CancellationToken cancellationToken = default)
    {
        var status = plan == AnswerSource.ClaudeCodePlan ? await PlanCLIs.ClaudeCodeStatusAsync() : await PlanCLIs.CodexStatusAsync();
        return status switch
        {
            Answers.PlanCliStatus.Ready => PlanCliStatus.Ready,
            Answers.PlanCliStatus.InstalledSignedOut => PlanCliStatus.InstalledSignedOut,
            _ => PlanCliStatus.NotInstalled,
        };
    }

    public VoicePickerState VoicePicker(AppRegion region)
    {
        var shell = OpenClickyConfiguration.Settings;
        var hasAccount = OpenClickyConfiguration.HasAccountToken;
        var languageCode = ReplyLanguage.CurrentCode;
        var kokoroInstalled = Kokoro.State.Kind == ModelDownloadState.Stage.Installed;
        var picked = SpeechVoiceChoices.FromStored(shell.SpeechVoice);
        var offered = SpeechVoiceChoices.Offered(shell, hasAccount, languageCode);
        var speaking = SpeechVoiceChoices.Resolve(shell, hasAccount, kokoroInstalled, languageCode);
        string sentence;
        if (picked != SpeechVoiceChoice.Automatic && picked != speaking)
        {
            sentence = picked == SpeechVoiceChoice.Kokoro
                ? "this PC’s voice until Kokoro is downloaded."
                : $"this PC’s voice: {picked.Title()} needs its key under Keys & Plans.";
        }
        else
        {
            sentence = speaking switch
            {
                SpeechVoiceChoice.OpenAI => "OpenAI, on your own key.",
                SpeechVoiceChoice.ElevenLabs => "ElevenLabs, on your own key.",
                SpeechVoiceChoice.Google => "Google Cloud, on your own key.",
                SpeechVoiceChoice.Gemini => "Google Gemini, on your own key.",
                SpeechVoiceChoice.Polly => "Amazon Polly, on your own key.",
                SpeechVoiceChoice.Sarvam => "Sarvam, on your own key.",
                SpeechVoiceChoice.Kokoro => "Kokoro, on this PC. Free, offline, nothing counted.",
                SpeechVoiceChoice.Account => "ElevenLabs, through OpenClicky.",
                _ => "this PC’s voice. Free, offline, nothing counted.",
            };
        }
        return new VoicePickerState
        {
            Offered = offered.Select(choice => new SettingsOption(choice.RawValue(), choice.Title())).ToList(),
            PickedId = picked.RawValue(),
            NowSpeakingSentence = sentence,
            ShowsKokoroRows = picked == SpeechVoiceChoice.Kokoro || Kokoro.State.IsBusy,
            ShowsSystemVoiceRow = speaking == SpeechVoiceChoice.Mac || picked == SpeechVoiceChoice.Mac,
            HasMoreVoices = offered.Count < SpeechVoiceChoices.AllCases.Count,
        };
    }

    public void PickVoice(string voiceId)
    {
        var choice = SpeechVoiceChoices.FromStored(voiceId);
        OpenClickyConfiguration.Update(shell => shell.SpeechVoice = choice.StoredValue());
        if (choice == SpeechVoiceChoice.Kokoro && Kokoro.State.Kind != ModelDownloadState.Stage.Installed) Kokoro.Download();
    }

    public async Task PreviewVoiceAsync(string voiceId)
    {
        try
        {
            var choice = SpeechVoiceChoices.FromStored(voiceId);
            if (choice == SpeechVoiceChoice.Automatic) choice = SpeechVoiceChoices.Current();
            var audio = choice == SpeechVoiceChoice.Account
                ? await new AnswerVoicePlayer(previewPlayer, () => $"{OpenClickyConfiguration.BackendBaseUrl}/tts").FetchAudioAsync(PreviewSentence)
                : await CloudVoiceSpeaker.AudioAsync(PreviewSentence, choice);
            await previewPlayer.PlayToCompletionAsync(audio);
        }
        catch (Exception error)
        {
            AppLog.Write($"voice: preview failed ({error.GetType().Name})");
        }
    }

    public IReadOnlyList<SettingsOption> KokoroSpeakers(string languageCode) =>
        KokoroVoice.Speakers.Where(speaker => speaker.LanguageCode == languageCode)
            .Select(speaker => new SettingsOption(speaker.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), speaker.Label)).ToList();

    public string? CurrentKokoroSpeaker(string languageCode) =>
        KokoroVoice.SpeakerFor(languageCode, OpenClickyConfiguration.Settings.KokoroSpeaker)?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The Settings tour's narration: the bundled recordings (RecordedVoice).</summary>
public sealed class RecordedVoiceTourNarrator(RecordedVoice recordedVoice) : ISettingsTourNarrator
{
    public async Task<bool> SayAsync(string line, CancellationToken cancellationToken)
    {
        if (RawValues.FromRawValue<RecordedVoice.Line>(line) is not { } recordedLine) return false;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!recordedVoice.Play(recordedLine, () => finished.TrySetResult())) return false;
        using (cancellationToken.Register(() =>
        {
            recordedVoice.Stop();
            finished.TrySetResult();
        }))
        {
            await finished.Task;
        }
        return true;
    }

    public void Stop() => recordedVoice.Stop();
}
