//
//  DrawStageController.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  The drawing stage: a see-through page over one monitor where an answer is drawn while it is
//  spoken. The model writes its answer as a small program (step(n), say("…") and drawing calls);
//  the program is fed to the page as it streams, and the page runs each statement once it is
//  complete and draws its marks with a pen, each beat's strokes in step with that beat's line.
//
//  This class decides what the page is told and when; the window and the browser that shows the
//  page are the Windows side's (IDrawStageSurface: OpenClicky.Windows/Plugins/DrawStage). Messages
//  sent before the page has loaded wait for its "ready".
//
//  Threading: called on the UI thread; page messages come back on it.
//

using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Plugins.DrawStage;

/// <summary>What only the app's windows can do for the stage.</summary>
public interface IDrawStageSurface
{
    /// <summary>Shows the stage over <paramref name="frame"/> (physical pixels, on a monitor of <paramref name="scale"/>) and
    /// loads the page if it isn't loaded. The page says "ready" through <see cref="MessageReceived"/> once it can take messages.</summary>
    void Present(PixelRect frame, double scale);

    /// <summary>Hides the stage; the page stays loaded for the next answer.</summary>
    void Hide();

    /// <summary>Loads the page without showing anything, so the first drawing doesn't wait for the browser to start.</summary>
    void Prepare();

    /// <summary>Sends one message (JSON, see <see cref="DrawStageMessages"/>) to the page.</summary>
    void Post(string json);

    /// <summary>Every message the page sends, as its JSON text.</summary>
    Action<string>? MessageReceived { get; set; }
}

public sealed class DrawStageController
{
    public static DrawStageController Shared { get; } = new();

    private IDrawStageSurface? surface;
    private readonly List<string> waiting = [];

    /// <summary>The windows (set by the composition root); without one, the stage only logs.</summary>
    public IDrawStageSurface? Surface
    {
        get => surface;
        set
        {
            if (surface is not null) surface.MessageReceived = null;
            surface = value;
            IsReady = false;
            waiting.Clear();
            if (surface is not null) surface.MessageReceived = Receive;
        }
    }

    /// <summary>The page has loaded and takes messages straight away.</summary>
    public bool IsReady { get; private set; }

    /// <summary>A program is being drawn (from <see cref="Begin"/> until it is cleared).</summary>
    public bool IsShowing { get; private set; }

    /// <summary>The program said line <c>k</c> (a say()), as soon as its statement ran: a voice can start fetching its audio.</summary>
    public event Action<int, string>? LineAdded;

    /// <summary>Every statement of the program has run, so every line it says has been added (the count).</summary>
    public event Action<int>? ProgramRan;

    /// <summary>The page wants line <c>k</c> spoken now (pace "speech": its strokes wait for <see cref="LineStarted"/>).</summary>
    public event Action<int, string>? LineDue;

    /// <summary>Everything the program drew is on screen and the program is done.</summary>
    public event Action? Finished;

    /// <summary>A line the page logged (errors included, prefixed "error: ").</summary>
    public event Action<string>? Logged;

    /// <summary>Gets the page loaded ahead of a drawing (the talk key went down); does nothing once it is.</summary>
    public void Prepare() => Surface?.Prepare();

    /// <summary>Opens the stage over <paramref name="frame"/> for a new program drawn on a canvas of
    /// <paramref name="canvasWidth"/> × <paramref name="canvasHeight"/> units (the screenshot the model saw), clearing
    /// anything drawn before.</summary>
    public void Begin(PixelRect frame, double scale, int canvasWidth, int canvasHeight, DrawStagePace pace = DrawStagePace.Estimate)
    {
        IsShowing = true;
        Send(DrawStageMessages.BeginStream(canvasWidth, canvasHeight, pace));
        Surface?.Present(frame, scale);
    }

    /// <summary>The program so far (the whole text each time, as a stream accumulates it); <paramref name="done"/> on the last.</summary>
    public void Feed(string programSoFar, bool done) => Send(DrawStageMessages.Feed(programSoFar, done));

    /// <summary>Line <paramref name="k"/>'s voice started (pace "speech").</summary>
    public void LineStarted(int k) => Send(DrawStageMessages.Line("lineStarted", k));

    /// <summary>Line <paramref name="k"/>'s voice ended (pace "speech").</summary>
    public void LineEnded(int k) => Send(DrawStageMessages.Line("lineEnded", k));

    /// <summary>Draws everything still waiting right away (the voice was cut short or skipped).</summary>
    public void FinishNow() => Send(DrawStageMessages.Simple("finishNow"));

    /// <summary>Wipes the drawing and hides the stage.</summary>
    public void Clear(string reason)
    {
        Logged?.Invoke($"stage cleared ({reason})");
        IsShowing = false;
        waiting.Clear();
        if (IsReady) Surface?.Post(DrawStageMessages.Simple("clear"));
        Surface?.Hide();
    }

    /// <summary>Clears the stage when something is drawn on it (a new question, esc); otherwise does nothing.</summary>
    public bool ClearIfShowing(string reason)
    {
        if (!IsShowing) return false;
        Clear(reason);
        return true;
    }

    private void Send(string json)
    {
        if (Surface is null) return;
        if (IsReady) Surface.Post(json);
        else waiting.Add(json);
    }

    private void Receive(string json)
    {
        if (DrawStageMessages.ParseEvent(json) is not { } message) return;
        switch (message.Type)
        {
            case "ready":
                IsReady = true;
                var queued = waiting.ToArray();
                waiting.Clear();
                foreach (var each in queued) Surface?.Post(each);
                break;
            case "line" when message.K is { } k:
                LineAdded?.Invoke(k, message.Text ?? "");
                break;
            case "lineDue" when message.K is { } k:
                LineDue?.Invoke(k, message.Text ?? "");
                break;
            case "programRan":
                ProgramRan?.Invoke(message.K ?? 0);
                break;
            case "finished":
                Finished?.Invoke();
                break;
            case "error":
                Logged?.Invoke("error: " + message.Text);
                break;
            case "log":
                Logged?.Invoke(message.Text ?? "");
                break;
        }
    }
}
