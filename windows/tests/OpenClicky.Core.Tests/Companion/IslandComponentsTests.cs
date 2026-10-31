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
    public void PluginsAreOffUntilTurnedOnAndADefaultSwitchedOffReadsAsHome()
    {
        var settings = new IslandComponentSettings(Preferences.InMemory());
        var registry = IslandComponentRegistry.All;
        Assert.Equal("home", settings.DefaultId);
        Assert.Empty(settings.EnabledPlugins);
        Assert.True(settings.IsEnabled("shortcuts", registry));
        Assert.False(settings.IsEnabled(IslandPluginRegistry.SystemId, registry));
        settings.DefaultId = IslandPluginRegistry.SystemId;
        Assert.Equal("home", settings.ResolvedDefaultId(registry));
        settings.SetPluginEnabled(IslandPluginRegistry.SystemId, true);
        Assert.True(settings.IsEnabled(IslandPluginRegistry.SystemId, registry));
        Assert.Equal(IslandPluginRegistry.SystemId, settings.ResolvedDefaultId(registry));
        Assert.Equal(new HashSet<string> { "home", "shortcuts", "transcript", "agent", "system" }, settings.EnabledIds(registry));
        settings.SetPluginEnabled(IslandPluginRegistry.SystemId, false);
        Assert.Empty(settings.EnabledPlugins);
    }

    [Fact]
    public void ADisabledDefaultFallsBackToHomeInTheSelector()
    {
        var home = new IslandComponent("home", "Home", "", 45);
        var system = new IslandComponent("system", "System", "", 84, IsPlugin: true);
        IReadOnlyList<IslandComponent> registry = [home, Shortcuts, system];
        var selection = IslandComponentSelector.Select(registry, [], "system", null, IslandContext.Idle, new HashSet<string> { "home", "shortcuts" });
        Assert.Equal(new IslandComponentSelection("home", ["home", "shortcuts"]), selection);
    }

    [Fact]
    public void TheOrderDropsUnknownIdsAndAppendsMissingOnes()
    {
        var settings = new IslandComponentSettings(Preferences.InMemory()) { Order = ["system", "gone"] };
        Assert.Equal(["system", "home", "shortcuts", "transcript", "agent", "shelf", "agents", "media", "teacher", "timer", "notes", "clipboard"],
            settings.ResolvedOrder(IslandComponentRegistry.All));
    }

    [Fact]
    public void TheRealRegistryListsTheBuiltInsThenThePlugins()
    {
        Assert.Equal(["home", "shortcuts", "transcript", "agent", "system", "shelf", "agents", "media", "teacher", "timer", "notes", "clipboard"],
            IslandComponentRegistry.All.Select(component => component.Id));
        // Home's height follows what is on (IslandHomeLayoutTests).
        Assert.Equal([45, 52, 52, 84, 84, 76, 84, 64, 110, 120, 130], IslandComponentRegistry.All.Skip(1).Select(component => component.PreferredHeight));
        Assert.Equal([false, false, false, false, true, true, true, true, true, true, true, true], IslandComponentRegistry.All.Select(component => component.IsPlugin));
        Assert.Equal(["system", "shelf", "agents", "media", "teacher", "timer", "notes", "clipboard"], IslandPluginRegistry.All.Select(plugin => plugin.Id));
        Assert.Equal([false, false, false, false, true, true, true, true, true, true, true, true],
            IslandComponentRegistry.All.Select(component => component.SettingsDetail is not null));
        Assert.All(IslandComponentRegistry.All, component => Assert.NotEqual("", component.Glyph));
        Assert.Equal(["shelf", "clipboard", "notes", "timer"], IslandPluginRegistry.ToolIds);
    }

    [Fact]
    public void ADragOutranksEveryMomentAndMusicComesAfterTheAgent()
    {
        var context = IslandContext.Idle with { IsTakeLive = true, IsDragHovering = true };
        Assert.Equal("shelf", context.ContextualKey);
        context = IslandContext.Idle with { IsMediaPlaying = true };
        // The track is on home, so home is the moment.
        Assert.Equal("home", context.ContextualKey);
        context = context with { AgentActivityText = "working" };
        Assert.Equal("agent", context.ContextualKey);
    }

    [Fact]
    public void ATakeLandsAtTheLiveToIdleFlipAndKeepsThatTimeUntilTheNextTake()
    {
        Assert.Null(IslandContext.LandingTime(wasLive: false, previousLandedAt: null, isLive: true, Now));
        Assert.Equal(Now, IslandContext.LandingTime(wasLive: true, previousLandedAt: null, isLive: false, Now));
        Assert.Equal(Now, IslandContext.LandingTime(wasLive: false, previousLandedAt: Now, isLive: false, Now.AddSeconds(3)));
        Assert.Null(IslandContext.LandingTime(wasLive: false, previousLandedAt: Now, isLive: true, Now.AddSeconds(3)));
    }

    /// <summary>A live take puts the transcript in the slot, an agent at work puts the agent there; both leave
    /// the capsule a way back to home and the shortcuts.</summary>
    [Fact]
    public void TheRealBuiltInsFollowTheMoment()
    {
        var geometry = IslandGeometry.VirtualNotch(new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1040));
        var model = new IslandModel(geometry);
        model.SetContext(IslandContext.Idle with { Now = Now, IsTakeLive = true, LiveTranscript = "hello" });
        Assert.Equal(new IslandComponentSelection("transcript", ["home", "shortcuts", "transcript"]), model.Selection);
        model.SetContext(IslandContext.Idle with { Now = Now, AgentActivityText = "ran: dir" });
        Assert.Equal(new IslandComponentSelection("agent", ["home", "shortcuts", "agent"]), model.Selection);
        model.SetContext(IslandContext.Idle with { Now = Now });
        Assert.Equal(new IslandComponentSelection("home", ["home", "shortcuts"]), model.Selection);
    }

    /// <summary>The tools get buttons of their own in the band, and Now playing and System are on home, so the capsule
    /// leaves them out.</summary>
    [Fact]
    public void TheCapsuleLeavesOutTheToolsAndWhatHomeShows()
    {
        var preferences = Preferences.InMemory();
        var settings = new IslandComponentSettings(preferences)
        {
            EnabledPlugins = ["timer", "shelf", "media", "system", "notes", "teacher", "clipboard"],
        };
        var geometry = IslandGeometry.VirtualNotch(new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1040));
        var model = new IslandModel(geometry, componentSettings: settings);
        Assert.Equal(["home", "shortcuts", "teacher"], model.CapsuleIds);
        Assert.Equal(["shelf", "clipboard", "notes", "timer"], model.ToolIds);
        Assert.True(model.ShowsCapsule);
        // A tool's button shows it; a second click goes back to where the island was.
        model.SelectComponent("notes");
        Assert.Equal("notes", model.Selection.Active);
        model.SelectComponent(null);
        Assert.Equal("home", model.Selection.Active);
    }

    [Fact]
    public void TheComposerOpenedForAThreadSendsItsWordsToThatThreadOnce()
    {
        var hub = new IslandCompanionModel();
        var newRequests = new List<string>();
        var followUps = new List<(string Text, string ThreadId)>();
        hub.ComposerSubmitted += newRequests.Add;
        hub.ComposerFollowUpSubmitted += (text, threadId) => followUps.Add((text, threadId));
        hub.OpenTextComposer("thread-1");
        hub.SubmitComposerCommand.Execute(" and the tests too ");
        Assert.Equal([("and the tests too", "thread-1")], followUps);
        hub.OpenTextComposer();
        hub.SubmitComposerCommand.Execute("what's this?");
        Assert.Equal(["what's this?"], newRequests);
        Assert.Single(followUps);
    }

    /// <summary>With home's columns all off, home is the shortcuts row (45); the System plugin on and the default makes
    /// the open panel the band plus its 84 rows, and the capsule flips back to the shortcuts until the moment changes.</summary>
    [Fact]
    public void ThePanelTakesTheActiveComponentsHeightAndTheCapsuleFlipsIt()
    {
        var preferences = Preferences.InMemory();
        preferences.Set(IslandHomeLayout.CalendarEnabledKey, false);
        var settings = new IslandComponentSettings(preferences);
        var geometry = IslandGeometry.VirtualNotch(new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1040));
        var model = new IslandModel(geometry, componentSettings: settings);
        Assert.Equal("home", model.Selection.Active);
        var shortcutsHeight = model.Layout.FullHeight;
        Assert.Equal(model.Layout.BandRowHeight + 45, shortcutsHeight);
        Assert.Equal(512, model.Layout.FullWidth);

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

    /// <summary>Home grows the open island to fit its columns, follows its switches while open, and in a side drawer is
    /// the shortcuts row in the drawer's own width.</summary>
    [Fact]
    public void HomeSizesTheOpenIslandToItsColumns()
    {
        var preferences = Preferences.InMemory();
        var settings = new IslandComponentSettings(preferences);
        var geometry = IslandGeometry.VirtualNotch(new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1040));
        var model = new IslandModel(geometry, componentSettings: settings);
        var layoutChanges = 0;
        model.LayoutChanged += () => layoutChanges++;
        model.OpenPinned();
        // The calendar is on by default: 308 wide is under the standard 512, so the island keeps 512.
        Assert.Equal(512, model.Layout.FullWidth);
        Assert.Equal(model.Layout.BandRowHeight + 134, model.Layout.FullHeight);

        settings.EnabledPlugins = ["media", "system"];
        preferences.Set(IslandHomeLayout.WeatherEnabledKey, true);
        layoutChanges = 0;
        model.RefreshComponentSettings();
        Assert.Equal(918, model.Layout.FullWidth);
        Assert.True(layoutChanges > 0);

        model.Placement = HUDPlacement.Right;
        Assert.Equal(360, model.Layout.FullWidth);
        Assert.Equal(model.Layout.BandRowHeight + 92, model.Layout.FullHeight);
    }
}
