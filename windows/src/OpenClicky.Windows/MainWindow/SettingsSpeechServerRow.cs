// Settings → Clicky → Your models → Speech server: the person's own speech server (Ollama, Speaches, LocalAI…), turned
// on by its address and connected by listing the models it serves; then a model that hears questions and one that speaks
// answers, each off until picked, and a Try button that speaks a line through it and hears it back.

using System.Diagnostics;
using System.Windows;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Settings;
using OpenClicky.Core.Speech;
using WpfUi = Wpf.Ui.Controls;

namespace OpenClicky.Windows.AppWindow;

internal sealed partial class SettingsRowFactory
{
    /// <summary>What the speech server rows remember between redraws: the models it serves and what the last step said.</summary>
    private sealed class SpeechServerState
    {
        public IReadOnlyList<string>? Models;
        public string? Status;
        public bool IsBusy;
    }

    private readonly SpeechServerState speechServer = new();
    private Platform.AudioPlayer? speechServerPlayer;

    /// <summary>The line the Try button says (and hears back).</summary>
    private const string SpeechServerTrySentence = "Hello! This is your speech server speaking.";

    private LiveContent SpeechServerRow(SettingsItem item, string query)
    {
        LiveContent? row = null;
        row = new LiveContent(() => Ui.Card(SpeechServerContent(item, query, () => row?.Rebuild())), Changes.Shell);
        return row;
    }

    private UIElement SpeechServerContent(SettingsItem item, string query, Action redraw)
    {
        var shell = OpenClickyConfiguration.Settings;
        var server = SpeechServer.From(shell);
        var detail = speechServer.Status
            ?? (server is null ? item.Detail : $"On at {server.BaseUrl}.");

        var urlField = new WpfUi.TextBox
        {
            Width = 175, Text = server?.BaseUrl ?? "", PlaceholderText = "http://localhost:11434", VerticalAlignment = VerticalAlignment.Center,
        };
        Ui.AutomationProperties(urlField, "Speech server address");
        var keyField = new WpfUi.PasswordBox { Width = 135, PlaceholderText = "key (optional)", Password = shell.SpeechServerKey ?? "", VerticalAlignment = VerticalAlignment.Center };
        Ui.AutomationProperties(keyField, "Speech server key");

        async void Connect()
        {
            var typed = urlField.Text.Trim().Length > 0 ? urlField.Text.Trim() : "http://localhost:11434";
            if (ProviderCredentials.BaseUrl(typed) is not { } baseUrl)
            {
                speechServer.Status = "That isn't an http:// or https:// address.";
                redraw();
                return;
            }
            var key = keyField.Password.Trim();
            speechServer.IsBusy = true;
            speechServer.Status = $"Connecting to {baseUrl}…";
            redraw();
            try
            {
                var served = await LocalModels.ListAsync(baseUrl, key);
                speechServer.Models = served;
                OpenClickyConfiguration.Update(saved =>
                {
                    saved.SpeechServerUrl = baseUrl;
                    saved.SpeechServerKey = key.Length == 0 ? null : key;
                });
                ShellSettingsRevision.NoteChanged();
                var hears = served.Count(SpeechServer.LooksLikeItHears);
                var speaks = served.Count(SpeechServer.LooksLikeItSpeaks);
                speechServer.Status = $"Connected: {served.Count} model{(served.Count == 1 ? "" : "s")} ({hears} look like they hear, {speaks} like they speak). Pick them below.";
            }
            catch (LocalModelsException failure)
            {
                speechServer.Status = Capitalized(failure.Message);
            }
            // From a click (async void): anything else, saving shell.json included, is shown on the row instead of ending the app.
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                AppLog.Write($"settings: connecting the speech server failed ({failure.GetType().Name}: {failure.Message})");
                speechServer.Status = $"Couldn't turn it on: {failure.Message}";
            }
            finally
            {
                speechServer.IsBusy = false;
                redraw();
            }
        }

        var connectButton = Ui.Button(speechServer.IsBusy ? "Working…" : server is null ? "Turn on" : "Refresh", Connect,
            server is null ? WpfUi.ControlAppearance.Primary : WpfUi.ControlAppearance.Secondary, isEnabled: !speechServer.IsBusy,
            toolTip: "Save the address and list the models it serves");
        urlField.KeyDown += (_, arguments) => { if (arguments.Key == System.Windows.Input.Key.Enter) Connect(); };
        var offButton = server is null ? null : Ui.Button("Turn off", () =>
        {
            OpenClickyConfiguration.Update(saved =>
            {
                saved.SpeechServerUrl = null;
                // Answers go back to the voice they had before.
                if (saved.SpeechVoice == SpeechVoiceChoice.Server.RawValue()) saved.SpeechVoice = null;
            });
            ShellSettingsRevision.NoteChanged();
            speechServer.Models = null;
            speechServer.Status = null;
            redraw();
        });

        var stack = new System.Windows.Controls.StackPanel();
        stack.Children.Add(Ui.Row(item.Title, detail, query, Ui.HStack(8, urlField, keyField, connectButton, offButton)));
        if (server is null) return stack;

