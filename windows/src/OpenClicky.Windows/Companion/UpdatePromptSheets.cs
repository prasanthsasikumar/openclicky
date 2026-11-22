// Sparkle's update alerts on the Mac (SPUStandardUserDriver), as sheets: "A new version is
// available" with Install Update / Skip This Version / Remind Me Later, "You're up to date", and a
// check that failed. The download's progress is the update window's now (UpdateWindows). Before this, "Check Now" downloaded and restarted
// into the new version without asking (2026-10-29).

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenClicky.Core.Dictation;
using OpenClicky.Windows.AppWindow;
using WpfUi = Wpf.Ui.Controls;

namespace OpenClicky.Windows.Companion;

public enum UpdateChoice
{
    Install,
    Skip,
    Later,
}

/// <summary>An update the person is asked about: its version, the one running, and its release notes (Markdown, may be empty).</summary>
public sealed record UpdateOffer(string Version, string CurrentVersion, string Notes);

public sealed class UpdatePromptSheets(Dispatcher dispatcher, DictationSettings settings)
{
    /// <summary>Notes longer than this are cut; the whole list is on GitHub.</summary>
    private const int NotesLimit = 2_000;

    public Task<UpdateChoice> AskToInstallAsync(UpdateOffer offer) => dispatcher.InvokeAsync(() =>
    {
        var choice = UpdateChoice.Later;
        var sheet = new SheetWindow(Owner(), settings, "Software Update", 480);
        var body = Ui.VStack(8,
            Ui.Text($"A new version of OpenClicky is available", 16, SettingsLook.TextPrimary, FontWeights.SemiBold),
            Ui.Text($"OpenClicky {offer.Version} is available; you have {offer.CurrentVersion}. Would you like to install it now? OpenClicky restarts to finish.",
                    13, SettingsLook.TextSecondary));
        var notes = offer.Notes.Trim();
        if (notes.Length > 0)
        {
            if (notes.Length > NotesLimit) notes = notes[..NotesLimit].TrimEnd() + "…";
            var notesText = Ui.Text(notes, 12, SettingsLook.TextSecondary);
            notesText.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(new ScrollViewer { Content = notesText, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }
        sheet.SetBody(body, new Thickness(24, 4, 24, 18));
        sheet.AddFooterButton("Skip This Version", () => { choice = UpdateChoice.Skip; sheet.Close(); }, alignLeft: true);
        sheet.AddFooterButton("Remind Me Later", sheet.Close, isCancel: true);
        sheet.AddFooterButton("Install Update", () => { choice = UpdateChoice.Install; sheet.Close(); }, isDefault: true);
        sheet.ShowDialog();
        return choice;
    }).Task;

    /// <summary>A one-button message: up to date, or the check failed.</summary>
    public Task TellAsync(string title, string message) => dispatcher.InvokeAsync(() =>
    {
        var sheet = new SheetWindow(Owner(), settings, "Software Update", 420);
        sheet.SetBody(Ui.VStack(8,
            Ui.Text(title, 16, SettingsLook.TextPrimary, FontWeights.SemiBold),
            Ui.Text(message, 13, SettingsLook.TextSecondary)), new Thickness(24, 4, 24, 18));
        sheet.AddFooterButton("OK", sheet.Close, isDefault: true, isCancel: true);
        sheet.ShowDialog();
    }).Task;

    private static Window? Owner() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive && window is not SheetWindow);
}
