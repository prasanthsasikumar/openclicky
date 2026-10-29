// Ports Dictation/UI/SettingsClickyPage.swift: Settings → Clicky (who answers and the Claude model,
// the pointer, agent mode and its workspace) and Settings → Voice (the language Clicky speaks and
// listens in, who speaks the answers with Kokoro's and this PC's own rows, fast voice, hands-free
// and who runs them). The voice picker's rules come from ISettingsServices (SpeechVoiceChoice); this PC's
// voice is Windows' own (ISystemVoice), the Mac voice's twin.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Settings;

namespace OpenClicky.Windows.AppWindow;

internal sealed partial class SettingsRowFactory
{
    private UIElement? ClickyRow(SettingsItem item, string query)
    {
        var companion = services.Companion;
        switch (item.Id)
        {
            case "clicky.answerSource":
                return new LiveContent(() => Ui.Card(AnswerSourceRow(item, query)), Changes.Shell);
            case "clicky.model":
                return new LiveContent(() => Ui.Card(Ui.Row(item.Title, item.Detail, query,
                    Ui.Picker(new[] { ("claude-sonnet-4-6", "Sonnet"), ("claude-opus-4-6", "Opus") }, companion.SelectedModel, companion.SetSelectedModel, 130))),
                    Changes.Of(companion, nameof(ICompanionSettings.SelectedModel)));
            case "clicky.showPointer":
                return CompanionToggle(item, query, () => companion.IsClickyCursorEnabled, companion.SetClickyCursorEnabled, nameof(ICompanionSettings.IsClickyCursorEnabled));
            case "clicky.pointerPresence":
                return new LiveContent(() => Ui.Card(PointerPresenceOptions(query)),
                    Changes.Of(companion, nameof(ICompanionSettings.PointerPresence), nameof(ICompanionSettings.IsClickyCursorEnabled)));

            case "clicky.agentMode":
                return CompanionToggle(item, query, () => companion.IsAgentModeEnabled, companion.SetAgentModeEnabled, nameof(ICompanionSettings.IsAgentModeEnabled));
            case "clicky.workspace":
                return new LiveContent(() => Ui.Card(Ui.Row(item.Title, SettingsCatalog.DisplayPath(OpenClickyConfiguration.WorkspacePath), query,
                    Ui.HStack(8, Ui.Button("Show in Explorer", () =>
                    {
                        Directory.CreateDirectory(OpenClickyConfiguration.WorkspacePath);
                        Ui.RevealInExplorer(OpenClickyConfiguration.WorkspacePath);
                    }), Ui.Button("Change…", ChooseWorkspaceFolder)))), Changes.Shell);
            default:
                return null;
        }
    }

    private LiveContent CompanionToggle(SettingsItem item, string query, Func<bool> read, Action<bool> write, string propertyName) =>
        new(() => Ui.Card(Ui.Row(item.Title, item.Detail, query, Ui.Toggle(read(), write))), Changes.Of(services.Companion, propertyName));

    /// <summary>Who answers questions: automatic, or one source that is set up. Shows who is answering now.</summary>
    private static Grid AnswerSourceRow(SettingsItem item, string query)
    {
        var credentials = ProviderCredentials.Current();
        var available = credentials.AnswerSourcesInOrder;
        var picked = AnswerSources.FromRawValue(OpenClickyConfiguration.Settings.AnswerSource);
        var options = new List<(string Id, string Title)> { ("auto", "Automatic") };
        options.AddRange(available.Select(source => (source.Id(), source.DisplayName())));
        // A pick that is no longer set up stays visible, so the menu does not jump.
        if (picked is { } stale && !available.Contains(stale)) options.Add((stale.Id(), $"{stale.DisplayName()} (not set up)"));
        var picker = Ui.Picker(options, picked?.Id() ?? "auto", choice =>
        {
            OpenClickyConfiguration.Update(shell => shell.AnswerSource = choice == "auto" ? null : choice);
            ShellSettingsRevision.NoteChanged();
        }, 220);
        return Ui.Row(item.Title, $"Now: {AccountRows.NowAnswering(credentials)}", query, picker);
    }

    private static string PresenceTitle(PointerPresence presence) => presence switch
    {
        PointerPresence.Always => "Always",
        PointerPresence.WhileMoving => "While the mouse moves",
        _ => "After a shake",
    };

    private static string PresenceDetail(PointerPresence presence) => presence switch
    {
        PointerPresence.Always => "Follows the mouse everywhere.",
        PointerPresence.WhileMoving => "Fades a few seconds after the mouse rests.",
        _ => "Hidden until you shake the mouse.",
    };

    /// <summary>Always / while the mouse moves / after a shake, as radio options; dimmed while the pointer is off.</summary>
    private StackPanel PointerPresenceOptions(string query)
    {
        var companion = services.Companion;
        var stack = new StackPanel { IsEnabled = companion.IsClickyCursorEnabled, Opacity = companion.IsClickyCursorEnabled ? 1 : 0.45 };
        foreach (var option in PointerPresenceExtensions.AllCases)
        {
            var radio = Ui.RadioOption(companion.PointerPresence == option, PresenceTitle(option), PresenceDetail(option), query, () => companion.SetPointerPresence(option));
            radio.Margin = new Thickness(0, stack.Children.Count == 0 ? 0 : 10, 0, 0);
            stack.Children.Add(radio);
        }
        return stack;
    }

