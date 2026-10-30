// Ports RealtimeVoiceClient.swift: the fast voice lane — speech in, speech out over one OpenAI
// Realtime (or Gemini Live) WebSocket, no transcribe → think → synthesize chain.
//
// - The client secret comes from ProviderRouter: the person's own OpenAI key mints it at OpenAI,
//   else the backend mints it (POST /agent/realtime/session); the real key never reaches OpenAI's socket.
// - A Gemini key (alone, or picked in Settings) runs the same turns on Gemini Live instead: the
//   client still speaks OpenAI's events, and GeminiLiveDialect translates them both ways.
// - The microphone is a communications capture (echo cancellation where Windows has it), so it can
//   stay open while OpenClicky talks (full duplex, barge-in).
// - Push-to-talk: audio streams while the shortcut is held; on release a 400 ms tail is kept, then
//   the buffer is committed and a response requested. Always-on: server VAD turns.
// - `send_to_agent` tool calls hand real work to the agent lane and speak the result.
// - `point_at` tool calls fly the pointer to a spot in the screenshot attached to the turn, one call
//   per step, resolved on a serial chain so the model can point while it explains.
// - `click` tool calls go through the same chain, then click for real and look again (never a
//   control that sends, buys or deletes, nor a target nothing located).
//
// Threading: as the Mac's @MainActor class, every public member is called on the main thread
// (IMainThread); socket events, audio frames and timers are posted back to it.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Speech;

/// <summary>The screen context attached to a Realtime turn: the JPEG the model sees, a caption with the
/// pointer position, and the capture it came from, so `point_at` coordinates map back onto the display.</summary>
public sealed record RealtimeScreenContext(byte[] Jpeg, string Caption, CompanionScreenCapture Capture);

/// <summary>What a desktop action (the fast lane) said: the sentence for the person, and whether the
/// arguments were refused rather than the action run.</summary>
/// <param name="ModelOutput">What the model is told in the tool's output when it differs from the sentence
/// (a screenshot to read, a rule to keep); null → the sentence.</param>
/// <param name="ThenLooksAtScreen">The action changed the screen (a page opened): the model sees a fresh
/// screenshot, once it has settled, before it speaks.</param>
/// <param name="LogDescription">The log's words when the sentence would carry something private; null → the sentence.</param>
public sealed record RealtimeToolResult(string SpokenSentence, bool WasRejected = false, string? ModelOutput = null,
                                        bool ThenLooksAtScreen = false, string? LogDescription = null);

/// <summary>
/// The `point_at` resolution steps after the OCR snap (which the client does itself through
/// IScreenTextRecognizer + ScreenTextLocator). Every point is in the capture's screenshot pixels.
/// Wired (CompanionManager.Realtime.cs / AppComposition): wire LocateBrowserTab to BrowserTabLocator.Locate over IBrowserTabSource,
/// LocateAccessibleElement to AccessibleElementLocator.BestMatch over IFocusedElementReader's
/// captioned controls (mapped into screenshot pixels), and GroundWithClaude to
/// ScreenElementGrounder.LocateAsync with the Answers port's image call.
/// </summary>
public sealed record RealtimePointingServices
{
    /// <summary>(label, text, userRequest, capture) → the tab's point and title, when the request is about a visible browser tab.</summary>
    public Func<string, string, string?, CompanionScreenCapture, Task<(ScreenPoint Point, string Title)?>>? LocateBrowserTab { get; init; }

    /// <summary>(label, text, userRequest, guess, capture, maxDistance, ambiguityMargin) → the control UI Automation says it is.</summary>
    public Func<string, string, string?, ScreenPoint, CompanionScreenCapture, double, double, Task<AccessibleElementCandidate?>>? LocateAccessibleElement { get; init; }

    /// <summary>(label, text, userRequest, capture) → where Claude says the element is (2–4 s).</summary>
    public Func<string, string, string?, CompanionScreenCapture, Task<ScreenElementGrounder.Located?>>? GroundWithClaude { get; init; }
}

public enum RealtimeTurnMode
{
    PushToTalk,
    AlwaysOn,
}

public enum RealtimeTranscriptRole
{
    User,
    Assistant,
}

public enum RealtimeVoiceErrorKind
{
    NotConfigured,
    NotConnected,
    Backend,
    Audio,
    InvalidUrl,
}

public sealed class RealtimeVoiceException : Exception
{
    public RealtimeVoiceErrorKind Kind { get; }
    public string Detail { get; }

    public RealtimeVoiceException(RealtimeVoiceErrorKind kind, string detail = "") : base(Describe(kind, detail))
    {
        Kind = kind;
        Detail = detail;
    }

    private static string Describe(RealtimeVoiceErrorKind kind, string detail) => kind switch
    {
        RealtimeVoiceErrorKind.NotConfigured => "OpenClicky backend token is not configured.",
        RealtimeVoiceErrorKind.NotConnected => "Realtime session is not connected.",
        RealtimeVoiceErrorKind.Backend => $"Realtime session could not be created: {detail}",
        RealtimeVoiceErrorKind.Audio => $"Audio setup failed: {detail}",
        _ => detail,
    };
}

public sealed partial class RealtimeVoiceClient : ObservableObject, IDisposable
{
    public const string DefaultInstructions =
        "You are OpenClicky, a friendly, fast Windows voice assistant. "
        + "Keep spoken replies short (one or two sentences). Every request comes with a screenshot of the user's current screen "
        + "(with the pointer position noted): \"this\", \"here\", \"that\" refer to what is on screen, usually near the pointer. Look "
        + "at the screenshot and answer about it directly; never say you cannot see the screen and never mention a camera. "
        + "Answer quick questions yourself. When the user asks how to do something, where something is, or what to "
        + "click, point at it with the point_at tool while you explain, one call per step, in order, with the element's "
        + "exact on-screen text when it has any, and keep speaking between calls. Do not point for general questions or things they are obviously already looking "
        + "at. "
        + "Do simple local things yourself with the fast tools (open_app, open_url, web_search, create_folder, "
        + "reveal_in_explorer, open_folder, find_files, find_on_page, scroll, switch_tab, set_volume, media_control, play_music, minimize_all, "
        + "restore_windows, check_email, draft_email) and then say what they give back in a few words. "
        + "When they tell you to click, open or press something on screen (\"click the first link\", \"open that video\"), call "
        + "click with the element's on-screen text; do not point at it or explain instead. \"Scroll down\", \"go back to the top\" "
        + "is the scroll tool. \"Look for\" a file is find_files; \"look for\" words on this page is find_on_page. "
        + "A link opens in the same tab; pass new_tab only when they say \"in a new tab\" or \"in another tab\". \"Go to that tab\", "
        + "\"next tab\", \"the YouTube tab\" is switch_tab. "
        + "You cannot know live facts (the weather, news, "
        + "scores, prices, opening hours): call web_search for them and answer from the results, never say you cannot check. "
        + Answers.AskLanePrompts.CommandOrQuestionInstruction + " "
        + "For anything bigger (creating or editing files or code, running commands, using integrations, research, "
        + "multi-step tasks), first say one short sentence acknowledging it, then call the send_to_agent "
        + "tool with a clear, self-contained task, and afterwards tell the user in one sentence what happened. "
        + "Never pretend work was done without the tool. Do not read file paths aloud character by character. "
        + "When the screen context quotes text they highlighted or the text box they are in, and they ask you to rephrase, "
        + "rewrite, shorten, fix, polish or translate it (\"rephrase this\", \"make this sound professional\", \"do that for me\"), "
        + "call write_in_place with the complete new text, then say only a few words about what you changed; never read the "
        + "new text out. If they ask how to rephrase it or for suggestions, answer in words and change nothing. "
        + "If no text was quoted, ask them to highlight it and ask again.";

    // MARK: - Connection rules (pure, tested)

    /// <summary>Replaced before OpenAI's 60-minute cap, with room to finish a conversation.</summary>
    public static readonly TimeSpan MaximumConnectionAge = TimeSpan.FromMinutes(50);
    /// <summary>Quiet this long, a socket is checked before a turn is spoken into it.</summary>
    public static readonly TimeSpan PingAfterSilence = TimeSpan.FromSeconds(60);
    /// <summary>How long after the last turn the keep-warm loop still reconnects in the background.</summary>
    public static readonly TimeSpan KeepWarmWhileUsedWithin = TimeSpan.FromMinutes(15);
    /// <summary>The keep-warm loop's period.</summary>
    public static readonly TimeSpan KeepWarmInterval = TimeSpan.FromSeconds(20);

    public static bool IsTooOld(DateTimeOffset? connectedAt, DateTimeOffset now, TimeSpan? maximumAge = null) =>
        connectedAt is not { } connected || now - connected >= (maximumAge ?? MaximumConnectionAge);

    /// <summary>How long a Gemini conversation is picked up again after its socket closed.</summary>
    public static readonly TimeSpan GeminiResumptionLifetime = TimeSpan.FromMinutes(30);

    public static bool NeedsPing(DateTimeOffset? lastServerEventAt, DateTimeOffset now) =>
        lastServerEventAt is not { } last || now - last >= PingAfterSilence;

    public static bool IsRecentlyUsed(DateTimeOffset lastTurnStartedAt, DateTimeOffset now) => now - lastTurnStartedAt < KeepWarmWhileUsedWithin;

    /// <summary>OpenAI refuses to commit less than 100 ms of audio; a turn shorter than that heard nothing.</summary>
    public static bool HasEnoughAudioForATurn(int pcm16Bytes) => pcm16Bytes >= (int)(RealtimeAudio.SampleRate * 0.1) * 2;

    /// <summary>
    /// The socket URL for the model the session answer named. `model` arrives verbatim from a server,
    /// so it is escaped: a malformed or hostile value still makes a well-formed URL on OpenAI's host.
    /// </summary>
    public static Uri RealtimeSocketUrl(string model)
    {
        if (Uri.TryCreate($"wss://api.openai.com/v1/realtime?model={Uri.EscapeDataString(model)}", UriKind.Absolute, out var url)) return url;
        return new Uri("wss://api.openai.com/v1/realtime?model=gpt-realtime");
    }

    /// <summary>What a refused socket handshake means, in words a person can act on.</summary>
    public static string HandshakeFailureDescription(int? statusCode, string underlyingError, string provider = "OpenAI") => statusCode switch
    {
        401 => $"{provider} refused the realtime session (401): the session key was rejected. Check your sign-in, or your own {provider} key in Settings → Account.",
        403 => $"{provider} refused the realtime session (403): this key has no access to the realtime model.",
        429 => $"{provider} refused the realtime session (429): this key is over its rate limit or out of credit.",
        { } status when status >= 400 => $"{provider} refused the realtime session ({status}).",
        _ => underlyingError,
    };

    /// <summary>The server's close frame in words, with the reason it gave (Gemini explains a refused setup there).</summary>
    public static string CloseDescription(string provider, int? status, string? reason) =>
        $"{provider} closed the connection ({status}){(string.IsNullOrWhiteSpace(reason) ? "" : $": {reason.Trim()}")}";

    /// <summary>The backend's own error text where it gave one, with the status, so a rejected key reads as a rejected key rather than as JSON.</summary>
    public static string BackendRefusalDescription(int statusCode, byte[] body)
    {
        string? stated = null;
        try
        {
            if (JsonNode.Parse(body) is JsonObject json)
                stated = SpeechJson.String(json["error"]) ?? SpeechJson.String((json["error"] as JsonObject)?["message"]);
        }
        catch (JsonException)
        {
        }
        stated ??= Encoding.UTF8.GetString(body).Trim();
        var detail = string.IsNullOrEmpty(stated) ? "no client secret" : stated;
        return statusCode switch
        {
            401 or 403 => $"the backend refused ({statusCode}): {detail}. Check your sign-in, or your own OpenAI key in %USERPROFILE%\\.openclicky\\shell.json.",
            0 => detail,
            _ => $"the backend answered {statusCode}: {detail}",
        };
    }

