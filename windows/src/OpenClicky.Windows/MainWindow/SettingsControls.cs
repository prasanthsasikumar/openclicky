// Ports the small rows and controls of SettingsCatalog.swift / SettingsPages.swift: SettingsRow
// (title, a line of copy, the control on the right), SettingsSwitch, SettingsTag, SettingsKeys,
// SettingsFootnote, SettingsRadioDot and the search highlighter, drawn with WPF-UI's Windows 11
// controls. A row is a card, as in Windows Settings; LiveContent redraws a row when what it shows
// changes (the Mac's @ObservedObject).

using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Settings;
using WpfUi = Wpf.Ui.Controls;

namespace OpenClicky.Windows.AppWindow;

internal static class Ui
{
    public static TextBlock Text(string text, double size = SettingsLook.RowTitleSize, string brushKey = SettingsLook.TextPrimary,
        FontWeight? weight = null, bool wrap = true)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    /// <summary><paramref name="text"/> with every word of <paramref name="query"/> marked with the highlighter.</summary>
    public static TextBlock Highlighted(string text, string query, double size = SettingsLook.RowTitleSize, string brushKey = SettingsLook.TextPrimary,
        FontWeight? weight = null)
    {
        var block = Text("", size, brushKey, weight);
        var ranges = string.IsNullOrWhiteSpace(query) ? [] : SettingsHighlight.Ranges(text, query);
        var position = 0;
        var highlighter = new SolidColorBrush(SettingsLook.Highlighter);
        foreach (var range in ranges)
        {
            if (range.Start > position) block.Inlines.Add(new Run(text[position..range.Start]));
            block.Inlines.Add(new Run(text.Substring(range.Start, range.Length)) { Background = highlighter });
            position = range.Start + range.Length;
        }
        if (position < text.Length) block.Inlines.Add(new Run(text[position..]));
        return block;
    }

