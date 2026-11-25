// Drawings that follow a scrolling page: how a strip of the screen moved between two grabs (ScrollFollow), which marks
// move with it (ScreenMarkFollower), and the page's screenMarks / the app's scrolled messages. The "screen" is made up:
// a tall page of rows of pixels, a toolbar that stays put over it, and the strip a window onto them.

using System.Text.Json.Nodes;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.DrawStage;

namespace OpenClicky.Core.Tests.Plugins;

public class ScrollFollowTests
{
    private const int Width = 64;
    private const int Height = 300;
    private const int Toolbar = 40;

    /// <summary>A long page: lines of "text" (rows of mixed pixels) with plain gaps between them, like a web page.</summary>
    private static byte[] Page(int rows, int seed = 7)
    {
        var random = new Random(seed);
        var page = new byte[rows * Width * 4];
        for (var y = 0; y < rows; y++)
        {
            var plain = y % 18 >= 12;
            for (var x = 0; x < Width; x++)
            {
                var at = (y * Width + x) * 4;
                var value = plain ? (byte)250 : (byte)random.Next(256);
                page[at] = value;
                page[at + 1] = plain ? (byte)250 : (byte)random.Next(256);
                page[at + 2] = value;
                page[at + 3] = 255;
            }
        }
        return page;
    }

    /// <summary>The screen: the toolbar's rows at the top, then the page scrolled down by <paramref name="scrolled"/> rows.</summary>
    private static byte[] Screen(byte[] page, int scrolled, byte[]? toolbar = null)
    {
        var screen = new byte[Width * Height * 4];
        toolbar ??= Page(Toolbar, seed: 99);
        Array.Copy(toolbar, screen, Toolbar * Width * 4);
        Array.Copy(page, scrolled * Width * 4, screen, Toolbar * Width * 4, (Height - Toolbar) * Width * 4);
        return screen;
    }

    [Fact]
    public void AScrollDownIsFoundWithThePartThatScrolled()
    {
        var page = Page(1000);
        var shift = ScrollFollow.Find(Screen(page, 100), Screen(page, 137), Width, Height, Width * 4);

        Assert.NotNull(shift);
        Assert.Equal(0, shift.Value.Dx);
        Assert.Equal(-37, shift.Value.Dy);
        // The toolbar stayed put, so the scrolled part starts below it and runs to the bottom.
        Assert.InRange(shift.Value.AreaStart, Toolbar, Toolbar + 12);
        Assert.InRange(shift.Value.AreaEnd, Height - 12, Height);
    }

    [Fact]
    public void LinesThatRepeatStillAgreeOnTheScroll()
    {
        // The same six lines over and over (a strip that sees the words but not the line numbers).
        var block = Page(108);
        var page = new byte[block.Length * 10];
        for (var i = 0; i < 10; i++) Array.Copy(block, 0, page, i * block.Length, block.Length);

        var shift = ScrollFollow.Find(Screen(page, 100), Screen(page, 140), Width, Height, Width * 4);

        Assert.Equal(-40, shift?.Dy);
        // Still, it repeats every 108 rows, but nothing moved.
        Assert.Null(ScrollFollow.Find(Screen(page, 100), Screen(page, 100), Width, Height, Width * 4));
    }

    [Fact]
    public void AScrollUpMovesTheOtherWay()
    {
        var page = Page(1000);
        var shift = ScrollFollow.Find(Screen(page, 300), Screen(page, 180), Width, Height, Width * 4);

        Assert.Equal(120, shift?.Dy);
    }

    [Fact]
    public void AStillScreenOrOneThatChangedWithoutScrollingMovesNothing()
    {
        var page = Page(1000);
        var before = Screen(page, 100);

        Assert.Null(ScrollFollow.Find(before, before, Width, Height, Width * 4));
        // A video playing: new pixels, nothing moved.
        Assert.Null(ScrollFollow.Find(before, Screen(Page(1000, seed: 3), 100), Width, Height, Width * 4));
    }

    [Fact]
    public void APlainScreenSaysNothing()
    {
        var plain = Enumerable.Repeat((byte)200, Width * Height * 4).ToArray();

        Assert.Null(ScrollFollow.Find(plain, plain, Width, Height, Width * 4));
    }

    [Fact]
    public void ASidewaysScrollIsFoundByColumns()
    {
        // A wide picture seen through a window that moves 9 pixels to the right.
        const int wide = 200;
        var random = new Random(5);
        var picture = new byte[Height * wide * 4];
        random.NextBytes(picture);
        byte[] View(int left)
        {
            var view = new byte[Width * Height * 4];
            for (var y = 0; y < Height; y++) Array.Copy(picture, (y * wide + left) * 4, view, y * Width * 4, Width * 4);
            return view;
        }

        var shift = ScrollFollow.Find(View(20), View(29), Width, Height, Width * 4);

        Assert.Equal(-9, shift?.Dx);
        Assert.Equal(0, shift?.Dy);
    }

    // MARK: - Which marks move