    /// <summary>The microphone line in the log (the Mac named its capture authorization here).</summary>
    public static string MicrophoneAuthorizationDescription(MicrophoneConsent consent)
    {
        var words = consent switch
        {
            MicrophoneConsent.Allowed => "allowed",
            MicrophoneConsent.DeniedForDevice => "denied for the whole device (Settings → Privacy & security → Microphone)",
            MicrophoneConsent.DeniedForAllApps => "denied for apps (Let apps access your microphone is off)",
            MicrophoneConsent.DeniedForDesktopApps => "denied for desktop apps (Let desktop apps access your microphone is off)",
            _ => "unknown (the privacy switches could not be read; capture decides)",
        };
        return $"microphone authorization: {words}";
    }

    /// <summary>The highlighted text as a line of the screen context (SelectedTextReader.contextLine).</summary>
    public static string SelectedTextContextLine(string selectedText) =>
        $"The user has selected (highlighted) this text on screen: «{selectedText}». When they say \"this\", \"this line\" or \"this word\", they almost certainly mean the selection, answer about that text, not about whatever is under the pointer.";

    // MARK: - State

    [ObservableProperty] private bool isConnected;
    /// <summary>True while assistant audio is queued or playing.</summary>
    [ObservableProperty] private bool isSpeaking;
    /// <summary>0…1 microphone level for the waveform while capturing.</summary>
    [ObservableProperty] private double inputLevel;
    /// <summary>True while microphone audio is being streamed to the server.</summary>
    [ObservableProperty] private bool isCapturing;

    public Action<RealtimeTranscriptRole, string>? OnTranscript { get; set; }
    public Action<string>? OnEvent { get; set; }
    public Action? OnResponseStarted { get; set; }
    public Action? OnResponseFinished { get; set; }
    /// <summary>The socket went away (or a send failed) while it was thought to be connected. Without this a turn
    /// in flight never got its `response.done`, the voice state stayed on processing, and that blocked dictation.</summary>
    public Action<string>? OnConnectionLost { get; set; }
    /// <summary>A push-to-talk turn ended with (almost) no audio, and was not sent.</summary>
    public Action? OnTurnHeardNothing { get; set; }
    /// <summary>Hands-free only: the person signed off by voice ("that's all, thanks"). Called once the
    /// goodbye that answers the sign-off has finished playing; the companion turns hands-free off.</summary>
    public Action? OnStopListeningRequested { get; set; }
    /// <summary>Runs the agent for a `send_to_agent` tool call; the returned text is spoken by the model.</summary>
    public Func<string, Task<string>>? OnAgentTask { get; set; }
    /// <summary>A desktop action (open an app, make a folder…) by tool name and arguments; null when the
    /// tool is not one of them. Wired (CompanionManager.Realtime.cs / AppComposition): DesktopActions (the MacActions port).</summary>
    public Func<string, JsonObject, Task<RealtimeToolResult?>>? OnDesktopAction { get; set; }
    /// <summary>The desktop actions' tool definitions, offered next to send_to_agent and point_at.</summary>
    public Func<IReadOnlyList<JsonObject>>? DesktopActionToolDefinitions { get; set; }
    /// <summary>The process in front, for "act, then look": the screen has settled once a different app is
    /// in front (a browser opened) or the wait runs out. Null → only the wait.</summary>
    public Func<int?>? FrontAppProvider { get; set; }
    /// <summary>Tools a plugin adds to the session while it is on (the Teacher's drawing tools). Read with every session
    /// update; a plugin switched on or off also changes the instructions, which is what sends the update.</summary>
    public Func<IReadOnlyList<JsonObject>>? PluginToolDefinitions { get; set; }
    /// <summary>Runs a plugin's tool call with the screenshot the turn carried; null when no plugin owns the tool. Must
    /// return quickly: tool calls are handled on the receive loop.</summary>
    public Func<string, JsonObject, CompanionScreenCapture?, Task<string?>>? OnPluginToolCall { get; set; }
    /// <summary>The screen context attached to every turn; null → the turn goes out without an image.</summary>
    public Func<Task<RealtimeScreenContext?>>? ScreenContextProvider { get; set; }
    /// <summary>The highlighted text in the app in front (IFocusedElementReader.SelectedTextAsync).</summary>
    public Func<Task<string?>>? SelectedTextProvider { get; set; }
    /// <summary>Reads the text a "rephrase this" would replace (the highlight, or the focused text box) when a
    /// turn's screen is attached; the turn quotes it and `write_in_place` writes over it.</summary>
    public Func<Task<WritingTarget?>>? WritingTargetProvider { get; set; }
    /// <summary>The OS side of `write_in_place` (UI Automation, the clipboard, SendInput); null → the tool says it cannot.</summary>
    public IWriteInPlaceHost? WriteInPlaceHost { get; set; }
    /// <summary>A rewrite landed in place (the ask lane counts it as a successful quick action too).</summary>
    public Action? OnWroteInPlace { get; set; }
    /// <summary>The text to write over for the turn in flight, captured with its screen.</summary>
    public WritingTarget? LastWritingTarget { get; private set; }
    /// <summary>A `point_at` call resolved: screenshot pixels, the model's label, and the capture they refer to.</summary>
    public Action<ScreenPoint, string, CompanionScreenCapture>? OnPointAt { get; set; }
    /// <summary>The `click` tool's left click at a physical virtual-desktop pixel, and whether they asked for a new tab
    /// (IDesktopActionExecutor.ClickAsync: a web link is followed in its own tab); null → nothing is clicked.</summary>
    public Func<PixelPoint, bool, Task<Actions.ScreenClickResult>>? OnClick { get; set; }
    /// <summary>Builds the session instructions for the next turn: the base prompt plus the skills that
    /// apply right now. Read at connect and before every turn; only a change is sent as `session.update`.</summary>
    public Func<string>? InstructionsProvider { get; set; }
    public RealtimePointingServices Pointing { get; set; } = new();

    /// <summary>Whether Esc has anything to stop here: a reply being made or played, or a tool call still
    /// working (an action whose page is settling before the look, the agent).</summary>
    public bool IsBusy => responseInProgress || IsSpeaking || pendingToolCalls > 0;

    /// <summary>The capture behind the most recently attached screen context; `point_at` maps onto it.</summary>
    public CompanionScreenCapture? LastScreenCapture { get; private set; }
    public RealtimeTurnMode TurnMode { get; private set; } = RealtimeTurnMode.PushToTalk;
    /// <summary>The prompt this client was created with; providers compose their skills block onto it.</summary>
    public string BaseInstructions => instructions;
    /// <summary>Who the current (or last) connection talks to, for the sentences a person sees.</summary>
    public string ProviderName => dialect.ProviderName;

    private readonly IMainThread mainThread;
    private readonly RealtimeAudio audio;
    private readonly IScreenTextRecognizer? screenTextRecognizer;
    private readonly IMicrophonePermission? microphonePermission;
    private readonly Func<ISpeechWebSocket> makeSocket;
    private readonly Func<DateTimeOffset> now;
    private readonly string? voice;
    private readonly string instructions;
    private string sentInstructions = "";

    private ISpeechWebSocket? webSocket;
    private OrderedSocketSender? socketSender;
    /// <summary>Translates the client's OpenAI-shaped events for the vendor of the current connection.</summary>
    private IRealtimeWireDialect dialect = new OpenAIWireDialect();
    /// <summary>Gemini's handle for the conversation, kept across connections (Gemini replaces a socket
    /// about every ten minutes) so a new one carries on where the last stopped.</summary>
    private (string Handle, DateTimeOffset SavedAt)? geminiResumption;
    /// <summary>Gemini said the socket is about to close; hands-free reconnects at the next quiet moment.</summary>
    private bool reconnectAfterGoAway;
    /// <summary>Tool calls still running (an agent task, a desktop action): not a quiet moment to reconnect.</summary>
    private int pendingToolCalls;
    private CancellationTokenSource? socketLifetime;
    /// <summary>True once the server's first event arrived on `webSocket`. Until then `Send` refuses:
    /// a message written while the handshake is in flight can fail and tear the socket down (issue #1).</summary>
    private bool isSocketOpen;
    private Task? connectTask;
    private CancellationTokenSource? keepWarmCancellation;
    private DateTimeOffset? connectedAt;
    private DateTimeOffset? lastServerEventAt;
    /// <summary>When the user last started a turn. The keep-warm loop reconnects in the background only while this is
    /// recent: every session is minted and billed, and a PC left running overnight must not buy one an hour for nobody.</summary>
    private DateTimeOffset? lastTurnStartedAt;
    private TaskCompletionSource? handshakeSource;
    /// <summary>A refusal that arrived before anything was waiting for the handshake.</summary>
    private string? handshakeFailureBeforeWaiting;
    /// <summary>Resolved by the next server event (the round trip that stands in for a websocket ping).</summary>
    private TaskCompletionSource? nextServerEventSource;

    private bool isAudioWired;
    private bool isForwardingMicrophone;
    private RealtimeTurnMode currentMode = RealtimeTurnMode.PushToTalk;
    private bool pushToTalkArmed;
    private CancellationTokenSource? idlePauseCancellation;
    private CancellationTokenSource? pushToTalkTailCancellation;
    private int microphoneAppends;
    /// <summary>PCM16 bytes forwarded since the current push-to-talk press.</summary>
    private int pushToTalkTurnAudioBytes;
    private string assistantTranscriptBuffer = "";
    private bool responseInProgress;
    /// <summary>A tool output was sent during the current response; the continuation `response.create` is sent once, on `response.done`.</summary>
    private bool needsContinuationAfterResponse;
    /// <summary>Id of the response the server is generating right now, so tool calls can be matched to it.</summary>
    private string? activeResponseId;
    /// <summary>Responses we cancelled (barge-in). Their late tool calls still get an output item, but never arm a continuation.</summary>
    private readonly HashSet<string> cancelledResponseIds = [];
    private bool wasListeningContinuouslyBeforeDictation;
    /// <summary>A sign-off was heard: hands-free ends after the goodbye response is done and has played.</summary>
    private bool stopListeningAfterGoodbye;
    /// <summary>Counts the turns: bumped whenever the conversation moves on (a cancel, a barge-in, a new
    /// turn's response). A tool call whose turn has moved on by the time it finishes still sends its
    /// output (it belongs in the conversation) but asks for no continuation and takes no second look.</summary>
    private int turnGeneration;

    /// <summary>OCR of `LastScreenCapture`, started as soon as the screen is attached so it is ready by the first `point_at`.</summary>
    private Task<IReadOnlyList<ScreenTextLine>>? lastScreenText;
    private CancellationTokenSource? lastScreenTextCancellation;
    /// <summary>`point_at` calls resolve one after another so a walkthrough's steps fly in the order the model gave.</summary>
    private Task? pointingChain;
    private CancellationTokenSource pointingCancellation = new();
    /// <summary>The last thing the user said, given to Claude when it has to locate an icon.</summary>
    private string? lastUserTranscript;

