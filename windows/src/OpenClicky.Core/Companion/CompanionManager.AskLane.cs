//
//  CompanionManager.AskLane.cs
//  OpenClicky (Windows port of CompanionManager.swift: the AI response pipeline)
//
//  Captures the screens, sends them with the question to Claude (or the answer source the router
//  picks), speaks the answer while it is still being written, moves the pointer when the voice
//  starts, runs the quick actions and write_in_place, and waits for a guided step to be done.
//

using OpenClicky.Core.Actions;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.Teacher;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Companion;

public sealed partial class CompanionManager
{
    /// <summary>Conversation history so Claude remembers prior exchanges within a session.</summary>
    private readonly List<(string UserTranscript, string AssistantResponse)> conversationHistory = [];
    /// <summary>Account notices already spoken this session: each is said once, then the lane falls back silently.</summary>
    private readonly HashSet<string> spokenAccountNotices = new(StringComparer.Ordinal);
    /// <summary>The last question sent down the ask lane and when: the same one again soon after means the
    /// answer missed what they meant (RepeatedQuestion).</summary>
    private (string Transcript, DateTimeOffset AskedAt)? lastAskQuestion;

    /// <summary>The running response, cancelled when the person speaks again.</summary>
    private CancellationTokenSource? currentResponseCancellation;

    /// <summary>The answer being spoken, kept so a new question stops it outright.</summary>
    private StreamingAnswerSpeaker? activeAnswerSpeaker;
    private CancellationTokenSource? activeAnswerPlayback;

    public IReadOnlyList<(string UserTranscript, string AssistantResponse)> ConversationHistory => conversationHistory;

    /// <summary>The island's stop button: silence whatever Clicky is saying. Agent work keeps going.</summary>
    public void StopSpeaking()
    {
        if (VoiceState != CompanionVoiceState.Responding) return;
        AppLog.Write("ask: stopped speaking from the island");
        InterruptCurrentAnswer(includingAgentWork: false);
        RealtimeVoiceClient.StopSpeaking();
        // A cancelled answer skips its own reset to idle.
        VoiceState = CompanionVoiceState.Idle;
        Overlay.ScheduleTransientHideIfNeeded();
    }

    /// <summary>
    /// Silences everything this manager is saying and abandons the answer in progress.
    /// `includingAgentWork` also ends a running agent turn (a new question replaces it; the stop
    /// button only silences the voice). The Realtime session is left alone.
    /// </summary>
    public void InterruptCurrentAnswer(bool includingAgentWork)
    {
        StopWatchingForGuidedStep();
        host.StopSettingsTour();
        StopActiveAnswer();
        currentResponseCancellation?.Cancel();
        IsAskInFlight = false;
        SetAskIslandStatus(null);
        if (includingAgentWork) openClickyAgentClient.Cancel();
        answerVoicePlayer.StopPlayback();
        StopSystemVoice();
    }

    /// <summary>Stops the spoken answer, its remaining fetches and its pointer, before the player is stopped.</summary>
    private void StopActiveAnswer()
    {
        activeAnswerSpeaker?.Cancel();
        activeAnswerPlayback?.Cancel();
        activeAnswerSpeaker = null;
        activeAnswerPlayback = null;
    }

    // MARK: - The pipeline

    /// <summary>
    /// Captures the screens, sends them with the transcript to Claude and speaks the answer. The
    /// spinner stays up until the voice begins; a [POINT:x,y:label] tag flies the buddy there.
    /// </summary>
    private void SendTranscriptToClaudeWithScreenshot(string transcript, bool isGuidedStepContinuation = false)
    {
        InterruptCurrentAnswer(includingAgentWork: false);
        // Anything the person asks starts afresh; only OpenClicky's own "what's next?" continues a guide.
        if (!isGuidedStepContinuation)
        {
            guidedStepsGivenSoFar = 0;
            guidedStepGoal = transcript;
            TeacherPlugin.ClearBoard("a new question");
        }
        guidedStepPointedLocation = null;
        var cancellation = new CancellationTokenSource();
        currentResponseCancellation = cancellation;
        IsAskInFlight = true;
        // A guide's own "what's next?" never pauses: the person already let the task go ahead, and
        // a pause after every click would only slow the steps down.
        var pauseSeconds = AskSendPause.Seconds(DictationSettings.PauseBeforeSendingScreen, isGuidedStepContinuation);
        _ = RunAskLaneAsync(transcript, pauseSeconds, isGuidedStepContinuation, cancellation);
    }