    private sealed class FakeScreen
    {
        public byte[] Page { get; } = Page(2000);
        public int Scrolled { get; set; } = 100;
        public List<PixelRect> Grabbed { get; } = [];

        public PixelGrab Grab(PixelRect area)
        {
            Grabbed.Add(area);
            return new PixelGrab(Screen(Page, Scrolled), Width, Height, Width * 4);
        }
    }

    [Fact]
    public void MarksOnTheScrolledPageMoveAndOneOnTheToolbarStays()
    {
        var screen = new FakeScreen();
        var follower = new ScreenMarkFollower(screen.Grab, () => TimeSpan.Zero, new PixelRect(0, 0, Width, Height));
        follower.SetMarks([new ScreenMark("ring", new PixelRect(10, 150, 20, 20)), new ScreenMark("bar", new PixelRect(10, 5, 20, 20))]);

        Assert.Empty(follower.Tick()); // the first grab has nothing to compare with
        screen.Scrolled = 160;
        var moves = follower.Tick();

        var move = Assert.Single(moves);
        Assert.Equal(("ring", 0, -60), (move.Id, move.Dx, move.Dy));
        Assert.InRange(move.AreaStart, Toolbar, Toolbar + 12);
        // The follower keeps where it moved the mark to.
        Assert.Equal(90, follower.Marks.Single(mark => mark.Id == "ring").Box.Top);
    }

    [Fact]
    public void AMovingScreenIsNotTakenForAScrollLongAfterThePersonDidAnything()
    {
        var screen = new FakeScreen();
        var follower = new ScreenMarkFollower(screen.Grab, () => TimeSpan.FromSeconds(30), new PixelRect(0, 0, Width, Height));
        follower.SetMarks([new ScreenMark("ring", new PixelRect(10, 150, 20, 20))]);

        follower.Tick();
        screen.Scrolled = 160;

        Assert.Empty(follower.Tick());
    }

    [Fact]
    public void WhileThePersonDoesNothingTheScreenIsGrabbedLessOften()
    {
        var screen = new FakeScreen();
        var idle = true;
        var follower = new ScreenMarkFollower(screen.Grab, () => idle ? TimeSpan.FromSeconds(30) : TimeSpan.Zero, new PixelRect(0, 0, Width, Height));
        follower.SetMarks([new ScreenMark("ring", new PixelRect(10, 150, 20, 20))]);

        for (var i = 0; i < 2 * ScreenMarkFollower.IdleEvery; i++) follower.Tick();
        Assert.Equal(2, screen.Grabbed.Count);

        // A wheel turns: every tick grabs, and the scroll is found against the last idle grab.
        idle = false;
        screen.Scrolled = 130;
        Assert.Equal(-30, Assert.Single(follower.Tick()).Dy);
        Assert.Equal(3, screen.Grabbed.Count);
    }

    [Fact]
    public void MarksCloseTogetherShareAStripAndNoMarksGrabNothing()
    {
        var screen = new FakeScreen();
        var monitor = new PixelRect(0, 0, 1920, 1080);
        var follower = new ScreenMarkFollower(screen.Grab, () => TimeSpan.Zero, monitor);
        follower.SetMarks([
            new ScreenMark("a", new PixelRect(400, 200, 40, 40)),
            new ScreenMark("b", new PixelRect(450, 700, 40, 40)),
            new ScreenMark("c", new PixelRect(1500, 300, 40, 40)),
        ]);

        var strips = follower.Strips();

        Assert.Equal(2, strips.Count);
        Assert.Equal(["a", "b"], strips[0].Ids.Order());
        Assert.Equal(new PixelRect(420 - ScreenMarkFollower.HalfStrip, 0, 2 * ScreenMarkFollower.HalfStrip, 1080), strips[0].Area);

        follower.SetMarks([]);
        screen.Grabbed.Clear();
        Assert.Empty(follower.Tick());
        Assert.Empty(screen.Grabbed);
    }

    // MARK: - Messages

    [Fact]
    public void ThePagesScreenMarksAreRead()
    {
        var marks = DrawStageMessages.ScreenMarks("""{"type":"screenMarks","marks":[{"id":"m1","x":10,"y":20.5,"w":30,"h":40},{"x":1}]}""");

        Assert.Equal([("m1", 10d, 20.5d, 30d, 40d)], marks);
        Assert.Null(DrawStageMessages.ScreenMarks("""{"type":"figures","rects":[]}"""));
        Assert.Null(DrawStageMessages.ScreenMarks("not json"));
    }

    [Fact]
    public void ScrolledTellsThePageEachMove()
    {
        var json = JsonNode.Parse(DrawStageMessages.Scrolled([("m1", 0, -48, 30, 900.333)]))!;

        Assert.Equal("scrolled", json["type"]!.GetValue<string>());
        var move = json["moves"]![0]!;
        Assert.Equal("m1", move["id"]!.GetValue<string>());
        Assert.Equal(-48, move["dy"]!.GetValue<double>());
        Assert.Equal(900.33, move["to"]!.GetValue<double>());
    }
}
