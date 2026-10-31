// Ports Dictation/UI/SettingsOrbPage.swift and SettingsKeyboardPage.swift's Shortcuts rows.
// The Orb: show it; its style (tiles with a still OrbPreview), theme and size, dimmed while the orb
// is hidden; when it appears. Shortcuts: every key OpenClicky listens for, at a glance, with the
// Windows keys (windows/PORTING.md); the dictation key row links to Dictation to change it.

using OpenClicky.Core.Shortcuts;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Settings;

namespace OpenClicky.Windows.AppWindow;

internal sealed partial class SettingsRowFactory
{
    private UIElement? OrbRow(SettingsItem item, string query)
    {
        switch (item.Id)
        {
            case "orb.visible":
                return LiveSettingsCard(() => Ui.Row(item.Title, $"Off: {Settings.DictationKey.KeycapLabel()} still works everywhere, just without narration.", query,
                    Ui.Toggle(Settings.OrbVisible, isOn => Settings.OrbVisible = isOn)));
            case "orb.look":
                return LiveSettingsCard(() => OrbDependent(Ui.Row(item.Title, null, query, OrbLookTiles())));
            case "orb.theme":
                return LiveSettingsCard(() => OrbDependent(Ui.Row(item.Title, null, query,
                    Ui.Picker(Enum.GetValues<OrbTheme>().Select(theme => (theme, theme.ToString())), Settings.OrbTheme, theme => Settings.OrbTheme = theme, 130))));
            case "orb.size":
                return LiveSettingsCard(() => OrbDependent(Ui.Row(item.Title, null, query,
                    Ui.Picker(new[] { (OrbSize.Full, "Full"), (OrbSize.Mini, "Mini") }, Settings.OrbSize, size => Settings.OrbSize = size, 130))));
            case "orb.hidesWhenIdle":
                return SettingsToggleRow(item.Title, item.Detail, query, () => Settings.OrbHidesWhenIdle, isOn => Settings.OrbHidesWhenIdle = isOn, nameof(DictationSettings.OrbHidesWhenIdle));
            case "orb.restsExpanded":
                return SettingsToggleRow(item.Title, item.Detail, query, () => Settings.OrbRestsExpanded, isOn => Settings.OrbRestsExpanded = isOn, nameof(DictationSettings.OrbRestsExpanded));
            case "orb.opensBoxWhenPasteUnverified":
                return SettingsToggleRow(item.Title, item.Detail, query, () => Settings.OrbOpensBoxWhenPasteUnverified,
                    isOn => Settings.OrbOpensBoxWhenPasteUnverified = isOn, nameof(DictationSettings.OrbOpensBoxWhenPasteUnverified));
            case "orb.isDraggable":
                // Free to drag, and the way back to the bottom once it was dragged somewhere.
                return LiveSettingsCard(() => Ui.Row(item.Title,
                    Settings.OrbPosition is null ? item.Detail : "Off pins the orb at the bottom centre. It rests where you dragged it.", query,
                    Ui.HStack(10,
                        Settings.OrbPosition is null ? null : Ui.Button("Move to Bottom", () => Settings.OrbPosition = null),
                        Ui.Toggle(Settings.OrbIsDraggable, isOn => Settings.OrbIsDraggable = isOn))));
            case "orb.tooltips":
                return SettingsToggleRow(item.Title, item.Detail, query, () => Settings.Tooltips, isOn => Settings.Tooltips = isOn, nameof(DictationSettings.Tooltips));
            default:
                return null;
        }
    }

    /// <summary>A row that only means something while the orb is shown: dimmed and inert while it is hidden.</summary>
    private FrameworkElement OrbDependent(FrameworkElement row)
    {
        row.IsEnabled = Settings.OrbVisible;
        row.Opacity = Settings.OrbVisible ? 1 : 0.45;
        return row;
    }

    private StackPanel OrbLookTiles()
    {
        var tiles = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var look in Enum.GetValues<OrbLook>())
        {
            var isSelected = Settings.OrbLook == look;
            var picture = new Grid { Width = 68, Height = 46 };
            var background = new Border { CornerRadius = new CornerRadius(7) };
            background.SetResourceReference(Border.BackgroundProperty, SettingsLook.SubtleFill);
            picture.Children.Add(background);
            picture.Children.Add(new OrbPreview(look, Settings.OrbTheme, OrbSize.Mini));
            var ring = new Border { CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(isSelected ? 2 : 1) };
            ring.SetResourceReference(Border.BorderBrushProperty, isSelected ? SettingsLook.Accent : SettingsLook.ControlStroke);
            picture.Children.Add(ring);
            var name = look.ToString();
            var caption = Ui.Text(name, SettingsLook.CaptionSize, isSelected ? SettingsLook.TextPrimary : SettingsLook.TextSecondary,
                isSelected ? FontWeights.SemiBold : FontWeights.Normal, wrap: false);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.Margin = new Thickness(0, 6, 0, 0);
            var stack = new StackPanel();
            stack.Children.Add(picture);
            stack.Children.Add(caption);
            var button = new Button { Content = stack, Template = Ui.TransparentButtonTemplate(), Cursor = Cursors.Hand, Margin = new Thickness(6, 0, 6, 0) };
            Ui.AutomationProperties(button, $"{name} style{(isSelected ? ", selected" : "")}");
            button.Click += (_, _) => Settings.OrbLook = look;
            tiles.Children.Add(button);
        }
        return tiles;
    }

    // MARK: - Shortcuts

    /// <summary>The shortcut rows (keyboard.edit … keyboard.handsFree): the keycaps to press. The dictation key's own rows are Dictation's.</summary>
    private UIElement? KeyboardRow(SettingsItem item, string query)
    {
        if (SettingsCatalog.KeysFor(item.Id, Settings.DictationKey).Keys.Count == 0) return null;
        return LiveSettingsCard(() =>
        {
            var keys = SettingsCatalog.KeysFor(item.Id, Settings.DictationKey);
            return Ui.Row(item.Title, item.Detail, query, Ui.Keys(keys.Keys, keys.Gesture));
        });
    }
}
