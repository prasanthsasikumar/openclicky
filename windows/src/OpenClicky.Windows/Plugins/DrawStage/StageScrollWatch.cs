//
//  StageScrollWatch.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  Keeps the marks a drawing points at the screen with (a circle round a button, an arrow to a line)
//  on what they point at when it scrolls. The page lists those marks ("screenMarks"); while there are
//  any, the screen under them is grabbed about ten times a second with GDI (the stage window is left
//  out of screen captures, so the grab sees the page, not the drawing) and ScreenMarkFollower says
//  which moved; the page glides them there ("scrolled") and forgets those that scrolled out of sight.
//
//  When OpenClicky's windows are let into screen captures (--openclicky-capturable-windows) the grab
//  would see the drawing itself, so the marks are left where they are.
//

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.DrawStage;
using OpenClicky.Windows.Overlay;
using OpenClicky.Windows.Platform;

namespace OpenClicky.Windows.Plugins.DrawStage;

internal sealed class StageScrollWatch(Action<string> post)
{
    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(100);

    private ScreenMarkFollower? follower;
    private (PixelRect Frame, double Scale) placement;
    private System.Windows.Threading.DispatcherTimer? timer;
    private bool saidLeftAlone;
    private DateTime lastSaid;

    /// <summary>Overrides the person's last input (the smoke test scrolls a window by itself).</summary>
    internal static Func<TimeSpan>? SinceInputOverride { get; set; }

    /// <summary>The page's "screenMarks", on a stage placed over <paramref name="frame"/> at <paramref name="scale"/>.</summary>
    public void Note(string json, PixelRect frame, double scale)
    {
        if (DrawStageMessages.ScreenMarks(json) is not { } listed) return;
        if (FloatingWindowSupport.AllowsScreenCapture)
        {
            if (listed.Count > 0 && !saidLeftAlone)
            {
                saidLeftAlone = true;
                AppLog.Write("draw: marks on the screen don't follow scrolling while OpenClicky's windows are in screen captures");
            }
            return;
        }
        if (follower is null || placement != (frame, scale))
        {
            follower = new ScreenMarkFollower(Grab, SinceInputOverride ?? SinceInput, frame);
            placement = (frame, scale);
        }
        follower.SetMarks([.. listed.Select(mark => new ScreenMark(mark.Id, new PixelRect(
            frame.Left + (int)Math.Round(mark.X * scale), frame.Top + (int)Math.Round(mark.Y * scale),
            (int)Math.Round(mark.Width * scale), (int)Math.Round(mark.Height * scale))))]);
        if (follower.Marks.Count > 0) Resume();
        else Pause();
    }

    /// <summary>The stage was hidden: its marks wait, unwatched, until it shows again.</summary>
    public void Pause()
    {
        timer?.Stop();
        timer = null;
    }

    /// <summary>The stage shows (again): its marks are watched, if it has any.</summary>
    public void Resume()
    {
        if (timer is not null || follower is null || follower.Marks.Count == 0) return;
        timer = new System.Windows.Threading.DispatcherTimer { Interval = Every };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    private void Tick()
    {
        if (follower is null) return;
        IReadOnlyList<MarkMove> moves;
        try
        {
            moves = follower.Tick();
        }
        catch (Exception error) when (error is ExternalException or ArgumentException or InvalidOperationException)
        {
            AppLog.Write($"draw: following the screen stopped ({error.GetType().Name})");
            follower.SetMarks([]);
            Pause();
            return;
        }
        if (moves.Count == 0) return;
        var (frame, scale) = placement;
        // Once a scroll, not at every step of it.
        if (DateTime.UtcNow - lastSaid > TimeSpan.FromSeconds(2))
        {
            AppLog.Write($"draw: the screen scrolled; {moves.Count} mark(s) follow it ({moves[0].Dx}, {moves[0].Dy}) px");
        }
        lastSaid = DateTime.UtcNow;
        post(DrawStageMessages.Scrolled(moves.Select(move =>
        {
            var origin = move.Dx != 0 ? frame.Left : frame.Top;
            return (move.Id, move.Dx / scale, move.Dy / scale, (move.AreaStart - origin) / scale, (move.AreaEnd - origin) / scale);
        })));
    }

    private static PixelGrab? Grab(PixelRect area)
    {
        using var bitmap = ScreenCapture.CaptureRegion(area);
        if (bitmap is null) return null;
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var pixels = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return new PixelGrab(pixels, bitmap.Width, bitmap.Height, stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static TimeSpan SinceInput()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
