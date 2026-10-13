// Ports Dictation/UI/WritingPageView.swift: "How it writes", the dictionary, the snippets (spoken
// shortcuts) and the styles on one page, drawn like the settings pages. The language, the script
// and the AI clean-up switch are on the Dictation page; Styles says whether clean-up is on, since
// only the clean-up reads the style rules. Anything too big for a row (a term, a snippet, a
// style's name and switches, picking an app) opens a dialog. Apps are named by their process
// image ("chrome.exe") and drawn with their icons.

using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Settings;
using WpfUi = Wpf.Ui.Controls;

namespace OpenClicky.Windows.AppWindow;

internal sealed class WritingPageView : ScrollViewer
{
    private readonly MainWindowServices services;
    private readonly IMainWindowActions actions;
    private readonly AppIconCache icons;
    private readonly StackPanel column;
    private string? expandedStyleId;
    private readonly Dictionary<string, string> rulesDrafts = [];
    private IReadOnlyList<AppUsage> usedApps = [];

    private DictationSpaceStore SpaceStore => services.SpaceStore;
    private DictationSettings Settings => services.Settings;

    public WritingPageView(MainWindowServices services, IMainWindowActions actions, AppIconCache icons)
    {
        this.services = services;
        this.actions = actions;
        this.icons = icons;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = false;
        column = SettingsFormView.PageColumn(DictationSection.Writing.SidebarTitle());
        Content = column;
        try { usedApps = services.TakeStore?.AppsUsed() ?? []; }
        catch (TakeStoreException) { usedApps = []; }
        void OnSpaceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs arguments) => Dispatcher.BeginInvoke(Render);
        void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs arguments) => Dispatcher.BeginInvoke(Render);
        Loaded += (_, _) =>
        {
            SpaceStore.PropertyChanged += OnSpaceChanged;
            Settings.PropertyChanged += OnSettingsChanged;
        };
        Unloaded += (_, _) =>
        {
            SpaceStore.PropertyChanged -= OnSpaceChanged;
            Settings.PropertyChanged -= OnSettingsChanged;
        };
        Render();
    }

    private void Render()
    {
        var offset = VerticalOffset;
        while (column.Children.Count > 1) column.Children.RemoveAt(1);
        var space = SpaceStore.Space;
        DictionarySection(space);
        SnippetsSection(space);
        StylesSection(space);
        ScrollToVerticalOffset(offset);
    }

    /// <summary>"Dictionary" with how many there are beside it.</summary>
    private static StackPanel SectionHeader(string title, int count, string unit)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 22, 0, 8) };
        header.Children.Add(Ui.Text(title, SettingsLook.SectionTitleSize, SettingsLook.TextPrimary, FontWeights.SemiBold, wrap: false));
        var countText = Ui.Text(WordsPageSentences.Plural(count, unit), SettingsLook.CaptionSize, SettingsLook.TextSecondary, wrap: false);
        countText.Margin = new Thickness(8, 0, 0, 0);
        countText.VerticalAlignment = VerticalAlignment.Bottom;
        countText.Padding = new Thickness(0, 0, 0, 1);
        header.Children.Add(countText);
        return header;
    }

    /// <summary>Edit and delete for one row of a list.</summary>
    private static StackPanel RowActions(string itemName, Action onEdit, Action onDelete)
    {
        var edit = Ui.Button("Edit…", onEdit);
        Ui.AutomationProperties(edit, $"Edit {itemName}");
        var delete = new WpfUi.Button
        {
            Icon = new WpfUi.SymbolIcon { Symbol = WpfUi.SymbolRegular.Delete24 },
            Appearance = WpfUi.ControlAppearance.Transparent,
            ToolTip = "Delete",
            Cursor = Cursors.Hand,
            Padding = new Thickness(8, 6, 8, 6),
        };
        Ui.AutomationProperties(delete, $"Delete {itemName}");
        delete.Click += (_, _) => onDelete();
        return Ui.HStack(4, edit, delete);
    }

    /// <summary>The last row of a list: a plus and what it adds, in the accent like a link.</summary>
    private static Button AddRow(string title, Action onClick)
    {
        var plus = Ui.Text("", 14, SettingsLook.Accent, wrap: false);
        plus.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        plus.VerticalAlignment = VerticalAlignment.Center;
        var label = Ui.Text(title, SettingsLook.RowTitleSize, SettingsLook.Accent, wrap: false);
        label.Margin = new Thickness(10, 0, 0, 0);
        var content = Ui.Card(Ui.HStack(0, plus, label), new Thickness(16, 12, 16, 12));
        var button = new Button { Content = content, Template = Ui.TransparentButtonTemplate(), Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static TextBlock EmptyLine(string text)
    {
        var line = Ui.Text(text, SettingsLook.RowTitleSize, SettingsLook.TextSecondary);
        return line;
    }

    private static ContextMenu EditDeleteMenu(Action onEdit, Action onDelete)
    {
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = "Edit…" };
        edit.Click += (_, _) => onEdit();
        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => onDelete();
        menu.Items.Add(edit);
        menu.Items.Add(delete);
        return menu;
    }

    // MARK: - Dictionary

    private void DictionarySection(DictationSpace space)
    {
        var dictionaryExample = Settings.Region == Core.Configuration.AppRegion.India
            ? "You say “aditya shatriya”, OpenClicky writes Aaditya Kshatriya."
            : "You say “shivon”, OpenClicky writes Siobhán.";
        column.Children.Add(SectionHeader("Dictionary", space.Dictionary.Count, "term"));
        var terms = space.Dictionary.OrderBy(term => term.Written.ToLowerInvariant()).ToList();
        if (terms.Count == 0) column.Children.Add(Ui.Card(EmptyLine("Nothing here yet. Teach OpenClicky its first term.")));
        foreach (var term in terms)
        {
            var heardAs = term.HeardAs.Count == 0 ? "" : "Heard as " + string.Join(", ", term.HeardAs.Select(alias => $"“{alias}”"));
            var card = Ui.Card(Ui.Row(term.Written, heardAs, "", RowActions(term.Written, () => EditTerm(term), () => DeleteTerm(term))));
            card.ContextMenu = EditDeleteMenu(() => EditTerm(term), () => DeleteTerm(term));
            column.Children.Add(card);
        }
        column.Children.Add(AddRow("Add a Term…", () => EditTerm(null)));
        column.Children.Add(Ui.Footnote($"Names and terms written exactly as you expect. {dictionaryExample} The written form is always matched too, in any casing.", ""));
    }

    private void DeleteTerm(DictionaryTerm term) => SpaceStore.Update(space => space.Dictionary.RemoveAll(existing => existing.Id == term.Id));

    /// <summary>Teach or edit a dictionary term: the written form and the ways it tends to be heard.</summary>
    private void EditTerm(DictionaryTerm? term)
    {
        var sheet = new SheetWindow(actions.OwnerWindow, Settings, term is null ? "Teach OpenClicky a term" : "Edit term", 480);
        var written = new WpfUi.TextBox { PlaceholderText = "Aaditya Kshatriya", Text = term?.Written ?? "" };
        var heardAs = new WpfUi.TextBox { PlaceholderText = "aditya shatriya, aditya", Text = term is null ? "" : string.Join(", ", term.HeardAs) };
        sheet.SetBody(Ui.VStack(6,
            Ui.Text("OpenClicky writes", 13, SettingsLook.TextPrimary), written,
            Spaced(Ui.Text("You say", 13, SettingsLook.TextPrimary), 12), heardAs,
            Spaced(Ui.Text("“You say” is optional; separate several with commas. The written form is always matched too, in any casing.", SettingsLook.CaptionSize, SettingsLook.TextSecondary), 8)));
        sheet.AddFooterButton("Cancel", sheet.Close, isCancel: true);
        var save = sheet.AddFooterButton("Save", () =>
        {
            var aliases = heardAs.Text.Split(',').Select(alias => alias.Trim()).Where(alias => alias.Length > 0).ToList();
            var changed = new DictionaryTerm(written.Text.Trim(), aliases, term?.Id ?? Guid.NewGuid(), term?.CreatedAt ?? DateTimeOffset.Now);
            SpaceStore.Update(space =>
            {
                var index = space.Dictionary.FindIndex(existing => existing.Id == changed.Id);
                if (index >= 0) space.Dictionary[index] = changed;
                else space.Dictionary.Add(changed);
            });
            sheet.Close();
        }, isDefault: true);
        save.IsEnabled = written.Text.Trim().Length > 0;
        written.TextChanged += (_, _) => save.IsEnabled = written.Text.Trim().Length > 0;
        sheet.Loaded += (_, _) => written.Focus();
        sheet.ShowDialog();
    }

    private static FrameworkElement Spaced(FrameworkElement element, double top)
    {
        element.Margin = new Thickness(0, top, 0, 0);
        return element;
    }

    // MARK: - Snippets

    private void SnippetsSection(DictationSpace space)
    {
        column.Children.Add(SectionHeader("Snippets", space.Shortcuts.Count, "snippet"));
        var snippets = space.Shortcuts.OrderBy(snippet => snippet.Trigger.ToLowerInvariant()).ToList();
        if (snippets.Count == 0) column.Children.Add(Ui.Card(EmptyLine("Nothing here yet. Teach OpenClicky its first snippet.")));
        foreach (var snippet in snippets)
        {
            var replacement = Ui.Text(snippet.Replacement, SettingsLook.CaptionSize, SettingsLook.TextSecondary);
            replacement.MaxHeight = 52;
            replacement.TextTrimming = TextTrimming.CharacterEllipsis;
            var text = Ui.VStack(2, Ui.Text($"“{snippet.Trigger}”", SettingsLook.RowTitleSize), replacement);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(text);
            var rowActions = RowActions(snippet.Trigger, () => EditSnippet(snippet, space.Shortcuts), () => DeleteSnippet(snippet));
            rowActions.Margin = new Thickness(16, 0, 0, 0);
            rowActions.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(rowActions, 1);
            grid.Children.Add(rowActions);
            var card = Ui.Card(grid);
            card.ContextMenu = EditDeleteMenu(() => EditSnippet(snippet, space.Shortcuts), () => DeleteSnippet(snippet));
            column.Children.Add(card);
        }
        column.Children.Add(AddRow("Add a Snippet…", () => EditSnippet(null, space.Shortcuts)));
        column.Children.Add(Ui.Footnote("Say the trigger on its own and the saved text is written instead: a sign-off, an address, anything you type a hundred times. A snippet expands only on its exact trigger; a dictionary term is replaced inside a longer sentence.", ""));
    }

    private void DeleteSnippet(SpokenShortcut snippet) => SpaceStore.Update(space => space.Shortcuts.RemoveAll(existing => existing.Id == snippet.Id));

    /// <summary>Teach or edit a snippet. A trigger another snippet already uses (compared the way takes are matched) cannot be saved.</summary>
    private void EditSnippet(SpokenShortcut? snippet, IReadOnlyList<SpokenShortcut> existingSnippets)
    {
        var sheet = new SheetWindow(actions.OwnerWindow, Settings, snippet is null ? "Teach OpenClicky a snippet" : "Edit snippet", 500);
        var trigger = new WpfUi.TextBox { PlaceholderText = "my sign-off", Text = snippet?.Trigger ?? "" };
        var inUse = Ui.Text("That spoken phrase is already in use.", SettingsLook.CaptionSize, SettingsLook.Danger);
        inUse.Visibility = Visibility.Collapsed;
        var replacement = new WpfUi.TextBox { Text = snippet?.Replacement ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, VerticalContentAlignment = VerticalAlignment.Top };
        sheet.SetBody(Ui.VStack(6,
            Ui.Text("You say", 13, SettingsLook.TextPrimary), trigger, inUse,
            Spaced(Ui.Text("OpenClicky writes", 13, SettingsLook.TextPrimary), 12), replacement,
            Spaced(Ui.Text("The saved text is written only when the whole take is the trigger.", SettingsLook.CaptionSize, SettingsLook.TextSecondary), 8)));
        sheet.AddFooterButton("Cancel", sheet.Close, isCancel: true);
        var save = sheet.AddFooterButton("Save", () =>
        {
            var changed = new SpokenShortcut(trigger.Text.Trim(), replacement.Text, snippet?.Id ?? Guid.NewGuid(), snippet?.CreatedAt ?? DateTimeOffset.Now);
            SpaceStore.Update(space =>
            {
                var index = space.Shortcuts.FindIndex(existing => existing.Id == changed.Id);
                if (index >= 0) space.Shortcuts[index] = changed;
                else space.Shortcuts.Add(changed);
            });
            sheet.Close();
        }, isDefault: true);
        void Validate()
        {
            var normalized = TakeFormatter.NormalizedForMatching(trigger.Text);
            var triggerIsInUse = existingSnippets.Any(existing => existing.Id != snippet?.Id && TakeFormatter.NormalizedForMatching(existing.Trigger) == normalized);
            inUse.Visibility = triggerIsInUse ? Visibility.Visible : Visibility.Collapsed;
            save.IsEnabled = trigger.Text.Trim().Length > 0 && replacement.Text.Trim().Length > 0 && !triggerIsInUse;
        }
        trigger.TextChanged += (_, _) => Validate();
        replacement.TextChanged += (_, _) => Validate();
        Validate();
        sheet.Loaded += (_, _) => trigger.Focus();
        sheet.ShowDialog();
    }

    // MARK: - Styles

    private sealed record StyleApp(string ProcessName, string Name, int TakeCount);

    /// <summary>The style that writes wherever no other style claims the app.</summary>
    private static DictationStyle FallbackStyle(DictationSpace space) => space.Style(null);

    /// <summary>The apps a style writes for: those it names that are on this PC, and the apps takes went into that it is the style for, most used first.</summary>
    private List<StyleApp> Apps(DictationStyle style, DictationSpace space)
    {
        var processNames = style.AppProcessNames.Where(icons.IsOnThisPC).ToList();
        foreach (var usedApp in usedApps)
        {
            if (space.Style(usedApp.AppProcessName).Id == style.Id && !processNames.Contains(usedApp.AppProcessName, StringComparer.OrdinalIgnoreCase))
                processNames.Add(usedApp.AppProcessName);
        }
        return processNames
            .Select(processName =>
            {
                var usage = usedApps.FirstOrDefault(app => string.Equals(app.AppProcessName, processName, StringComparison.OrdinalIgnoreCase));
                return new StyleApp(processName, icons.Name(processName) ?? usage?.Name ?? processName, usage?.Count ?? 0);
            })
            .OrderByDescending(app => app.TakeCount).ThenBy(app => app.Name.ToLowerInvariant())
            .ToList();
    }

    private void StylesSection(DictationSpace space)
    {
        column.Children.Add(SectionHeader("Styles", space.Styles.Count, "style"));
        // Whether the AI clean-up that reads these rules is on; the switch itself is on the Dictation page.
        var isOn = DictationCleanUp.SwitchShowsOn(Settings);
        column.Children.Add(Ui.Card(Ui.Row(isOn ? "AI clean-up is on" : "AI clean-up is off",
            isOn ? "It reads the rules of the app’s style below." : "Styles’ rules are read only while clean-up is on.", "",
            Ui.Button("Language and Clean-up…", () => actions.ShowSettings(DictationSettingsPage.Dictation)))));
        foreach (var style in space.Styles) column.Children.Add(StyleRow(style, space));
        column.Children.Add(Ui.Footnote($"How you sound, app by app. Each app is written in one style, and the model reads that style’s rules when it cleans up. New apps start in “{FallbackStyle(space).Name}”.", ""));
    }

    private static string CapitalizedFirstLetter(string text) => text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];

    /// <summary>One style as a row that opens: name, tagline and its apps; opened, the rules the model reads and the apps, each movable to another style.</summary>
    private Border StyleRow(DictationStyle style, DictationSpace space)
    {
        var apps = Apps(style, space);
        var isExpanded = expandedStyleId == style.Id;
        var summary = apps.Count == 0
            ? (style.AppProcessNames.Count == 0 ? "Writes wherever no style claims the app" : "None of its apps are on this PC")
            : string.Join(", ", apps.Take(3).Select(app => app.Name)) + (apps.Count > 3 ? $" +{apps.Count - 3}" : "");

        var chevron = Ui.Text(isExpanded ? "" : "", 12, SettingsLook.TextSecondary, wrap: false);
        chevron.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        chevron.VerticalAlignment = VerticalAlignment.Center;
        var labelGrid = new Grid();
        labelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        labelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        labelGrid.Children.Add(Ui.TitleAndDetail(CapitalizedFirstLetter(style.Name), CapitalizedFirstLetter(style.Tagline), ""));
        if (!isExpanded)
        {
            var summaryText = Ui.Text(summary, SettingsLook.CaptionSize, SettingsLook.TextSecondary, wrap: false);
            summaryText.MaxWidth = 300;
            summaryText.Margin = new Thickness(16, 0, 12, 0);
            summaryText.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(summaryText, 1);
            labelGrid.Children.Add(summaryText);
        }
        Grid.SetColumn(chevron, 2);
        labelGrid.Children.Add(chevron);
        // The whole label opens the row, not only the chevron.
        var label = new Button { Content = labelGrid, Template = Ui.TransparentButtonTemplate(), Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Ui.AutomationProperties(label, $"{style.Name}, {style.Tagline}", isExpanded ? "Collapse" : "Show its rules and apps");
        label.Click += (_, _) =>
        {
            expandedStyleId = isExpanded ? null : style.Id;
            Render();
        };
        var stack = new StackPanel();
        stack.Children.Add(label);
        if (isExpanded)
        {
            stack.Children.Add(Ui.Divider());
            stack.Children.Add(RulesEditor(style));
            stack.Children.Add(AppsList(style, apps, space));
        }
        return Ui.Card(stack);
    }

    private StackPanel RulesEditor(DictationStyle style)
    {
        var draft = rulesDrafts.TryGetValue(style.Id, out var saved) ? saved : style.Rules;
        var editor = new WpfUi.TextBox { Text = draft, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96, VerticalContentAlignment = VerticalAlignment.Top };
        Ui.AutomationProperties(editor, "Rules the model reads");
        var revert = Ui.Button("Revert", () => { rulesDrafts.Remove(style.Id); Render(); });
        var saveRules = Ui.Button("Save Rules", () =>
        {
            var rules = editor.Text;
            rulesDrafts.Remove(style.Id);
            SpaceStore.Update(space =>
            {
                if (space.Styles.FirstOrDefault(existing => existing.Id == style.Id) is { } found) found.Rules = rules;
            });
        }, WpfUi.ControlAppearance.Primary);
        void UpdateButtons()
        {
            var changed = editor.Text != style.Rules;
            revert.Visibility = saveRules.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
        }
        editor.TextChanged += (_, _) =>
        {
            rulesDrafts[style.Id] = editor.Text;
            UpdateButtons();
        };
        UpdateButtons();
        var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var rightButtons = Ui.HStack(8, revert, saveRules);
        DockPanel.SetDock(rightButtons, Dock.Right);
        buttons.Children.Add(rightButtons);
        var nameButton = Ui.Button("Name, Clean-up and Apps…", () => EditStyle(style), toolTip: "Rename the style, its local clean-up switches and its apps");
        nameButton.HorizontalAlignment = HorizontalAlignment.Left;
        buttons.Children.Add(nameButton);
        return Ui.VStack(6, Ui.Text("Rules the model reads", SettingsLook.CaptionSize, SettingsLook.TextSecondary), editor, buttons);
    }

    private StackPanel AppsList(DictationStyle style, List<StyleApp> apps, DictationSpace space)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        stack.Children.Add(Ui.Text("Apps in this style", SettingsLook.CaptionSize, SettingsLook.TextSecondary));
        if (apps.Count == 0)
        {
            var none = Ui.Text(style.AppProcessNames.Count == 0 ? "Every app no other style claims." : "None of its apps are on this PC.", SettingsLook.CaptionSize, SettingsLook.TextSecondary);
            none.Margin = new Thickness(0, 8, 0, 4);
            stack.Children.Add(none);
        }
        foreach (var app in apps)
        {
            var line = new DockPanel { Margin = new Thickness(0, 6, 0, 0), MinHeight = 30 };
            var moveTo = new WpfUi.DropDownButton { Content = "Move to", ToolTip = $"Write {app.Name} in another style", Cursor = Cursors.Hand, Padding = new Thickness(8, 3, 8, 4), FontSize = 12 };
            var menu = new ContextMenu();
            foreach (var otherStyle in space.Styles.Where(other => other.Id != style.Id))
            {
                var menuItem = new MenuItem { Header = CapitalizedFirstLetter(otherStyle.Name) };
                var targetId = otherStyle.Id;
                menuItem.Click += (_, _) => Move(app.ProcessName, targetId);
                menu.Items.Add(menuItem);
            }
            moveTo.Flyout = menu;
            DockPanel.SetDock(moveTo, Dock.Right);
            line.Children.Add(moveTo);
            if (app.TakeCount > 0)
            {
                var count = Ui.Text(WordsPageSentences.Plural(app.TakeCount, "take"), SettingsLook.CaptionSize, SettingsLook.TextSecondary, wrap: false);
                count.Margin = new Thickness(0, 0, 10, 0);
                count.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(count, Dock.Right);
                line.Children.Add(count);
            }
            var icon = AppIconView.Make(icons, app.ProcessName, 16);
            DockPanel.SetDock(icon, Dock.Left);
            line.Children.Add(icon);
            var name = Ui.Text(app.Name, SettingsLook.RowTitleSize, SettingsLook.TextPrimary, wrap: false);
            name.Margin = new Thickness(8, 0, 0, 0);
            name.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(name);
            stack.Children.Add(line);
        }
        var addApp = Ui.Button("Add an App…", () =>
        {
            if (AppPickerSheet.Pick(actions.OwnerWindow, Settings, icons) is { } processName) Move(processName, style.Id);
        });
        addApp.HorizontalAlignment = HorizontalAlignment.Left;
        addApp.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(addApp);
        return stack;
    }

    /// <summary>Moves an app to a style. The fallback style holds no apps of its own (that is how it is found), so moving there takes the app away from every other style.</summary>
    private void Move(string appProcessName, string styleId)
    {
        SpaceStore.Update(space =>
        {
            if (styleId == FallbackStyle(space).Id)
            {
                foreach (var style in space.Styles) style.AppProcessNames.RemoveAll(name => string.Equals(name, appProcessName, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                space.Assign(appProcessName, styleId);
            }
        });
    }

    /// <summary>A style, opened in full: its name and tagline, the rules, the local clean-up switches and the apps it names. Saving takes those apps away from every other style.</summary>
    private void EditStyle(DictationStyle style)
    {
        var draft = style.Clone();
        var sheet = new SheetWindow(actions.OwnerWindow, Settings, CapitalizedFirstLetter(style.Name), 560);
        var name = new WpfUi.TextBox { Text = draft.Name, PlaceholderText = "Name" };
        var tagline = new WpfUi.TextBox { Text = draft.Tagline, PlaceholderText = "Tagline" };
        var rules = new WpfUi.TextBox { Text = draft.Rules, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, VerticalContentAlignment = VerticalAlignment.Top };
        var appsPanel = new StackPanel();
        void RenderApps()
        {
            appsPanel.Children.Clear();
            if (draft.AppProcessNames.Count == 0)
                appsPanel.Children.Add(Ui.Text("None. This style writes wherever no app has its own.", SettingsLook.CaptionSize, SettingsLook.TextSecondary));
            foreach (var processName in draft.AppProcessNames.ToList())
            {
                var line = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                var remove = new WpfUi.Button
                {
                    Icon = new WpfUi.SymbolIcon { Symbol = WpfUi.SymbolRegular.SubtractCircle24 },
                    Appearance = WpfUi.ControlAppearance.Transparent,
                    ToolTip = "Take this app out of the style",
                    Padding = new Thickness(6),
                    Cursor = Cursors.Hand,
                };
                Ui.AutomationProperties(remove, $"Remove {icons.Name(processName) ?? processName}");
                remove.Click += (_, _) => { draft.AppProcessNames.Remove(processName); RenderApps(); };
                DockPanel.SetDock(remove, Dock.Right);
                line.Children.Add(remove);
                var icon = AppIconView.Make(icons, processName, 16);
                DockPanel.SetDock(icon, Dock.Left);
                line.Children.Add(icon);
                var appName = Ui.Text(icons.Name(processName) ?? processName, SettingsLook.RowTitleSize, SettingsLook.TextPrimary, wrap: false);
                appName.Margin = new Thickness(8, 0, 0, 0);
                appName.VerticalAlignment = VerticalAlignment.Center;
                line.Children.Add(appName);
                appsPanel.Children.Add(line);
            }
        }
        RenderApps();
        var addApp = AddRow("Add an App…", () =>
        {
            if (AppPickerSheet.Pick(sheet, Settings, icons) is { } processName && !draft.AppProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
            {
                draft.AppProcessNames.Add(processName);
                RenderApps();
            }
        });
        addApp.Margin = new Thickness(0, 8, 0, 0);
        var sentenceCase = Ui.Toggle(draft.SentenceCase, isOn => draft.SentenceCase = isOn);
        var removeFillers = Ui.Toggle(draft.RemoveFillers, isOn => draft.RemoveFillers = isOn);
        var polish = Ui.Toggle(draft.PolishWithModel, isOn => draft.PolishWithModel = isOn);
        sheet.SetBody(Ui.VStack(0,
            Ui.Text("Name", 13, SettingsLook.TextPrimary), Spaced(name, 6),
            Spaced(Ui.Text("Tagline", 13, SettingsLook.TextPrimary), 12), Spaced(tagline, 6),
            SettingsFormView.SectionHeader("How this style writes"), rules,
            Spaced(Ui.Text("The model reads this when it cleans up a take in one of these apps.", SettingsLook.CaptionSize, SettingsLook.TextSecondary), 6),
            Spaced(Ui.Card(Ui.Row("Sentence case", "Capital letters and full stops.", "", sentenceCase)), 16),
            Ui.Card(Ui.Row("Remove fillers", "Um, uh, and friends.", "", removeFillers)),
            Ui.Card(Ui.Row("Clean up with AI", "For this style, when “Clean up my words with AI” is on.", "", polish)),
            SettingsFormView.SectionHeader("Apps written in this style"), appsPanel, addApp));
        sheet.AddFooterButton("Cancel", sheet.Close, isCancel: true);
        sheet.AddFooterButton("Save", () =>
        {
            draft.Name = name.Text;
            draft.Tagline = tagline.Text;
            draft.Rules = rules.Text;
            SpaceStore.Update(space =>
            {
                var index = space.Styles.FindIndex(existing => existing.Id == draft.Id);
                if (index < 0) return;
                space.Styles[index] = draft;
                // An app belongs to one style: take it away from the others.
                for (var otherIndex = 0; otherIndex < space.Styles.Count; otherIndex++)
                {
                    if (otherIndex == index) continue;
                    space.Styles[otherIndex].AppProcessNames.RemoveAll(processName => draft.AppProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase));
                }
            });
            rulesDrafts.Remove(style.Id);
            sheet.Close();
        }, isDefault: true);
        sheet.ShowDialog();
    }
}

/// <summary>The apps with a window open now, searchable, plus Browse… for any installed program.</summary>
internal static class AppPickerSheet
{
    private sealed record RunningApp(string ProcessName, string Name, string Path);

    private static List<RunningApp> RunningApps()
    {
        var ownProcessId = Environment.ProcessId;
        var apps = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == ownProcessId || process.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(process.MainWindowTitle)) continue;
                    var path = process.MainModule?.FileName;
                    if (path is null) continue;
                    var processName = System.IO.Path.GetFileName(path);
                    if (apps.ContainsKey(processName)) continue;
                    var info = FileVersionInfo.GetVersionInfo(path);
                    var name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription.Trim() : System.IO.Path.GetFileNameWithoutExtension(path);
                    apps[processName] = new RunningApp(processName, name, path);
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
                {
                    // An elevated or exiting process: not offered.
                }
            }
        }
        return apps.Values.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The process name ("chrome.exe") of the app picked, or null.</summary>
    public static string? Pick(Window owner, DictationSettings settings, AppIconCache icons)
    {
        string? picked = null;
        var sheet = new SheetWindow(owner, settings, "Choose an app for this style", 440);
        var search = new WpfUi.TextBox
        {
            PlaceholderText = "Search running apps",
            Icon = new WpfUi.SymbolIcon { Symbol = WpfUi.SymbolRegular.Search24 },
            IconPlacement = WpfUi.ElementPlacement.Left,
        };
        var list = new StackPanel();
        var running = RunningApps();
        foreach (var app in running) icons.Remember(app.ProcessName, app.Path);
        void RenderList()
        {
            list.Children.Clear();
            foreach (var app in running.Where(app => search.Text.Length == 0 || app.Name.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)))
            {
                var row = Ui.HStack(10, AppIconView.Make(icons, app.ProcessName, 20), Ui.Text(app.Name, SettingsLook.RowTitleSize, SettingsLook.TextPrimary, wrap: false));
                var button = new WpfUi.Button
                {
                    Content = row,
                    Appearance = WpfUi.ControlAppearance.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(8, 6, 8, 6),
                    Cursor = Cursors.Hand,
                };
                var processName = app.ProcessName;
                button.Click += (_, _) => { picked = processName; sheet.Close(); };
                list.Children.Add(button);
            }
        }
        search.TextChanged += (_, _) => RenderList();
        RenderList();
        var scroller = new ScrollViewer { Content = list, Height = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 0) };
        sheet.SetBody(Ui.VStack(0, search, scroller));
        sheet.AddFooterButton("Browse…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Programs (*.exe)|*.exe",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            };
            if (dialog.ShowDialog(sheet) == true)
            {
                picked = System.IO.Path.GetFileName(dialog.FileName);
                icons.Remember(picked, dialog.FileName);
            }
            sheet.Close();
        }, alignLeft: true);
        sheet.AddFooterButton("Close", sheet.Close, isCancel: true);
        sheet.Loaded += (_, _) => search.Focus();
        sheet.ShowDialog();
        return picked;
    }
}
