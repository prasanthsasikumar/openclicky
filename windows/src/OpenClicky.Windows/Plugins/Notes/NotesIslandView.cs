//
//  NotesIslandView.cs
//  OpenClicky (Windows port of Plugins/Notes/NotesIslandView.swift)
//
//  The Notes plugin's view: a plain multi-line notepad on the island, saved on this PC as you type.
//  The island never takes the keyboard on its own (it is a non-activating window), so a click in
//  the pad borrows it, like the composer does (IslandWindow.BeginComponentTyping), and esc, the
//  island closing or the pad going away gives it back to the window that had it.
//

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenClicky.Core.Plugins.Notes;
using OpenClicky.Windows.Island;
using OpenClicky.Windows.Theme;

namespace OpenClicky.Windows.Plugins.Notes;

internal sealed class NotesIslandView : Border, IIslandContent
{
    /// <summary>The app's one store (the Mac's NotesStore.shared). The last keystrokes before quitting are written then.</summary>
    public static NotesStore Store { get; } = MakeStore();

    private readonly TextBox editor;
    private readonly TextBlock placeholder;

    public NotesIslandView()
    {
        Background = DS.Brush(Colors.White, 0.07);
        CornerRadius = new CornerRadius(10);
        ClipToBounds = true;
        editor = new TextBox
        {
            Text = Store.Text,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = DS.HUD.TextBrush,
            CaretBrush = DS.HUD.TextBrush,
            SelectionBrush = DS.Brush(Colors.White, 0.6),
            FontFamily = DS.Fonts.Ui,
            FontSize = 13,
            Padding = new Thickness(6, 7, 6, 7),
        };
        AutomationProperties.SetName(editor, "Notes");
        placeholder = IslandUi.Text("Click to jot something down", 13, DS.Brush(Colors.White, 0.35));
        placeholder.VerticalAlignment = VerticalAlignment.Top;
        placeholder.Margin = new Thickness(10, 7, 10, 0);
        var grid = new Grid();
        grid.Children.Add(editor);
        grid.Children.Add(placeholder);
        Child = grid;

        editor.TextChanged += (_, _) =>
        {
            Store.Update(editor.Text);
            placeholder.Visibility = editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        // The first click borrows the keyboard (the island is usually not the active window), then places the caret.
        editor.PreviewMouseLeftButtonDown += (_, _) => BeginTyping();
        editor.PreviewKeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key != Key.Escape) return;
            eventArgs.Handled = true;
            EndTyping();
        };
        editor.LostKeyboardFocus += (_, _) => Store.SaveNow();
        // A click in the pad is the pad's, not a click on the island that would close it.
        MouseLeftButtonUp += (_, eventArgs) => eventArgs.Handled = true;
        MouseLeftButtonDown += (_, eventArgs) => eventArgs.Handled = true;
        Unloaded += (_, _) =>
        {
            Store.SaveNow();
            EndTyping();
        };
        placeholder.Visibility = Store.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BeginTyping()
    {
        if (typingWindow is not null || Window.GetWindow(this) is not IslandWindow window) return;
        typingWindow = window;
        window.BeginComponentTyping();
        Dispatcher.BeginInvoke(() => editor.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Kept from the click, since the pad is out of the window by the time it is unloaded.</summary>
    private IslandWindow? typingWindow;

    private void EndTyping()
    {
        if (typingWindow is not { } window) return;
        typingWindow = null;
        Store.SaveNow();
        window.EndComponentTyping();
    }

    public void Refresh() { }
    public void Tick() { }

    private static NotesStore MakeStore()
    {
        var store = new NotesStore(NotesStore.DefaultFilePath);
        if (Application.Current is { } application) application.Exit += (_, _) => store.SaveNow();
        return store;
    }
}
