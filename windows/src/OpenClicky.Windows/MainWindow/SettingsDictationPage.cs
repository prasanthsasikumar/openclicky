// Ports Dictation/UI/SettingsDictationPage.swift and the key rows of SettingsKeyboardPage.swift:
// everything about typing with your voice, in the order someone sets it up. The key you hold (with
// a sketch of where it is), who hears you, in which language, whether AI tidies the words, through
// which microphone, when a quiet take ends, and the tones. Each engine says where the audio goes;
// its key lives under Keys & Plans. (No fn rows and no haptics on Windows.)

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Settings;
using OpenClicky.Core.Shortcuts;
using WpfUi = Wpf.Ui.Controls;

namespace OpenClicky.Windows.AppWindow;

internal sealed partial class SettingsRowFactory
{
    /// <summary>The key picker's label ("Right Ctrl").</summary>
    public static string DictationKeyTitle(DictationHotkey key) => key switch
    {
        DictationHotkey.RightControl => "Right Ctrl",
        DictationHotkey.RightAlt => "Right Alt (AltGr)",
        DictationHotkey.RightShift => "Right Shift",
        _ => "Caps Lock",
    };

    /// <summary>The language the "Pin one" choice comes back to (same key the Mac's Styles page used).</summary>
    private const string LastPinnedLanguageKey = "dictation.lastPinnedLanguage";

    private UIElement? DictationRow(SettingsItem item, string query)
    {
        if (item.Id.StartsWith("dictation.engine.", StringComparison.Ordinal))
        {
            var choice = DictationEngineChoices.FromRawValue(item.Id["dictation.engine.".Length..]) ?? DictationEngineChoice.Offline;
            return EngineOption(choice, query);
        }
        switch (item.Id)
        {
            case "keyboard.dictationKey":
                return LiveSettingsCard(() => Ui.Row(item.Title, SettingsCatalog.DictationKeyDetail(Settings.DictationKey), query,
                    Ui.Picker(DictationHotkeys.AllCases.Select(key => (key, DictationKeyTitle(key))), Settings.DictationKey, key => Settings.DictationKey = key, 170)));
            case "keyboard.where":
                return new LiveContent(() => Ui.Card(KeyboardSketch.WithCaption(Settings.DictationKey, animates: !Settings.ReduceAnimation)),
                    Changes.Of(Settings, nameof(DictationSettings.DictationKey), nameof(DictationSettings.ReduceAnimation)));
            case "dictation.language":
                return LiveSettingsCard(() => DictationLanguageRow(item, query));
            case "dictation.script":
                return LiveSettingsCard(() => Ui.Row(item.Title, "Same words, written two ways. The AI clean-up writes it.", query,
                    Ui.Picker(new[]
                    {
                        (DictationScript.Native, "Native · नमस्ते, आप कैसे हैं?"),
                        (DictationScript.Roman, "Roman · namaste, aap kaise hain?"),
                    }, Settings.Script, script => Settings.Script = script, 220)));
            case "dictation.cleanUp":
                return new LiveContent(() => Ui.Card(Ui.Row(item.Title, DictationCleanUp.Detail(hasModel: DictationEngineResolver.MakePolisher() is not null), query,
                    Ui.Toggle(DictationCleanUp.SwitchShowsOn(Settings), isOn => DictationCleanUp.SetSwitch(Settings, isOn)))),
                    Changes.Of(Settings), Changes.Shell);
            case "dictation.input":
                return new LiveContent(() => Ui.Card(MicrophoneInputRow(item, query)),
                    Changes.Of(Settings, nameof(DictationSettings.PreferredMicrophoneUID)),
                    Changes.Event(handler => services.Microphones.DevicesChanged += handler, handler => services.Microphones.DevicesChanged -= handler));
            case "dictation.micTest":
                return Ui.Card(new MicrophoneTestRow(item, query, services));
            case "dictation.inactivityTimeout":
                return LiveSettingsCard(() => Ui.Row(item.Title, item.Detail, query,
                    Ui.Picker(new[] { (1, "1 minute"), (3, "3 minutes"), (5, "5 minutes"), (10, "10 minutes"), (0, "Never") },
                        Settings.InactivityTimeoutMinutes, minutes => Settings.InactivityTimeoutMinutes = minutes, 140)));
            case "dictation.pasteLast":
                return Ui.Card(Ui.Row(item.Title, item.Detail, query, Ui.Button("Paste Now", services.History.PasteLast)));
            case "dictation.sounds":
                return SettingsToggleRow(item.Title, item.Detail, query, () => Settings.Sounds, isOn => Settings.Sounds = isOn, nameof(DictationSettings.Sounds));
            default:
                return null;
        }
    }