    /// <summary>A title and, under it, its copy in the secondary colour.</summary>
    public static StackPanel TitleAndDetail(string title, string? detail, string query, string detailBrushKey = SettingsLook.TextSecondary,
        double titleSize = SettingsLook.RowTitleSize, FontWeight? titleWeight = null)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Highlighted(title, query, titleSize, SettingsLook.TextPrimary, titleWeight));
        if (!string.IsNullOrEmpty(detail))
        {
            var detailBlock = Highlighted(detail, query, SettingsLook.CaptionSize, detailBrushKey);
            detailBlock.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(detailBlock);
        }
        return stack;
    }

    /// <summary>A settings row: title, a line of copy, and the control on the right.</summary>
    public static Grid Row(string title, string? detail, string query, UIElement? trailing, string detailBrushKey = SettingsLook.TextSecondary)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = TitleAndDetail(title, detail, query, detailBrushKey);
        grid.Children.Add(text);
        if (trailing is FrameworkElement trailingElement)
        {
            trailingElement.Margin = new Thickness(16, 0, 0, 0);
            trailingElement.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(trailingElement, 1);
            grid.Children.Add(trailingElement);
        }
        return grid;
    }

    /// <summary>A card: one row of a page, as Windows Settings draws them.</summary>
    public static Border Card(UIElement content, Thickness? padding = null)
    {
        var card = new Border
        {
            Child = content,
            Padding = padding ?? new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            MinHeight = 56,
            Margin = new Thickness(0, 0, 0, 3),
        };
        card.SetResourceReference(Border.BackgroundProperty, SettingsLook.CardBackground);
        card.SetResourceReference(Border.BorderBrushProperty, SettingsLook.CardStroke);
        return card;
    }

    public static WpfUi.ToggleSwitch Toggle(bool isOn, Action<bool> onChange, bool isEnabled = true)
    {
        var toggle = new WpfUi.ToggleSwitch { IsChecked = isOn, IsEnabled = isEnabled, Cursor = Cursors.Hand };
        toggle.Checked += (_, _) => onChange(true);
        toggle.Unchecked += (_, _) => onChange(false);
        return toggle;
    }

    public static ComboBox Picker<T>(IEnumerable<(T Value, string Title)> options, T selected, Action<T> onChange, double minWidth = 160,
        bool isEnabled = true, double? maxWidth = null)
    {
        var comboBox = new ComboBox { MinWidth = minWidth, IsEnabled = isEnabled, Cursor = Cursors.Hand };
        if (maxWidth is { } width) comboBox.MaxWidth = width;
        var selectedIndex = -1;
        foreach (var (value, title) in options)
        {
            if (Equals(value, selected)) selectedIndex = comboBox.Items.Count;
            comboBox.Items.Add(new ComboBoxItem { Content = title, Tag = value });
        }
        comboBox.SelectedIndex = selectedIndex;
        comboBox.SelectionChanged += (_, _) =>
        {
            if (comboBox.SelectedItem is ComboBoxItem { Tag: T value } && !Equals(value, selected)) onChange(value);
        };
        return comboBox;
    }

    public static WpfUi.Button Button(string title, Action onClick, WpfUi.ControlAppearance appearance = WpfUi.ControlAppearance.Secondary,
        bool isEnabled = true, string? toolTip = null)
    {
        var button = new WpfUi.Button { Content = title, Appearance = appearance, IsEnabled = isEnabled, Cursor = Cursors.Hand, Padding = new Thickness(12, 5, 12, 6) };
        if (toolTip is not null) button.ToolTip = toolTip;
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A small text button in the accent, like a link ("use my own key instead").</summary>
    public static Button LinkButton(string title, Action onClick, double size = SettingsLook.CaptionSize)
    {
        var text = Text(title, size, SettingsLook.Accent, wrap: false);
        text.TextDecorations = null;
        var button = new Button
        {
            Content = text,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Template = TransparentButtonTemplate(),
        };
        button.Click += (_, _) => onClick();
        button.MouseEnter += (_, _) => text.TextDecorations = TextDecorations.Underline;
        button.MouseLeave += (_, _) => text.TextDecorations = null;
        return button;
    }

    private static ControlTemplate? transparentButtonTemplate;

    /// <summary>A button drawn only as its content (rows that are clickable as a whole).</summary>
    public static ControlTemplate TransparentButtonTemplate()
    {
        if (transparentButtonTemplate is not null) return transparentButtonTemplate;
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.AppendChild(presenter);
        transparentButtonTemplate = new ControlTemplate(typeof(ButtonBase)) { VisualTree = border };
        transparentButtonTemplate.Seal();
        return transparentButtonTemplate;
    }

    /// <summary>A small tag beside a title ("Private · offline", "Audio goes to Sarvam").</summary>
    public static Border Tag(string text, bool isPositive = false)
    {
        var label = Text(text, MicroSizeFor(text), isPositive ? SettingsLook.Success : SettingsLook.TextSecondary, FontWeights.SemiBold, wrap: false);
        var tag = new Border { Child = label, Padding = new Thickness(6, 1, 6, 2), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        if (isPositive)
        {
            var green = new SolidColorBrush(Color.FromArgb(0x26, 0x34, 0xC7, 0x59));
            tag.Background = green;
        }
        else
        {
            tag.SetResourceReference(Border.BackgroundProperty, SettingsLook.SubtleFill);
        }
        return tag;
    }

    private static double MicroSizeFor(string text) => SettingsLook.MicroSize;

    /// <summary>Keycaps for a shortcut row ("Ctrl" "Win" hold): one cap per key and the gesture beside them.</summary>
    public static StackPanel Keys(IReadOnlyList<string> keys, string? gesture)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var key in keys)
        {
            var cap = new Border
            {
                Child = Text(key, 12, SettingsLook.TextPrimary, wrap: false),
                MinWidth = 26,
                MinHeight = 24,
                Padding = new Thickness(7, 2, 7, 3),
                Margin = new Thickness(0, 0, 4, 0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1, 1, 1, 2),
            };
            ((TextBlock)cap.Child).HorizontalAlignment = HorizontalAlignment.Center;
            ((TextBlock)cap.Child).VerticalAlignment = VerticalAlignment.Center;
            cap.SetResourceReference(Border.BackgroundProperty, SettingsLook.ControlFill);
            cap.SetResourceReference(Border.BorderBrushProperty, SettingsLook.ControlStroke);
            row.Children.Add(cap);
        }
        if (gesture is not null)
        {
            var gestureText = Text(gesture, 12, SettingsLook.TextSecondary, wrap: false);
            gestureText.Margin = new Thickness(4, 0, 0, 0);
            gestureText.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(gestureText);
        }
        return row;
    }

    /// <summary>A note under a section ("Your keys go straight to that provider…").</summary>
    public static TextBlock Footnote(string text, string query)
    {
        var block = Highlighted(text, query, SettingsLook.CaptionSize, SettingsLook.TextSecondary);
        block.Margin = new Thickness(4, 4, 4, 6);
        return block;
    }

    /// <summary>A radio button drawn like the system's, in the accent.</summary>
    public static FrameworkElement RadioDot(bool isSelected)
    {
        var grid = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        var outer = new System.Windows.Shapes.Ellipse { StrokeThickness = isSelected ? 0 : 1 };
        if (isSelected) outer.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, SettingsLook.Accent);
        else
        {
            outer.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, SettingsLook.ControlFill);
            outer.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, SettingsLook.TextSecondary);
        }
        grid.Children.Add(outer);
        if (isSelected) grid.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Brushes.White });
        return grid;
    }

    /// <summary>A whole option clickable: the radio dot, a title (with an optional tag) and its copy, and something on the right.</summary>
    public static Button RadioOption(bool isSelected, string title, string detail, string query, Action onSelect, UIElement? titleAccessory = null,
        UIElement? trailing = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = RadioDot(isSelected);
        grid.Children.Add(dot);
        var titleLine = new WrapPanel { Orientation = Orientation.Horizontal };
        titleLine.Children.Add(Highlighted(title, query));
        if (titleAccessory is FrameworkElement accessory)
        {
            accessory.Margin = new Thickness(8, 1, 0, 0);
            titleLine.Children.Add(accessory);
        }
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        text.Children.Add(titleLine);
        var detailText = Highlighted(detail, query, SettingsLook.CaptionSize, SettingsLook.TextSecondary);
        detailText.Margin = new Thickness(0, 2, 0, 0);
        text.Children.Add(detailText);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (trailing is FrameworkElement trailingElement)
        {
            trailingElement.Margin = new Thickness(12, 0, 0, 0);
            trailingElement.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(trailingElement, 2);
            grid.Children.Add(trailingElement);
        }
        var button = new Button
        {
            Content = grid,
            Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Template = TransparentButtonTemplate(),
            Padding = new Thickness(0),
        };
        AutomationProperties(button, $"{title}{(isSelected ? ", selected" : "")}", detail);
        button.Click += (_, _) => onSelect();
        return button;
    }

    public static void AutomationProperties(DependencyObject element, string name, string? helpText = null)
    {
        System.Windows.Automation.AutomationProperties.SetName(element, name);
        if (!string.IsNullOrEmpty(helpText)) System.Windows.Automation.AutomationProperties.SetHelpText(element, helpText);
    }

    public static StackPanel HStack(double spacing, params UIElement?[] children)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var first = true;
        foreach (var child in children)
        {
            if (child is null) continue;
            if (!first && child is FrameworkElement element) element.Margin = new Thickness(spacing, element.Margin.Top, element.Margin.Right, element.Margin.Bottom);
            if (child is FrameworkElement centered) centered.VerticalAlignment = VerticalAlignment.Center;
            stack.Children.Add(child);
            first = false;
        }
        return stack;
    }

    public static StackPanel VStack(double spacing, params UIElement?[] children)
    {
        var stack = new StackPanel();
        var first = true;
        foreach (var child in children)
        {
            if (child is null) continue;
            if (!first && child is FrameworkElement element) element.Margin = new Thickness(element.Margin.Left, spacing, element.Margin.Right, element.Margin.Bottom);
            stack.Children.Add(child);
            first = false;
        }
        return stack;
    }

    public static Separator Divider()
    {
        var separator = new Separator { Margin = new Thickness(0, 8, 0, 8) };
        separator.SetResourceReference(Control.BackgroundProperty, SettingsLook.Divider);
        return separator;
    }

    public static void OpenExternal(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"window: couldn't open {target}: {error.Message}");
        }
    }

    /// <summary>Explorer with the file selected (Show in Finder).</summary>
    public static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            else if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder)) System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\"");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"window: couldn't show {path}: {error.Message}");
        }
    }
}

