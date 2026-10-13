// Ports Dictation/UI/WordsPageView.swift: "Your words", the record and history pages as one, drawn
// like the settings pages. At the top, how to dictate and who hears you; a "try it here" box a take
// lands in when this window is in front; this week's count; then every take, newest first, a
// section per day. Typing in the search filters the takes and Enter asks a model across them; the
// answer names the takes it was drawn from. A row selects with a click (details · copy),
// double-click opens it, a failed take offers retry · delete, and every action is also on the
// row's context menu.

using OpenClicky.Core.Shortcuts;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Settings;
using WpfUi = Wpf.Ui.Controls;

namespace OpenClicky.Windows.AppWindow;

internal sealed class WordsPageView : ScrollViewer
{
    private static readonly string OwnProcessName = Process.GetCurrentProcess().ProcessName + ".exe";

    private readonly MainWindowServices services;
    private readonly DictationWindowModel model;
    private readonly IMainWindowActions actions;
    private readonly AppIconCache icons;
    private DictationSettings Settings => services.Settings;

    private readonly WpfUi.TextBox tryItHereBox;
    private readonly TextBlock tryItHereCount = Ui.Text("0 words", SettingsLook.CaptionSize, SettingsLook.TextSecondary, wrap: false);
    private readonly WpfUi.Button copyTryItButton;
    private readonly WpfUi.Button clearTryItButton;
    private readonly ContentControl thisWeek = new() { Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly WpfUi.TextBox searchField;
    private readonly WpfUi.Button askButton;
    private readonly ContentControl filters = new() { Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock searchFootnote = Ui.Text("", SettingsLook.CaptionSize, SettingsLook.TextSecondary);
    private readonly StackPanel answerArea = new();
    private readonly StackPanel takesArea = new();

    private string query = "";
    private TakeMode? modeFilter;
    private string? appFilter;
    private IReadOnlyList<TakeRecord> takes = [];
    private IReadOnlySet<Guid> revisedTakeIds = new HashSet<Guid>();
    private Guid? selectedTakeId;
    private string? answer;
    private IReadOnlyList<TakeRecord> answerSources = [];
    private bool isAsking;
    private string? historyProblem;

    public WordsPageView(MainWindowServices services, DictationWindowModel model, IMainWindowActions actions, AppIconCache icons)
    {
        this.services = services;
        this.model = model;
        this.actions = actions;
        this.icons = icons;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = false;

        var column = SettingsFormView.PageColumn(DictationSection.Words.SidebarTitle());
        column.Children.Add(new LiveContent(HowToDictateSection, Changes.Of(Settings), Changes.Shell));

        // Try it here: the box a take lands in when this window is in front and no field took it.
        column.Children.Add(SettingsFormView.SectionHeader("Try it here"));
        tryItHereBox = new WpfUi.TextBox
        {
            PlaceholderText = "With this window in front, your words land here.",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 72,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        copyTryItButton = Ui.Button("Copy", () => services.Clipboard.SetText(tryItHereBox.Text), isEnabled: false);
        clearTryItButton = Ui.Button("Clear", () => tryItHereBox.Text = "", isEnabled: false);
        tryItHereBox.TextChanged += (_, _) =>
        {
            tryItHereCount.Text = WordsPageSentences.WordCount(tryItHereBox.Text);
            copyTryItButton.IsEnabled = clearTryItButton.IsEnabled = tryItHereBox.Text.Length > 0;
        };
        var tryItFoot = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var tryItButtons = Ui.HStack(8, copyTryItButton, clearTryItButton);
        DockPanel.SetDock(tryItButtons, Dock.Right);
        tryItFoot.Children.Add(tryItButtons);
        tryItHereCount.VerticalAlignment = VerticalAlignment.Center;
        tryItFoot.Children.Add(tryItHereCount);
        column.Children.Add(Ui.Card(Ui.VStack(0, tryItHereBox, tryItFoot)));

        column.Children.Add(SettingsFormView.SectionHeader("This week"));
        column.Children.Add(thisWeek);

        // Your takes: search, ask, filters.
        column.Children.Add(SettingsFormView.SectionHeader("Your takes"));
        searchField = new WpfUi.TextBox
        {
            PlaceholderText = "What did I say about…?",
            Icon = new WpfUi.SymbolIcon { Symbol = WpfUi.SymbolRegular.Search24 },
            IconPlacement = WpfUi.ElementPlacement.Left,
            ClearButtonEnabled = true,
        };
        Ui.AutomationProperties(searchField, "Search your takes");
        askButton = Ui.Button("Ask", Ask, isEnabled: false, toolTip: "Ask a model across your takes (Enter)");
        searchField.TextChanged += (_, _) =>
        {
            query = searchField.Text;
            if (query.Length == 0) { answer = null; answerSources = []; RenderAnswer(); }
            askButton.IsEnabled = CanAsk;
            Reload();
        };
        searchField.KeyDown += (_, arguments) =>
        {
            if (arguments.Key == Key.Enter) { Ask(); arguments.Handled = true; }
        };
        var searchRow = new DockPanel();
        DockPanel.SetDock(askButton, Dock.Right);
        askButton.Margin = new Thickness(8, 0, 0, 0);
        searchRow.Children.Add(askButton);
        searchRow.Children.Add(searchField);
        var filtersCard = Ui.Card(Ui.VStack(10, searchRow, filters));
        column.Children.Add(filtersCard);
        searchFootnote.Margin = new Thickness(4, 4, 4, 6);
        column.Children.Add(searchFootnote);
        column.Children.Add(answerArea);
        column.Children.Add(takesArea);
        Content = column;

        void OnHistoryChanged() => Dispatcher.BeginInvoke(Reload);
        void OnTakeFinished(TakeRecord take) => Dispatcher.BeginInvoke(() => CatchTake(take));
        Loaded += (_, _) =>
        {
            services.History.HistoryChanged += OnHistoryChanged;
            services.History.TakeFinished += OnTakeFinished;
            if (model.PendingHistorySearch is { } pendingSearch)
            {
                searchField.Text = pendingSearch;
                model.PendingHistorySearch = null;
            }
        };
        Unloaded += (_, _) =>
        {
            services.History.HistoryChanged -= OnHistoryChanged;
            services.History.TakeFinished -= OnTakeFinished;
        };
        Reload();
    }

    private bool CanAsk => query.Trim().Length > 0 && !isAsking;

    /// <summary>A take while this window was in front and no field took it: it lands in the box (one pasted into the box itself is already there).</summary>
    private void CatchTake(TakeRecord take)
    {
        var landedNowhere = take.PasteOutcome == TakePasteOutcome.LeftInOrb
            || (string.Equals(take.AppProcessName, OwnProcessName, StringComparison.OrdinalIgnoreCase) && take.PasteOutcome != TakePasteOutcome.Verified);
        if (!landedNowhere) return;
        tryItHereBox.Text = tryItHereBox.Text.Length == 0 ? take.DisplayText : tryItHereBox.Text + " " + take.DisplayText;
    }

    // MARK: - how to dictate, and who hears you

    private StackPanel HowToDictateSection()
    {
        var keyLabel = Settings.DictationKey.KeycapLabel();
        var unavailableReason = DictationEngineResolver.UnavailableReason(Settings.Engine);
        var howTo = Ui.Card(Ui.Row($"Hold {keyLabel} in any app, speak, let go.",
            "Words are tidied for the app in front and pasted at the cursor. Tap to start, tap to finish; Esc discards.", "",
            Ui.Keys([keyLabel], "hold")));

        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, unavailableReason is null ? SettingsLook.Success : SettingsLook.Danger);
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(dot);
        title.Children.Add(Ui.Text(Settings.Engine.HowToDictateTitle(), SettingsLook.RowTitleSize, SettingsLook.TextPrimary, wrap: false));
        var sentence = unavailableReason is not null
            ? Ui.Text(WordsPageSentences.SentenceCased(unavailableReason) + ".", SettingsLook.CaptionSize, SettingsLook.Danger)
            : Ui.Text(WordsPageSentences.EngineSentenceInSentenceCase(Settings, DictationEngineResolver.MakePolisher() is not null), SettingsLook.CaptionSize, SettingsLook.TextSecondary);
        sentence.Margin = new Thickness(0, 2, 0, 0);
        var text = Ui.VStack(0, title, sentence);
        var engineRow = new Grid();
        engineRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        engineRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        engineRow.Children.Add(text);
        var change = Ui.Button("Change Engine…", () => actions.ShowSettings(DictationSettingsPage.Dictation), toolTip: "Opens Settings → Dictation");
        change.Margin = new Thickness(16, 0, 0, 0);
        change.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(change, 1);
        engineRow.Children.Add(change);
        return Ui.VStack(0, howTo, Ui.Card(engineRow));
    }

    // MARK: - this week

    private Border ThisWeekCard(TakeStats stats, IReadOnlyList<DayWords> wordsPerDay)
    {
        var count = Ui.Text(stats.WordsThisWeek.ToString("N0", CultureInfo.CurrentCulture), 30, SettingsLook.TextPrimary, FontWeights.SemiBold, wrap: false);
        var takesLine = Ui.Text($"words in {WordsPageSentences.Plural(stats.TakesThisWeek, "take")}", SettingsLook.CaptionSize, SettingsLook.TextSecondary, wrap: false);
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var numbers = Ui.VStack(0, count, takesLine);
        numbers.VerticalAlignment = VerticalAlignment.Bottom;
        top.Children.Add(numbers);
        var bars = WeekBars(wordsPerDay);
        Grid.SetColumn(bars, 1);
        top.Children.Add(bars);

        Grid LabeledLine(string label, string value)
        {
            var line = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(Ui.Text(label, SettingsLook.RowTitleSize, SettingsLook.TextPrimary, wrap: false));
            var valueText = Ui.Text(value, SettingsLook.RowTitleSize, SettingsLook.TextSecondary, wrap: false);
            Grid.SetColumn(valueText, 1);
            line.Children.Add(valueText);
            return line;
        }

        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(Ui.Divider());
        var today = LabeledLine("Today", $"{WordsPageSentences.Plural(stats.WordsToday, "word")} · {WordsPageSentences.Plural(stats.TakesToday, "take")}");
        today.Margin = new Thickness(0);
        stack.Children.Add(today);
        stack.Children.Add(LabeledLine("All time", WordsPageSentences.Plural(stats.AllTimeTakes, "take")));
        return Ui.Card(stack, new Thickness(16, 14, 16, 14));
    }

    /// <summary>Seven bars, today on the right in the accent, scaled to the busiest day.</summary>
    private static StackPanel WeekBars(IReadOnlyList<DayWords> wordsPerDay)
    {
        const double maximumBarHeight = 36;
        var busiestDayWords = Math.Max(1, wordsPerDay.Count == 0 ? 1 : wordsPerDay.Max(entry => entry.Words));
        var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        for (var index = 0; index < wordsPerDay.Count; index++)
        {
            var entry = wordsPerDay[index];
            var isToday = index == wordsPerDay.Count - 1;
            var bar = new Border
            {
                Width = 14,
                Height = Math.Max(2, maximumBarHeight * entry.Words / busiestDayWords),
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Bottom,
                ToolTip = $"{entry.Words} words",
            };
            bar.SetResourceReference(Border.BackgroundProperty, isToday ? SettingsLook.Accent : "ControlStrongFillColorDisabledBrush");
            var initial = Ui.Text(entry.Day.ToString("ddd", CultureInfo.CurrentCulture)[..1], SettingsLook.MicroSize, SettingsLook.TextSecondary, wrap: false);
            initial.HorizontalAlignment = HorizontalAlignment.Center;
            initial.Margin = new Thickness(0, 3, 0, 0);
            var day = new StackPanel { Margin = new Thickness(2.5, 0, 2.5, 0), VerticalAlignment = VerticalAlignment.Bottom };
            var barArea = new Grid { Height = maximumBarHeight };
            barArea.Children.Add(bar);
            day.Children.Add(barArea);
            day.Children.Add(initial);
            Ui.AutomationProperties(day, $"{entry.Day.ToString("dddd", CultureInfo.CurrentCulture)}: {entry.Words} words");
            bars.Children.Add(day);
        }
        return bars;
    }

    // MARK: - search and ask

    private void RenderFilters(IReadOnlyList<Core.Dictation.AppUsage> appsUsed)
    {
        var appOptions = new List<(string Id, string Title)> { ("", "All apps") };
        appOptions.AddRange(appsUsed.Select(app => (app.AppProcessName, app.Name ?? icons.Name(app.AppProcessName) ?? app.AppProcessName)));
        var appPicker = Ui.Picker(appOptions, appFilter ?? "", id => { appFilter = id.Length == 0 ? null : id; Reload(); }, 200);
        var kindOptions = new List<(string Id, string Title)> { ("", "Every kind of take"), ("dictate", "Dictations"), ("edit", "Hey Clicky edits") };
        if (Settings.ClipboardHistoryEnabled || modeFilter == TakeMode.Clipboard) kindOptions.Add(("clipboard", "Clipboard"));
        var kindPicker = Ui.Picker(kindOptions, modeFilter switch { TakeMode.Dictate => "dictate", TakeMode.Edit => "edit", TakeMode.Clipboard => "clipboard", _ => "" },
            id =>
            {
                modeFilter = id switch { "dictate" => TakeMode.Dictate, "edit" => TakeMode.Edit, "clipboard" => TakeMode.Clipboard, _ => null };
                Reload();
            }, 200);
        Grid Labeled(string label, Control picker)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = Ui.Text(label, SettingsLook.RowTitleSize, SettingsLook.TextPrimary, wrap: false);
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            Grid.SetColumn(picker, 1);
            row.Children.Add(picker);
            return row;
        }
        filters.Content = Ui.VStack(8, Labeled("App", appPicker), Labeled("Kind", kindPicker));
    }

    /// <summary>Enter in the field: a question goes to the model over the matching takes.</summary>
    private async void Ask()
    {
        var question = query.Trim();
        if (question.Length == 0 || isAsking) return;
        isAsking = true;
        askButton.IsEnabled = false;
        answerSources = services.History.HistoryMatches(question).Take(8).ToList();
        RenderAnswer();
        string reply;
        try { reply = await services.History.AskHistoryAsync(question); }
        catch (Exception error) { reply = error.Message; }
        answer = reply;
        isAsking = false;
        askButton.IsEnabled = CanAsk;
        RenderAnswer();
    }

    private void RenderAnswer()
    {
        answerArea.Children.Clear();
        if (!isAsking && answer is null) return;
        var caption = isAsking ? "Asking a model"
            : answerSources.Count == 0 ? "Answer from your latest takes"
            : $"Answer from {WordsPageSentences.Plural(answerSources.Count, "take")} that match";
        answerArea.Children.Add(SettingsFormView.SectionHeader(caption));
        var body = new StackPanel();
        if (isAsking)
        {
            body.Children.Add(Ui.HStack(10, new WpfUi.ProgressRing { IsIndeterminate = true, Width = 16, Height = 16 },
                Ui.Text("Asking across your takes…", SettingsLook.RowTitleSize, SettingsLook.TextSecondary, wrap: false)));
        }
        else
        {
            var answerText = new TextBox
            {
                Text = answer,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                FontSize = 14,
                Padding = new Thickness(0),
            };
            answerText.SetResourceReference(Control.ForegroundProperty, SettingsLook.TextPrimary);
            var close = Ui.Button("Close", () => { answer = null; answerSources = []; RenderAnswer(); }, WpfUi.ControlAppearance.Transparent, toolTip: "Close the answer");
            var top = new DockPanel();
            DockPanel.SetDock(close, Dock.Right);
            top.Children.Add(close);
            top.Children.Add(answerText);
            body.Children.Add(top);
            foreach (var source in answerSources)
            {
                var line = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
                var label = Ui.Text(WordsPageSentences.SourceLabel(source, source.AppName ?? (source.AppProcessName is { } process ? icons.Name(process) : null)),
                    SettingsLook.CaptionSize, SettingsLook.TextSecondary, wrap: false);
                DockPanel.SetDock(label, Dock.Right);
                line.Children.Add(label);
                var icon = TakeIcon(source);
                DockPanel.SetDock(icon, Dock.Left);
                line.Children.Add(icon);
                var words = Ui.Text(source.DisplayText.Replace('\n', ' '), SettingsLook.RowTitleSize, SettingsLook.TextPrimary, wrap: false);
                words.Margin = new Thickness(10, 0, 10, 0);
                line.Children.Add(words);
                var reveal = new Button { Content = line, Template = Ui.TransparentButtonTemplate(), Cursor = Cursors.Hand, ToolTip = "Show this take in the list", HorizontalContentAlignment = HorizontalAlignment.Stretch };
                var takeToReveal = source;
                reveal.Click += (_, _) => Reveal(takeToReveal);
                body.Children.Add(reveal);
            }
        }
        answerArea.Children.Add(Ui.Card(body));
    }

    /// <summary>A source selects its take in the list (clearing the filters that would hide it).</summary>
    private void Reveal(TakeRecord take)
    {
        if (!takes.Any(listed => listed.Id == take.Id))
        {
            modeFilter = null;
            appFilter = null;
            searchField.Text = "";
        }
        selectedTakeId = take.Id;
        Reload();
    }

    // MARK: - the takes

    private void Reload()
    {
        var store = services.TakeStore;
        IReadOnlyList<Core.Dictation.AppUsage> appsUsed = [];
        if (store is not null)
        {
            try
            {
                thisWeek.Content = ThisWeekCard(store.Stats(), store.WordsPerDay());
                appsUsed = store.AppsUsed();
                revisedTakeIds = store.RevisedTakeIds();
                var rows = store.Recent(limit: 500, query: query.Length == 0 ? null : query, mode: modeFilter, appProcessName: appFilter);
                if (!Settings.ClipboardHistoryEnabled && modeFilter is null) rows = rows.Where(take => take.Mode != TakeMode.Clipboard).ToList();
                takes = rows;
                historyProblem = null;
            }
            catch (TakeStoreException error)
            {
                AppLog.Write($"history couldn't be read: {error.Message}");
                historyProblem = "History couldn't be read just now. The log says why.";
                takes = [];
            }
        }
        else
        {
            thisWeek.Content = ThisWeekCard(new TakeStats(0, 0, 0, 0, null, 0), []);
        }
        RenderFilters(appsUsed);
        // Without these lines a take missing from history, or a delete that did nothing, has no explanation anywhere.
        if (store is null)
        {
            searchFootnote.Text = "History can't be saved on this PC right now. Takes still paste; the log says why.";
            searchFootnote.SetResourceReference(TextBlock.ForegroundProperty, SettingsLook.Danger);
        }
        else if (historyProblem is not null)
        {
            searchFootnote.Text = historyProblem;
            searchFootnote.SetResourceReference(TextBlock.ForegroundProperty, SettingsLook.Danger);
        }
        else
        {
            searchFootnote.Text = "Type to filter your takes; press Enter to ask a model across them.";
            searchFootnote.SetResourceReference(TextBlock.ForegroundProperty, SettingsLook.TextSecondary);
        }
        RenderTakes();
    }

    private void RenderTakes()
    {
        takesArea.Children.Clear();
        if (takes.Count == 0)
        {
            var empty = Ui.Text(query.Length == 0 ? "Your dictations will land here, with the app each one went to." : "No matching takes. Press Enter to ask anyway.",
                SettingsLook.RowTitleSize, SettingsLook.TextSecondary);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(0, 14, 0, 14);
            takesArea.Children.Add(new Border { Height = 12 });
            takesArea.Children.Add(Ui.Card(empty));
            return;
        }
        foreach (var dayGroup in WordsPageSentences.GroupedByDay(takes))
        {
            var header = new DockPanel { Margin = new Thickness(2, 22, 2, 8) };
            var count = Ui.Text(WordsPageSentences.Plural(dayGroup.Takes.Count, "take"), SettingsLook.CaptionSize, SettingsLook.TextSecondary, wrap: false);
            DockPanel.SetDock(count, Dock.Right);
            header.Children.Add(count);
            header.Children.Add(Ui.Text(dayGroup.Title, SettingsLook.SectionTitleSize, SettingsLook.TextPrimary, FontWeights.SemiBold, wrap: false));
            takesArea.Children.Add(header);
            var rows = new StackPanel();
            foreach (var take in dayGroup.Takes) rows.Children.Add(TakeRow(take));
            var dayCard = Ui.Card(rows, new Thickness(8, 6, 8, 6));
            dayCard.MinHeight = 0;
            takesArea.Children.Add(dayCard);
        }
    }

    private FrameworkElement TakeIcon(TakeRecord take) =>
        take.Mode == TakeMode.Clipboard ? AppIconView.ClipboardIcon() : AppIconView.Make(icons, take.AppProcessName);

    /// <summary>One take as a row: the app it went to, its words, and the time; selected, its actions.</summary>
    private Border TakeRow(TakeRecord take)
    {
        var isSelected = selectedTakeId == take.Id;
        var canRetry = services.History.CanRetry(take.Id);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = TakeIcon(take);
        grid.Children.Add(icon);
        var words = Ui.Text(take.DisplayText.Length == 0 ? " " : take.DisplayText.Replace('\n', ' '), SettingsLook.RowTitleSize, SettingsLook.TextPrimary, wrap: false);
        words.Margin = new Thickness(10, 0, 10, 0);
        words.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);
        var extras = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (take.Pinned)
        {
            var pin = Ui.Text("", 10, SettingsLook.Accent, wrap: false);
            pin.FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            pin.ToolTip = "Pinned";
            pin.Margin = new Thickness(0, 0, 8, 0);
            pin.VerticalAlignment = VerticalAlignment.Center;
            extras.Children.Add(pin);
        }
        if (take.Mode == TakeMode.Edit)
        {
            var tag = Ui.Tag("Hey Clicky");
            tag.Margin = new Thickness(0, 0, 8, 0);
            extras.Children.Add(tag);
        }
        if (take.Status == TakeStatus.Failed)
        {
            var failure = Ui.Text(WordsPageSentences.FailureLine(take), SettingsLook.CaptionSize, SettingsLook.Danger, wrap: false);
            failure.MaxWidth = 260;
            failure.Margin = new Thickness(0, 0, 8, 0);
            failure.VerticalAlignment = VerticalAlignment.Center;
            extras.Children.Add(failure);
            if (canRetry) extras.Children.Add(SmallButton("Retry", () => _ = services.History.RetryAsync(take.Id), "Hear the saved recording again"));
            extras.Children.Add(SmallButton("Delete", () => Delete(take), "Delete this take", WpfUi.ControlAppearance.Danger));
        }
        else if (isSelected)
        {
            extras.Children.Add(SmallButton("Details…", () => Open(take), "Open the take to edit, pin, paste again or delete it"));
            extras.Children.Add(SmallButton("Copy", () => Copy(take), "Copy the take"));
        }
        else if (revisedTakeIds.Contains(take.Id))
        {
            extras.Children.Add(Ui.Text("Edited", SettingsLook.CaptionSize, SettingsLook.TextSecondary, wrap: false));
        }
        Grid.SetColumn(extras, 2);
        grid.Children.Add(extras);
        var time = Ui.Text(take.CreatedAt.LocalDateTime.ToString("t", CultureInfo.CurrentCulture), SettingsLook.CaptionSize, SettingsLook.TextSecondary, wrap: false);
        time.MinWidth = 64;
        time.TextAlignment = TextAlignment.Right;
        time.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(time, 3);
        grid.Children.Add(time);

        var row = new Border { Child = grid, Padding = new Thickness(8, 7, 8, 7), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent };
        if (isSelected) row.SetResourceReference(Border.BackgroundProperty, SettingsLook.Selection);
        row.MouseEnter += (_, _) => { if (!isSelected) row.SetResourceReference(Border.BackgroundProperty, SettingsLook.SubtleFill); };
        row.MouseLeave += (_, _) => { if (!isSelected) row.Background = System.Windows.Media.Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, arguments) =>
        {
            if (arguments.OriginalSource is DependencyObject source && FindAncestor<ButtonBase>(source) is not null) return;
            selectedTakeId = isSelected ? null : take.Id;
            RenderTakes();
        };
        row.MouseLeftButtonDown += (_, arguments) =>
        {
            if (arguments.ClickCount == 2) { Open(take); arguments.Handled = true; }
        };
        row.ToolTip = $"{(take.Mode == TakeMode.Edit ? "Hey Clicky edit" : "Dictation")} into {take.AppName ?? "an unknown app"}. Double-click to open.";
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Copy", () => Copy(take), take.DisplayText.Length > 0));
        menu.Items.Add(MenuItem("Details…", () => Open(take)));
        if (take.Status == TakeStatus.Failed && canRetry) menu.Items.Add(MenuItem("Retry", () => _ = services.History.RetryAsync(take.Id)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Delete", () => Delete(take)));
        row.ContextMenu = menu;
        Ui.AutomationProperties(row, take.DisplayText.Length == 0 ? WordsPageSentences.FailureLine(take) : take.DisplayText,
            $"{take.AppName ?? "Unknown app"}, {take.CreatedAt.LocalDateTime.ToString("t", CultureInfo.CurrentCulture)}");
        return row;
    }

    private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var current = element; current is not null; current = current is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is T found) return found;
        }
        return null;
    }

    private static WpfUi.Button SmallButton(string title, Action onClick, string toolTip, WpfUi.ControlAppearance appearance = WpfUi.ControlAppearance.Secondary)
    {
        var button = Ui.Button(title, onClick, appearance, toolTip: toolTip);
        button.Padding = new Thickness(8, 2, 8, 3);
        button.FontSize = 12;
        button.Margin = new Thickness(0, 0, 6, 0);
        return button;
    }

    private static MenuItem MenuItem(string title, Action onClick, bool isEnabled = true)
    {
        var item = new MenuItem { Header = title, IsEnabled = isEnabled };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void Copy(TakeRecord take) => services.Clipboard.SetText(take.DisplayText);

    private void Delete(TakeRecord take)
    {
        if (!HistoryWrite("delete", take.Id, () => services.TakeStore?.Delete(take.Id)))
        {
            historyProblem = "Couldn't delete that take. The log says why.";
        }
        if (selectedTakeId == take.Id) selectedTakeId = null;
        services.History.HistoryDidChange();
        Reload();
    }

    private void Open(TakeRecord take)
    {
        TakeDetailSheet.Show(actions.OwnerWindow, services, icons, take);
        Reload();
    }

    /// <summary>A change this page makes to the history file. A failure (the file locked, a full disk) is logged rather than lost.</summary>
    internal static bool HistoryWrite(string whatIsWritten, Guid takeId, Action write)
    {
        try
        {
            write();
            return true;
        }
        catch (TakeStoreException error)
        {
            AppLog.Write($"history {whatIsWritten} failed for take {takeId.ToString()[..8]}: {error.Message}");
            return false;
        }
    }
}