    public RealtimeVoiceClient(
        IMainThread mainThread,
        RealtimeAudio audio,
        IScreenTextRecognizer? screenTextRecognizer = null,
        IMicrophonePermission? microphonePermission = null,
        Func<ISpeechWebSocket>? makeSocket = null,
        string? voice = null,
        string instructions = DefaultInstructions,
        Func<DateTimeOffset>? now = null)
    {
        this.mainThread = mainThread;
        this.audio = audio;
        this.screenTextRecognizer = screenTextRecognizer;
        this.microphonePermission = microphonePermission;
        this.makeSocket = makeSocket ?? (() => new ClientSpeechWebSocket());
        this.voice = voice;
        this.instructions = instructions;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Settings → microphone (null = the default), the same one dictation hears with. Applied when the audio is next set up.</summary>
    public string? PreferredMicrophoneId
    {
        get => audio.PreferredMicrophoneId;
        set => audio.PreferredMicrophoneId = value;
    }

    /// <summary>After sleep a socket can look open and be dead: the next press checks it first.
    /// Wired (CompanionManager.Realtime.cs / AppComposition): call from SystemEvents.PowerModeChanged (Resume).</summary>
    public void NoteSystemResumed() => lastServerEventAt = DateTimeOffset.MinValue;

    /// <summary>Waits for every queued `point_at` to resolve and fly (the smoke harness exits after this).</summary>
    public Task AwaitPendingPointingAsync() => pointingChain ?? Task.CompletedTask;

    /// <summary>Capture/send statistics for diagnostics.</summary>
    public string DebugSummary() =>
        $"mic: {audio.DebugSummary()}; appends sent {microphoneAppends}; forwarding {isForwardingMicrophone}; connected {IsConnected}";

    // MARK: - Connection

    /// <summary>
    /// Connect (or reuse the live connection). Safe to call before every push-to-talk press. A live
    /// connection is reused only if it is still worth trusting: one close to the vendor's cap (OpenAI's 60 minutes, Gemini's ten)
    /// is replaced before it is cut off mid-turn, and one the server has been quiet on is checked first.
    /// </summary>
    public async Task ConnectIfNeededAsync(RealtimeTurnMode mode)
    {
        if (IsConnected && TurnMode == mode)
        {
            var currentTime = now();
            if (!IsTooOld(connectedAt, currentTime, dialect.MaximumConnectionAge))
            {
                if (!NeedsPing(lastServerEventAt, currentTime)) return;
                if (await PingAsync())
                {
                    lastServerEventAt = now();
                    return;
                }
            }
            Log("realtime connection stale, reconnecting");
        }
        if (connectTask is { } pending && TurnMode == mode)
        {
            await pending;
            return;
        }
        CloseConnection();
        TurnMode = mode;
        var task = OpenConnectionAsync(mode);
        connectTask = task;
        try
        {
            await task;
        }
        finally
        {
            if (connectTask == task) connectTask = null;
        }
    }

    /// <summary>
    /// The server's answer within two seconds, or the socket is treated as gone. ClientWebSocket has
    /// no ping-with-pong API, so the round trip is an `input_audio_buffer.clear` (answered with
    /// `input_audio_buffer.cleared`); it is only sent before a turn starts, which clears the buffer anyway.
    /// </summary>
    private async Task<bool> PingAsync()
    {
        // Gemini has nothing that answers like input_audio_buffer.cleared (the Mac pings the socket
        // itself, which ClientWebSocket cannot). Its keep-alive pings (every 20 s) have held the
        // socket open, so only a wake from sleep, which may have killed it unseen, reconnects.
        if (dialect is not OpenAIWireDialect) return lastServerEventAt != DateTimeOffset.MinValue;
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        nextServerEventSource = waiter;
        try
        {
            Send(new JsonObject { ["type"] = "input_audio_buffer.clear" });
        }
        catch (RealtimeVoiceException)
        {
            return false;
        }
        var finished = await Task.WhenAny(waiter.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        if (nextServerEventSource == waiter) nextServerEventSource = null;
        return finished == waiter.Task;
    }

    /// <summary>
    /// Keep a session open from launch so the first press only starts audio. Reconnects on drop,
    /// while it is being used (see lastTurnStartedAt) or when always-on needs it.
    /// </summary>
    public void KeepWarm(RealtimeTurnMode mode)
    {
        keepWarmCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        keepWarmCancellation = cancellation;
        var warmStartedAt = now();
        _ = KeepWarmLoopAsync(mode, warmStartedAt, cancellation.Token);
    }

    private async Task KeepWarmLoopAsync(RealtimeTurnMode mode, DateTimeOffset warmStartedAt, CancellationToken cancellationToken)
    {
        var hasConnectedOnce = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var recentlyUsed = IsRecentlyUsed(lastTurnStartedAt ?? warmStartedAt, now());
            if (!IsConnected && (!hasConnectedOnce || mode == RealtimeTurnMode.AlwaysOn || recentlyUsed))
            {
                try
                {
                    await ConnectIfNeededAsync(mode);
                    hasConnectedOnce = true;
                }
                catch (Exception error)
                {
                    Log($"warm-up failed: {error.Message}");
                }
            }
            try
            {
                await Task.Delay(KeepWarmInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Disconnect(string? reason)
    {
        keepWarmCancellation?.Cancel();
        keepWarmCancellation = null;
        CloseConnection();
        if (reason is not null) Log(reason);
    }

    /// <summary>Everything Disconnect does except stopping the keep-warm loop, which is what calls this when it reconnects.</summary>
    private void CloseConnection()
    {
        if (dialect is GeminiLiveDialect gemini && gemini.ResumptionHandle is { } handle) geminiResumption = (handle, now());
        reconnectAfterGoAway = false;
        FailPendingHandshake(new RealtimeVoiceException(RealtimeVoiceErrorKind.NotConnected));
        handshakeFailureBeforeWaiting = null;
        connectedAt = null;
        lastServerEventAt = null;
        StopForwardingMicrophone();
        pushToTalkArmed = false;
        // A sign-off heard on a session that is going away belongs to that session only.
        stopListeningAfterGoodbye = false;
        idlePauseCancellation?.Cancel();
        FlushPlayback();
        audio.Release();
        socketLifetime?.Cancel();
        socketSender?.Complete();
        if (webSocket is { } closingSocket) _ = closingSocket.CloseAsync();
        webSocket = null;
        socketSender = null;
        socketLifetime = null;
        isSocketOpen = false;
        if (IsConnected) IsConnected = false;
        responseInProgress = false;
        needsContinuationAfterResponse = false;
        activeResponseId = null;
        cancelledResponseIds.Clear();
        LastScreenCapture = null;
        lastScreenTextCancellation?.Cancel();
        lastScreenText = null;
        pointingCancellation.Cancel();
        pointingCancellation = new CancellationTokenSource();
        pointingChain = null;
        lastUserTranscript = null;
        sentInstructions = "";
    }

    private void FailPendingHandshake(Exception error)
    {
        var pending = handshakeSource;
        handshakeSource = null;
        pending?.TrySetException(error);
    }

    private async Task OpenConnectionAsync(RealtimeTurnMode mode)
    {
        if (!OpenClickyConfiguration.IsConfigured) throw new RealtimeVoiceException(RealtimeVoiceErrorKind.NotConfigured);
        var connectInstructions = CurrentInstructions();
        var credentials = ProviderRouter.CurrentCredentials();
        var provider = RealtimeVoiceProviders.Choose(credentials, OpenClickyConfiguration.PreferredRealtimeProvider);
        if (provider == RealtimeVoiceProvider.Gemini && credentials.GeminiKey is { } geminiKey)
        {
            await OpenGeminiConnectionAsync(geminiKey, mode, connectInstructions);
            return;
        }
        // Set before the secret is minted, so a refusal there is said in OpenAI's name.
        dialect = new OpenAIWireDialect();
        HttpResponseMessage secretResponse;
        var route = ProviderRouter.Current(ProviderCapability.Realtime);
        switch (route.Kind)
        {
            case ProviderRouteKind.OpenAIDirect:
            {
                // The person's own OpenAI key mints the client secret at OpenAI itself; the answer has the backend's shape.
                var openAIKey = ProviderRouter.CurrentCredentials().OpenAIKey ?? throw new RealtimeVoiceException(RealtimeVoiceErrorKind.NotConfigured);
                var secretRequest = DirectProviderRequests.OpenAIRealtimeSecret(openAIKey, connectInstructions, voice);
                secretResponse = await HttpClients.Shared.SendAsync(secretRequest);
                break;
            }
            case ProviderRouteKind.Unavailable:
                throw new ProviderRouteUnavailableException(ProviderCapability.Realtime, route.Sentence ?? ProviderRouter.NeedsOpenAIKeySentence);
            default:
            {
                var secretUrl = $"{OpenClickyConfiguration.BackendBaseUrl}/agent/realtime/session";
                if (!Uri.TryCreate(secretUrl, UriKind.Absolute, out var secretUri))
                    throw new RealtimeVoiceException(RealtimeVoiceErrorKind.InvalidUrl, $"OpenClicky backend URL is invalid: {OpenClickyConfiguration.BackendBaseUrl}");
                var body = new JsonObject { ["instructions"] = connectInstructions };
                if (voice is not null) body["voice"] = voice;
                var secretRequest = new HttpRequestMessage(HttpMethod.Post, secretUri) { Content = DirectProviderRequests.JsonContent(body) };
                OpenClickyConfiguration.Authorize(secretRequest);
                secretResponse = await BackendSignIn.SendAsync(secretRequest);
                break;
            }
        }
        string clientSecret;
        string model;
        using (secretResponse)
        {
            var secretData = await secretResponse.Content.ReadAsByteArrayAsync();
            var secretStatusCode = (int)secretResponse.StatusCode;
            var parsed = secretStatusCode is >= 200 and <= 299 ? ParseClientSecret(secretData) : null;
            if (parsed is null) throw new RealtimeVoiceException(RealtimeVoiceErrorKind.Backend, BackendRefusalDescription(secretStatusCode, secretData));
            (clientSecret, model) = parsed.Value;
        }
        var socketUrl = RealtimeSocketUrl(model);
        var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {clientSecret}" };
        await StartSessionAsync(makeSocket(), socketUrl, headers, model, mode, connectInstructions, () => WarmUpAudioAsync(mode));
    }

    /// <summary>
    /// Push-to-talk only opens the microphone while the shortcut is held; always-on keeps it open.
    /// The audio is set up once while connecting (and paused right away) so key-down is a fast
    /// restart. Nothing is forwarded yet: that starts with a press or StartListeningContinuously.
    /// </summary>
    private async Task WarmUpAudioAsync(RealtimeTurnMode mode)
    {
        await StartAudioIfNeededAsync();
        if (mode == RealtimeTurnMode.PushToTalk) audio.Pause();
    }

    /// <summary>
    /// Gemini Live on the person's own key, which goes in a header straight to Google. A resumed
    /// conversation that Gemini no longer knows is refused at setup; then it starts afresh once.
    /// </summary>
    private async Task OpenGeminiConnectionAsync(string key, RealtimeTurnMode mode, string connectInstructions)
    {
        AppLog.Write("route: realtime → gemini, own key");
        var handle = geminiResumption is { } saved && now() - saved.SavedAt < GeminiResumptionLifetime ? saved.Handle : null;
        try
        {
            await StartGeminiSessionAsync(key, mode, connectInstructions, handle);
        }
        catch (Exception error) when (handle is not null)
        {
            Log($"gemini did not resume the conversation ({error.Message}); starting a new one");
            geminiResumption = null;
            await StartGeminiSessionAsync(key, mode, connectInstructions, null);
        }
    }

    private Task StartGeminiSessionAsync(string key, RealtimeTurnMode mode, string connectInstructions, string? resumptionHandle)
    {
        var gemini = new GeminiLiveDialect(RealtimeAudio.SampleRate, resumptionHandle);
        var headers = new Dictionary<string, string> { ["x-goog-api-key"] = key };
        return StartSessionAsync(makeSocket(), GeminiLiveDialect.SocketUrl, headers, gemini.Model, mode, connectInstructions, () => WarmUpAudioAsync(mode), gemini);
    }

    /// <summary>The client secret (`value`) and the model (`session.model`, else gpt-realtime) of a session answer.</summary>
    public static (string ClientSecret, string Model)? ParseClientSecret(byte[] data)
    {
        try
        {
            if (JsonNode.Parse(data) is not JsonObject json || SpeechJson.String(json["value"]) is not { } secret) return null;
            var model = SpeechJson.String((json["session"] as JsonObject)?["model"]) ?? "gpt-realtime";
            return (secret, model);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens <paramref name="socket"/> and brings the session up, in this order: connect, wait for
    /// the server's first event (normally `session.created`), then `session.update`, then connected.
    /// `session.update` used to go out while the handshake could still be in flight, and that first
    /// send failed and tore the fresh socket down (issue #1). <paramref name="whileWaitingForHandshake"/>
    /// overlaps the audio warm-up with the handshake and must not send.
    /// A vendor that waits for the client (<see cref="IRealtimeWireDialect.ClientSpeaksFirst"/>, Gemini)
    /// gets its setup right after the connect instead, before anything is received.
    /// <paramref name="wireDialect"/> null is OpenAI's own protocol.
    /// </summary>
    public async Task StartSessionAsync(ISpeechWebSocket socket, Uri url, IReadOnlyDictionary<string, string> headers, string model,
                                        RealtimeTurnMode mode, string connectInstructions, Func<Task> whileWaitingForHandshake,
                                        IRealtimeWireDialect? wireDialect = null)
    {
        webSocket = socket;
        isSocketOpen = false;
        dialect = wireDialect ?? new OpenAIWireDialect();
        var lifetime = new CancellationTokenSource();
        socketLifetime = lifetime;
        socketSender = new OrderedSocketSender(socket, error => mainThread.Post(() =>
        {
            Log($"send failed: {error.Message}");
            // A failed send means the socket is gone; say so once, and let the next press reconnect.
            if (webSocket != socket) return;
            MarkConnectionLost($"the connection to {ProviderName} dropped ({error.Message})");
        }), lifetime.Token);
        // Built here, on the main thread, because the dialect keeps what it sent; written by the
        // socket loop the moment the connect returns.
        IReadOnlyList<string> firstMessages = dialect.ClientSpeaksFirst
            ? dialect.Outgoing(SessionUpdate(mode, connectInstructions)).Select(message => message.ToJsonString(SpeechJson.CompactOptions)).ToList()
            : [];
        _ = Task.Run(() => RunSocketAsync(socket, url, headers, firstMessages, lifetime.Token));
        currentMode = mode;
        // Connected means the vendor accepted the socket, not that the connect returned. A refused
        // handshake (a 401) fails the wait with its status, so the press says what was refused.
        try
        {
            await whileWaitingForHandshake();
            await AwaitHandshakeAsync(socket);
        }
        catch
        {
            CloseConnection();
            throw;
        }
        Send(SessionUpdate(mode, connectInstructions));
        sentInstructions = connectInstructions;
        connectedAt = now();
        lastServerEventAt = now();
        IsConnected = true;
        Log($"realtime connected ({model}, {(mode == RealtimeTurnMode.PushToTalk ? "push-to-talk" : "always on")})");
    }

    /// <summary>Waits for the server's first event on <paramref name="socket"/> for up to ten seconds.</summary>
    private async Task AwaitHandshakeAsync(ISpeechWebSocket socket)
    {
        if (handshakeFailureBeforeWaiting is { } refusal)
        {
            handshakeFailureBeforeWaiting = null;
            throw new RealtimeVoiceException(RealtimeVoiceErrorKind.Backend, refusal);
        }
        // The first event may already be in (it arrived during the audio warm-up).
        if (isSocketOpen && webSocket == socket) return;
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handshakeSource = source;
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => mainThread.Post(() =>
        {
            if (handshakeSource != source) return;
            FailPendingHandshake(new RealtimeVoiceException(RealtimeVoiceErrorKind.Backend, $"{ProviderName} did not answer the realtime connection within 10 seconds."));
        }), TaskScheduler.Default);
        await source.Task;
    }

    /// <summary>The connect and the receive loop, off the main thread; every result is handled on it, one after another.</summary>
    private async Task RunSocketAsync(ISpeechWebSocket socket, Uri url, IReadOnlyDictionary<string, string> headers,
                                      IReadOnlyList<string> firstMessages, CancellationToken cancellationToken)
    {
        try
        {
            await socket.ConnectAsync(url, headers, cancellationToken).ConfigureAwait(false);
            // ClientWebSocket is open once ConnectAsync returns, so unlike URLSession (the Mac retries
            // for ten seconds) the setup can go out at once; a failure here fails the handshake.
            foreach (var message in firstMessages) await socket.SendTextAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (cancellationToken.IsCancellationRequested) return;
            var status = (error as SpeechWebSocketHandshakeException)?.StatusCode ?? socket.HandshakeStatusCode;
            await mainThread.InvokeAsync(() => HandleSocketFailure(socket, status, error.Message)).ConfigureAwait(false);
            return;
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            SpeechWebSocketMessage message;
            try
            {
                message = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (cancellationToken.IsCancellationRequested) return;
                var status = (error as SpeechWebSocketHandshakeException)?.StatusCode ?? socket.HandshakeStatusCode;
                await mainThread.InvokeAsync(() => HandleSocketFailure(socket, status, error.Message)).ConfigureAwait(false);
                return;
            }
            if (message.Kind == SpeechWebSocketMessageKind.Close)
            {
                if (cancellationToken.IsCancellationRequested) return;
                await mainThread.InvokeAsync(() => HandleSocketFailure(socket, null, CloseDescription(ProviderName, message.CloseStatus, message.CloseReason))).ConfigureAwait(false);
                return;
            }
            await mainThread.InvokeAsync(() => HandleSocketMessageAsync(socket, message)).ConfigureAwait(false);
        }
    }

    private void HandleSocketFailure(ISpeechWebSocket socket, int? statusCode, string underlyingError)
    {
        if (webSocket != socket) return;
        var reason = HandshakeFailureDescription(statusCode, underlyingError, ProviderName);
        if (handshakeSource is not null)
        {
            FailPendingHandshake(new RealtimeVoiceException(RealtimeVoiceErrorKind.Backend, reason));
            return;
        }
        if (!IsConnected)
        {
            // Still connecting, and nothing is waiting yet: keep the reason for the wait.
            handshakeFailureBeforeWaiting = reason;
            return;
        }
        MarkConnectionLost(reason);
    }

    private async Task HandleSocketMessageAsync(ISpeechWebSocket socket, SpeechWebSocketMessage message)
    {
        if (webSocket != socket) return;
        lastServerEventAt = now();
        isSocketOpen = true;
        var pendingHandshake = handshakeSource;
        handshakeSource = null;
        pendingHandshake?.TrySetResult();
        var eventWaiter = nextServerEventSource;
        nextServerEventSource = null;
        eventWaiter?.TrySetResult();
        // Gemini sends every message as a binary frame of JSON; OpenAI as text.
        if (message.AsText is not { } text) return;
        JsonObject? wireMessage;
        try { wireMessage = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return; }
        if (wireMessage is null) return;
        foreach (var serverEvent in dialect.Incoming(wireMessage))
        {
            if (webSocket != socket) break;
            await HandleServerEventAsync(serverEvent);
        }
    }

    private void MarkConnectionLost(string reason)
    {
        var wasConnected = IsConnected;
        IsConnected = false;
        socketLifetime?.Cancel();
        socketSender?.Complete();
        webSocket?.Abort();
        webSocket = null;
        socketSender = null;
        socketLifetime = null;
        isSocketOpen = false;
        connectedAt = null;
        StopForwardingMicrophone();
        responseInProgress = false;
        Log($"realtime disconnected: {reason}");
        if (wasConnected) OnConnectionLost?.Invoke(reason);
    }

    /// <summary>The session.update payload: audio formats, pinned transcription language, turn detection, tools.</summary>
    public static JsonObject BuildSessionUpdate(RealtimeTurnMode mode, string instructionsText, string? voice, string transcriptionLanguage,
                                                IReadOnlyList<JsonObject>? extraTools = null)
    {
        var input = new JsonObject
        {
            ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = RealtimeAudio.SampleRate },
            // Pinned, like the reply: unpinned, English audio was transcribed as Chinese and the
            // model took that as the user switching language. See ReplyLanguage.
            ["transcription"] = new JsonObject { ["model"] = "gpt-4o-mini-transcribe", ["language"] = transcriptionLanguage },
        };
        input["turn_detection"] = mode == RealtimeTurnMode.PushToTalk
            ? null
            // create_response is off: the screen capture is attached when the server commits the
            // utterance, then the response is requested (see input_audio_buffer.committed).
            : new JsonObject { ["type"] = "server_vad", ["silence_duration_ms"] = 600, ["create_response"] = false, ["interrupt_response"] = true };
        var output = new JsonObject { ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = RealtimeAudio.SampleRate } };
        if (voice is not null) output["voice"] = voice;
        var tools = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "function",
                ["name"] = "send_to_agent",
                ["description"] = "Hand a task that requires doing work (files, code, commands, apps, research) to the OpenClicky agent. Returns a short result summary.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["task"] = new JsonObject { ["type"] = "string", ["description"] = "A clear, self-contained description of what to do." } },
                    ["required"] = new JsonArray("task"),
                },
            },
            new JsonObject
            {
                ["type"] = "function",
                ["name"] = "point_at",
                ["description"] = "Fly the on-screen cursor buddy to a UI element in the attached screenshot and show a short label. Use it while you explain: one call per step, in order, as you say each step. Coordinates are pixels in the screenshot, origin top-left; the buddy snaps to the element's visible text near them, so always pass that text when the element has any.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["x"] = new JsonObject { ["type"] = "integer", ["description"] = "Horizontal pixel in the screenshot, from the left edge." },
                        ["y"] = new JsonObject { ["type"] = "integer", ["description"] = "Vertical pixel in the screenshot, from the top edge." },
                        ["label"] = new JsonObject { ["type"] = "string", ["description"] = "1-3 words naming the element, e.g. 'export button'" },
                        ["text"] = new JsonObject { ["type"] = "string", ["description"] = "The text written on the element itself (button caption, menu item, link, tab title), copied exactly as it appears on screen. Empty string for icon-only elements." },
                    },
                    ["required"] = new JsonArray("x", "y", "label", "text"),
                },
            },
        };
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["name"] = ClickToolName,
            ["description"] = "Click a link, button or field on screen for the user, the way they would with the mouse. Use it when they tell you to click or open something they can see ('click the first link', 'open that video', 'press Sign in'); point_at is for teaching instead. Same arguments as point_at: always pass the element's visible text. A link opens in the same tab unless new_tab is true. It never presses anything that sends, buys, pays, deletes or confirms; those it points at for them to click.",
            ["parameters"] = ClickToolParameters((JsonObject)tools.OfType<JsonObject>().First(tool => SpeechJson.String(tool["name"]) == "point_at")["parameters"]!),
        });
        tools.Add(WriteInPlace.RealtimeToolDefinition());
        foreach (var tool in extraTools ?? []) tools.Add(tool.DeepClone());
        // Ending hands-free by voice only makes sense while hands-free is on.
        if (mode == RealtimeTurnMode.AlwaysOn) tools.Add(StopListeningToolDefinition());
        return new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["type"] = "realtime",
                ["instructions"] = instructionsText,
                ["tools"] = tools,
                ["tool_choice"] = "auto",
                ["audio"] = new JsonObject { ["input"] = input, ["output"] = output },
            },
        };
    }

    public const string StopListeningToolName = "stop_listening";
    public const string ClickToolName = "click";

    /// <summary>point_at's parameters (copied) plus `new_tab`, only for "open it in a new tab".</summary>
    public static JsonObject ClickToolParameters(JsonObject pointParameters)
    {
        var parameters = (JsonObject)pointParameters.DeepClone();
        if (parameters["properties"] is not JsonObject properties)
        {
            properties = new JsonObject();
            parameters["properties"] = properties;
        }
        properties["new_tab"] = new JsonObject { ["type"] = "boolean", ["description"] = "True only when they asked to open it in a new or another tab. Otherwise leave it out." };
        return parameters;
    }

    /// <summary>
    /// The model decides what counts as a sign-off, since people end in many ways; fixed words would
    /// also fire on "how do I quit Photoshop" (the standard lane's HandsFreeSignOff is fixed only
    /// because a model round trip costs it seconds). A missed sign-off costs one key press, a wrong one
    /// cuts the person off, so the description leans towards keeping on listening.
    /// </summary>
    public static JsonObject StopListeningToolDefinition() => new()
    {
        ["type"] = "function",
        ["name"] = StopListeningToolName,
        ["description"] = "Turn hands-free listening off and go back to push-to-talk. Call it only when the user clearly signs off from talking to you: \"that's all I need\", \"stop listening\", \"I'm done, you can stop\", \"ok bye\", \"that's it for now\". Never for thanks alone (\"thank you\", \"great, thanks\"), and never when they ask to stop or quit something else (an app, music, a task, an answer). If you are not sure, keep listening and do not call it. Call it before saying anything; its result tells you what to say.",
        ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray() },
    };

    private JsonObject SessionUpdate(RealtimeTurnMode mode, string instructionsText) =>
        BuildSessionUpdate(mode, instructionsText, voice, ReplyLanguage.CurrentCode,
            [.. DesktopActionToolDefinitions?.Invoke() ?? [], .. PluginToolDefinitions?.Invoke() ?? []]);

    // MARK: - Instructions (skills)

    private string CurrentInstructions() => InstructionsProvider?.Invoke() ?? instructions;

    /// <summary>
    /// Re-send the session when the provider's instructions changed (a different app is in front, a
    /// skill was toggled). Called from the push-to-talk tail and at speech start in always-on; never on
    /// the key-down path in front of the mic open. An unchanged prompt sends nothing.
    /// </summary>
    public void RefreshInstructionsIfNeeded()
    {
        if (!IsConnected) return;
        var text = CurrentInstructions();
        if (text == sentInstructions) return;
        try
        {
            Send(SessionUpdate(currentMode, text));
            sentInstructions = text;
            Log($"instructions updated ({text.Length} chars)");
        }
        catch (Exception error)
        {
            Log($"instructions update failed: {error.Message}");
        }
    }

    // MARK: - Push-to-talk / always-on control

    /// <summary>Shortcut pressed: interrupt any reply and stream the microphone.</summary>
    public void BeginPushToTalk()
    {
        lastTurnStartedAt = now();
        pushToTalkTurnAudioBytes = 0;
        pushToTalkTailCancellation?.Cancel();
        pushToTalkTailCancellation = null;
        idlePauseCancellation?.Cancel();
        idlePauseCancellation = null;
        if (responseInProgress) CancelActiveResponse();
        FlushPlayback();
        TrySend(new JsonObject { ["type"] = "input_audio_buffer.clear" });
        pushToTalkArmed = true;
        // No instructions refresh here: any work done on the key-down path delays the mic open.
        _ = OpenMicrophoneForPressAsync();
    }

    private async Task OpenMicrophoneForPressAsync()
    {
        await Task.Yield();
        var started = now();
        try
        {
            await StartAudioIfNeededAsync();
        }
        catch (Exception error)
        {
            Log($"microphone unavailable: {error.Message}");
            return;
        }
        if (!pushToTalkArmed) return;
        Log($"microphone open in {(int)(now() - started).TotalMilliseconds} ms");
        StartForwardingMicrophone();
    }

    /// <summary>Shortcut released: keep the mic open 400 ms so the last word is not clipped, then commit.</summary>
    public void EndPushToTalk()
    {
        pushToTalkArmed = false;
        pushToTalkTailCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        pushToTalkTailCancellation = cancellation;
        _ = PushToTalkTailAsync(cancellation.Token);
    }

    private async Task PushToTalkTailAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        StopForwardingMicrophone();
        // No audio, no turn. Released before the microphone opened (a press during the connect, or
        // a tap), the commit used to go out empty and the reply was requested anyway, with the
        // screen attached: the model invented a request and handed it to the agent.
        var turnAudioBytes = pushToTalkTurnAudioBytes;
        pushToTalkTurnAudioBytes = 0;
        if (!HasEnoughAudioForATurn(turnAudioBytes))
        {
            TrySend(new JsonObject { ["type"] = "input_audio_buffer.clear" });
            Log($"turn not sent: {turnAudioBytes} bytes of audio");
            OnTurnHeardNothing?.Invoke();
            ScheduleIdlePauseIfNeeded();
            return;
        }
        // Skills for the app in front go up before the screen and the response request.
        RefreshInstructionsIfNeeded();
        await AttachScreenContextAsync();
        TrySend(new JsonObject { ["type"] = "input_audio_buffer.commit" });
        RequestTurnResponse();
        ScheduleIdlePauseIfNeeded();
    }

    /// <summary>A typed turn (the island composer): the text goes into the conversation with the current
    /// screen, then a reply is requested like a released push-to-talk. The reply is spoken, so audio starts.</summary>
    public async Task SendTextTurnAsync(string text)
    {
        lastTurnStartedAt = now();
        pushToTalkTailCancellation?.Cancel();
        pushToTalkTailCancellation = null;
        idlePauseCancellation?.Cancel();
        idlePauseCancellation = null;
        if (responseInProgress) CancelActiveResponse();
        FlushPlayback();
        _ = StartAudioQuietlyAsync();
        RefreshInstructionsIfNeeded();
        await AttachScreenContextAsync();
        TrySend(new JsonObject
        {
            ["type"] = "conversation.item.create",
            ["item"] = new JsonObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text }),
            },
        });
        RequestTurnResponse();
        ScheduleIdlePauseIfNeeded();
        Log($"text turn: {text}");
    }

    /// <summary>Always-on: stream continuously; the server decides the turns.</summary>
    public void StartListeningContinuously()
    {
        idlePauseCancellation?.Cancel();
        idlePauseCancellation = null;
        _ = StartListeningContinuouslyAsync();
    }

    private async Task StartListeningContinuouslyAsync()
    {
        try
        {
            await StartAudioIfNeededAsync();
        }
        catch (Exception error)
        {
            Log($"microphone unavailable: {error.Message}");
        }
        StartForwardingMicrophone();
    }

    public void StopListening()
    {
        StopForwardingMicrophone();
        ScheduleIdlePauseIfNeeded();
    }

    /// <summary>Frees the microphone for another capture (dictation). Returns once the capture is torn down;
    /// <see cref="ResumeListeningAfterDictation"/> puts always-on back.</summary>
    public async Task ReleaseMicrophoneForDictationAsync()
    {
        idlePauseCancellation?.Cancel();
        idlePauseCancellation = null;
        wasListeningContinuouslyBeforeDictation = isForwardingMicrophone && currentMode == RealtimeTurnMode.AlwaysOn;
        StopForwardingMicrophone();
        await audio.ReleaseNowAsync();
        Log("microphone released for dictation");
    }

    public void ResumeListeningAfterDictation()
    {
        if (!wasListeningContinuouslyBeforeDictation) return;
        wasListeningContinuouslyBeforeDictation = false;
        if (!IsConnected || currentMode != RealtimeTurnMode.AlwaysOn) return;
        StartListeningContinuously();
        Log("always-on listening resumed after dictation");
    }

    /// <summary>The turn's caption: the screenshot's own, then the text a rewrite would replace (quoted in
    /// full) or, without one, the highlighted text as context for "what does this mean".</summary>
    public static string ScreenCaption(string caption, WritingTarget? writingTarget, string? selectedText)
    {
        if (writingTarget is not null) return caption + " " + writingTarget.ContextLine;
        if (!string.IsNullOrEmpty(selectedText)) return caption + " " + SelectedTextContextLine(selectedText);
        return caption;
    }

    /// <summary>Adds the current screen to the conversation so the model can answer "what is this?".</summary>
    private async Task AttachScreenContextAsync()
    {
        if (ScreenContextProvider is not { } provider) return;
        var started = now();
        RealtimeScreenContext? context;
        try
        {
            context = await provider();
        }
        catch (Exception error)
        {
            Log($"no screen context ({error.GetType().Name})");
            return;
        }
        if (context is null)
        {
            Log("no screen context (screen capture failed?)");
            return;
        }
        LastWritingTarget = null;
        if (WritingTargetProvider is { } writingTargetProvider)
        {
            try { LastWritingTarget = await writingTargetProvider(); }
            catch (Exception error) { Log($"write in place: target unreadable ({error.GetType().Name})"); }
        }
        string? selectedText = null;
        if (LastWritingTarget is null && SelectedTextProvider is { } selectedTextProvider)
        {
            try { selectedText = await selectedTextProvider(); }
            catch (Exception) { }
        }
        var caption = ScreenCaption(context.Caption, LastWritingTarget, selectedText);
        TrySend(new JsonObject
        {
            ["type"] = "conversation.item.create",
            ["item"] = new JsonObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "input_text", ["text"] = $"[Screen context, not spoken] {caption}" },
                    new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:image/jpeg;base64,{Convert.ToBase64String(context.Jpeg)}" }),
            },
        });
        LastScreenCapture = context.Capture;
        lastScreenTextCancellation?.Cancel();
        var textCancellation = new CancellationTokenSource();
        lastScreenTextCancellation = textCancellation;
        lastScreenText = RecognizeScreenTextAsync(context.Jpeg, textCancellation.Token);
        Log($"screen attached ({context.Jpeg.Length / 1024} KB, {(int)(now() - started).TotalMilliseconds} ms)");
    }

    private async Task<IReadOnlyList<ScreenTextLine>> RecognizeScreenTextAsync(byte[] jpeg, CancellationToken cancellationToken)
    {
        if (screenTextRecognizer is null) return [];
        var started = now();
        IReadOnlyList<RecognizedTextLine> recognized;
        try
        {
            recognized = await screenTextRecognizer.RecognizeAsync(jpeg, 2, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return [];
        }
        var lines = RealtimeScreenText.ToScreenTextLines(recognized);
        if (!cancellationToken.IsCancellationRequested) mainThread.Post(() => Log($"screen text: {lines.Count} lines in {(int)(now() - started).TotalMilliseconds} ms"));
        return lines;
    }

    /// <summary>
    /// Push-to-talk: once nothing is captured, generated or played, release the microphone so the
    /// privacy indicator goes away between turns. After half a minute idle everything is torn down.
    /// </summary>
    private void ScheduleIdlePauseIfNeeded()
    {
        if (currentMode != RealtimeTurnMode.PushToTalk) return;
        idlePauseCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        idlePauseCancellation = cancellation;
        _ = IdlePauseAsync(cancellation.Token);
    }

    private async Task IdlePauseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
            if (isForwardingMicrophone || responseInProgress || IsSpeaking || pushToTalkArmed) return;
            audio.Pause();
            await Task.Delay(TimeSpan.FromSeconds(28.5), cancellationToken);
            if (isForwardingMicrophone || responseInProgress || IsSpeaking || pushToTalkArmed) return;
            audio.Release();
            Log("audio released after idle");
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Speak something proactively (the greeting on first connect).</summary>
    public void RequestResponse(string? responseInstructions = null)
    {
        idlePauseCancellation?.Cancel();
        idlePauseCancellation = null;
        _ = StartAudioQuietlyAsync();
        var body = new JsonObject { ["type"] = "response.create" };
        if (responseInstructions is not null) body["response"] = new JsonObject { ["instructions"] = responseInstructions };
        TrySend(body);
    }

    // MARK: - Audio

    private async Task StartAudioQuietlyAsync()
    {
        try { await StartAudioIfNeededAsync(); }
        catch (Exception) { }
    }

    private async Task StartAudioIfNeededAsync()
    {
        var consent = microphonePermission?.ReadConsent() ?? MicrophoneConsent.Unknown;
        if (!isAudioWired) Log(MicrophoneAuthorizationDescription(consent));
        if (consent is MicrophoneConsent.DeniedForDevice or MicrophoneConsent.DeniedForAllApps or MicrophoneConsent.DeniedForDesktopApps)
            throw new RealtimeVoiceException(RealtimeVoiceErrorKind.Audio, "microphone access is denied for OpenClicky (Settings → Privacy & security → Microphone)");
        if (!isAudioWired)
        {
            isAudioWired = true;
            audio.SetCallbacks(
                onMicrophoneFrame: (pcm16, level) => mainThread.Post(() =>
                {
                    InputLevel = level;
                    if (!isForwardingMicrophone) return;
                    ForwardMicrophoneAudio(pcm16);
                }),
                onPlaybackActiveChanged: isPlaying => mainThread.Post(() =>
                {
                    IsSpeaking = isPlaying;
                    if (isPlaying) return;
                    ScheduleIdlePauseIfNeeded();
                    // A reply's audio can outlast its response.done; the quiet moment is when it stops.
                    ReconnectAfterGoAwayIfQuiet();
                }));
        }
        var description = await audio.StartAsync();
        if (description != "audio already running") Log(description);
    }

    private void StartForwardingMicrophone()
    {
        isForwardingMicrophone = true;
        IsCapturing = true;
    }

    /// <summary>Test hook (the talk smoke run): PCM16 mono 24 kHz as if the microphone produced it.</summary>
    public void InjectMicrophoneAudio(byte[] pcm16)
    {
        if (!isForwardingMicrophone) return;
        ForwardMicrophoneAudio(pcm16);
    }

    /// <summary>Counts a frame only once it was handed to an open socket, so "appends sent" means audio the server can have received.</summary>
    private void ForwardMicrophoneAudio(byte[] pcm16)
    {
        try
        {
            Send(new JsonObject { ["type"] = "input_audio_buffer.append", ["audio"] = Convert.ToBase64String(pcm16) });
        }
        catch (RealtimeVoiceException)
        {
            return;
        }
        microphoneAppends += 1;
        pushToTalkTurnAudioBytes += pcm16.Length;
    }

    private void StopForwardingMicrophone()
    {
        isForwardingMicrophone = false;
        IsCapturing = false;
        InputLevel = 0;
    }

    private void EnqueuePlayback(string base64Audio)
    {
        byte[] data;
        try { data = Convert.FromBase64String(base64Audio); }
        catch (FormatException) { return; }
        if (data.Length == 0) return;
        audio.Enqueue(data);
        if (!IsSpeaking) IsSpeaking = true;
    }

    /// <summary>Barge-in: drop everything queued.</summary>
    private void FlushPlayback()
    {
        audio.FlushPlayback();
        IsSpeaking = false;
    }

    // MARK: - Socket plumbing

    /// <summary>One client event, in the current vendor's words (none, one or several wire messages).</summary>
    private void Send(JsonObject message)
    {
        if (webSocket is null || socketSender is null || !isSocketOpen) throw new RealtimeVoiceException(RealtimeVoiceErrorKind.NotConnected);
        foreach (var wireMessage in dialect.Outgoing(message)) socketSender.SendText(wireMessage.ToJsonString(SpeechJson.CompactOptions));
    }

    private void TrySend(JsonObject message)
    {
        try { Send(message); }
        catch (RealtimeVoiceException) { }
    }

    /// <summary>One server event in OpenAI's shape (the dialect has translated it already).</summary>
    private async Task HandleServerEventAsync(JsonObject serverEvent)
    {
        if (SpeechJson.String(serverEvent["type"]) is not { } type) return;
        switch (type)
        {
            case "input_audio_buffer.speech_started":
                Log("listening…");
                FlushPlayback();
                turnGeneration += 1;
                if (responseInProgress) CancelActiveResponse();
                if (currentMode == RealtimeTurnMode.AlwaysOn) RefreshInstructionsIfNeeded();
                // A vendor that replies on its own gets the screen while the person is still talking.
                if (currentMode == RealtimeTurnMode.AlwaysOn && dialect.RespondsWithoutRequest) await AttachScreenContextAsync();
                break;
            case "session.go_away":
                Log($"{ProviderName} will close this connection soon ({SpeechJson.String(serverEvent["time_left"]) ?? "?"} left)");
                // The next press reconnects; hands-free moves over at the next quiet moment.
                connectedAt = DateTimeOffset.MinValue;
                if (currentMode == RealtimeTurnMode.AlwaysOn)
                {
                    reconnectAfterGoAway = true;
                    ReconnectAfterGoAwayIfQuiet();
                }
                break;
            case "conversation.item.input_audio_transcription.completed":
                if (SpeechJson.String(serverEvent["transcript"]) is { } transcript && transcript.Trim().Length > 0)
                {
                    lastUserTranscript = transcript.Trim();
                    OnTranscript?.Invoke(RealtimeTranscriptRole.User, lastUserTranscript);
                }
                break;
            case "input_audio_buffer.committed":
                Log(type);
                if (currentMode == RealtimeTurnMode.AlwaysOn)
                {
                    await AttachScreenContextAsync();
                    RequestTurnResponse();
                }
                break;
            case "input_audio_buffer.speech_stopped" or "session.created" or "session.updated":
                Log(type);
                break;
            case "response.created":
                responseInProgress = true;
                activeResponseId = SpeechJson.String((serverEvent["response"] as JsonObject)?["id"]);
                assistantTranscriptBuffer = "";
                OnResponseStarted?.Invoke();
                break;
            case "response.output_audio.delta" or "response.audio.delta":
                if (SpeechJson.String(serverEvent["delta"]) is { } delta) EnqueuePlayback(delta);
                break;
            case "response.output_audio_transcript.delta" or "response.audio_transcript.delta":
                assistantTranscriptBuffer += SpeechJson.String(serverEvent["delta"]) ?? "";
                break;
            case "response.output_audio_transcript.done" or "response.audio_transcript.done":
            {
                var assistantTranscript = (SpeechJson.String(serverEvent["transcript"]) ?? assistantTranscriptBuffer).Trim();
                assistantTranscriptBuffer = "";
                if (assistantTranscript.Length > 0) OnTranscript?.Invoke(RealtimeTranscriptRole.Assistant, assistantTranscript);
                break;
            }
            case "response.function_call_arguments.done":
                await HandleToolCallAsync(serverEvent);
                break;
            case "response.done":
            {
                responseInProgress = false;
                activeResponseId = null;
                var status = SpeechJson.String((serverEvent["response"] as JsonObject)?["status"]);
                var wantsContinuation = needsContinuationAfterResponse;
                needsContinuationAfterResponse = false;
                var continued = false;
                if (wantsContinuation && status != "cancelled")
                {
                    // Tool outputs were added during this response: ask for the follow-up once.
                    try
                    {
                        Send(new JsonObject { ["type"] = "response.create" });
                        continued = true;
                    }
                    catch (Exception error)
                    {
                        Log($"continuation failed: {error.Message}");
                    }
                }
                if (!continued)
                {
                    OnResponseFinished?.Invoke();
                    ScheduleIdlePauseIfNeeded();
                    ReconnectAfterGoAwayIfQuiet();
                    if (stopListeningAfterGoodbye)
                    {
                        stopListeningAfterGoodbye = false;
                        _ = EndHandsFreeOnceTheGoodbyeHasPlayedAsync();
                    }
                }
                break;
            }
            case "error":
                Log($"realtime error: {SpeechJson.String((serverEvent["error"] as JsonObject)?["message"]) ?? serverEvent.ToJsonString(SpeechJson.CompactOptions)}");
                break;
        }
    }

    private async Task HandleToolCallAsync(JsonObject serverEvent)
    {
        pendingToolCalls += 1;
        try
        {
            await RunToolCallAsync(serverEvent);
        }
        finally
        {
            pendingToolCalls -= 1;
        }
    }

    private async Task RunToolCallAsync(JsonObject serverEvent)
    {
        var callId = SpeechJson.String(serverEvent["call_id"]) ?? "";
        var name = SpeechJson.String(serverEvent["name"]) ?? "";
        var arguments = new JsonObject();
        if (SpeechJson.String(serverEvent["arguments"]) is { } argumentsText)
        {
            try
            {
                if (JsonNode.Parse(argumentsText) is JsonObject parsed) arguments = parsed;
            }
            catch (JsonException)
            {
            }
        }
        // The turn this call belongs to: Esc, the person speaking or a new turn moves it on while an
        // action (or the agent) is still working, and then nothing may follow its output.
        var generation = turnGeneration;
        if (name is not ("send_to_agent" or "point_at" or ClickToolName or StopListeningToolName or WriteInPlace.ToolName) && OnDesktopAction is { } desktopAction)
        {
            var startedAt = now();
            var frontAppBefore = FrontAppProvider?.Invoke();
            RealtimeToolResult? outcome;
            try
            {
                outcome = await desktopAction(name, arguments);
            }
            catch (Exception error)
            {
                outcome = new RealtimeToolResult($"that didn't work: {error.Message}");
            }
            if (outcome is not null)
            {
                // Act, then look: the model describes what is on screen now, not what was there before the page opened.
                // Not once the turn moved on: Esc while the page loads stops the look too.
                if (outcome.ThenLooksAtScreen && !outcome.WasRejected && turnGeneration == generation)
                {
                    await WaitForScreenToSettleAsync(frontAppBefore);
                    if (turnGeneration == generation) await AttachScreenContextAsync();
                }
                var logged = outcome.LogDescription ?? outcome.SpokenSentence;
                Log(outcome.WasRejected
                    ? $"desktop action: {name} rejected, {logged}"
                    : $"desktop action: {name} {logged} in {(now() - startedAt).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s");
                SendToolOutput(callId, outcome.ModelOutput ?? outcome.SpokenSentence);
                ContinueUnlessTheTurnMovedOn(serverEvent, generation);
                return;
            }
        }
        var output = $"unknown tool {name}";
        switch (name)
        {
            case "send_to_agent":
            {
                var task = SpeechJson.String(arguments["task"]) ?? "";
                Log($"agent task: {task}");
                var startedAt = now();
                output = OnAgentTask is { } agentTask ? await agentTask(task) : "The agent lane is not available in this session.";
                Log($"agent task finished in {(now() - startedAt).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s");
                break;
            }
            case "point_at":
                output = QueuePointAt(arguments);
                break;
            case ClickToolName:
                // The output goes out from the pointing chain once the click (and the look after it) is done.
                if (QueueClick(arguments, callId, serverEvent, generation) is not { } refusal) return;
                output = refusal;
                break;
            case WriteInPlace.ToolName:
            {
                var newText = SpeechJson.String(arguments["text"]) ?? "";
                if (LastWritingTarget is { } target && WriteInPlaceHost is { } host)
                {
                    var outcome = await WriteInPlace.ReplaceAsync(target, newText, host, Log);
                    Log($"write in place: {outcome}");
                    if (outcome.DidReplace()) OnWroteInPlace?.Invoke();
                    output = outcome.SpokenSentence() + " Say only that, in a few words; never read the new text out.";
                }
                else
                {
                    Log("write in place: no text to write over");
                    output = "No highlighted text or text box was found, so nothing was changed. Ask them to highlight the text and ask again; do not read a rewrite out.";
                }
                break;
            }
            case StopListeningToolName:
                if (currentMode == RealtimeTurnMode.AlwaysOn)
                {
                    Log("hands-free: sign-off heard");
                    stopListeningAfterGoodbye = true;
                    output = "Hands-free listening is ending. Say a short goodbye in a few words, nothing else.";
                }
                else
                {
                    output = "Hands-free is not on; nothing to stop. Do not mention it.";
                }
                break;
            default:
                if (OnPluginToolCall is { } pluginToolCall && await pluginToolCall(name, arguments, LastScreenCapture) is { } pluginOutput)
                {
                    Log($"plugin tool: {name}");
                    output = pluginOutput;
                }
                break;
        }
        // The output goes out immediately; the follow-up response is requested once, on response.done.
        SendToolOutput(callId, output);
        ContinueUnlessTheTurnMovedOn(serverEvent, generation);
    }

    /// <summary>
    /// A tool output whose turn has moved on (Esc, speech or a new turn since the call came in) stays in
    /// the conversation for the next turn but asks for no continuation: Esc while Gmail loads must not
    /// be followed by Clicky reading the inbox.
    /// </summary>
    private void ContinueUnlessTheTurnMovedOn(JsonObject serverEvent, int generation)
    {
        if (turnGeneration == generation) RequestContinuation(serverEvent);
        else Log("tool output after the turn moved on (esc, speech or a new turn): no continuation");
    }

    /// <summary>
    /// How much longer to wait for the screen to settle after an action, or null to stop. Two phases:
    /// another app coming to the front (<paramref name="frontChangedAt"/>, waited for up to 1.5 s; a
    /// browser already in front never changes), then a second for the page to paint; four seconds in
    /// all at most.
    /// </summary>
    public static TimeSpan? SettleDelay(TimeSpan? frontChangedAt, TimeSpan elapsed)
    {
        var paintStartsAt = frontChangedAt ?? TimeSpan.FromSeconds(1.5);
        var settledAt = TimeSpan.FromTicks(Math.Min((paintStartsAt + TimeSpan.FromSeconds(1)).Ticks, TimeSpan.FromSeconds(4).Ticks));
        return elapsed >= settledAt ? null : TimeSpan.FromSeconds(0.25);
    }

    private async Task WaitForScreenToSettleAsync(int? frontAppBefore)
    {
        var startedAt = now();
        TimeSpan? frontChangedAt = null;
        while (true)
        {
            var elapsed = now() - startedAt;
            if (frontChangedAt is null && FrontAppProvider is { } frontApp && frontApp() != frontAppBefore) frontChangedAt = elapsed;
            if (SettleDelay(frontChangedAt, elapsed) is not { } wait) return;
            await Task.Delay(wait);
        }
    }

    private void SendToolOutput(string callId, string output) => TrySend(new JsonObject
    {
        ["type"] = "conversation.item.create",
        ["item"] = new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = callId,
            ["output"] = output.Length <= 4000 ? output : output[..4000],
        },
    });

    /// <summary>Queues one `point_at` on the serial chain and returns the tool output.</summary>
    private string QueuePointAt(JsonObject arguments)
    {
        // Coordinates come as integers (or occasionally as numeric strings); label is free text.
        var label = (SpeechJson.String(arguments["label"]) ?? "").Trim();
        if (label.Length == 0) label = "here";
        // The model controls these numbers: reject non-finite values before any conversion.
        if (SpeechJson.Number(arguments["x"]) is not { } x || SpeechJson.Number(arguments["y"]) is not { } y || !double.IsFinite(x) || !double.IsFinite(y))
            return "invalid coordinates: point_at needs finite integer x and y";
        if (LastScreenCapture is not { } capture)
        {
            Log("point_at ignored: no screenshot attached to this turn");
            return "no screenshot attached; describe the location in words";
        }
        var guess = new ScreenPoint(x, y);
        var elementText = (SpeechJson.String(arguments["text"]) ?? "").Trim();
        var previousPointing = pointingChain;
        var screenText = lastScreenText;
        var userRequest = lastUserTranscript;
        pointingChain = ResolvePointAtAsync(previousPointing, guess, label, elementText, userRequest, capture, screenText, pointingCancellation.Token);
        return $"pointing at {label}";
    }

    /// <summary>Resolves one `point_at` on the chain, off the receive path, one after another.</summary>
    private async Task ResolvePointAtAsync(Task? previousPointing, ScreenPoint guess, string label, string elementText, string? userRequest,
                                           CompanionScreenCapture capture, Task<IReadOnlyList<ScreenTextLine>>? screenText, CancellationToken cancellationToken)
    {
        // Like the Mac's `Task { }`: the chain starts after the tool call's handler has sent its output.
        await Task.Yield();
        if (previousPointing is not null)
        {
            try { await previousPointing; }
            catch (Exception) { }
        }
        if (cancellationToken.IsCancellationRequested) return;
        var target = await LocateElementAsync(guess, label, elementText, userRequest, capture, screenText);
        if (cancellationToken.IsCancellationRequested) return;
        var location = capture.GlobalPoint(target.Point.X, target.Point.Y);
        Log($"point_at ({Formatted(guess.X)}, {Formatted(guess.Y)}) \"{label}\" {target.Resolution} → screen ({location.X}, {location.Y}){(OnPointAt is null ? " (no handler)" : "")}");
        OnPointAt?.Invoke(target.Point, label, capture);
    }

    /// <summary>
    /// Queues one `click` on the pointing chain (after any `point_at` still flying). Null when it was
    /// queued: its output goes out from the chain. Otherwise the output for arguments it cannot use.
    /// </summary>
    private string? QueueClick(JsonObject arguments, string callId, JsonObject serverEvent, int generation)
    {
        if (SpeechJson.Number(arguments["x"]) is not { } x || SpeechJson.Number(arguments["y"]) is not { } y || !double.IsFinite(x) || !double.IsFinite(y))
            return "invalid coordinates: click needs finite integer x and y";
        if (LastScreenCapture is not { } capture)
        {
            Log("click ignored: no screenshot attached to this turn");
            return "no screenshot attached; nothing was clicked";
        }
        var label = (SpeechJson.String(arguments["label"]) ?? "").Trim();
        if (label.Length == 0) label = "it";
        var elementText = (SpeechJson.String(arguments["text"]) ?? "").Trim();
        // A boolean, or now and then the string "true".
        var wantsNewTab = arguments["new_tab"] is JsonValue newTab
            && (newTab.TryGetValue<bool>(out var flag) ? flag : string.Equals(SpeechJson.String(newTab), "true", StringComparison.OrdinalIgnoreCase));
        var previousPointing = pointingChain;
        var screenText = lastScreenText;
        var userRequest = lastUserTranscript;
        var frontAppBefore = FrontAppProvider?.Invoke();
        // Esc has something to stop while the element is being found (Claude can take 4 s).
        pendingToolCalls += 1;
        pointingChain = ClickAsync(previousPointing, new ScreenPoint(x, y), label, elementText, wantsNewTab, userRequest, capture, screenText,
                                   frontAppBefore, callId, serverEvent, generation, pointingCancellation.Token);
        return null;
    }

    /// <summary>
    /// point_at's locator chain, the buddy flies there, a real left click (a web link in the browser in
    /// front is loaded in its own tab instead, or a new selected tab for new_tab), then act-then-look. Never
    /// clicks a control whose words commit something (ScreenNavigation.ClickIsRisky), a target nothing
    /// located (the model's raw guess), OpenClicky's own window, or once the turn moved on (Esc, the
    /// person speaking) while the locator was still working: those are pointed at instead, or dropped.
    /// </summary>
    private async Task ClickAsync(Task? previousPointing, ScreenPoint guess, string label, string elementText, bool wantsNewTab, string? userRequest,
                                  CompanionScreenCapture capture, Task<IReadOnlyList<ScreenTextLine>>? screenText, int? frontAppBefore,
                                  string callId, JsonObject serverEvent, int generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
            if (previousPointing is not null)
            {
                try { await previousPointing; }
                catch (Exception) { }
            }
            if (cancellationToken.IsCancellationRequested) return;
            var startedAt = now();
            var target = await LocateElementAsync(guess, label, elementText, userRequest, capture, screenText);
            if (cancellationToken.IsCancellationRequested) return;
            var location = capture.GlobalPoint(target.Point.X, target.Point.Y);
            string clickLog;
            string clickOutput;
            if (Actions.ScreenNavigation.ClickIsRisky(label, elementText))
            {
                // Pointed at, never pressed: a misheard "click send" must not send.
                OnPointAt?.Invoke(target.Point, label, capture);
                clickLog = "refused, it could commit something";
                clickOutput = $"Not clicked: {label} could send, buy, delete or confirm something, so the person clicks it themselves. The pointer is on it; tell them in a few words.";
            }
            else if (!target.IsLocated)
            {
                OnPointAt?.Invoke(target.Point, label, capture);
                clickLog = "refused, not found precisely";
                clickOutput = $"Not clicked: {label} could not be found precisely enough to click safely. The pointer is at the best guess; ask them to click it or to say it differently.";
            }
            else if (turnGeneration != generation)
            {
                // Esc or new speech while the element was still being found.
                clickLog = "dropped, the turn moved on";
                clickOutput = "Not clicked: they stopped it.";
            }
            else
            {
                OnPointAt?.Invoke(target.Point, label, capture);
                var clickResult = OnClick is { } click ? await click(location, wantsNewTab) : Actions.ScreenClickResult.NotAllowed;
                switch (clickResult)
                {
                    case Actions.ScreenClickResult.FollowedLink or Actions.ScreenClickResult.OpenedLinkInNewTab:
                    {
                        var inNewTab = clickResult == Actions.ScreenClickResult.OpenedLinkInNewTab;
                        if (turnGeneration == generation)
                        {
                            await WaitForScreenToSettleAsync(frontAppBefore);
                            if (cancellationToken.IsCancellationRequested) return;
                            if (turnGeneration == generation) await AttachScreenContextAsync();
                        }
                        clickLog = inNewTab ? "opened the link in a new tab" : "followed the link in this tab";
                        clickOutput = $"Opened {label}{(inNewTab ? " in a new tab, which is now the one in front" : "")}. The page is the latest screenshot; say in a few words what opened. Do not read the page out unless asked.";
                        break;
                    }
                    case Actions.ScreenClickResult.Clicked:
                        // Act, then look, as the fast actions do; not once the turn moved on.
                        if (turnGeneration == generation)
                        {
                            await WaitForScreenToSettleAsync(frontAppBefore);
                            if (cancellationToken.IsCancellationRequested) return;
                            if (turnGeneration == generation) await AttachScreenContextAsync();
                        }
                        clickLog = "clicked";
                        clickOutput = $"Clicked {label}. The screen after the click is the latest screenshot; say in a few words what happened (which page or window opened). Do not read the page out unless asked.";
                        break;
                    case Actions.ScreenClickResult.UnderTaskbar:
                        clickLog = "refused, under the taskbar";
                        clickOutput = $"Not clicked: {label} is at the edge of the screen, under the taskbar. Call scroll with amount little (down if it is near the bottom, up if near the top), then call click again for it without asking.";
                        break;
                    case Actions.ScreenClickResult.OwnWindow:
                        clickLog = "refused, OpenClicky's own window";
                        clickOutput = "Not clicked: that spot is on OpenClicky's own window.";
                        break;
                    default:
                        clickLog = "refused, Windows would not take the click";
                        clickOutput = "Not clicked: Windows won't let me click in an app that runs as administrator. Ask them to click it.";
                        break;
                }
            }
            if (cancellationToken.IsCancellationRequested) return;
            Log($"click \"{label}\" {target.Resolution} → screen ({location.X}, {location.Y}): {clickLog} in {(now() - startedAt).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s");
            SendToolOutput(callId, clickOutput);
            ContinueUnlessTheTurnMovedOn(serverEvent, generation);
        }
        finally
        {
            pendingToolCalls -= 1;
        }
    }

    private static string Formatted(double value) => value.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>Where an element the model named really is, and how that was found. IsLocated is false
    /// only when nothing found it and the model's guess is all there is.</summary>
    private sealed record LocatedElement(ScreenPoint Point, string Resolution, bool IsLocated);

    /// <summary>
    /// Where an element the model named really is in <paramref name="capture"/> (the Mac's
    /// locateElement, shared by `point_at` and `click`). The model's guess is coarse (30–100 px off
    /// for captions, hundreds for icons): the window's caption buttons and the browser's tab strip
    /// first, then a snap to the element's visible text (OCR of the screenshot it saw), then UI
    /// Automation, then Claude on the same screenshot (2–4 s).
    /// </summary>
    private async Task<LocatedElement> LocateElementAsync(ScreenPoint guess, string label, string elementText, string? userRequest,
                                                          CompanionScreenCapture capture, Task<IReadOnlyList<ScreenTextLine>>? screenText)
    {
        static string Prefix(string text, int length) => text.Length <= length ? text : text[..length];
        var point = guess;
        var resolution = "using the guess";
        var resolved = false;
        // A video or picture is the image itself, not its caption: snapping "first video" to the title
        // under the thumbnail clicked words at the window's bottom edge (2026-10-29).
        var aimsAtPicture = Actions.ScreenNavigation.NamesAPicture(label);
        // A browser tab is a favicon plus a truncated title, which no screenshot model reads reliably;
        // the browser's own tab strip does.
        if (Pointing.LocateBrowserTab is { } locateBrowserTab)
        {
            try
            {
                if (await locateBrowserTab(label, elementText, userRequest, capture) is { } tab)
                {
                    point = tab.Point;
                    resolved = true;
                    resolution = $"browser tab \"{Prefix(tab.Title, 40)}\" ({Formatted(point.X)}, {Formatted(point.Y)})";
                }
            }
            catch (Exception error)
            {
                Log($"point_at: browser tabs unreadable ({error.GetType().Name})");
            }
        }
        var radius = capture.ScreenshotWidthInPixels * 0.3;
        // Repeated captions ("Edit" on every row) are the case snapping gets wrong: two candidates
        // closer together than the model's own error cannot be told apart by distance.
        var ambiguityMargin = capture.ScreenshotWidthInPixels * 0.1;
        IReadOnlyList<ScreenTextLine> lines = [];
        if (!resolved && !aimsAtPicture && elementText.Length > 0 && screenText is not null)
        {
            lines = await screenText;
            if (lines.Count > 0 && ScreenTextLocator.Locate(elementText, guess, lines, radius, ambiguityMargin) is { } match)
            {
                point = match.Center;
                resolved = true;
                resolution = $"snapped to \"{match.Text}\" ({Formatted(point.X)}, {Formatted(point.Y)}, {Formatted(match.Distance)} px away)";
            }
        }
        // UI Automation knows what the pixels cannot: the control's role, and the title of the row
        // it sits in. It costs nothing, so it runs before paying for a Claude round trip.
        if (!resolved && !aimsAtPicture && Pointing.LocateAccessibleElement is { } locateAccessibleElement)
        {
            try
            {
                if (await locateAccessibleElement(label, elementText, userRequest, guess, capture, radius, ambiguityMargin) is { } element)
                {
                    point = element.Center;
                    resolved = true;
                    var container = element.ContainerTitles.Count > 0 ? $" in \"{Prefix(element.ContainerTitles[0], 30)}\"" : "";
                    resolution = $"accessibility \"{Prefix(element.Title, 40)}\"{container} ({Formatted(point.X)}, {Formatted(point.Y)})";
                }
            }
            catch (Exception error)
            {
                Log($"point_at: accessibility unreadable ({error.GetType().Name})");
            }
        }
        if (!resolved)
        {
            var started = now();
            ScreenElementGrounder.Located? located = null;
            if (Pointing.GroundWithClaude is { } groundWithClaude)
            {
                try { located = await groundWithClaude(label, elementText, userRequest, capture); }
                catch (Exception) { located = null; }
            }
            if (located is not null)
            {
                point = located.Point;
                // Located, as on the Mac since 5602681: Claude found the element on the same screenshot,
                // and leaving this unresolved refused every click it found ("click on the video", 2026-10-29).
                resolved = true;
                resolution = $"located by Claude ({Formatted(point.X)}, {Formatted(point.Y)}) in {(int)(now() - started).TotalMilliseconds} ms";
                // Claude picks the right element and misplaces it by up to ~150 px; the text it named
                // has an exact box in the OCR of the same screenshot, with a tighter margin. Not for a
                // picture, whose caption is not the thing to click.
                if (!aimsAtPicture && located.VisibleText is { } visibleText && screenText is not null)
                {
                    if (lines.Count == 0) lines = await screenText;
                    if (lines.Count > 0 && ScreenTextLocator.Locate(visibleText, located.Point, lines, radius * 0.6, ambiguityMargin * 0.5) is { } match)
                    {
                        point = match.Center;
                        resolution += $", snapped to \"{match.Text}\" ({Formatted(point.X)}, {Formatted(point.Y)}, {Formatted(match.Distance)} px from Claude's)";
                    }
                }
            }
            else
            {
                resolution = "Claude could not locate it; using the guess";
            }
        }
        return new LocatedElement(point, resolution, resolved);
    }

    /// <summary>
    /// response.done arrives when the server has sent the goodbye, not when it has played: turning
    /// hands-free off closes the session, which would cut it off. Waits for playback to end (at most
    /// 8 s, in case no audio came) before asking the companion to stop.
    /// </summary>
    private async Task EndHandsFreeOnceTheGoodbyeHasPlayedAsync()
    {
        var deadline = now() + TimeSpan.FromSeconds(8);
        while (IsSpeaking && now() < deadline) await Task.Delay(TimeSpan.FromMilliseconds(100));
        Log("hands-free: ending after the goodbye");
        OnStopListeningRequested?.Invoke();
    }

    /// <summary>Ask the model to continue after a tool output: the barge-in and ordering rules, in one place.</summary>
    private void RequestContinuation(JsonObject serverEvent)
    {
        var responseId = SpeechJson.String(serverEvent["response_id"]);
        if (responseId is not null && cancelledResponseIds.Contains(responseId))
        {
            // A late call from a response we cancelled (barge-in): the output is in the conversation
            // for the next turn, but nothing may follow the cancel.
            Log($"tool output for cancelled response {responseId}: no continuation");
        }
        else if (responseInProgress)
        {
            if (responseId is null || responseId == activeResponseId)
                needsContinuationAfterResponse = true;
            else
                // Belongs to an earlier response while a newer one is active: it picks the output up.
                Log($"tool output for inactive response {responseId}: no continuation");
        }
        else
        {
            // No response is active (event ordering can put arguments.done after done): continue now.
            TrySend(new JsonObject { ["type"] = "response.create" });
        }
    }

    /// <summary>
    /// After Gemini's `goAway`, hands-free moves to a new connection (the conversation resumes on
    /// it) as soon as nothing is being said or done, with the microphone still streaming.
    /// </summary>
    private void ReconnectAfterGoAwayIfQuiet()
    {
        if (!reconnectAfterGoAway || currentMode != RealtimeTurnMode.AlwaysOn || responseInProgress || IsSpeaking || pendingToolCalls > 0) return;
        reconnectAfterGoAway = false;
        var wasListening = isForwardingMicrophone;
        Log($"moving hands-free to a new {ProviderName} connection");
        _ = ReconnectAfterGoAwayAsync(wasListening);
    }

    private async Task ReconnectAfterGoAwayAsync(bool wasListening)
    {
        await Task.Yield();
        CloseConnection();
        try
        {
            await ConnectIfNeededAsync(RealtimeTurnMode.AlwaysOn);
            if (wasListening) StartListeningContinuously();
        }
        catch (Exception error)
        {
            Log($"reconnect after goAway failed: {error.Message}");
            OnConnectionLost?.Invoke(error.Message);
        }
    }

    /// <summary>The person asked Clicky to stop talking: cancel the reply and drop what is queued to play.</summary>
    public void StopSpeaking()
    {
        CancelActiveResponse();
        FlushPlayback();
    }

    /// <summary>Barge-in: cancel the active response and drop any continuation queued for it; the response is
    /// remembered as cancelled so its tool calls still in flight cannot re-arm the continuation.</summary>
    private void CancelActiveResponse()
    {
        turnGeneration += 1;
        needsContinuationAfterResponse = false;
        // Only cancel what is in progress: OpenAI answers a cancel with nothing active with an error
        // ("no active response found"), and the generation bump above is what stops a late action
        // from continuing. Gemini's dialect sends nothing for a cancel but drops the turn's open
        // calls, so their late outputs do not make Gemini carry on by itself: it always gets it.
        var hadActiveResponse = responseInProgress;
        if (activeResponseId is not null) cancelledResponseIds.Add(activeResponseId);
        activeResponseId = null;
        responseInProgress = false;
        if (hadActiveResponse || dialect is not OpenAIWireDialect) TrySend(new JsonObject { ["type"] = "response.cancel" });
    }

    /// <summary>Request the reply for a committed user turn; any queued continuation is superseded (one create per turn).</summary>
    private void RequestTurnResponse()
    {
        turnGeneration += 1;
        needsContinuationAfterResponse = false;
        TrySend(new JsonObject { ["type"] = "response.create" });
    }

    private void Log(string line)
    {
        OnEvent?.Invoke(line);
        AppLog.Write($"realtime: {line}");
    }

    public void Dispose()
    {
        Disconnect(null);
        audio.Dispose();
    }
}