    // MARK: - who hears you

    /// <summary>
    /// One engine as a radio option: its name and tag, what it does, and on the right what it still
    /// needs. The chosen engine that can't run yet says why, with the way to fix it.
    /// </summary>
    private LiveContent EngineOption(DictationEngineChoice choice, string query)
    {
        var parakeet = services.Services.Parakeet;
        return new LiveContent(() =>
        {
            var isSelected = Settings.Engine == choice;
            var unavailableReason = DictationEngineResolver.UnavailableReason(choice);
            UIElement? badge = null;
            if (unavailableReason is not null && choice.UnavailableBadge().Length > 0)
            {
                var badgeText = Ui.Text(choice.UnavailableBadge(), SettingsLook.CaptionSize, isSelected ? SettingsLook.Danger : SettingsLook.TextSecondary, wrap: false);
                badgeText.ToolTip = unavailableReason;
                badge = badgeText;
            }
            var option = Ui.RadioOption(isSelected, choice.SettingsTitle(), choice.SettingsDetail(), query, () =>
            {
                Settings.Engine = choice;
                if (choice == DictationEngineChoice.Parakeet && parakeet.State.Kind == ModelDownloadState.Stage.NotInstalled) parakeet.Download();
            }, Ui.Tag(choice.AudioDestinationTag(), choice.IsOnDevice()), badge);
            var stack = new StackPanel();
            stack.Children.Add(option);
            if (choice == DictationEngineChoice.Parakeet && (isSelected || parakeet.State.IsBusy || parakeet.State.Kind == ModelDownloadState.Stage.Installed))
            {
                var downloadRow = ModelDownloadRow(parakeet, ParakeetCaption);
                downloadRow.Margin = new Thickness(30, 10, 0, 0);
                stack.Children.Add(downloadRow);
            }
            else if (isSelected && unavailableReason is not null)
            {
                var reason = Ui.Text(WordsPageSentences.SentenceCased(unavailableReason) + ".", SettingsLook.CaptionSize, SettingsLook.Danger);
                var fix = choice == DictationEngineChoice.Sarvam
                    ? Ui.Button("Add Key…", () => actions.ShowSettings(DictationSettingsPage.Keys))
                    : Ui.Button("Sign In…", () => actions.ShowSettings(DictationSettingsPage.Account));
                var fixRow = new DockPanel { Margin = new Thickness(30, 10, 0, 0), LastChildFill = true };
                DockPanel.SetDock(fix, Dock.Right);
                fix.Margin = new Thickness(12, 0, 0, 0);
                fixRow.Children.Add(fix);
                reason.VerticalAlignment = VerticalAlignment.Center;
                fixRow.Children.Add(reason);
                stack.Children.Add(fixRow);
            }
            return Ui.Card(stack);
        }, Changes.Of(Settings, nameof(DictationSettings.Engine), nameof(DictationSettings.LanguageCode)), Changes.Shell, Changes.Of(parakeet));
    }