/// <summary>One take, opened: the written text to edit, the raw words, what happened to it, and copy, pin, paste again, delete and save.</summary>
internal static class TakeDetailSheet
{
    public static void Show(Window owner, MainWindowServices services, AppIconCache icons, TakeRecord take)
    {
        var sheet = new SheetWindow(owner, services.Settings, take.AppName ?? "Take", 580);
        var header = Ui.HStack(10, AppIconView.Make(icons, take.AppProcessName, 24),
            Ui.VStack(2, Ui.Text(take.AppName ?? "Take", 14, SettingsLook.TextPrimary, FontWeights.SemiBold, wrap: false),
                Ui.Text(WordsPageSentences.TakeDetailLine(take), SettingsLook.CaptionSize, SettingsLook.TextSecondary, wrap: false)));
        var editor = new WpfUi.TextBox { Text = take.DisplayText, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, VerticalContentAlignment = VerticalAlignment.Top };
        var body = new StackPanel();
        body.Children.Add(Ui.Card(header));
        body.Children.Add(SettingsFormView.SectionHeader("Written"));
        body.Children.Add(editor);
        if (take.RawText != take.FormattedText && take.RawText.Length > 0)
        {
            body.Children.Add(SettingsFormView.SectionHeader("What you said"));
            body.Children.Add(Ui.Card(Ui.Text(take.RawText, SettingsLook.RowTitleSize, SettingsLook.TextSecondary)));
        }
        if (take.FailureReason is { } failureReason)
        {
            var failure = Ui.Card(Ui.Text(failureReason, SettingsLook.RowTitleSize, SettingsLook.Danger));
            failure.Margin = new Thickness(0, 14, 0, 0);
            body.Children.Add(failure);
        }
        sheet.SetBody(body);
        sheet.AddFooterButton("Copy", () => services.Clipboard.SetText(editor.Text), alignLeft: true);
        sheet.AddFooterButton(take.Pinned ? "Unpin" : "Pin", () =>
        {
            WordsPageView.HistoryWrite(take.Pinned ? "unpin" : "pin", take.Id, () => services.TakeStore?.SetPinned(!take.Pinned, take.Id));
            services.History.HistoryDidChange();
            sheet.Close();
        }, alignLeft: true);
        var pasteAgain = sheet.AddFooterButton("Paste Again", () =>
        {
            var textToPaste = editor.Text;
            sheet.Close();
            // The window steps aside so the app behind it is in front for the paste.
            owner.WindowState = WindowState.Minimized;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(0.45) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                services.TextInserter.Insert(textToPaste);
            };
            timer.Start();
        }, alignLeft: true);
        pasteAgain.ToolTip = "Minimizes OpenClicky, then pastes into the app behind it";
        sheet.AddFooterButton("Delete", () =>
        {
            WordsPageView.HistoryWrite("delete", take.Id, () => services.TakeStore?.Delete(take.Id));
            services.History.HistoryDidChange();
            sheet.Close();
        }, alignLeft: true, appearance: WpfUi.ControlAppearance.Danger);
        sheet.AddFooterButton("Close", sheet.Close, isCancel: true);
        var save = sheet.AddFooterButton("Save Edit", () =>
        {
            WordsPageView.HistoryWrite("save edit", take.Id, () => services.TakeStore?.Revise(take.Id, editor.Text, "history"));
            services.History.HistoryDidChange();
            sheet.Close();
        }, isDefault: true);
        save.Visibility = Visibility.Collapsed;
        editor.TextChanged += (_, _) => save.Visibility = editor.Text != take.DisplayText ? Visibility.Visible : Visibility.Collapsed;
        sheet.ShowDialog();
    }
}