/// <summary>
/// A part of a page that redraws when what it shows changes: <c>build</c> runs now and again (once
/// per dispatcher turn) after any of the sources fires, while the element is loaded.
/// </summary>
internal sealed class LiveContent : ContentControl
{
    private readonly Func<UIElement> build;
    private readonly Func<Action, IDisposable>[] sources;
    private readonly List<IDisposable> subscriptions = [];
    private bool rebuildQueued;

    public LiveContent(Func<UIElement> build, params Func<Action, IDisposable>[] sources)
    {
        this.build = build;
        this.sources = sources;
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Content = build();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    public void Rebuild()
    {
        rebuildQueued = false;
        Content = build();
    }

    private void QueueRebuild()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(QueueRebuild);
            return;
        }
        if (rebuildQueued) return;
        rebuildQueued = true;
        Dispatcher.BeginInvoke(Rebuild, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Subscribe()
    {
        if (subscriptions.Count > 0) return;
        foreach (var source in sources) subscriptions.Add(source(QueueRebuild));
    }

    private void Unsubscribe()
    {
        foreach (var subscription in subscriptions) subscription.Dispose();
        subscriptions.Clear();
    }
}

/// <summary>What a LiveContent listens to.</summary>
internal static class Changes
{
    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? disposeAction = dispose;
        public void Dispose() => Interlocked.Exchange(ref disposeAction, null)?.Invoke();
    }

