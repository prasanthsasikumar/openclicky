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
using OpenClicky.Windows.Plugins.CodingAgents;
using OpenClicky.Windows.Plugins.Media;
using OpenClicky.Windows.Plugins.Shelf;
using OpenClicky.Windows.Plugins.SystemStats;
using OpenClicky.Windows.Theme;
using static OpenClicky.Windows.Island.IslandUi;

namespace OpenClicky.Windows.Island;

internal static class IslandComponentViews
{
    /// <summary>The view for a component other than the shortcuts; null for the shortcuts (the panel's own row).</summary>
    public static FrameworkElement? Make(IslandComponent component, IslandCompanionModel hub, IslandModel model) => component.Id switch
    {
        IslandComponentRegistry.TranscriptId => new TranscriptIslandView(hub),
        IslandComponentRegistry.AgentId => new AgentIslandView(hub, model),
        IslandPluginRegistry.SystemId => new SystemIslandView(),
        IslandPluginRegistry.ShelfId => new ShelfIslandView(model),
        IslandPluginRegistry.AgentsId => new AgentsIslandView(),
        IslandPluginRegistry.MediaId => new MediaIslandView(),
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

/// <summary>The take's words so far: the engine's name on the left, the words on the right, dim while the take is
/// still live and white once it has landed.</summary>
internal sealed class TranscriptIslandView : Grid, IIslandContent
{
    private readonly IslandCompanionModel hub;
    private readonly TextBlock engineName;
    private readonly TextBlock words;

    public TranscriptIslandView(IslandCompanionModel hub)
    {
        this.hub = hub;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        VerticalAlignment = VerticalAlignment.Center;
        engineName = Text("", 11, DS.HUD.Text3Brush);
        engineName.Margin = new Thickness(0, 2, 12, 0);
        engineName.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(engineName);
        words = Text("", 13, DS.HUD.TextBrush, wrapping: TextWrapping.Wrap, trimming: TextTrimming.CharacterEllipsis);
        words.MaxHeight = 36;
        SetColumn(words, 1);
        Children.Add(words);
        Refresh();
    }

    /// <summary>"listening…" until the first words; once landed, the take's own words as pasted.</summary>
    public static string Words(IslandContext context)
    {
        if (context.IsTakeLive) return context.LiveTranscript.Length == 0 ? "listening…" : context.LiveTranscript;
        return context.LandedTranscript.Length == 0 ? context.LiveTranscript : context.LandedTranscript;
    }

    public void Refresh()
    {
        var context = hub.IslandContext;
        engineName.Text = hub.DictationEngineName;
        words.Text = Words(context);
        words.Foreground = context.IsTakeLive ? DS.HUD.Text2Brush : DS.HUD.TextBrush;
    }

    public void Tick() { }
}

/// <summary>The working agent's latest line over a thin bar that slides while it runs, and a "follow up" pill that
/// opens the composer on its thread once its result card is up.</summary>
internal sealed class AgentIslandView : Grid, IIslandContent
{
    private readonly IslandCompanionModel hub;
    private readonly IslandModel model;
    private readonly TextBlock line;
    private readonly TextBlock status;
    private readonly Canvas bar = new() { Height = 2, ClipToBounds = true, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Border barHighlight = new() { Height = 2, CornerRadius = new CornerRadius(1), Background = DS.HUD.PointerBrush };
    private readonly Border followUp;

    public AgentIslandView(IslandCompanionModel hub, IslandModel model)
    {
        this.hub = hub;
        this.model = model;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        VerticalAlignment = VerticalAlignment.Center;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        line = Text("", 13, DS.HUD.TextBrush, trimming: TextTrimming.CharacterEllipsis);
        stack.Children.Add(line);
        bar.Background = DS.Brush(System.Windows.Media.Colors.White, 0.12);
        bar.Children.Add(barHighlight);
        stack.Children.Add(bar);
        status = Text("", 11, DS.HUD.Text3Brush, trimming: TextTrimming.CharacterEllipsis);
        stack.Children.Add(status);
        Children.Add(stack);
        var followUpLabel = Text("follow up", 12, DS.HUD.TextBrush, FontWeights.SemiBold);
        followUpLabel.VerticalAlignment = VerticalAlignment.Center;
        followUp = Clickable(followUpLabel, () =>
        {
            var threadId = hub.AgentCardThreadId;
            model.Close();
            hub.OpenTextComposer(threadId);
        }, "type a follow-up for this agent", DS.HUD.BandControlBrush, new CornerRadius(DS.HUD.PillHeight / 2));
        followUp.Height = DS.HUD.PillHeight;
        followUp.Padding = new Thickness(12, 0, 12, 0);
        followUp.Margin = new Thickness(12, 0, 0, 0);
        SetColumn(followUp, 1);
        Children.Add(followUp);
        Refresh();
    }

    /// <summary>The follow-up pill: only while a result card is up and no agent is running. A typed follow-up
    /// interrupts whatever agent is working, so mid-run there is nothing safe to offer.</summary>
    public static bool ShowsFollowUp(bool isRunning, bool isResultCardUp) => isResultCardUp && !isRunning;

    private string? Activity => hub.IslandContext.AgentActivityText ?? hub.AgentActivityText;

    public void Refresh()
    {
        var activity = Activity;
        var isRunning = activity is not null;
        line.Text = activity ?? (hub.AgentCardTitle.Length == 0 ? "agent" : hub.AgentCardTitle);
        bar.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
        status.Text = hub.AgentCardStatus;
        status.Visibility = !isRunning && hub.AgentCardStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        followUp.Visibility = ShowsFollowUp(isRunning, hub.IslandContext.IsResultCardUp) && hub.AgentCardThreadId.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>The bar's highlight, a third of its width, slides back and forth every 1.1 s.</summary>
    public void Tick()
    {
        if (bar.Visibility != Visibility.Visible || bar.ActualWidth <= 0) return;
        var phase = (1 - Math.Cos(Overlay.MotionClock.Now * Math.PI / 1.1)) / 2;
        barHighlight.Width = bar.ActualWidth * 0.3;
        Canvas.SetLeft(barHighlight, phase * bar.ActualWidth * 0.7);
    }
}
