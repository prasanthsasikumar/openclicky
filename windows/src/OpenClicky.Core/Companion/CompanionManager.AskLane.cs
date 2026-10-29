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
        }
        var cancellation = new CancellationTokenSource();
        currentResponseCancellation = cancellation;
        _ = RunAskLaneAsync(transcript, cancellation);
    }

    private async Task RunAskLaneAsync(string transcript, CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        VoiceState = CompanionVoiceState.Processing;
        try
        {
            await AnswerAsync(transcript, cancellationToken);
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
            VoiceState = CompanionVoiceState.Idle;
            Overlay.ScheduleTransientHideIfNeeded();
        }
        if (currentResponseCancellation == cancellation) currentResponseCancellation = null;
    }

    private async Task AnswerAsync(string transcript, CancellationToken cancellationToken)
    {
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

        var labeledImages = screenCaptures.Select(ImageLabeledWithItsPixelSize).ToList();
        // The text a "rephrase this" would replace: the highlight, or the box the cursor is in.
        var writingTarget = await WritingTarget.CaptureFromFrontAppAsync(services.WriteInPlace, AppLog.Write);
        var selectedText = writingTarget is null ? await ReadSelectedTextAsync() : null;
        cancellationToken.ThrowIfCancellationRequested();

        var historyForApi = conversationHistory.Select(entry => (entry.UserTranscript, entry.AssistantResponse)).ToList();
        var claudeRoute = ProviderRouter.Route(ProviderCapability.Claude, ProviderRouter.CurrentCredentials());
        // Offered wherever the agent lane doesn't run, and to a plan through PlanToolProtocol's [ACTION: …] lines.
        var offersQuickActions = AskLanePrompts.OffersQuickActionsToAskLane(agentLaneRuns, claudeRoute.AnsweringSource);
        var askTools = AskLanePrompts.AskTools(offersQuickActions, writingTarget is not null, DesktopAction.AnthropicToolDefinitions());
        var systemPrompt = AskLanePrompts.AskSystemPrompt(offersQuickActions, writingTarget is not null, TalkSkillsBlock());

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
                AskLanePrompts.AskUserPrompt(transcript, writingTarget, selectedText),
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

        var toolOutcomes = new List<string>();
        foreach (var call in toolCalls.Take(2))
        {
            if (call.Name == WriteInPlace.ToolName)
            {
                if (writingTarget is null) continue;
                var rewrite = await WriteInPlace.ReplaceAsync(writingTarget, call.Arguments.GetValueOrDefault("text") ?? "", services.WriteInPlace, AppLog.Write);
                if (rewrite.DidReplace()) LastSuccessfulQuickActionAt = DateTimeOffset.Now;
                toolOutcomes.Add(rewrite.SpokenSentence());
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

        // The tag and the guide's marker are stripped so they don't confuse future context.
        var replyIsAGuidedStep = GuidedStepMarker.ContainsMarker(fullResponseText);
        var parseResult = PointTagParser.ParsePointingCoordinates(GuidedStepMarker.RemovingMarkers(fullResponseText));
        var spokenText = AskLaneFailures.SpokenReply(parseResult.SpokenText, toolOutcomes);

        if (spokenText.Length == 0)
        {
            // Nothing to say (an empty answer, or a bare tag): "thinking…" would stay up in silence.
            AppLog.Write($"ask: empty answer ({(parseResult.Coordinate is null ? "no point" : "point only")})");
            Overlay.DismissCursorCaption();
            if (parseResult.Coordinate is null)
            {
                LastQuestionFailed = true;
                SpeakWithSystemVoice(AskLaneFailures.EmptyAnswerSentence);
            }
        }
        else
        {
            RememberExchange(transcript, spokenText);
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
            onStepDone: () => services.MainThread.Post(() =>
            {
                if (guidedStepWatcher != watcher) return;
                AppLog.Write($"guide: step {guidedStepsGivenSoFar} done, asking for the next");
                guidedStepWatcher = null;
                SendTranscriptToClaudeWithScreenshot(GuidedStepRules.NextStepRequest(guidedStepGoal), isGuidedStepContinuation: true);
            }),
            onEnded: reason => services.MainThread.Post(() =>
            {
                if (guidedStepWatcher != watcher) return;
                AppLog.Write($"guide: ended ({GuidedStepWatcher.Describe(reason)}) after {guidedStepsGivenSoFar} steps");
                guidedStepWatcher = null;
                guidedStepsGivenSoFar = 0;
                // Esc means stop: the step still being said goes quiet too.
                if (reason == GuidedStepWatcher.EndReason.Escape) StopSpeaking();
            }));
        guidedStepWatcher = watcher;
        watcher.Start();
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