/// <summary>OCR lines (IScreenTextRecognizer) → ScreenTextLocator's lines, with per-word boxes for character ranges.</summary>
public static class RealtimeScreenText
{
    public static IReadOnlyList<ScreenTextLine> ToScreenTextLines(IReadOnlyList<RecognizedTextLine> recognized) =>
        recognized.Select(ToScreenTextLine).ToList();

    public static ScreenTextLine ToScreenTextLine(RecognizedTextLine line)
    {
        // Each word's place in the line's text, found in order.
        var wordRanges = new List<(int Start, int End, ScreenRect Box)>();
        var searchFrom = 0;
        foreach (var word in line.Words)
        {
            if (word.Text.Length == 0) continue;
            var index = line.Text.IndexOf(word.Text, searchFrom, StringComparison.Ordinal);
            if (index < 0) continue;
            wordRanges.Add((index, index + word.Text.Length, ToScreenRect(word.Box)));
            searchFrom = index + word.Text.Length;
        }
        Func<int, int, ScreenRect?>? rangeBox = wordRanges.Count == 0 ? null : (start, length) =>
        {
            var end = start + length;
            var covering = wordRanges.Where(range => range.Start < end && range.End > start).ToList();
            if (covering.Count == 0) return null;
            var left = covering.Min(range => range.Box.Left);
            var top = covering.Min(range => range.Box.Top);
            var right = covering.Max(range => range.Box.Right);
            var bottom = covering.Max(range => range.Box.Bottom);
            return new ScreenRect(left, top, right - left, bottom - top);
        };
        return new ScreenTextLine(line.Text, ToScreenRect(line.Box), rangeBox);
    }

    private static ScreenRect ToScreenRect(DoubleRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}