    public static Func<Action, IDisposable> Of(INotifyPropertyChanged source, params string[] propertyNames) => changed =>
    {
        void OnChanged(object? sender, PropertyChangedEventArgs arguments)
        {
            if (propertyNames.Length == 0 || arguments.PropertyName is null || propertyNames.Contains(arguments.PropertyName)) changed();
        }
        source.PropertyChanged += OnChanged;
        return new Subscription(() => source.PropertyChanged -= OnChanged);
    };

    /// <summary>shell.json changed (a key, the token, a sign-in, the voice), from these pages or by hand.</summary>
    public static Func<Action, IDisposable> Shell => changed =>
    {
        void OnChanged() => changed();
        OpenClickyConfiguration.SettingsChanged += OnChanged;
        OpenClickyConfiguration.CredentialsChanged += OnChanged;
        ShellSettingsRevision.Changed += OnChanged;
        return new Subscription(() =>
        {
            OpenClickyConfiguration.SettingsChanged -= OnChanged;
            OpenClickyConfiguration.CredentialsChanged -= OnChanged;
            ShellSettingsRevision.Changed -= OnChanged;
        });
    };

    public static Func<Action, IDisposable> Event(Action<Action> add, Action<Action> remove) => changed =>
    {
        void OnChanged() => changed();
        add(OnChanged);
        return new Subscription(() => remove(OnChanged));
    };

    /// <summary>Every <paramref name="interval"/> (the permission row's poll).</summary>
    public static Func<Action, IDisposable> Every(TimeSpan interval) => changed =>
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => changed();
        timer.Start();
        return new Subscription(timer.Stop);
    };
}

/// <summary>Bumps whenever the window itself changed shell.json, so rows reading it redraw (ShellSettingsRevision).</summary>
internal static class ShellSettingsRevision
{
    public static event Action? Changed;

    public static void NoteChanged() => Changed?.Invoke();
}
