// Ports IslandHomeLayoutTests.swift and IslandStyleTests.swift's style half: home's columns and the island's size for
// them, the island's style with its old-slider fallback, and the hover's direction.

using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Tests.Companion;

public class IslandHomeLayoutTests
{
    [Fact]
    public void NothingOnFallsBackToTheShortcutsRow()
    {
        var layout = new IslandHomeLayout(mediaOn: false, calendarOn: false, weatherOn: false, systemOn: false);
        Assert.True(layout.IsEmpty);
        Assert.Null(layout.Width);
        Assert.Equal(IslandHomeLayout.ShortcutsHeight, layout.Height);
    }

    [Fact]
    public void ColumnsSitInOrderAndTilesShareOneGroup()
    {
        var layout = new IslandHomeLayout(mediaOn: true, calendarOn: true, weatherOn: true, systemOn: true);
        Assert.Equal([IslandHomeColumn.Media, IslandHomeColumn.Calendar, IslandHomeColumn.Weather, IslandHomeColumn.System], layout.Columns);
        Assert.Equal(3, layout.Groups.Count);
        Assert.Equal([IslandHomeColumn.Media], layout.Groups[0]);
        Assert.Equal([IslandHomeColumn.Calendar], layout.Groups[1]);
        Assert.Equal([IslandHomeColumn.Weather, IslandHomeColumn.System], layout.Groups[2]);
    }

    [Fact]
    public void TheWidthIsTheColumnsTheGuttersAndThePadding()
    {
        // 300 + 268 + (2 × 118 + 8), two gutters of 16 + 1 + 16, 20 each side.
        var everything = new IslandHomeLayout(mediaOn: true, calendarOn: true, weatherOn: true, systemOn: true);
        Assert.Equal(918, everything.Width);
        var calendarOnly = new IslandHomeLayout(mediaOn: false, calendarOn: true, weatherOn: false, systemOn: false);
        Assert.Equal(308, calendarOnly.Width);
        Assert.Equal(134, everything.Height);
    }

    [Fact]
    public void TheSwitchesDecideTheColumnsCalendarOnAndWeatherOffByDefault()
    {
        var preferences = Preferences.InMemory();
        Assert.Equal([IslandHomeColumn.Calendar], IslandHomeLayout.Current(preferences).Columns);
        preferences.Set(IslandHomeLayout.WeatherEnabledKey, true);
        preferences.Set(IslandHomeLayout.CalendarEnabledKey, false);
        new IslandComponentSettings(preferences).EnabledPlugins = ["system", "media"];
        Assert.Equal([IslandHomeColumn.Media, IslandHomeColumn.Weather, IslandHomeColumn.System], IslandHomeLayout.Current(preferences).Columns);
        Assert.Equal(new IslandHomeLayout(true, false, true, true), IslandHomeLayout.Current(preferences));
    }

    [Fact]
    public void TheStyleDefaultsToLiquidGlassAndHonoursTheOldSlider()
    {
        var preferences = Preferences.InMemory();
        var appearance = new IslandAppearanceSettings(preferences);
        Assert.Equal(IslandLook.Glass, appearance.Look);
        preferences.Set(IslandLooks.LegacyGlassLevelKey, 0.6);
        Assert.Equal(IslandLook.Blend, appearance.Look);
        preferences.Set(IslandLooks.LegacyGlassLevelKey, 0.0);
        Assert.Equal(IslandLook.Black, appearance.Look);
        appearance.Look = IslandLook.Glass;
        Assert.Equal("glass", preferences.GetString(IslandLooks.DefaultsKey));
        Assert.Equal(IslandLook.Glass, appearance.Look);
        Assert.True(IslandLook.Blend.HasGlass());
        Assert.False(IslandLook.Black.HasGlass());
    }

    [Fact]
    public void TheHoverGrowsSidewaysByDefaultAndDownwardsOnRequest()
    {
        var preferences = Preferences.InMemory();
        var appearance = new IslandAppearanceSettings(preferences);
        Assert.Equal(IslandPeekDirection.Sideways, appearance.PeekDirection);
        var changes = 0;
        using var observation = appearance.Observe(() => changes++);
        appearance.PeekDirection = IslandPeekDirection.Down;
        Assert.Equal("down", preferences.GetString(IslandPeekDirections.DefaultsKey));
        Assert.Equal(IslandPeekDirection.Down, appearance.PeekDirection);
        Assert.Equal(1, changes);

        var geometry = IslandGeometry.VirtualNotch(new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1040));
        var sideways = new IslandLayout(geometry, HUDPlacement.Notch, new IslandContentState(IslandExpansion.Peek, PeekGrowsSideways: true));
        var down = new IslandLayout(geometry, HUDPlacement.Notch, new IslandContentState(IslandExpansion.Peek));
        // Sideways: wider and thinner, along the top edge.
        Assert.Equal(190 + 170 * 2, sideways.Width);
        Assert.Equal(22, sideways.Height);
        Assert.Equal(280, down.Width);
        Assert.Equal(30, down.Height);
        // A side drawer's handle never peeks, whichever way.
        var drawer = new IslandLayout(geometry, HUDPlacement.Left, new IslandContentState(IslandExpansion.Peek, PeekGrowsSideways: true));
        Assert.Equal(drawer.CollapsedWidth, drawer.Width);
    }

    [Fact]
    public void TheModelPeeksTheWayItIsTold()
    {
        var geometry = IslandGeometry.VirtualNotch(new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1040));
        var model = new IslandModel(geometry);
        Assert.True(model.Layout.State.PeekGrowsSideways);
        model.PeekDirection = IslandPeekDirection.Down;
        Assert.False(model.Layout.State.PeekGrowsSideways);
    }
}
