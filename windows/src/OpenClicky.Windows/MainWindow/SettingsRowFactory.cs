// The "view" half of every SettingsItem (SettingsCatalog.swift kept a SwiftUI view in each item):
// for an item id, the row that shows and changes that setting, so a page and a search result draw
// the very same row. One partial file per Settings*Page.swift.

using System.Windows;
using System.Windows.Controls;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Settings;

namespace OpenClicky.Windows.AppWindow;

/// <summary>What a row may ask of the window it is in.</summary>
internal interface IMainWindowActions
{
    /// <summary>CompanionManager.showSettings(page).</summary>
    void ShowSettings(DictationSettingsPage page);

    /// <summary>CompanionManager.showDictationWindow(section:).</summary>
    void ShowSection(DictationSection section);

    /// <summary>A row opening a page under this one (settingsNavigate): the back button returns.</summary>
    void Navigate(DictationDestination destination);

    /// <summary>CompanionManager.startSettingsTour().</summary>
    void StartSettingsTour();

    /// <summary>The window, for owned dialogs.</summary>
    Window OwnerWindow { get; }
}

internal sealed partial class SettingsRowFactory(MainWindowServices services, IMainWindowActions actions)
{
    private DictationSettings Settings => services.Settings;

    /// <summary>The row(s) for one item: a card, several cards (the voice and key lists), or a footnote.</summary>
    public UIElement Build(SettingsItem item, string query)
    {
        if (item.Chrome == SettingsItemChrome.Footer) return BuildFooter(item, query);
        if (item.OpensSection is { } section)
        {
            return Ui.Card(Ui.Row(item.Title, item.Detail, query,
                Ui.Button($"Open {section.SidebarTitle()}", () => actions.ShowSection(section))));
        }
        if (item.OpensDestination is { } destination) return NavigationRow(item, query, destination);
        // The rows are placed by SettingsLayout, not by the page that builds them: each page's
        // builder answers for its own ids and passes on the rest.
        return GeneralRow(item, query)
            ?? DictationRow(item, query)
            ?? ClickyRow(item, query)
            ?? VoiceRow(item, query)
            ?? OrbRow(item, query)
            ?? PluginsRow(item, query)
            ?? KeyboardRow(item, query)
            ?? KeysRow(item, query)
            ?? AccountRow(item, query)
            ?? PrivacyRow(item, query)
            ?? AboutRow(item, query)
            ?? IslandRow(item, query)
            ?? Ui.Card(Ui.Row(item.Title, item.Detail, query, null));
    }

    /// <summary>A row that opens another page (The orb from Dictation): its name, a line on what is there, and a chevron. The whole row is the button.</summary>
    private UIElement NavigationRow(SettingsItem item, string query, DictationDestination destination)
    {
        var chevron = new TextBlock
        {
            Text = "",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, SettingsLook.TextSecondary);
        var button = new Button
        {
            Content = Ui.Card(Ui.Row(item.Title, item.Detail, query, chevron)),
            Template = Ui.TransparentButtonTemplate(),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        Ui.AutomationProperties(button, item.Title);
        button.Click += (_, _) => actions.Navigate(destination);
        return button;
    }

    private UIElement BuildFooter(SettingsItem item, string query)
    {
        if (item.Id == "privacy.summary") return PrivacySummaryNote(query);
        return Ui.Footnote(item.Detail, query);
    }

    /// <summary>A card whose content redraws when the dictation settings change.</summary>
    private LiveContent LiveSettingsCard(Func<UIElement> build, params Func<Action, IDisposable>[] moreSources) =>
        new(() => Ui.Card(build()), [Changes.Of(Settings), .. moreSources]);

    /// <summary>A switch row bound to one dictation setting.</summary>
    private LiveContent SettingsToggleRow(string title, string detail, string query, Func<bool> read, Action<bool> write, string propertyName) =>
        new(() => Ui.Card(Ui.Row(title, detail, query, Ui.Toggle(read(), write))), Changes.Of(Settings, propertyName));
}