    private async Task RunAskLaneAsync(string transcript, double? pauseSeconds, bool isGuidedStepContinuation, CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        VoiceState = CompanionVoiceState.Processing;
        try
        {
            await AnswerAsync(transcript, pauseSeconds, isGuidedStepContinuation, cancellationToken);
        }
        catch (Exception error) when (AskLaneFailures.IsCancellation(error) || cancellationToken.IsCancellationRequested)
        {
            // The person spoke again: the response was interrupted.
        }
        catch (Exception error)
        {
            await SayWhyTheQuestionFailedAsync(error);
        }

        // A notice in the system voice is still being said: the turn is over when it ends.
        await WaitForSystemVoiceNoticeToEndAsync();
        if (!cancellationToken.IsCancellationRequested)
        {
            IsAskInFlight = false;
            VoiceState = CompanionVoiceState.Idle;
            Overlay.ScheduleTransientHideIfNeeded();
        }
        if (currentResponseCancellation == cancellation) currentResponseCancellation = null;
    }

    private async Task AnswerAsync(string transcript, double? pauseSeconds, bool isGuidedStepContinuation, CancellationToken cancellationToken)
    {
        // Where the mouse was when they let go: what "this" points at.
        var mouseLocationAtRelease = services.Screens.CursorPosition();
        if (pauseSeconds is { } seconds)
        {
            // A moment to call it off before anything leaves the PC. Esc or a click on the island
            // cancels this question, and nothing is captured or sent.
            AppLog.Write($"ask: pausing {seconds} s before sending the screen");
            SetAskIslandStatus(AskSendPause.Caption);
            Overlay.ShowLiveCaption(AskSendPause.Caption);
            if (!await AskSendPause.WaitOutAsync(seconds, cancellationToken)) return;
            SetAskIslandStatus(null);
            ShowAskCaption($"“{transcript}” · thinking…", 30);
        }
        // Captured after the pause, so what is sent is what is on screen now.
        // Every screen (an account sends at most two), told apart from a backend failure.
        IReadOnlyList<CompanionScreenCapture> allScreenCaptures;
        try
        {
            allScreenCaptures = await services.ScreenCapture.CaptureAllScreensAsync(cancellationToken: cancellationToken);
        }
        catch (Exception error) when (!AskLaneFailures.IsCancellation(error))
        {
            throw new AskScreenCaptureException(error);
        }
        if (allScreenCaptures.Count == 0) throw new AskScreenCaptureException(new InvalidOperationException("no screen was captured"));
        var screenCaptures = ScreensForAsk(allScreenCaptures, AccountCapabilities.Current());
        cancellationToken.ThrowIfCancellationRequested();

        // Two-tier routing: the gate decides whether this is a quick question (the ask lane below) or
        // real work (the agent lane). The agent lane needs the openclicky tool; without it the quick
        // actions are offered to whoever answers instead.
        var agentLaneRuns = IsAgentModeEnabled && OpenClickyConfiguration.IsConfigured
            && AccountCapabilities.Current().UsesAgent && openClickyAgentClient.IsToolInstalled;
        if (agentLaneRuns)
        {
            AgentActivityText = "deciding…";
            var lane = await openClickyAgentClient.ClassifyLaneAsync(transcript, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            AppLog.Write($"agent: gate says {lane}");
            if (lane == "agent")
            {
                await RunOpenClickyAgentLaneAsync(transcript, screenCaptures, cancellationToken);
                return;
            }
            AgentActivityText = null;
        }

        // A teaching question's lesson starts now, while the answer is still being asked for.
        var cursorCapture = CursorCapture(screenCaptures);
        if (!isGuidedStepContinuation) TeacherPlugin.StartLesson(transcript, cursorCapture);
        var labeledImages = screenCaptures.Select(ImageLabeledWithItsPixelSize).ToList();
        // The pointer's place in its screenshot goes into the question, and a close-up of what is
        // around it rides along, so "what is this?" is about the thing pointed at and not the
        // window with focus.
        var pointer = PointerInCapture.Locate(mouseLocationAtRelease, screenCaptures);
        if (pointer is not null && screenCaptures.FirstOrDefault(capture => capture.Label == pointer.ScreenLabel) is { } pointedCapture
            && PointerCloseUp.CropRect(pointer, pointedCapture.ScreenshotWidthInPixels, pointedCapture.ScreenshotHeightInPixels) is { } closeUpArea
            && services.ScreenCapture.CropJpeg(pointedCapture.ImageData, closeUpArea) is { } closeUp)
        {
            labeledImages.Add((closeUp, PointerCloseUp.Label()));
        }
        // A guide's own "what's next?" is the same words every step: it is never a question asked
        // again, and is not remembered as one (the Mac compared those too).
        var isRepeatedQuestion = false;
        if (!isGuidedStepContinuation)
        {
            var secondsSinceLastQuestion = lastAskQuestion is { } last ? (DateTimeOffset.Now - last.AskedAt).TotalSeconds : double.PositiveInfinity;
            isRepeatedQuestion = RepeatedQuestion.IsRepeat(transcript, lastAskQuestion?.Transcript, secondsSinceLastQuestion);
            lastAskQuestion = (transcript, DateTimeOffset.Now);
        }
        if (isRepeatedQuestion) AppLog.Write("ask: the same question again; answering about what is under the pointer");
        // The text a "rephrase this" would replace: the highlight, or the box the cursor is in.
        var writingTarget = await WritingTarget.CaptureFromFrontAppAsync(services.WriteInPlace, AppLog.Write);
        var selectedText = writingTarget is null ? await ReadSelectedTextAsync() : null;
        cancellationToken.ThrowIfCancellationRequested();

        var historyForApi = conversationHistory.Select(entry => (entry.UserTranscript, entry.AssistantResponse)).ToList();
        var claudeRoute = ProviderRouter.Route(ProviderCapability.Claude, ProviderRouter.CurrentCredentials());
        // Offered wherever the agent lane doesn't run, and to a plan through PlanToolProtocol's [ACTION: …] lines.
        var offersQuickActions = AskLanePrompts.OffersQuickActionsToAskLane(agentLaneRuns, claudeRoute.AnsweringSource);
        var teacherIsOn = TeacherPlugin.IsEnabled;
        IReadOnlyList<System.Text.Json.Nodes.JsonObject> askTools =
        [
            .. AskLanePrompts.AskTools(offersQuickActions, writingTarget is not null, DesktopAction.AnthropicToolDefinitions()),
            // Windows only: with no fast voice (a free account), "click the first link" is answered here.
            .. offersQuickActions ? [AskLaneClick.AnthropicToolDefinition()] : Array.Empty<System.Text.Json.Nodes.JsonObject>(),
            .. teacherIsOn ? TeacherPlugin.AnthropicToolDefinitions() : [],
        ];
        var systemPrompt = AskLanePrompts.AskSystemPrompt(offersQuickActions, writingTarget is not null, TalkSkillsBlock(),
            pluginPromptLines: TeacherPlugin.PromptLines(teacherIsOn));

        // Spoken while it is still being written: each finished sentence goes to the voice, and the
        // pointer moves when the voice starts (the prompt puts the tag first).
        var answerSpeaker = MakeAnswerSpeaker(
            onVoiceStarts: () =>
            {
                VoiceState = CompanionVoiceState.Responding;
                Overlay.DismissCursorCaption();
                AppLog.Write("ask: voice started");
            },
            onPointTag: pointTag => PointAtAnswerTag(pointTag, screenCaptures));
        var playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var answerPlayback = answerSpeaker.PlayAsync(playbackCancellation.Token);
        activeAnswerSpeaker = answerSpeaker;
        activeAnswerPlayback = playbackCancellation;
        var askStartedAt = DateTimeOffset.UtcNow;
        AnsweringSourceName = claudeRoute.AnsweringSource?.ShortName();
        AppLog.Write($"ask: sending to claude ({screenCaptures.Count} screen(s))");

        string fullResponseText;
        IReadOnlyList<Answers.ClaudeToolCall> toolCalls;
        try
        {
            (fullResponseText, toolCalls, _) = await claudeAPI.AnalyzeImageStreamingAsync(
                labeledImages,
                systemPrompt,
                historyForApi,
                AskLanePrompts.AskUserPrompt(transcript, writingTarget, selectedText, pointer, isRepeatedQuestion),
                askTools,
                accumulatedAnswer => services.MainThread.Post(() => answerSpeaker.Receive(accumulatedAnswer)),
                cancellationToken);
        }
        catch
        {
            answerSpeaker.Cancel();
            playbackCancellation.Cancel();
            throw;
        }
        AppLog.Write($"ask: answer {fullResponseText.Length} chars in {(DateTimeOffset.UtcNow - askStartedAt).TotalSeconds:0.0} s");
        // Stopped (esc, the island, a new question) while the answer was coming in: nothing it asked
        // for may run. Each call is checked too, since a rewrite or an action awaits.
        if (cancellationToken.IsCancellationRequested)
        {
            answerSpeaker.Cancel();
            playbackCancellation.Cancel();
            return;
        }

        var toolOutcomes = new List<string>();
        foreach (var call in toolCalls.Take(2))
        {
            if (cancellationToken.IsCancellationRequested) break;
            // The drawing is the answer's picture, not something to say: nothing is added to the spoken reply.
            if (TeacherPlugin.ToolNames.Contains(call.Name))
            {
                TeacherPlugin.Perform(call.Name, call.Arguments, cursorCapture);
                continue;
            }
            if (call.Name == WriteInPlace.ToolName)
            {
                if (writingTarget is null) continue;
                var rewrite = await WriteInPlace.ReplaceAsync(writingTarget, call.Arguments.GetValueOrDefault("text") ?? "", services.WriteInPlace, AppLog.Write);
                if (rewrite.DidReplace()) LastSuccessfulQuickActionAt = DateTimeOffset.Now;
                toolOutcomes.Add(rewrite.SpokenSentence());
                continue;
            }
            if (call.Name == AskLaneClick.ToolName)
            {
                toolOutcomes.Add(await ClickForAskLaneAsync(call.Arguments, screenCaptures, cancellationToken));
                continue;
            }
            switch (DesktopAction.Parse(call.Name, call.Arguments))
            {
                case DesktopActionParseResult.Action action:
                    var outcome = await desktopActionRunner.PerformAsync(action.Value);
                    if (outcome.DidSomething) LastSuccessfulQuickActionAt = DateTimeOffset.Now;
                    toolOutcomes.Add(outcome.SpokenSentence);
                    break;
                case DesktopActionParseResult.BadArguments badArguments:
                    toolOutcomes.Add(badArguments.Outcome.SpokenSentence);
                    break;
                default:
                    toolOutcomes.Add(AccountLimitError.NotOnPlan.Message());
                    break;
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            answerSpeaker.Cancel();
            playbackCancellation.Cancel();
            return;
        }
        answerSpeaker.Finish(toolOutcomes);

        // The tag and the markers are stripped so they don't confuse future context. A reply that is
        // not sure what to point at never points, and is never a guide's step: the guide ends there
        // and the person asks again with what they see. Keeping the guide waiting for a click would
        // only guess at what the next click means.
        var replyIsUnsure = UnsureMarker.ContainsMarker(fullResponseText);
        var replyIsAGuidedStep = !replyIsUnsure && GuidedStepMarker.ContainsMarker(fullResponseText);
        var parseResult = PointTagParser.ParsePointingCoordinates(UnsureMarker.RemovingMarkers(GuidedStepMarker.RemovingMarkers(fullResponseText)));
        var pointedCoordinate = replyIsUnsure ? null : parseResult.Coordinate;
        var spokenText = AskLaneFailures.SpokenReply(parseResult.SpokenText, toolOutcomes);
        if (replyIsUnsure)
        {
            AppLog.Write($"ask: not sure what to point at{(parseResult.Coordinate is null ? "" : " (its point tag ignored)")}");
            // The marker came after a tag the pointer already flew to: bring it back.
            Overlay.ClearDetectedElementLocation();
            ShowAskIslandStatus(UnsureMarker.IslandStatus, 10);
        }

        if (spokenText.Length == 0)
        {
            // Nothing to say (an empty answer, or a bare tag): "thinking…" would stay up in silence.
            AppLog.Write($"ask: empty answer ({(pointedCoordinate is null ? "no point" : "point only")})");
            Overlay.DismissCursorCaption();
            if (pointedCoordinate is null)
            {
                LastQuestionFailed = true;
                SpeakWithSystemVoice(AskLaneFailures.EmptyAnswerSentence);
            }
        }
        else
        {
            RememberExchange(transcript, spokenText);
            TeacherPlugin.WriteAnswerIfTheyCannotHear(spokenText, lastQuestionWasTyped, cursorCapture);
            if (replyIsAGuidedStep) WaitForTheGuidedStepToBeDone();
        }

        // An answer spent some of the allowance: the profile store notices crossing 80%.
        if (AccountCapabilities.Current().Kind == AccountKind.Account) AccountProfileStore.Shared.Refresh();

        // Wait for the voice to finish; a question asked meanwhile cancels the playback too.
        try
        {
            await answerPlayback;
        }
        catch (SpeechQueueFailure failure)
        {
            AppLog.Write($"ask: voice failed ({failure.UnderlyingError.GetType().Name}); using the system voice");
            if (!cancellationToken.IsCancellationRequested)
            {
                // The success path dismisses "thinking…" when the voice starts; the system voice starts here.
                Overlay.DismissCursorCaption();
                SpeakWithSystemVoice(string.Join(" ", failure.UnspokenText));
                answerSpeaker.VoiceFellBack();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The highlighted text in the app in front, as context for "this".</summary>
    private async Task<string?> ReadSelectedTextAsync()
    {
        try { return await services.FocusedElements.SelectedTextAsync(); }
        catch (Exception error)
        {
            AppLog.Write($"ask: couldn't read the selection ({error.GetType().Name})");
            return null;
        }
    }

    /// <summary>What the person hears or reads when a question fails.</summary>
    private async Task SayWhyTheQuestionFailedAsync(Exception error)
    {
        AppLog.Write($"ask: failed: {error.GetType().Name} {error.HResult}");
        LastQuestionFailed = true;
        Overlay.DismissCursorCaption();
        AgentActivityText = null;
        if (error is AnswerProviderException providerError)
        {
            // A key or plan the person set up refused: say which and why, in text.
            AppLog.Write($"ask: {providerError.AnswerSource.RawValue()} refused ({providerError.Status})");
            ShowAskCaption($"{providerError.AnswerSource.DisplayName()} couldn’t answer: {providerError.VendorMessage}", 8);
        }
        else if (error is ProviderRouteUnavailableException unavailable)
        {
            // Not an outage: nothing is set up to answer yet. Said in text beside the pointer.
            ShowAskCaption(unavailable.Sentence, 8);
        }
        else if (error is BackendSignInException signInError)
        {
            SpeakWithSystemVoice(OpenClickyAuthSession.SpokenSentence(signInError.Error));
        }
        else if (AskLaneFailures.RefusedQuestion(error) is { Status: 401 } refusal)
        {
            SpeakWithSystemVoice(await OpenClickyAuthSession.Shared.SentenceAfterBackendRejectionAsync(refusal.Body));
        }
        else if (IsAccountNotice(error))
        {
            if (AccountNoticeToSpeak(error, spokenAccountNotices) is { } notice) SpeakWithSystemVoice(notice);
        }
        else
        {
            SpeakWithSystemVoice(AskLaneFailures.AskFailureSentence(error));
        }
    }

    // MARK: - Speaking

    private StreamingAnswerSpeaker MakeAnswerSpeaker(Action onVoiceStarts, Action<string> onPointTag)
    {
        // One session per answer, so a late or failed free voice hands the rest of this answer to the local voice
        // and the next answer tries again (AccountVoiceGuard.cs).
        var answerVoiceSession = answerVoicePlayer.MakeAnswerSession();
        return new(new SpeechQueue(
                answerVoiceSession.AudioAsync,
                (audio, cancellationToken) => answerVoicePlayer.PlayToCompletionAsync(audio, cancellationToken)),
            onVoiceStarts: () => services.MainThread.Post(onVoiceStarts),
            onPointTag: pointTag => services.MainThread.Post(() => onPointTag(pointTag)));
    }

    /// <summary>
    /// Speaks a reply that is already whole (the agent's result) in pieces through SpeechQueue,
    /// kept as the active answer so the stop button and a new question silence it.
    /// </summary>
    private async Task SpeakWholeReplyAsync(string replyText, CancellationToken cancellationToken)
    {
        var answerSpeaker = MakeAnswerSpeaker(
            onVoiceStarts: () =>
            {
                VoiceState = CompanionVoiceState.Responding;
                Overlay.DismissCursorCaption();
            },
            // The agent's reply carries no [POINT:…] tag; the chunker would only strip one.
            onPointTag: _ => { });
        var playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var answerPlayback = answerSpeaker.PlayAsync(playbackCancellation.Token);
        activeAnswerSpeaker = answerSpeaker;
        activeAnswerPlayback = playbackCancellation;
        answerSpeaker.ReceiveWholeReply(replyText);
        try
        {
            await answerPlayback;
        }
        catch (SpeechQueueFailure failure)
        {
            if (cancellationToken.IsCancellationRequested) return;
            AppLog.Write($"agent: voice failed ({failure.UnderlyingError.GetType().Name}); using the system voice");
            SpeakWithSystemVoice(string.Join(" ", failure.UnspokenText));
            await WaitForSystemVoiceNoticeToEndAsync();
        }
        catch (OperationCanceledException)
        {
            // Interrupted by a new question or the stop button.
        }
    }

    // MARK: - Notices in this PC's voice

    /// <summary>Bumped for every notice and every interruption, so the end of an older notice never ends a newer turn.</summary>
    private int systemVoiceNoticeNumber;
    private bool isSystemVoiceNoticeSpeaking;
    private CancellationTokenSource? systemVoiceNoticeCancellation;
    private readonly List<TaskCompletionSource> systemVoiceNoticeEndWaiters = [];

    /// <summary>
    /// Says <paramref name="text"/> in this PC's voice, or plays its recording when it is one of the
    /// fixed notices. Responding while it talks, idle when it ends.
    /// </summary>
    private void SpeakWithSystemVoice(string text)
    {
        AnsweringSourceName = null;
        StopSystemVoice();
        var noticeNumber = systemVoiceNoticeNumber;
        isSystemVoiceNoticeSpeaking = true;
        void NoticeEnded() => services.MainThread.Post(() => SystemVoiceNoticeEnded(noticeNumber));
        VoiceState = CompanionVoiceState.Responding;
        AppLog.Write($"voice: notice ({text.Length} chars)");
        // A fixed notice that has a recording plays it, in the app's own voice.
        if (RecordedVoice.LineFor(text) is { } line && RecordedVoice.Play(line, NoticeEnded)) return;
        var cancellation = new CancellationTokenSource();
        systemVoiceNoticeCancellation = cancellation;
        _ = SpeakNoticeAsync(text, cancellation.Token, NoticeEnded);
    }

    private async Task SpeakNoticeAsync(string text, CancellationToken cancellationToken, Action noticeEnded)
    {
        try
        {
            var audio = await systemVoice.AudioAsync(text, cancellationToken: cancellationToken);
            await noticePlayer.PlayToCompletionAsync(audio, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // No voice installed, or no speakers: the notice ends at once (the caption still says what happened).
            AppLog.Write($"voice: this pc's voice failed ({error.GetType().Name})");
        }
        catch (OperationCanceledException)
        {
        }
        noticeEnded();
    }

    private void SystemVoiceNoticeEnded(int noticeNumber)
    {
        if (noticeNumber != systemVoiceNoticeNumber || !isSystemVoiceNoticeSpeaking) return;
        isSystemVoiceNoticeSpeaking = false;
        ResumeSystemVoiceNoticeEndWaiters();
        // A notice is the last thing a turn says: once it has been said, the turn is over.
        if (VoiceState != CompanionVoiceState.Responding) return;
        VoiceState = CompanionVoiceState.Idle;
        Overlay.ScheduleTransientHideIfNeeded();
    }

    /// <summary>Stops a notice being said or played from its recording.</summary>
    private void StopSystemVoice()
    {
        systemVoiceNoticeNumber++;
        isSystemVoiceNoticeSpeaking = false;
        systemVoiceNoticeCancellation?.Cancel();
        systemVoiceNoticeCancellation = null;
        noticePlayer.StopPlayback();
        RecordedVoice.Stop();
        ResumeSystemVoiceNoticeEndWaiters();
    }

    /// <summary>Returns once the notice being said has ended or was stopped; at once when none is.</summary>
    private Task WaitForSystemVoiceNoticeToEndAsync()
    {
        if (!isSystemVoiceNoticeSpeaking) return Task.CompletedTask;
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        systemVoiceNoticeEndWaiters.Add(waiter);
        return waiter.Task;
    }

    private void ResumeSystemVoiceNoticeEndWaiters()
    {
        var waiters = systemVoiceNoticeEndWaiters.ToList();
        systemVoiceNoticeEndWaiters.Clear();
        foreach (var waiter in waiters) waiter.TrySetResult();
    }

    /// <summary>Spoken when the agent lane fails (the synthesizer every new question stops).</summary>
    private void SpeakCreditsErrorFallback() =>
        SpeakWithSystemVoice("something went wrong talking to the openclicky backend. check the backend and your token in the settings file.");

    // MARK: - Guided steps

    /// <summary>Steps given in the guide under way (0 when none is).</summary>
    private int guidedStepsGivenSoFar;
    private GuidedStepWatcher? guidedStepWatcher;
    /// <summary>What the person asked that started the guide, repeated in every "what's next?".</summary>
    private string guidedStepGoal = "";
    /// <summary>
    /// Where the pointer last pointed for the answer under way (global physical pixels), and the
    /// scale of that monitor, so the click that finishes a step can be checked against it. Cleared
    /// with each new request.
    /// </summary>
    private ScreenPoint? guidedStepPointedLocation;
    private double guidedStepPointedScale = 1;

    /// <summary>The reply was one step of a guide: once the person does it, ask for the next on a fresh screenshot.</summary>
    private void WaitForTheGuidedStepToBeDone()
    {
        guidedStepsGivenSoFar++;
        if (guidedStepsGivenSoFar >= GuidedStepRules.MaximumSteps)
        {
            AppLog.Write($"guide: stopped after {guidedStepsGivenSoFar} steps");
            return;
        }
        StopWatchingForGuidedStep();
        AppLog.Write($"guide: step {guidedStepsGivenSoFar} given, waiting for a click or return");
        GuidedStepWatcher? watcher = null;
        watcher = new GuidedStepWatcher(
            services.GuidedStepInput,
            onStepDone: clickLocation => services.MainThread.Post(() =>
            {
                if (guidedStepWatcher != watcher) return;
                var clickCheck = GuidedStepClickCheck.Check(guidedStepPointedLocation, clickLocation, guidedStepPointedScale);
                AppLog.Write($"guide: step {guidedStepsGivenSoFar} done ({GuidedStepClickCheck.Describe(clickCheck)}), asking for the next");
                guidedStepWatcher = null;
                if (clickCheck is GuidedStepClickCheck.Verdict.OffTarget) ShowAskCaption(GuidedStepClickCheck.OffTargetCaption, 4);
                SendTranscriptToClaudeWithScreenshot(GuidedStepRules.NextStepRequest(guidedStepGoal, clickCheck), isGuidedStepContinuation: true);
            }),
            onEnded: reason => services.MainThread.Post(() =>
            {
                if (guidedStepWatcher != watcher) return;
                AppLog.Write($"guide: ended ({GuidedStepWatcher.Describe(reason)}) after {guidedStepsGivenSoFar} steps");
                // Esc means stop: the step still being said goes quiet too, as esc does anywhere else.
                // (The global esc usually gets there first; then this watcher was already stopped.)
                // Stopped while the watcher is still set, so the guide counts as something to stop.
                if (reason == GuidedStepWatcher.EndReason.Escape)
                {
                    StopAskLane("esc during a guide");
                    return;
                }
                guidedStepWatcher = null;
                guidedStepsGivenSoFar = 0;
            }));
        guidedStepWatcher = watcher;
        watcher.Start();
    }

    // MARK: - Stopping the ask lane

    private bool isAskInFlight;
    /// <summary>
    /// From the moment the ask lane takes a question (the pause before sending included) until its
    /// answer has been said, failed, or was stopped. Esc and a click on the island stop it.
    /// </summary>
    public bool IsAskInFlight
    {
        get => isAskInFlight;
        private set
        {
            if (!SetProperty(ref isAskInFlight, value)) return;
            Island.IsAskInFlight = value;
        }
    }

    /// <summary>The expiry of a status shown for a while (<see cref="ShowAskIslandStatus"/>).</summary>
    private CancellationTokenSource? askIslandStatusExpiry;

    /// <summary>
    /// What the island's status says instead of the ask lane's state, while set: the pause before
    /// sending the screen, or a reply that was not sure what to point at. Null gives the status back
    /// to the state.
    /// </summary>
    private void SetAskIslandStatus(string? status)
    {
        askIslandStatusExpiry?.Cancel();
        askIslandStatusExpiry = null;
        Island.AskIslandStatus = status;
    }

    /// <summary>Puts <paramref name="status"/> on the island for a while (a new question or a stop takes it away sooner).</summary>
    private void ShowAskIslandStatus(string status, double holdSeconds)
    {
        SetAskIslandStatus(status);
        var expiry = new CancellationTokenSource();
        askIslandStatusExpiry = expiry;
        _ = ClearAskIslandStatusLaterAsync(status, holdSeconds, expiry.Token);
    }

    private async Task ClearAskIslandStatusLaterAsync(string status, double holdSeconds, CancellationToken cancellationToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(holdSeconds), cancellationToken); }
        catch (OperationCanceledException) { return; }
        services.MainThread.Post(() =>
        {
            if (cancellationToken.IsCancellationRequested || Island.AskIslandStatus != status) return;
            Island.AskIslandStatus = null;
        });
    }

    /// <summary>
    /// Whether esc has anything in the ask lane to stop: a press still listening or transcribing, a
    /// question on its way or being answered, or a guide waiting for its step to be done.
    /// </summary>
    public bool AskLaneCanBeStopped =>
        AskLaneEscape.CanStop(askOutcome == AskOutcome.Resolved, IsAskInFlight, guidedStepWatcher is null ? null : guidedStepsGivenSoFar);

    /// <summary>
    /// Esc, or a click on the island while a question is on its way: what a new question does,
    /// without the question. The request in flight is cancelled (so nothing it asked for runs), the
    /// voice and the pointer stop, a guide ends, and the island goes back to rest. Returns false when
    /// there was nothing to stop.
    /// </summary>
    public bool StopAskLane(string source)
    {
        if (!AskLaneCanBeStopped) return false;
        AppLog.Write($"ask: stopped ({source})");
        if (askOutcome != AskOutcome.Resolved)
        {
            askOutcome = AskOutcome.Resolved;
            askPressWasStopped = true;
            pendingKeyboardShortcutStart?.Cancel();
            pendingKeyboardShortcutStart = null;
            BuddyDictationManager.CancelCurrentDictation(preserveDraftText: false);
        }
        guidedStepsGivenSoFar = 0;
        InterruptCurrentAnswer(includingAgentWork: true);
        Overlay.ClearDetectedElementLocation();
        // A cancelled answer skips its own reset to idle.
        VoiceState = CompanionVoiceState.Idle;
        ShowAskCaption("stopped", 1.5);
        Overlay.ScheduleTransientHideIfNeeded();
        return true;
    }

    private void StopWatchingForGuidedStep()
    {
        guidedStepWatcher?.Stop();
        guidedStepWatcher = null;
    }

    // MARK: - History

    /// <summary>Remembers one exchange, keeping only the last 10.</summary>
    private void RememberExchange(string userTranscript, string assistantResponse)
    {
        conversationHistory.Add((userTranscript, assistantResponse));
        if (conversationHistory.Count > AskLanePrompts.MaximumRememberedExchanges)
        {
            conversationHistory.RemoveRange(0, conversationHistory.Count - AskLanePrompts.MaximumRememberedExchanges);
        }
    }

    // MARK: - Pure rules (AskLaneFailureTests)

    /// <summary>A screenshot with its label and real pixel size, so Claude's coordinates are in the image it saw.</summary>
    public static (byte[] Data, string Label) ImageLabeledWithItsPixelSize(CompanionScreenCapture capture) =>
        (capture.ImageData, AskLanePrompts.ImageLabelWithItsPixelSize(capture.Label, capture.ScreenshotWidthInPixels, capture.ScreenshotHeightInPixels));

    /// <summary>The screens a question carries: the cursor's screen first, and for an account at most one other.</summary>
    public static IReadOnlyList<CompanionScreenCapture> ScreensForAsk(IReadOnlyList<CompanionScreenCapture> captures, AccountCapabilities capabilities)
    {
        var cursorFirst = captures.Where(capture => capture.IsCursorScreen).Concat(captures.Where(capture => !capture.IsCursorScreen)).ToList();
        return capabilities.MaxAskScreens is { } limit ? cursorFirst.Take(limit).ToList() : cursorFirst;
    }

    /// <summary>The plain sentence for an account limit, when the ask lane's Claude call answered 402.</summary>
    public static string? LimitSentence(Exception error)
    {
        if (AskLaneFailures.RefusedQuestion(error) is not { Status: 402 } refusal) return null;
        var message = AccountLimitErrors.From(402, refusal.Body)?.Message() ?? "";
        return message.Length == 0 ? AccountLimitError.PersonalLimit.Message() : message;
    }

    /// <summary>The account's answer to a failed ask: a 402 limit, or the free service's 503 trouble.</summary>
    public static bool IsAccountNotice(Exception error) =>
        AskLaneFailures.RefusedQuestion(error) is { } refusal && (refusal.Status == 402 || IsServiceTrouble(refusal));

    /// <summary>The sentence for an account notice the first time this session; null afterwards.</summary>
    public static string? AccountNoticeToSpeak(Exception error, ISet<string> alreadySpoken)
    {
        var sentence = AskLaneFailures.RefusedQuestion(error) is { } refusal && IsServiceTrouble(refusal)
            ? AccountLimitErrors.ServiceTroubleMessage
            : LimitSentence(error);
        if (sentence is null || !alreadySpoken.Add(sentence)) return null;
        return sentence;
    }

    private static bool IsServiceTrouble(BackendHttpException refusal) =>
        refusal.Status == 503 && AccountLimitErrors.IsServiceTrouble(refusal.Status, refusal.Body);
}
