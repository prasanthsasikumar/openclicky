// Ports ShelfStoreTests.swift (what a drop becomes, how items are kept across launches, the tile policies)
// and the shelf half of NotchHUDModelTests.swift (a drag unfolds the island only with the shelf on; a drop
// keeps it open while the pointer stays).

using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins;
using OpenClicky.Core.Plugins.Shelf;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Tests.Companion;

namespace OpenClicky.Core.Tests.Plugins;

public class ShelfTests
{
    private static string TemporaryDirectory() => Path.Combine(Path.GetTempPath(), $"shelf-tests-{Guid.NewGuid():N}");

    [Fact]
    public void TitlesReadLikeTheThingDropped()
    {
        var noon = new DateTimeOffset(2026, 10, 28, 12, 5, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 28, 12, 5, 0)));
        Assert.Equal("Report final.pdf", ShelfItem.TitleFor(new ShelfDrop.File(@"C:\Users\me\Report final.pdf"), noon));
        Assert.Equal("example.com", ShelfItem.TitleFor(new ShelfDrop.Link(new Uri("https://example.com/a/b")), noon));
        Assert.Equal("first line of a long note that keeps goi",
            ShelfItem.TitleFor(new ShelfDrop.Text("  first line of a long note that keeps going and going\nsecond"), noon));
        Assert.Equal("image 12:05", ShelfItem.TitleFor(new ShelfDrop.Image([1]), noon));
    }

    [Fact]
    public void DropsBecomeItemsNewestFirstAndSurviveARelaunch()
    {
        var directory = TemporaryDirectory();
        var store = new ShelfStore(directory);
        store.Add([new ShelfDrop.Text("hello")]);
        store.Add([new ShelfDrop.Link(new Uri("https://example.com"))]);
        Assert.Equal(["example.com", "hello"], store.Items.Select(item => item.Title));
        var again = new ShelfStore(directory);
        Assert.Equal(store.Items, again.Items);
    }

    [Fact]
    public void ImagesAndIconsAreFilesInTheShelfFolderAndGoWithTheirItem()
    {
        var store = new ShelfStore(TemporaryDirectory(), iconRenderer: _ => [1, 2, 3]);
        var items = store.Add([new ShelfDrop.Image([9, 9]), new ShelfDrop.File(@"C:\x.txt")]);
        Assert.Equal(ShelfItemKind.Image, items[0].Kind);
        Assert.True(File.Exists(items[0].Value));
        var iconPath = Assert.IsType<string>(items[1].IconPath);
        Assert.True(File.Exists(iconPath));
        Assert.Equal(@"C:\x.txt", items[1].Value);
        store.Remove(items[0].Id);
        store.Remove(items[1].Id);
        Assert.False(File.Exists(items[0].Value));
        Assert.False(File.Exists(iconPath));
        Assert.Empty(store.Items);
    }

    [Fact]
    public void TheFortyFirstItemPushesTheOldestOut()
    {
        var store = new ShelfStore(TemporaryDirectory());
        for (var index = 0; index < 41; index++) store.Add([new ShelfDrop.Text($"note {index}")]);
        Assert.Equal(40, store.Items.Count);
        Assert.Equal("note 1", store.Items[^1].Title);
    }

    [Fact]
    public void ABrokenIndexMeansAnEmptyShelf()
    {
        var directory = TemporaryDirectory();
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "index.json"), "not json");
        Assert.Empty(new ShelfStore(directory).Items);
    }

    [Fact]
    public void FilesComeFirstThenALinkThenTextThenAnImage()
    {
        Assert.Equal([new ShelfDrop.File(@"C:\a.txt"), new ShelfDrop.File(@"C:\b.txt")],
            ShelfDropReader.Drops([@"C:\a.txt", @"C:\b.txt"], "https://example.com", "words", [1]));
        Assert.Equal([new ShelfDrop.Link(new Uri("https://example.com/page"))],
            ShelfDropReader.Drops(null, "https://example.com/page", "the page title", null));
        Assert.Equal([new ShelfDrop.Link(new Uri("https://example.com/page"))], ShelfDropReader.Drops([], null, " https://example.com/page ", null));
        Assert.Equal([new ShelfDrop.Text("just words")], ShelfDropReader.Drops(null, null, "just words", [1]));
        Assert.Equal([new ShelfDrop.Text("see https://example.com")], ShelfDropReader.Drops(null, null, "see https://example.com", null));
        Assert.Equal([new ShelfDrop.Image([7, 7])], ShelfDropReader.Drops(null, null, "  ", [7, 7]));
        Assert.Empty(ShelfDropReader.Drops(null, null, null, null));
        Assert.Null(ShelfDropReader.WebLink("ftp://example.com"));
        Assert.Null(ShelfDropReader.WebLink("file:///C:/a.txt"));
    }

    [Fact]
    public void TheIslandTakesDropsOnlyWithTheShelfOnAndNeverItsOwnTiles()
    {
        Assert.False(ShelfDragPolicy.Accepts(new HashSet<string> { "shortcuts", "system" }, ["FileDrop"]));
        Assert.True(ShelfDragPolicy.Accepts(new HashSet<string> { "shortcuts", "shelf" }, ["FileDrop"]));
        Assert.False(ShelfDragPolicy.Accepts(new HashSet<string> { "shelf" }, [ShelfDragPolicy.MarkerFormat, "FileDrop"]));
        Assert.True(ShelfDragPolicy.RemovesAfterDrag(dropLanded: true));
        Assert.False(ShelfDragPolicy.RemovesAfterDrag(dropLanded: false));
        Assert.True(ShelfDragPolicy.AllowsMove(ShelfItemKind.File));
        Assert.False(ShelfDragPolicy.AllowsMove(ShelfItemKind.Text));
    }

    private static (IslandModel Model, ManualDelays Clock, IslandComponentSettings Settings) MakeIsland()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var clock = new ManualDelays();
        var settings = new IslandComponentSettings(Preferences.InMemory());
        var geometry = IslandGeometry.VirtualNotch(new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1040));
        return (new IslandModel(geometry, HUDPlacement.Notch, clock.Delay, settings), clock, settings);
    }

    [Fact]
    public void ADragOverTheIslandUnfoldsItOnlyWhenTheShelfIsOn()
    {
        var (model, clock, settings) = MakeIsland();
        model.SetDragHovering(true);
        Assert.Equal(IslandExpansion.Collapsed, model.Expansion);
        settings.SetPluginEnabled(IslandPluginRegistry.ShelfId, true);
        model.RefreshComponentSettings();
        model.SetDragHovering(true);
        Assert.Equal(IslandExpansion.Full, model.Expansion);
        Assert.Equal("shelf", model.Selection.Active);
        model.SetContext(IslandContext.Idle);                 // the shared moment never clears this island's drag
        Assert.Equal("shelf", model.Selection.Active);
        model.SetDragHovering(false);
        clock.Advance(0.5);
        Assert.Equal(IslandExpansion.Collapsed, model.Expansion);
        model.SetDragOut(true);
        Assert.Equal(IslandExpansion.Full, model.Expansion);
        model.SetDragOut(false);
        model.HoldOpenAfterDrop();
        Assert.Equal(IslandExpansion.Full, model.Expansion);
        clock.Advance(2);
        Assert.Equal(IslandExpansion.Collapsed, model.Expansion);
    }

    [Fact]
    public void ADropKeepsTheShelfInFrontAndTheIslandOpenWhileThePointerStays()
    {
        var (model, clock, settings) = MakeIsland();
        settings.SetPluginEnabled(IslandPluginRegistry.ShelfId, true);
        model.RefreshComponentSettings();
        model.SetDragHovering(true);
        model.SetDragHovering(false);
        model.HoldOpenAfterDrop();
        Assert.Equal(IslandExpansion.Full, model.Expansion);
        Assert.Equal("shelf", model.Selection.Active);       // the new tile is what the hold shows
        model.SetHovering(true);
        clock.Advance(1.7);
        Assert.Equal(IslandExpansion.Full, model.Expansion);  // the hold outlasts 1.5 s while the pointer stays
        model.SetHovering(false);
        clock.Advance(0.5);
        Assert.Equal(IslandExpansion.Collapsed, model.Expansion);
    }
}