    private void ChooseWorkspaceFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the agents' workspace",
            InitialDirectory = Directory.Exists(OpenClickyConfiguration.WorkspacePath) ? OpenClickyConfiguration.WorkspacePath : OpenClickyPaths.HomeDirectory,
        };
        if (dialog.ShowDialog(actions.OwnerWindow) != true) return;
        OpenClickyConfiguration.Update(shell => shell.Workspace = dialog.FolderName);
        ShellSettingsRevision.NoteChanged();
    }

    // MARK: - Voice

    private UIElement? VoiceRow(SettingsItem item, string query)
    {
        var companion = services.Companion;
        switch (item.Id)
        {
            case "clicky.language":
                return new LiveContent(() =>
                {
                    var currentCode = ReplyLanguage.CurrentCode;
                    return Ui.Card(Ui.Row(item.Title, "Clicky speaks and listens in this. Dictation has its own, on the Dictation page.", query,
                        Ui.Picker(ReplyLanguage.ChoicesFor(Settings.Region, currentCode).Select(choice => (choice.Code, choice.Name)), currentCode, code =>
                        {
                            OpenClickyConfiguration.Update(shell => shell.Language = code);
                            ShellSettingsRevision.NoteChanged();
                        }, 150)));
                }, Changes.Shell, Changes.Of(Settings, nameof(Core.Dictation.DictationSettings.Region)));
            case "clicky.speechVoice":
                return new LiveContent(() => SpeechVoiceRows(item, query), Changes.Shell, Changes.Of(Settings, nameof(Core.Dictation.DictationSettings.Region)),
                    Changes.Of(services.Services.Kokoro));
            case "clicky.fastVoice":
                return CompanionToggle(item, query, () => companion.IsRealtimeVoiceEnabled, companion.SetRealtimeVoiceEnabled, nameof(ICompanionSettings.IsRealtimeVoiceEnabled));
            case "clicky.alwaysListening":
                return CompanionToggle(item, query, () => companion.IsAlwaysListening, companion.SetAlwaysListening, nameof(ICompanionSettings.IsAlwaysListening));
            case "clicky.realtimeProvider":
                return new LiveContent(() => Ui.Card(Ui.Row(item.Title, item.Detail, query,
                    Ui.Picker(SettingsCatalog.RealtimeProviderChoices, SettingsCatalog.RealtimeProviderChoice(OpenClickyConfiguration.Settings.RealtimeProvider), choice =>
                    {
                        OpenClickyConfiguration.Update(shell => shell.RealtimeProvider = choice == "auto" ? null : choice);
                        ShellSettingsRevision.NoteChanged();
                        companion.RealtimeProviderChanged();
                    }, 170))), Changes.Shell);
            case "clicky.fastVoiceNeedsKey":
                return Ui.Card(Ui.Row(item.Title, item.Detail, query, Ui.Button("Add Key…", () => actions.ShowSettings(DictationSettingsPage.Keys))));
            default:
                return null;
        }
    }

    /// <summary>
    /// Who speaks the answers: Automatic, every voice there is a key or an account for, Kokoro and
    /// this PC; the line under it names who speaks now. Kokoro and this PC's voice each get their own
    /// rows under it, and "More voices" points at Keys & Plans.
    /// </summary>
    private StackPanel SpeechVoiceRows(SettingsItem item, string query)
    {
        var voices = services.Services;
        var picker = voices.VoicePicker(Settings.Region);
        var stack = new StackPanel();
        stack.Children.Add(Ui.Card(Ui.Row(item.Title, $"Now: {picker.NowSpeakingSentence}", query,
            Ui.Picker(picker.Offered.Select(option => (option.Id, option.Title)), picker.PickedId, voiceId =>
            {
                voices.PickVoice(voiceId);
                ShellSettingsRevision.NoteChanged();
            }, 200))));
        if (picker.ShowsKokoroRows) foreach (var row in KokoroRows()) stack.Children.Add(row);
        if (picker.ShowsSystemVoiceRow) foreach (var row in SystemVoiceRows()) stack.Children.Add(row);
        if (picker.HasMoreVoices)
        {
            var vendors = Settings.Region.OffersIndianOptions()
                ? "OpenAI, ElevenLabs, Gemini, Google Cloud, Amazon Polly or Sarvam"
                : "OpenAI, ElevenLabs, Gemini, Google Cloud or Amazon Polly";
            stack.Children.Add(Ui.Card(Ui.Row("More voices", $"Add an {vendors} key under Keys & Plans, and its voice shows up here.", query,
                Ui.Button("Add a Key…", () => actions.ShowSettings(DictationSettingsPage.Keys)))));
        }
        return stack;
    }

    /// <summary>Kokoro's rows: the download (progress, cancel, retry), then which speaker, a preview, and removing the download.</summary>
    private IEnumerable<UIElement> KokoroRows()
    {
        var kokoro = services.Services.Kokoro;
        var state = kokoro.State;
        switch (state.Kind)
        {
            case ModelDownloadState.Stage.NotInstalled:
                yield return Ui.Card(Ui.Row("Kokoro", $"An open voice model that runs on this PC, free and offline. {kokoro.DownloadSizeText} to download.", "",
                    Ui.Button("Download", kokoro.Download)));
                break;
            case ModelDownloadState.Stage.Downloading:
                yield return Ui.Card(Ui.Row("Downloading Kokoro", $"{(int)Math.Round(state.Fraction * 100)}% of {kokoro.DownloadSizeText}. Answers keep the current voice until it’s ready.", "",
                    Ui.HStack(8, new ProgressBar { Width = 120, Maximum = 1, Value = state.Fraction }, Ui.Button("Cancel", kokoro.CancelDownload))));
                break;
            case ModelDownloadState.Stage.Unpacking:
                yield return Ui.Card(Ui.Row("Unpacking Kokoro", "Almost there.", "", new Wpf.Ui.Controls.ProgressRing { IsIndeterminate = true, Width = 18, Height = 18 }));
                break;
            case ModelDownloadState.Stage.Failed:
                yield return Ui.Card(Ui.Row("Kokoro", state.Message, "", Ui.Button("Try Again", kokoro.Download), SettingsLook.Danger));
                break;
            default:
                var language = ReplyLanguage.CurrentCode;
                var speakers = services.Services.KokoroSpeakers(language);
                var current = services.Services.CurrentKokoroSpeaker(language);
                UIElement speakerControls = Ui.HStack(8,
                    speakers.Count > 0
                        ? Ui.Picker(speakers.Select(speaker => (speaker.Id, speaker.Title)), current ?? "", speakerId =>
                        {
                            if (int.TryParse(speakerId, out var speakerNumber)) OpenClickyConfiguration.Update(shell => shell.KokoroSpeaker = speakerNumber);
                            ShellSettingsRevision.NoteChanged();
                            _ = services.Services.PreviewVoiceAsync("kokoro");
                        }, 180, maxWidth: 220)
                        : null,
                    Ui.Button("Preview", () => _ = services.Services.PreviewVoiceAsync("kokoro")));
                yield return Ui.Card(Ui.Row("Kokoro voice", "The first answer after launch takes a few seconds while the model loads.", "", speakerControls));
                yield return Ui.Card(Ui.Row("Kokoro download", "About 400 MB in %LOCALAPPDATA%\\OpenClicky.", "", Ui.Button("Remove", () =>
                {
                    kokoro.Remove();
                    if (OpenClickyConfiguration.Settings.SpeechVoice == "kokoro")
                    {
                        OpenClickyConfiguration.Update(shell => shell.SpeechVoice = null);
                        ShellSettingsRevision.NoteChanged();
                    }
                })));
                break;
        }
    }

    /// <summary>This PC's voice: which one, a preview, and where Windows downloads the better ones.</summary>
    private IEnumerable<UIElement> SystemVoiceRows()
    {
        var systemVoice = services.SystemVoice;
        var language = ReplyLanguage.CurrentCode;
        IReadOnlyList<Core.Platform.SystemVoiceInfo> installed;
        try { installed = systemVoice.Voices(language); }
        catch (Exception error)
        {
            AppLog.Write($"voice: couldn't list this PC's voices: {error.Message}");
            installed = [];
        }
        var pickedId = OpenClickyConfiguration.Settings.MacVoiceIdentifier;
        var current = installed.Count == 0 ? null : systemVoice.CurrentVoice(language, pickedId);
        var picker = Ui.Picker(installed.Select(voice => (voice.Id, voice.Name)), current?.Id ?? "", voiceId =>
        {
            OpenClickyConfiguration.Update(shell => shell.MacVoiceIdentifier = voiceId);
            ShellSettingsRevision.NoteChanged();
            _ = PreviewSystemVoiceAsync(voiceId);
        }, 200, maxWidth: 240);
        yield return Ui.Card(Ui.Row("This PC’s voice", installed.Count == 0 ? "No voice for this language is installed yet." : "Picking one says a line in it.", "",
            Ui.HStack(8, picker, Ui.Button("Preview", () => _ = PreviewSystemVoiceAsync(OpenClickyConfiguration.Settings.MacVoiceIdentifier)))));
        yield return Ui.Card(Ui.Row("Better voices",
            "Windows downloads more natural voices once, then they run offline. Get Voices opens Settings › Time & language › Speech.", "",
            Ui.Button("Get Voices…", () => Ui.OpenExternal("ms-settings:speech"))));
    }

    private async Task PreviewSystemVoiceAsync(string? voiceId)
    {
        try
        {
            var wav = await services.SystemVoice.RenderWavAsync("hi, i'm clicky. this is how i sound.", ReplyLanguage.CurrentCode, voiceId);
            await services.AudioPlayer.PlayToCompletionAsync(wav);
        }
        catch (Exception error)
        {
            AppLog.Write($"voice: preview failed: {error.Message}");
        }
    }
}
