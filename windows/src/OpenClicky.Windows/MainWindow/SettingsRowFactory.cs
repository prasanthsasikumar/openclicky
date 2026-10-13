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
        return item.Page switch
        {
            DictationSettingsPage.General => GeneralRow(item, query),
            DictationSettingsPage.Dictation => DictationRow(item, query),
            DictationSettingsPage.Clicky => ClickyRow(item, query),
            DictationSettingsPage.Voice => VoiceRow(item, query),
            DictationSettingsPage.Orb => OrbRow(item, query),
            DictationSettingsPage.Keyboard => KeyboardRow(item, query),
            DictationSettingsPage.Keys => KeysRow(item, query),
            DictationSettingsPage.Account => AccountRow(item, query),
            DictationSettingsPage.Privacy => PrivacyRow(item, query),
            _ => AboutRow(item, query),
        } ?? Ui.Card(Ui.Row(item.Title, item.Detail, query, null));
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
