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
//  A drawing that finished stays up and is remembered (Current): a new question holds it, so a
//  follow-up can add to it or draw it again another way; one that isn't about it clears it. It can
//  be saved (IDrawingSaver) from the page's Picture and Video buttons or by asking; the page shows
//  how the save is going (ShowSaving).
//
//  Threading: called on the UI thread; page messages come back on it.
//

using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Plugins.DrawStage;

/// <summary>Where a saved drawing went: a picture of it and a video of it being drawn, or why it could not be saved.</summary>
public sealed record DrawingSaved(string? PicturePath, string? VideoPath, string? Failure = null);

/// <summary>What to save of a drawing: a picture of it, a video of it being drawn, or both.</summary>
public enum DrawingSaveKind
{
    Both,
    Picture,
    Video,
}

/// <summary>How a save is going: what it is doing, and how far through it is (0 to 1) when it can tell.</summary>
public sealed record DrawingSaveProgress(string Text, double? Fraction = null);

/// <summary>Saves the drawing on the stage to disk (the Windows side: a picture, and a video of it replayed with its voice).</summary>
public interface IDrawingSaver
{
    /// <summary>Saves <paramref name="what"/> of <paramref name="drawing"/>, which is on the stage now; <paramref name="progress"/>
    /// says how it is going.</summary>
    Task<DrawingSaved> SaveAsync(DrawnAnswer drawing, DrawingSaveKind what, Action<DrawingSaveProgress> progress, CancellationToken cancellationToken);
}

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

    /// <summary>Finds a real photo for a query (the Teacher's Wikimedia search); set by the composition root. Without one,
    /// photo cards say none was found.</summary>
    public Func<string, string, CancellationToken, Task<DrawStagePhoto?>>? FindPhoto { get; set; }

    /// <summary>Makes a 3D piece a drawing asks for (f.piece); set by the companion. Without one, the page fills each piece's
    /// box itself.</summary>
    public Func<PieceRequest, Task<DrawStagePiece?>>? MakePiece { get; set; }

    /// <summary>A part of a 3D figure was clicked: its label (or its kind when it has none).</summary>
    public event Action<string>? FigurePartClicked;

    /// <summary>The page's Picture or Video button was clicked.</summary>
    public event Action<DrawingSaveKind>? SaveRequested;

    /// <summary>The drawing's close button was clicked: it goes, as with esc.</summary>
    public event Action? CloseRequested;

    /// <summary>The drawing's Quiz me button was clicked.</summary>
    public event Action? QuizRequested;

    /// <summary>The page asks for a line to be said aloud: a quiz's question, whether the answer was right, the score.</summary>
    public event Action<string>? SayRequested;

    /// <summary>Whether a finished drawing shows the Quiz me button (Settings → Clicky → Drawings); none when unset.</summary>
    public Func<bool>? OffersQuiz { get; set; }

    /// <summary>Saves a drawing to disk; set by the composition root. Without one, drawings can't be saved.</summary>
    public IDrawingSaver? Saver { get; set; }

    /// <summary>The drawing on the stage, once it has finished (null while one is being drawn, and after a clear).</summary>
    public DrawnAnswer? Current { get; private set; }

    /// <summary>The drawing on the stage has finished: it is kept for a follow-up question or for saving.</summary>
    public void Remember(DrawnAnswer drawing)
    {
        if (!IsShowing) return;
        Current = drawing;
        if (IsReady) Surface?.Post(DrawStageMessages.Remembered(OffersQuiz?.Invoke() ?? false));
    }

    /// <summary>The page has loaded and takes messages straight away.</summary>
    public bool IsReady { get; private set; }

    private readonly TaskCompletionSource pageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True once the page has loaded; false when it hasn't within <paramref name="timeout"/> (it never will, most
    /// likely) or <paramref name="cancellationToken"/> stops the wait.</summary>
    public async Task<bool> WhenReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (IsReady) return true;
        await Task.WhenAny(pageReady.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        return IsReady;
    }

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
    /// anything drawn before. <paramref name="keepPlace"/> draws it where the last one was moved to (a replay of it).</summary>
    public void Begin(PixelRect frame, double scale, int canvasWidth, int canvasHeight, DrawStagePace pace = DrawStagePace.Estimate,
        bool keepPlace = false)
    {
        IsShowing = true;
        Current = null;
        Send(DrawStageMessages.BeginStream(canvasWidth, canvasHeight, pace, keepPlace,
            Configuration.OpenClickyConfiguration.Settings.DrawingScriptFont?.Trim()));
        Surface?.Present(frame, scale);
    }

    /// <summary>A follow-up's program, run after the drawing on the stage, which stays (its names too).</summary>
    public void Continue(PixelRect frame, double scale, DrawStagePace pace = DrawStagePace.Estimate)
    {
        IsShowing = true;
        Current = null;
        Send(DrawStageMessages.ContinueStream(pace));
        Surface?.Present(frame, scale);
    }

    /// <summary>A new question: a finished drawing stays up while it is answered (it may be about the drawing); anything
    /// else on the stage is cleared.</summary>
    public void HoldForFollowUp(string reason)
    {
        if (Current is null) ClearIfShowing(reason);
        else Logged?.Invoke($"drawing kept for a follow-up ({reason})");
    }

    /// <summary>What is drawn on the page, as a box in page pixels; null without an answer within <paramref name="timeout"/>.</summary>
    public async Task<DrawStageBounds?> MeasureAsync(TimeSpan timeout)
    {
        if (Surface is null || !IsReady) return null;
        var measured = new TaskCompletionSource<DrawStageBounds>(TaskCreationOptions.RunContinuationsAsynchronously);
        measuring = measured;
        Surface.Post(DrawStageMessages.Simple("measure"));
        var done = await Task.WhenAny(measured.Task, Task.Delay(timeout));
        if (ReferenceEquals(measuring, measured)) measuring = null;
        return done == measured.Task ? measured.Task.Result : null;
    }

    private TaskCompletionSource<DrawStageBounds>? measuring;

    /// <summary>While a drawing is recorded, the page leaves out its caption and its buttons.</summary>
    public void SetRecording(bool on) => Send(DrawStageMessages.Recording(on));

    /// <summary>Shows how a save is going beside the drawing (its buttons wait meanwhile).</summary>
    public void ShowSaving(DrawingSaveProgress progress) => Send(DrawStageMessages.Saving(progress.Text, progress.Fraction, "working"));

    /// <summary>Shows how a save ended beside the drawing, for a few seconds: saved, or <paramref name="failed"/>.</summary>
    public void ShowSaved(string text, bool failed = false) => Send(DrawStageMessages.Saving(text, null, failed ? "failed" : "done"));

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
        Current = null;
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

    private async Task SendPhotoAsync(string id, string query, string caption)
    {
        DrawStagePhoto? photo = null;
        try
        {
            if (FindPhoto is { } find && query.Trim().Length > 0)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                photo = await find(query, caption, timeout.Token);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Logged?.Invoke($"photo search failed ({error.GetType().Name})");
        }
        Logged?.Invoke(photo is null ? $"no photo for \"{query}\"" : $"photo for \"{query}\" ({photo.ImageData.Length / 1024} KB, {photo.Credit})");
        var message = DrawStageMessages.Photo(id, photo);
        if (MainThread is { } mainThread) mainThread.Post(() => Surface?.Post(message));
        else Surface?.Post(message);
    }

    private async Task SendPieceAsync(string id, PieceRequest request)
    {
        var startedAt = DateTimeOffset.UtcNow;
        DrawStagePiece? piece = null;
        try
        {
            if (MakePiece is { } make) piece = await make(request);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Logged?.Invoke($"piece failed ({error.GetType().Name})");
        }
        Logged?.Invoke(piece is null
            ? $"no piece for \"{request.Make}\""
            : $"piece \"{request.Make}\" ({piece.Kind}, {(piece.Data?.Length ?? piece.Text?.Length ?? 0) / 1024} KB) by {piece.MadeBy} in {(DateTimeOffset.UtcNow - startedAt).TotalSeconds:0.0} s");
        var message = DrawStageMessages.Piece(id, piece);
        if (MainThread is { } mainThread) mainThread.Post(() => Surface?.Post(message));
        else Surface?.Post(message);
    }

    /// <summary>Where page replies are posted (the UI thread); set by the composition root.</summary>
    public IMainThread? MainThread { get; set; }

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
                pageReady.TrySetResult();
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
            case "figurePart" when message.Text is { Length: > 0 } part:
                FigurePartClicked?.Invoke(part);
                break;
            case "save":
                SaveRequested?.Invoke(message.Text switch { "picture" => DrawingSaveKind.Picture, "video" => DrawingSaveKind.Video, _ => DrawingSaveKind.Both });
                break;
            case "close":
                CloseRequested?.Invoke();
                break;
            case "quiz":
                QuizRequested?.Invoke();
                break;
            case "say" when message.Text is { Length: > 0 } said:
                SayRequested?.Invoke(said);
                break;
            case "bounds" when message.Bounds is { } bounds:
                measuring?.TrySetResult(bounds);
                break;
            case "photoWanted" when message.Id is { } photoId:
                _ = SendPhotoAsync(photoId, message.Query ?? "", message.Text ?? "");
                break;
            case "pieceWanted" when message.Id is { } pieceId && message.Text is { Length: > 0 } make:
                var size = message.Size ?? [1, 1, 1];
                _ = SendPieceAsync(pieceId, new PieceRequest(make, size[0], size[1], size[2], message.Of));
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
