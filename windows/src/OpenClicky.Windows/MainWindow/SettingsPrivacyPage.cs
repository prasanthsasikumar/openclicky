// Ports Dictation/UI/SettingsPrivacyPage.swift and SettingsAboutPage.swift. Privacy & Security:
// the microphone permission (the one Windows has: Settings › Privacy & security › Microphone,
// polled like the island's cards), reading nearby text, what is kept on this PC and for how long,
// where each file lives, and what leaves the PC. About: the version, the source and reporting a bug.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Settings;
using WpfUi = Wpf.Ui.Controls;

namespace OpenClicky.Windows.AppWindow;

internal sealed partial class SettingsRowFactory
{
    private UIElement? PrivacyRow(SettingsItem item, string query)
    {
        switch (item.Id)
        {
            case "privacy.microphone":
                return new LiveContent(() => Ui.Card(MicrophonePermissionRow(item, query)), Changes.Every(TimeSpan.FromSeconds(1.5)));
            case "privacy.readNearbyText":
                return SettingsToggleRow(item.Title, item.Detail, query, () => Settings.ReadNearbyText, isOn => Settings.ReadNearbyText = isOn, nameof(DictationSettings.ReadNearbyText));
            case "privacy.pauseBeforeSendingScreen":
                return SettingsToggleRow(item.Title, item.Detail, query, () => Settings.PauseBeforeSendingScreen, isOn => Settings.PauseBeforeSendingScreen = isOn, nameof(DictationSettings.PauseBeforeSendingScreen));
            case "privacy.memoryOnThisMac":
                return SettingsToggleRow(item.Title, item.Detail, query, () => Settings.KeepMemoryOnThisPC, isOn => Settings.KeepMemoryOnThisPC = isOn, nameof(DictationSettings.KeepMemoryOnThisPC));
            case "privacy.incognito":
                return SettingsToggleRow(item.Title, item.Detail, query, () => Settings.Incognito, isOn => Settings.Incognito = isOn, nameof(DictationSettings.Incognito));
            case "privacy.clipboardHistory":
                return SettingsToggleRow(item.Title, item.Detail, query, () => Settings.ClipboardHistoryEnabled, isOn => Settings.ClipboardHistoryEnabled = isOn, nameof(DictationSettings.ClipboardHistoryEnabled));
            case "privacy.retryFailedTakes":
                return SettingsToggleRow(item.Title, item.Detail, query, () => Settings.RetainFailedTakeAudio, isOn =>
                {
                    Settings.RetainFailedTakeAudio = isOn;
                    if (!isOn) services.AudioStore.DiscardAll();
                }, nameof(DictationSettings.RetainFailedTakeAudio));
            case "privacy.deleteTakes":
                return Ui.Card(Ui.Row(item.Title, item.Detail, query, Ui.Button("Delete History…", DeleteHistory, WpfUi.ControlAppearance.Danger)));
            case "privacy.whereHistory":
                return Ui.Card(Ui.Row(item.Title, item.Detail, query, Ui.Button("Show in Explorer", () => Ui.RevealInExplorer(OpenClickyPaths.DictationDatabaseFile))));
            case "privacy.whereSpace":
                return Ui.Card(Ui.Row(item.Title, item.Detail, query, Ui.Button("Show in Explorer", () =>
                {
                    Directory.CreateDirectory(OpenClickyPaths.DictationDirectory);
                    Ui.RevealInExplorer(OpenClickyPaths.DictationDirectory);
                })));
            case "privacy.whereKeys":
                return Ui.Card(Ui.Row(item.Title, item.Detail, query, Ui.Button("Show in Explorer", () => Ui.RevealInExplorer(OpenClickyPaths.ShellSettingsFile))));
            default:
                return null;
        }
    }

