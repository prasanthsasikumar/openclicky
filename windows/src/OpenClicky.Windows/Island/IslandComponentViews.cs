//
//  IslandComponentViews.cs
//  OpenClicky (Windows port of the component half of IslandComponentViews.swift)
//
//  What fills the island's slot for a component the selector picked, and the capsule in the band
//  that flips between the components available now. The shortcuts row is the panel's own body
//  (FullPanelView); every other component's view is made here by its id.
//

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Plugins;
using OpenClicky.Windows.Plugins.SystemStats;
using OpenClicky.Windows.Theme;
using static OpenClicky.Windows.Island.IslandUi;

namespace OpenClicky.Windows.Island;

internal static class IslandComponentViews
{
    /// <summary>The view for a component other than the shortcuts; null for the shortcuts (the panel's own row).</summary>
    public static FrameworkElement? Make(IslandComponent component) => component.Id switch
    {
        IslandPluginRegistry.SystemId => new SystemIslandView(),
        _ => null,
    };

    /// <summary>One round button per component the slot can show now, the active one on a lighter disc. A click
    /// shows that component until the moment changes.</summary>
    public static FrameworkElement Capsule(IslandModel model)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var id in model.Selection.Available)
        {
            var component = model.Registry.FirstOrDefault(candidate => candidate.Id == id);
            if (component is null) continue;
            var isActive = id == model.Selection.Active;
            var glyph = Glyph(component.Glyph, 11, isActive ? DS.HUD.TextBrush : DS.HUD.Text2Brush);
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            var button = Clickable(glyph, () => model.SelectComponent(id), component.Title,
                isActive ? DS.HUD.SurfaceRaisedBrush : System.Windows.Media.Brushes.Transparent, new CornerRadius(10));
            button.Width = 20;
            button.Height = 20;
            if (buttons.Children.Count > 0) button.Margin = new Thickness(4, 0, 0, 0);
            AutomationProperties.SetName(button, component.Title);
            AutomationProperties.SetItemStatus(button, isActive ? "selected" : "");
            buttons.Children.Add(button);
        }
        return new Border
        {
            Child = buttons,
            Background = DS.HUD.BandControlBrush,
            CornerRadius = new CornerRadius(DS.HUD.PillHeight / 2),
            Height = DS.HUD.PillHeight,
            Padding = new Thickness(3, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
