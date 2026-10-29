// Ports IslandComponentSelectorTests.swift (the selector, the moment, the plugins' switches) and the
// slot half of NotchHUDModelTests.swift: a plugin turned on and made the default grows the open panel.

using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins;
using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Tests.Companion;

public class IslandComponentsTests
{
    private static readonly IslandComponent Shortcuts = new("shortcuts", "Shortcuts", "", 45);
    private static readonly IslandComponent Transcript = new("transcript", "Transcript", "", 52) { IsAvailable = context => context.ContextualKey == "transcript" };
    private static readonly IslandComponent Agent = new("agent", "Agent", "", 52) { IsAvailable = context => context.ContextualKey == "agent" };
    private static readonly IReadOnlyList<IslandComponent> Registry = [Shortcuts, Transcript, Agent];
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);

    [Fact]
    public void IdleShowsTheDefaultAloneSoNoCapsule()
    {
        var selection = IslandComponentSelector.Select(Registry, [], "shortcuts", null, IslandContext.Idle);
        Assert.Equal(new IslandComponentSelection("shortcuts", ["shortcuts"]), selection);
    }

    [Fact]
    public void ALiveTakeMakesTheTranscriptActiveAndKeepsItEightSecondsAfterLanding()
    {
        var context = IslandContext.Idle with { Now = Now, IsTakeLive = true };
        Assert.Equal("transcript", IslandComponentSelector.Select(Registry, [], "shortcuts", null, context).Active);
        context = context with { IsTakeLive = false, TakeLandedAt = Now.AddSeconds(-7.9) };
        Assert.Equal("transcript", context.ContextualKey);
        context = context with { TakeLandedAt = Now.AddSeconds(-8.1) };
        Assert.Null(context.ContextualKey);
    }

    [Fact]
    public void TheOverrideWinsWhileAvailableAndTheOrderIsTheUsers()
    {
        var context = IslandContext.Idle with { AgentActivityText = "working" };
        var selection = IslandComponentSelector.Select(Registry, ["agent", "bogus", "shortcuts"], "shortcuts", "shortcuts", context);
        Assert.Equal(new IslandComponentSelection("shortcuts", ["agent", "shortcuts"]), selection);
        Assert.Equal("agent", IslandComponentSelector.Select(Registry, [], "shortcuts", "transcript", context).Active);
    }

    [Fact]
    public void ADisabledComponentIsNeverAvailableAndADisabledDefaultFallsBackToShortcuts()
    {
        var system = new IslandComponent("system", "System", "", 84, IsPlugin: true);
        IReadOnlyList<IslandComponent> registry = [Shortcuts, Transcript, Agent, system];
        var off = IslandComponentSelector.Select(registry, [], "system", "system", IslandContext.Idle, new HashSet<string> { "shortcuts", "transcript", "agent" });
        Assert.Equal(new IslandComponentSelection("shortcuts", ["shortcuts"]), off);
        var on = IslandComponentSelector.Select(registry, [], "system", null, IslandContext.Idle, new HashSet<string> { "shortcuts", "transcript", "agent", "system" });
        Assert.Equal(new IslandComponentSelection("system", ["shortcuts", "system"]), on);
    }

    [Fact]
    public void PluginsAreOffUntilTurnedOnAndADefaultSwitchedOffReadsAsTheShortcuts()
    {
        var settings = new IslandComponentSettings(Preferences.InMemory());
        var registry = IslandComponentRegistry.All;
        Assert.Empty(settings.EnabledPlugins);
        Assert.True(settings.IsEnabled("shortcuts", registry));
        Assert.False(settings.IsEnabled(IslandPluginRegistry.SystemId, registry));
        settings.DefaultId = IslandPluginRegistry.SystemId;
        Assert.Equal("shortcuts", settings.ResolvedDefaultId(registry));
        settings.SetPluginEnabled(IslandPluginRegistry.SystemId, true);
        Assert.True(settings.IsEnabled(IslandPluginRegistry.SystemId, registry));
        Assert.Equal(IslandPluginRegistry.SystemId, settings.ResolvedDefaultId(registry));
        Assert.Equal(new HashSet<string> { "shortcuts", "system" }, settings.EnabledIds(registry));
        settings.SetPluginEnabled(IslandPluginRegistry.SystemId, false);
        Assert.Empty(settings.EnabledPlugins);
    }

    [Fact]
    public void TheOrderDropsUnknownIdsAndAppendsMissingOnes()
    {
        var settings = new IslandComponentSettings(Preferences.InMemory()) { Order = ["system", "gone"] };
        Assert.Equal(["system", "shortcuts"], settings.ResolvedOrder(IslandComponentRegistry.All));
    }

    [Fact]
    public void TheRealRegistryListsTheBuiltInsThenThePlugins()
    {
        Assert.Equal("shortcuts", IslandComponentRegistry.All[0].Id);
        Assert.All(IslandPluginRegistry.All, plugin => Assert.True(plugin.IsPlugin));
        Assert.Contains(IslandComponentRegistry.All, component => component.Id == IslandPluginRegistry.SystemId);
    }

    [Fact]
    public void ADragOutranksEveryMomentAndMusicComesAfterTheAgent()
    {
        var context = IslandContext.Idle with { IsTakeLive = true, IsDragHovering = true };
        Assert.Equal("shelf", context.ContextualKey);
        context = IslandContext.Idle with { IsMediaPlaying = true };
        Assert.Equal("media", context.ContextualKey);
        context = context with { AgentActivityText = "working" };
        Assert.Equal("agent", context.ContextualKey);
    }

    /// <summary>The System plugin on and the default: the open panel is the band plus its 84 rows, and the capsule
    /// flips back to the shortcuts (45) until the moment changes.</summary>
    [Fact]
    public void ThePanelTakesTheActiveComponentsHeightAndTheCapsuleFlipsIt()
    {
        var preferences = Preferences.InMemory();
        var settings = new IslandComponentSettings(preferences);
        var geometry = IslandGeometry.VirtualNotch(new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1040));
        var model = new IslandModel(geometry, componentSettings: settings);
        Assert.False(model.ShowsCapsule);
        var shortcutsHeight = model.Layout.FullHeight;

        settings.SetPluginEnabled(IslandPluginRegistry.SystemId, true);
        settings.DefaultId = IslandPluginRegistry.SystemId;
        model.RefreshComponentSettings();
        Assert.Equal(IslandPluginRegistry.SystemId, model.Selection.Active);
        Assert.True(model.ShowsCapsule);
        Assert.Equal(shortcutsHeight - 45 + 84, model.Layout.FullHeight);

        model.SelectComponent("shortcuts");
        Assert.Equal("shortcuts", model.Selection.Active);
        Assert.Equal(shortcutsHeight, model.Layout.FullHeight);
    }
}
