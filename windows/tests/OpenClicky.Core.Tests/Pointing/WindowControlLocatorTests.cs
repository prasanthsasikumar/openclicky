// Port of macos/OpenClicky/OpenClickyTests/WindowControlLocatorTests.swift
//
// Which requests are about a window's close / minimize / maximize button, and where Windows puts
// those buttons (top right, minimize | maximize | close). The Win32 / DWM reads are not tested here.

using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Tests.Pointing;

public class WindowControlLocatorTests
{
    [Fact]
    public void TheModelsLabelNamesTheButton()
    {
        // The request that went wrong on the Mac: the model asked for the minimize button with no text (it has none).
        Assert.Equal(WindowControl.Minimize, WindowControlLocator.RequestedControl("minimize button", "", "I am talking about the terminal."));
        Assert.Equal(WindowControl.Close, WindowControlLocator.RequestedControl("close button", "", null));
        Assert.Equal(WindowControl.Maximize, WindowControlLocator.RequestedControl("maximize button", "", null));
        Assert.Equal(WindowControl.Maximize, WindowControlLocator.RequestedControl("full screen button", "", null));
        Assert.Equal(WindowControl.Maximize, WindowControlLocator.RequestedControl("restore button", "", null));
    }

    [Fact]
    public void TheUsersWordsNameTheButtonOrTheWindow()
    {
        Assert.Equal(WindowControl.Minimize, WindowControlLocator.RequestedControl("here", "", "how do I minimize this window"));
        Assert.Equal(WindowControl.Close, WindowControlLocator.RequestedControl("here", "", "where is the x button"));
        Assert.Equal(WindowControl.Close, WindowControlLocator.RequestedControl("here", "", "where do i click to close it"));
        Assert.Equal(WindowControl.Maximize, WindowControlLocator.RequestedControl("here", "", "make the terminal full screen"));
    }

    [Fact]
    public void OtherRequestsAreLeftToTheRestOfTheChain()
    {
        // A tab's ✕ is not the window's close button.
        Assert.Null(WindowControlLocator.RequestedControl("close tab", "", "how do I close this tab"));
        // "close" in a menu, a caption, an unrelated question.
        Assert.Null(WindowControlLocator.RequestedControl("Close menu item", "Close", "where is close in the file menu"));
        Assert.Null(WindowControlLocator.RequestedControl("export button", "Export", "how do I export this"));
        // Windows' buttons have no colours: a colour on its own is never one.
        Assert.Null(WindowControlLocator.RequestedControl("yellow folder", "Yellow", "point at the yellow folder"));
    }

    private static readonly ScreenRect Window = new(100, 50, 1200, 800);

    [Fact]
    public void DwmsCaptionAreaIsSplitIntoMinimizeMaximizeAndClose()
    {
        // 3 × 46 px buttons, 32 px tall, at the window's top right.
        var layout = new CaptionButtonLayout(Window, new ScreenRect(1162, 50, 138, 32), true, 1, "Notepad");
        Assert.Equal(new ScreenRect(1254, 50, 46, 32), WindowControlLocator.ButtonFrame(WindowControl.Close, layout));
        Assert.Equal(new ScreenRect(1208, 50, 46, 32), WindowControlLocator.ButtonFrame(WindowControl.Maximize, layout));
        Assert.Equal(new ScreenRect(1162, 50, 46, 32), WindowControlLocator.ButtonFrame(WindowControl.Minimize, layout));
    }

    [Fact]
    public void WithoutABelievableDwmAreaTheUsualButtonsAtTheTopRightAreUsed()
    {
        // A window that draws its own title bar reports nothing (or something elsewhere); 150% scale.
        foreach (var area in new ScreenRect?[] { null, new ScreenRect(100, 50, 30, 30) })
        {
            var layout = new CaptionButtonLayout(Window, area, true, 1.5, "Chrome");
            Assert.Equal(new ScreenRect(1231, 50, 69, 48), WindowControlLocator.ButtonFrame(WindowControl.Close, layout));
            Assert.Equal(new ScreenRect(1093, 50, 69, 48), WindowControlLocator.ButtonFrame(WindowControl.Minimize, layout));
        }
    }

    [Fact]
    public void AWindowWithoutMinimizeOrMaximizeHasOnlyClose()
    {
        var dialog = new CaptionButtonLayout(Window, new ScreenRect(1254, 50, 46, 32), false, 1, "Dialog");
        Assert.Equal(new ScreenRect(1254, 50, 46, 32), WindowControlLocator.ButtonFrame(WindowControl.Close, dialog));
        Assert.Null(WindowControlLocator.ButtonFrame(WindowControl.Minimize, dialog));
        Assert.Null(WindowControlLocator.ButtonFrame(WindowControl.Maximize, dialog));
    }

    [Fact]
    public void TheButtonIsPointedAtInTheScreenshotAndOnlyWhenItIsOnIt()
    {
        var layout = new CaptionButtonLayout(Window, new ScreenRect(1162, 50, 138, 32), true, 1, "Notepad");
        var capture = new ScreenCaptureGeometry(new ScreenRect(0, 0, 2560, 1440), 1280, 720);
        var located = WindowControlLocator.Locate("close button", "", null, capture, () => layout);
        Assert.NotNull(located);
        Assert.Equal(new ScreenPoint(638.5, 33), located!.Point);
        Assert.Equal(WindowControl.Close, located.Control);
        // On another monitor: not on this capture, not pointed at.
        var otherMonitor = new ScreenCaptureGeometry(new ScreenRect(2560, 0, 1920, 1080), 1280, 720);
        Assert.Null(WindowControlLocator.Locate("close button", "", null, otherMonitor, () => layout));
        // Not about a window button: the layout is never read.
        Assert.Null(WindowControlLocator.Locate("save button", "Save", null, capture, () => throw new InvalidOperationException()));
    }
}