    private static string ParakeetCaption(ModelDownloadState state) => state.Kind switch
    {
        ModelDownloadState.Stage.NotInstalled => "Not downloaded yet. Takes stay with Windows speech recognition until it is.",
        ModelDownloadState.Stage.Downloading => $"Downloading, {(int)Math.Round(state.Fraction * 100)}%. Takes stay with Windows speech recognition until it’s ready.",
        ModelDownloadState.Stage.Unpacking => "Unpacking. Almost there.",
        ModelDownloadState.Stage.Failed => state.Message ?? "The download failed.",
        _ => "Downloaded. It loads while you speak, so takes start straight away.",
    };

    /// <summary>A model's download under its option: download (progress, cancel, retry), or remove it.</summary>
    private static DockPanel ModelDownloadRow(IModelDownload model, Func<ModelDownloadState, string> caption)
    {
        var state = model.State;
        var row = new DockPanel { LastChildFill = true };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        switch (state.Kind)
        {
            case ModelDownloadState.Stage.NotInstalled:
                controls.Children.Add(Ui.Button($"Download {model.DownloadSizeText}", model.Download));
                break;
            case ModelDownloadState.Stage.Downloading:
                controls.Children.Add(new ProgressBar { Width = 110, Value = state.Fraction, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
                controls.Children.Add(Ui.Button("Cancel", model.CancelDownload));
                break;
            case ModelDownloadState.Stage.Unpacking:
                controls.Children.Add(new WpfUi.ProgressRing { IsIndeterminate = true, Width = 18, Height = 18 });
                break;
            case ModelDownloadState.Stage.Failed:
                controls.Children.Add(Ui.Button("Try Again", model.Download));
                break;
            default:
                controls.Children.Add(Ui.Button("Remove", model.Remove));
                break;
        }
        DockPanel.SetDock(controls, Dock.Right);
        row.Children.Add(controls);
        var text = Ui.Text(caption(state), SettingsLook.CaptionSize, state.Kind == ModelDownloadState.Stage.Failed ? SettingsLook.Danger : SettingsLook.TextSecondary);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        return row;
    }

    // MARK: - language

    /// <summary>Auto-detect or one pinned language, from the languages the region offers. Pinning again comes back to the language last pinned.</summary>
    private Grid DictationLanguageRow(SettingsItem item, string query)
    {
        var isAuto = Settings.Language == DictationLanguage.Auto;
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(Ui.Picker(new[] { (true, "Auto-detect"), (false, "Pin one") }, isAuto, chooseAuto =>
        {
            if (chooseAuto) Settings.LanguageCode = DictationLanguage.Auto.Code;
            else if (Settings.Language == DictationLanguage.Auto) Settings.LanguageCode = LanguageToPin().Code;
        }, 130));
        if (!isAuto)
        {
            var languages = DictationLanguage.PinnableChoices(Settings.Region, Settings.Language)
                .Select(language => (language.Code, language.Name == language.NativeName ? language.Name : $"{language.Name} · {language.NativeName}"));
            var languagePicker = Ui.Picker(languages, Settings.LanguageCode, code =>
            {
                Settings.LanguageCode = code;
                services.Preferences.Set(LastPinnedLanguageKey, code);
            }, 170);
            languagePicker.Margin = new Thickness(8, 0, 0, 0);
            controls.Children.Add(languagePicker);
        }
        return Ui.Row(item.Title, item.Detail, query, controls);
    }

    /// <summary>The last pinned language if the region still offers it, else the region's English.</summary>
    private DictationLanguage LanguageToPin()
    {
        var lastPinned = DictationLanguage.Named(services.Preferences.GetString(LastPinnedLanguageKey));
        var offered = DictationLanguage.PinnableChoices(Settings.Region, DictationLanguage.Auto);
        return offered.Contains(lastPinned) ? lastPinned : DictationLanguage.DefaultPinned(Settings.Region);
    }

    // MARK: - microphone

    private Grid MicrophoneInputRow(SettingsItem item, string query)
    {
        var microphones = services.Microphones;
        var devices = microphones.Available();
        var automaticName = microphones.EffectiveInputName(null) ?? "none";
        var chosen = Settings.PreferredMicrophoneUID is { } chosenId ? devices.FirstOrDefault(device => device.Id == chosenId) : null;
        var currentName = chosen?.Name ?? microphones.EffectiveInputName(null) ?? "no microphone found";
        var options = new List<(string Id, string Title)> { ("", $"Automatic ({automaticName})") };
        options.AddRange(devices.Select(device => (device.Id, device.Name)));
        // A chosen microphone that isn't connected stays listed, so the picker never shows a blank.
        if (Settings.PreferredMicrophoneUID is { } missingId && chosen is null) options.Add((missingId, "Not connected"));
        var picker = Ui.Picker(options, Settings.PreferredMicrophoneUID ?? "", id => Settings.PreferredMicrophoneUID = id.Length == 0 ? null : id, 200, maxWidth: 280);
        return Ui.Row(item.Title, $"Automatic follows Windows’ default microphone. Clicky listens through it too. Now: {currentName}.", query, picker);
    }

    /// <summary>"Speak to Test": the chosen input's level as five bars while it listens.</summary>
    private sealed class MicrophoneTestRow : ContentControl
    {
        private readonly SettingsItem item;
        private readonly string query;
        private readonly MainWindowServices services;
        private IAudioCapture? capture;
        private readonly Border[] bars = new Border[5];
        private bool isTesting;

        public MicrophoneTestRow(SettingsItem item, string query, MainWindowServices services)
        {
            this.item = item;
            this.query = query;
            this.services = services;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            Render();
            Unloaded += (_, _) => StopTest();
        }

        private void Render()
        {
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            if (isTesting)
            {
                var meter = new StackPanel { Orientation = Orientation.Horizontal, Height = 18, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                for (var index = 0; index < bars.Length; index++)
                {
                    bars[index] = new Border { Width = 4, Height = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(1.5, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Center };
                    bars[index].SetResourceReference(Border.BackgroundProperty, SettingsLook.Success);
                    meter.Children.Add(bars[index]);
                }
                controls.Children.Add(meter);
            }
            controls.Children.Add(Ui.Button(isTesting ? "Stop" : "Speak to Test", () => { if (isTesting) StopTest(); else _ = StartTestAsync(); }));
            Content = Ui.Row(item.Title, isTesting ? "Speak, and the bars should follow your voice." : item.Detail, query, controls);
        }

        private async Task StartTestAsync()
        {
            var newCapture = services.CreateAudioCapture();
            newCapture.FramesAvailable += frame => Dispatcher.BeginInvoke(() => ShowLevel(frame.Level));
            try
            {
                await newCapture.StartAsync(new AudioCaptureOptions { DeviceId = services.Settings.PreferredMicrophoneUID, SampleRate = 16_000 });
                capture = newCapture;
                isTesting = true;
                Render();
            }
            catch (Exception error)
            {
                newCapture.Dispose();
                AppLog.Write($"microphone test: couldn't start: {error.Message}");
                Content = Ui.Row(item.Title, "Couldn’t open the microphone. Check Privacy & Security › Microphone.", query,
                    Ui.Button("Speak to Test", () => _ = StartTestAsync()), SettingsLook.Danger);
            }
        }

        private void ShowLevel(float level)
        {
            if (!isTesting) return;
            var scaled = Math.Min(1, level * 4);
            for (var index = 0; index < bars.Length; index++)
            {
                // The middle bar is tallest, like the orb's level bars.
                var shape = 1 - Math.Abs(index - 2) * 0.22;
                bars[index].Height = Math.Max(3, 18 * scaled * shape);
            }
        }

        private void StopTest()
        {
            capture?.Stop();
            capture?.Dispose();
            capture = null;
            if (!isTesting) return;
            isTesting = false;
            Render();
        }
    }
}
