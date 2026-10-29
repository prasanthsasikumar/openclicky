// Ports Dictation/UI/SettingsPluginsPage.swift's rows: the island's optional extras, one switch each
// (what it reads), and under them how the island picks between the components it has: the default and
// the order of its buttons. Every row follows the island's preferences, so a switch flipped here
// updates the default's choices and the order's dimmed names at once.

using System.Windows;
using System.Windows.Controls;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Plugins;
using OpenClicky.Core.Settings;

namespace OpenClicky.Windows.AppWindow;

internal sealed partial class SettingsRowFactory
{
    private IslandComponentSettings IslandSettings => new(services.Preferences);

    private UIElement? PluginsRow(SettingsItem item, string query)
    {
        if (IslandPluginRegistry.All.FirstOrDefault(plugin => $"plugins.{plugin.Id}" == item.Id) is { } plugin)
        {
            return IslandSettingsCard(() => Ui.Row(item.Title, item.Detail, query,
                Ui.Toggle(IslandSettings.EnabledPlugins.Contains(plugin.Id), isOn => IslandSettings.SetPluginEnabled(plugin.Id, isOn))));
        }
        return item.Id switch
        {
            "plugins.islandDefault" => IslandSettingsCard(() =>
            {
                var settings = IslandSettings;
                var registry = IslandComponentRegistry.All;
                // The built-ins and the extras that are on.
                var choices = registry.Where(component => settings.IsEnabled(component.Id, registry)).Select(component => (component.Id, component.Title));
                return Ui.Row(item.Title, item.Detail, query,
                    Ui.Picker(choices, settings.ResolvedDefaultId(registry), id => IslandSettings.DefaultId = id));
            }),
            "plugins.islandOrder" => IslandSettingsCard(() => Ui.Row(item.Title, item.Detail, query, ComponentOrderList())),
            _ => null,
        };
    }

    /// <summary>One line per component with up and down buttons; a plugin that is off is dimmed.</summary>
    private FrameworkElement ComponentOrderList()
    {
        var settings = IslandSettings;
        var registry = IslandComponentRegistry.All;
        var order = settings.ResolvedOrder(registry).ToList();
        var list = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        for (var index = 0; index < order.Count; index++)
        {
            var id = order[index];
            var component = registry.First(candidate => candidate.Id == id);
            var name = Ui.Text(component.Title, SettingsLook.CaptionSize, SettingsLook.TextPrimary, wrap: false);
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Opacity = settings.IsEnabled(id, registry) ? 1 : 0.5;
            var position = index;
            var up = OrderButton("", $"Move {component.Title} up", position > 0, () => Move(order, position, -1));
            var down = OrderButton("", $"Move {component.Title} down", position < order.Count - 1, () => Move(order, position, 1));
            var line = Ui.HStack(6, name, up, down);
            line.HorizontalAlignment = HorizontalAlignment.Right;
            if (index > 0) line.Margin = new Thickness(0, 4, 0, 0);
            list.Children.Add(line);
        }
        return list;
    }

    private static Button OrderButton(string glyph, string label, bool isEnabled, Action onClick)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10 },
            Padding = new Thickness(6, 3, 6, 3),
            IsEnabled = isEnabled,
            ToolTip = label,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void Move(List<string> order, int index, int offset)
    {
        var target = index + offset;
        if (target < 0 || target >= order.Count) return;
        (order[index], order[target]) = (order[target], order[index]);
        IslandSettings.Order = order;
    }

    /// <summary>A card that redraws when the island's preferences change, here or elsewhere.</summary>
    private LiveContent IslandSettingsCard(Func<UIElement> build) =>
        new(() => Ui.Card(build()), [.. IslandComponentSettings.Keys.Select(key => (Func<Action, IDisposable>)(changed => services.Preferences.Observe(key, changed)))]);
}