    /// <summary>The microphone's live state: "Allowed", or what Windows has switched off and the way to Settings.</summary>
    private Grid MicrophonePermissionRow(SettingsItem item, string query)
    {
        MicrophoneConsent consent;
        try { consent = services.MicrophonePermission.ReadConsent(); }
        catch (Exception error)
        {
            AppLog.Write($"privacy: couldn't read the microphone consent: {error.Message}");
            consent = MicrophoneConsent.Allowed;
        }
        if (consent == MicrophoneConsent.Allowed)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8 };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, SettingsLook.Success);
            return Ui.Row(item.Title, item.Detail, query, Ui.HStack(6, dot, Ui.Text("Allowed", SettingsLook.RowTitleSize, SettingsLook.TextSecondary, wrap: false)));
        }
        var whatIsOff = consent switch
        {
            MicrophoneConsent.DeniedForDevice => " Microphone access is off for this PC.",
            MicrophoneConsent.DeniedForAllApps => " “Let apps access your microphone” is off.",
            MicrophoneConsent.DeniedForDesktopApps => " “Let desktop apps access your microphone” is off.",
            _ => "",
        };
        return Ui.Row(item.Title, item.Detail + whatIsOff, query,
            Ui.Button("Allow…", services.MicrophonePermission.OpenPrivacySettings, WpfUi.ControlAppearance.Primary));
    }

    private void DeleteHistory()
    {
        if (!SheetWindow.Confirm(actions.OwnerWindow, Settings, "Delete every take on this PC?", "This can’t be undone.", "Delete All History")) return;
        try { services.TakeStore?.DeleteAll(); }
        catch (TakeStoreException error) { AppLog.Write($"history delete all failed: {error.Message}"); }
        services.AudioStore.DiscardAll();
        services.History.HistoryDidChange();
    }

    private static StackPanel PrivacySummaryNote(string query)
    {
        var note = Ui.Highlighted(SettingsCatalog.PrivacySummaryText, query, SettingsLook.CaptionSize, SettingsLook.TextSecondary);
        var link = Ui.HStack(3,
            Ui.Text("The full privacy policy is in the project’s", SettingsLook.CaptionSize, SettingsLook.TextSecondary, wrap: false),
            Ui.LinkButton("README", () => Ui.OpenExternal(AppVersion.ReadmeUrl)),
            Ui.Text(".", SettingsLook.CaptionSize, SettingsLook.TextSecondary, wrap: false));
        link.Margin = new Thickness(0, 4, 0, 0);
        var stack = new StackPanel { Margin = new Thickness(4, 4, 4, 6) };
        stack.Children.Add(note);
        stack.Children.Add(link);
        return stack;
    }

    // MARK: - About

    private UIElement? AboutRow(SettingsItem item, string query)
    {
        switch (item.Id)
        {
            case "about.app":
                var icon = new Image { Source = OpenClickyMarkDrawing.AppIconImage(), Width = 44, Height = 44 };
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(Ui.Text("OpenClicky", 15, SettingsLook.TextPrimary, FontWeights.SemiBold));
                var version = new TextBox
                {
                    Text = item.Detail,
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Padding = new Thickness(0),
                    // A TextBox insets its text 2 px on its own; pulled back so it lines up under the name.
                    Margin = new Thickness(-2, 0, 0, 0),
                    FontSize = SettingsLook.CaptionSize,
                    Style = null,
                    Template = PlainTextBoxTemplate(),
                };
                version.SetResourceReference(Control.ForegroundProperty, SettingsLook.TextSecondary);
                text.Children.Add(version);
                // HStack sets each later child's left margin to its spacing: the gap is given here.
                return Ui.Card(Ui.HStack(14, icon, text), new Thickness(16, 14, 16, 14));
            case "about.source":
                return Ui.Card(Ui.Row(item.Title, item.Detail, query, Ui.Button("GitHub ↗", () => Ui.OpenExternal(AppVersion.SourceUrl))));
            case "about.reportBug":
                return Ui.Card(Ui.Row(item.Title, item.Detail, query, Ui.HStack(8,
                    Ui.Button("Show Log", () => Ui.RevealInExplorer(AppLog.FilePath)),
                    Ui.Button("Open Issues ↗", () => Ui.OpenExternal(AppVersion.IssuesUrl)))));
            default:
                return null;
        }
    }

    /// <summary>A text box drawn as plain text, so the version can be selected and copied (textSelection).</summary>
    private static ControlTemplate PlainTextBoxTemplate()
    {
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        return new ControlTemplate(typeof(TextBox)) { VisualTree = host };
    }
}
