//
//  DrawingRecorder.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  Saves the drawing on the stage (IDrawingSaver) to Videos\OpenClicky: a PNG of it as it is, an
//  MP4 of it being drawn with its voice, or both. The video is made by drawing it once more on the
//  stage, its lines started and ended on the clock they were first spoken on, while WebView2's
//  own captures of the page are taken; each line's audio is laid under the video at the moment
//  its line starts. Only the drawing's box is kept, on paper (the page puts paper behind it while
//  it is recorded): never the screen under the stage.
//

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.DrawStage;

namespace OpenClicky.Windows.Plugins.DrawStage;

internal sealed class DrawingRecorder(DrawStageWindow window, DrawStageController stage) : IDrawingSaver
{
    /// <summary>Before the first line, the card and anything drawn ahead of the voice appear.</summary>
    private const double Lead = 0.6;
    /// <summary>After the last line, the finished drawing stays on for a moment.</summary>
    private const double Tail = 1.5;
    private static readonly TimeSpan FrameGap = TimeSpan.FromMilliseconds(55);
    private static readonly TimeSpan Longest = TimeSpan.FromMinutes(4);

    /// <summary>Where saved drawings go: Videos\OpenClicky.</summary>
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "OpenClicky");

    /// <summary>Opens Explorer on the saved file (off for the smoke command).</summary>
    public bool RevealWhenSaved { get; init; } = true;

    /// <summary>How much of a video's save is its replay (the rest is writing the MP4).</summary>
    private const double ReplayShare = 0.85;

    public async Task<DrawingSaved> SaveAsync(DrawnAnswer drawing, DrawingSaveKind what, Action<DrawingSaveProgress> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Folder);
        var name = FileNameFor(drawing.Title, DateTime.Now);
        var picturePath = what == DrawingSaveKind.Video ? null : Path.Combine(Folder, name + ".png");
        var videoPath = what == DrawingSaveKind.Picture ? null : Path.Combine(Folder, name + ".mp4");
        stage.SetRecording(true);
        var replayed = false;
        try
        {
            // The drawing's box, with paper behind it now and the buttons gone.
            var bounds = await stage.MeasureAsync(TimeSpan.FromSeconds(3));
            if (bounds is null || bounds.IsEmpty) return new DrawingSaved(null, null, "nothing is drawn");
            await Task.Delay(120, cancellationToken);
            var still = await window.CaptureAsync(png: true);
            if (still is null) return new DrawingSaved(null, null, "the stage isn't showing");
            Rectangle crop;
            using (var image = new Bitmap(new MemoryStream(still)))
            {
                crop = CropFor(bounds, image.Width, image.Height);
                if (picturePath is not null)
                {
                    using var picture = image.Clone(crop, PixelFormat.Format32bppArgb);
                    picture.Save(picturePath, ImageFormat.Png);
                    AppLog.Write($"draw: picture saved ({crop.Width}×{crop.Height})");
                }
            }
            if (videoPath is null)
            {
                if (RevealWhenSaved) Reveal(picturePath!);
                return new DrawingSaved(picturePath, null);
            }

            progress(new DrawingSaveProgress("Recording the video…", 0));
            replayed = true;
            var (frames, voice, length) = await ReplayAsync(drawing,
                done => progress(new DrawingSaveProgress("Recording the video…", done * ReplayShare)), cancellationToken);
            AppLog.Write($"draw: replayed in {length:0.0} s ({frames.Count} frames, {voice.Count} lines of voice)");
            progress(new DrawingSaveProgress("Saving the video…", ReplayShare));
            var lastShown = ReplayShare;
            await Task.Run(() => DrawingVideo.Write(videoPath, frames, crop, voice, length, cancellationToken, written =>
            {
                var fraction = ReplayShare + (1 - ReplayShare) * written;
                if (fraction - lastShown < 0.01) return;
                lastShown = fraction;
                progress(new DrawingSaveProgress("Saving the video…", fraction));
            }), cancellationToken);
            AppLog.Write($"draw: video saved ({new FileInfo(videoPath).Length / 1024} KB)");
            if (RevealWhenSaved) Reveal(videoPath);
            return new DrawingSaved(picturePath, videoPath);
        }
        catch (Exception error) when (error is not OperationCanceledException && picturePath is not null && File.Exists(picturePath))
        {
            AppLog.Write($"draw: the video could not be made ({error.GetType().Name}: {error.Message})");
            try { if (videoPath is not null) File.Delete(videoPath); } catch (IOException) { }
            if (RevealWhenSaved) Reveal(picturePath);
            return new DrawingSaved(picturePath, null);
        }
        catch (Exception error) when (error is not OperationCanceledException && videoPath is not null)
        {
            AppLog.Write($"draw: the video could not be made ({error.GetType().Name}: {error.Message})");
            try { File.Delete(videoPath); } catch (IOException) { }
            return new DrawingSaved(null, null, "the video couldn't be made");
        }
        finally
        {
            stage.SetRecording(false);
            // The replay drew it again from the start: it is the same drawing, kept for follow-ups and saving.
            if (replayed && !cancellationToken.IsCancellationRequested) stage.Remember(drawing);
        }
    }

    /// <summary>Draws <paramref name="drawing"/> again on its clock (where it was moved to), capturing the page as it goes: the
    /// frames (each with its time), each line's audio with when it started, and how long it all took. <paramref name="done"/>
    /// hears how far through it is (0 to 1).</summary>
    private async Task<(List<(double At, byte[] Jpeg)> Frames, List<(double At, byte[] Audio)> Voice, double Length)> ReplayAsync(
        DrawnAnswer drawing, Action<double> done, CancellationToken cancellationToken)
    {
        // When each line starts and ends, from the lead.
        var cues = new List<(double At, int Line, bool Starts)>();
        var at = Lead;
        for (var k = 0; k < drawing.Lines.Count; k++)
        {
            var line = drawing.Lines[k];
            at += k == 0 ? Math.Min(line.PauseBefore, 1) : line.PauseBefore;
            cues.Add((at, k, true));
            at += line.Seconds;
            cues.Add((at, k, false));
        }
        var end = at + Tail;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finished() => finished.TrySetResult();
        stage.Finished += Finished;
        var frames = new List<(double At, byte[] Jpeg)>();
        var voice = new List<(double At, byte[] Audio)>();
        try
        {
            stage.Begin(drawing.Frame, drawing.Scale, drawing.CanvasWidth, drawing.CanvasHeight, DrawStagePace.Speech, keepPlace: true);
            stage.Feed(drawing.Program, done: true);
            var clock = Stopwatch.StartNew();
            var next = 0;
            var lastShown = 0.0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = clock.Elapsed.TotalSeconds;
                if (now - lastShown >= 0.25)
                {
                    lastShown = now;
                    done(Math.Min(1, now / end));
                }
                for (; next < cues.Count && cues[next].At <= now; next++)
                {
                    var (_, k, starts) = cues[next];
                    if (starts)
                    {
                        stage.LineStarted(k);
                        if (drawing.Lines[k].Audio is { } audio) voice.Add((now, audio));
                    }
                    else stage.LineEnded(k);
                }
                if (await window.CaptureAsync(png: false) is { } frame) frames.Add((clock.Elapsed.TotalSeconds, frame));
                if (now >= end && (finished.Task.IsCompleted || now >= end + 10)) break;
                if (clock.Elapsed > Longest) break;
                await Task.Delay(FrameGap, cancellationToken);
            }
            return (frames, voice, clock.Elapsed.TotalSeconds);
        }
        finally
        {
            stage.Finished -= Finished;
        }
    }

    /// <summary>The drawing's box (page pixels) in the captured image's pixels, with even sides for the video.</summary>
    internal static Rectangle CropFor(DrawStageBounds bounds, int imageWidth, int imageHeight)
    {
        var sx = imageWidth / bounds.PageWidth;
        var sy = imageHeight / bounds.PageHeight;
        var left = Math.Clamp((int)Math.Floor(bounds.X * sx), 0, imageWidth - 2);
        var top = Math.Clamp((int)Math.Floor(bounds.Y * sy), 0, imageHeight - 2);
        var width = Math.Clamp((int)Math.Ceiling(bounds.Width * sx), 2, imageWidth - left) & ~1;
        var height = Math.Clamp((int)Math.Ceiling(bounds.Height * sy), 2, imageHeight - top) & ~1;
        return new Rectangle(left, top, width, height);
    }

    /// <summary>"Inside a jet engine 2026-11-02 15.30": the title without characters a file name can't hold.</summary>
    internal static string FileNameFor(string title, DateTime when)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(title.Select(c => invalid.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray());
        clean = string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (clean.Length > 60) clean = clean[..60].TrimEnd('.', ' ');
        if (clean.Length == 0) clean = "Drawing";
        return $"{clean} {when:yyyy-MM-dd HH.mm.ss}";
    }

    /// <summary>The saved file, selected in the Explorer window already on Videos\OpenClicky when there is one (a second
    /// save doesn't open a second window).</summary>
    private static void Reveal(string path)
    {
        if (!Platform.ExplorerReveal.Select(path)) AppLog.Write("draw: could not show the saved file");
    }
}