        // The models to pick from: those listed this session, else just the ones already picked (Refresh lists them all).
        var known = speechServer.Models ?? [.. new[] { server.HearingModel, server.VoiceModel }.OfType<string>().Distinct()];
        IEnumerable<(string Id, string Title)> Options(bool forHearing, string? picked)
        {
            var models = SpeechServer.Ordered(known, forHearing).ToList();
            if (picked is not null && !models.Contains(picked)) models.Insert(0, picked);
            return [("", "Off"), .. models.Select(model => (model, model))];
        }

        var hearingPicker = Ui.Picker(Options(forHearing: true, server.HearingModel), server.HearingModel ?? "", choice =>
        {
            OpenClickyConfiguration.Update(saved => saved.SpeechServerHearingModel = choice.Length == 0 ? null : choice);
            ShellSettingsRevision.NoteChanged();
        }, 230);
        Ui.AutomationProperties(hearingPicker, "Speech server model that hears");
        stack.Children.Add(Spaced(Ui.Row("Hears your questions",
            "With Fast voice off, push-to-talk goes to this model, in any language it hears (Whisper, Gemma 4). Dictation can use it too: pick it under Dictation → Who hears you.",
            query, hearingPicker)));

        var voiceField = new WpfUi.TextBox
        {
            Width = 120, Text = server.Voice ?? "", PlaceholderText = "voice (optional)", VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "The server's voice name: af_heart for Kokoro, Zoey for Phonon. Empty uses the server's default.",
        };
        Ui.AutomationProperties(voiceField, "Speech server voice");
        voiceField.LostFocus += (_, _) =>
        {
            var voice = voiceField.Text.Trim();
            if (voice == (OpenClickyConfiguration.Settings.SpeechServerVoice ?? "")) return;
            OpenClickyConfiguration.Update(saved => saved.SpeechServerVoice = voice.Length == 0 ? null : voice);
            ShellSettingsRevision.NoteChanged();
        };
        var voicePicker = Ui.Picker(Options(forHearing: false, server.VoiceModel), server.VoiceModel ?? "", choice =>
        {
            OpenClickyConfiguration.Update(saved =>
            {
                saved.SpeechServerVoiceModel = choice.Length == 0 ? null : choice;
                // Picking a model is picking the voice; Off hands answers back to Automatic.
                if (choice.Length > 0) saved.SpeechVoice = SpeechVoiceChoice.Server.RawValue();
                else if (saved.SpeechVoice == SpeechVoiceChoice.Server.RawValue()) saved.SpeechVoice = null;
            });
            ShellSettingsRevision.NoteChanged();
        }, 230);
        Ui.AutomationProperties(voicePicker, "Speech server model that speaks");
        stack.Children.Add(Spaced(Ui.Row("Speaks answers",
            "With Fast voice off, answers are spoken by this model (Kokoro, Piper, Phonon, Chatterbox). It becomes the voice under Talking → Voice.",
            query, Ui.HStack(8, voiceField, voicePicker))));

        async void Try()
        {
            if (SpeechServer.From(OpenClickyConfiguration.Settings) is not { } saved) return;
            var typedVoice = voiceField.Text.Trim();
            var current = saved with { Voice = typedVoice.Length == 0 ? null : typedVoice };
            speechServer.IsBusy = true;
            speechServer.Status = "Trying it…";
            redraw();
            var said = new List<string>();
            try
            {
                byte[]? audio = null;
                if (current.VoiceModel is not null)
                {
                    var clock = Stopwatch.StartNew();
                    audio = await SpeechServer.SpeakAsync(current, SpeechServerTrySentence);
                    said.Add($"{current.VoiceModel} spoke in {clock.Elapsed.TotalSeconds:0.0} s");
                    speechServerPlayer ??= new Platform.AudioPlayer();
                    await speechServerPlayer.PlayToCompletionAsync(audio);
                }
                if (current.HearingModel is not null)
                {
                    // It hears what the voice just said; with no voice model there is nothing to hear yet.
                    var clock = Stopwatch.StartNew();
                    var heard = audio is null
                        ? null
                        : await SpeechServer.TranscribeAsync(current, audio, "en", prompt: null);
                    said.Add(heard is null
                        ? $"{current.HearingModel} is picked to hear (pick a voice model too, and Try hears what it says)"
                        : $"{current.HearingModel} heard “{heard}” in {clock.Elapsed.TotalSeconds:0.0} s");
                }
                speechServer.Status = said.Count == 0 ? "Pick a model that hears or speaks first." : string.Join("; ", said) + ".";
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                AppLog.Write($"settings: trying the speech server failed ({failure.GetType().Name}: {failure.Message})");
                speechServer.Status = (said.Count > 0 ? string.Join("; ", said) + ". Then: " : "") + failure.Message;
            }
            finally
            {
                speechServer.IsBusy = false;
                redraw();
            }
        }

        stack.Children.Add(Spaced(Ui.Row("Try it", "Speaks a line through the server and hears it back, with how long each took.", query,
            Ui.Button(speechServer.IsBusy ? "Working…" : "Try", Try, isEnabled: !speechServer.IsBusy && (server.HearingModel ?? server.VoiceModel) is not null))));
        return stack;
    }

    private static UIElement Spaced(FrameworkElement row)
    {
        row.Margin = new Thickness(0, 12, 0, 0);
        return row;
    }
}
