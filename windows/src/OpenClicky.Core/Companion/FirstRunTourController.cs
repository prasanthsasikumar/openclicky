//
//  FirstRunTourController.cs
//  OpenClicky (Windows port of FirstRunTour.swift's FirstRunTourController)
//
//  The first-run tour, told entirely from the island. Once the permission card is done the pointer
//  flies out of the island to the mouse and introduces itself; then one card at a time shows the
//  keys for each thing OpenClicky does (dictate, ask, hand over a task) and moves on only when it
//  has seen that thing work: a take with words, a spoken answer, an agent task or quick action.
//  A PC with no key and no account gets a guest account on OpenClicky's credits; only at the end
//  may the person add an email, or skip it. The step is kept in the preferences.
//
//  Windows has no speech-recognition permission and no fn key to free, so the Mac's "allow" and
//  "free fn" cards are gone. The cards' words are FirstRunTourCards (IslandCards.cs).
//

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Settings;
using OpenClicky.Core.Shortcuts;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Companion;

public sealed class FirstRunTourController
{
    public const string StepKey = "openclicky.firstRunTour.step";
    public const string DoneKey = "openclicky.firstRunTour.done";
    public const string UseCountKey = "openclicky.useCount";
    public const string OpenAtLoginAskedKey = "openclicky.openAtLoginAsked";
    /// <summary>Uses (a dictation with words, a spoken answer) before the island asks about opening at sign-in.</summary>
    public const int UsesBeforeOpenAtLoginOffer = 5;
    /// <summary>Below this the orb's level is room noise, not a voice.</summary>
    public const double VoiceLevel = 0.05;

    private readonly CompanionManager companion;
    private readonly Preferences preferences;

    private FirstRunTourStep? step;
    private string? status;
    private bool statusIsGood;
    private bool offersMicrophoneChange;
    private bool isShowingDemo;
    /// <summary>The step "add email" left from, to come back to once the email gave the PC an account.</summary>
    private FirstRunTourStep? stepAfterAccount;
    private CompanionVoiceState previousVoiceState = CompanionVoiceState.Idle;
    private bool isObserving;
    private CancellationTokenSource? advanceCancellation;
    private CancellationTokenSource? permissionWait;
    private CancellationTokenSource? idleWatch;
    private CancellationTokenSource? silenceCheck;
    private bool isWatchingForSilence;
    private double loudestLevelThisTake;

    public FirstRunTourController(CompanionManager companion, Preferences preferences)
    {
        this.companion = companion;
        this.preferences = preferences;
    }

    /// <summary>The card the island shows; null for none.</summary>
    public FirstRunTourCard? Card { get; private set; }

    public FirstRunTourStep? Step => step;

    /// <summary>What is typed in the account step's field.</summary>
    public string FieldText { get; set; } = "";

    /// <summary>The tour has been completed (or skipped, or predates this PC's first run).</summary>
    public bool IsFinished => preferences.GetBool(DoneKey);

    // MARK: - Starting

    /// <summary>Called once at launch, after the island and the permission card are up.</summary>
    public void Start()
    {
        if (preferences.GetBool(DoneKey)) return;
        var storedStep = StepFromRaw(preferences.GetString(StepKey));
        // Someone who used OpenClicky before the tour existed has been through the old welcome.
        if (storedStep is null && (companion.DictationSettings.HasCompletedDictationOnboarding || companion.HasCompletedOnboarding))
        {
            preferences.Set(DoneKey, true);
            return;
        }
        Observe();
        WaitForPermissions(storedStep ?? FirstRunTourStep.Meet);
    }

    /// <summary>The tray's "Replay Welcome…" and Settings → General: the whole tour again.</summary>
    public void Replay()
    {
        preferences.Set(DoneKey, false);
        Observe();
        WaitForPermissions(FirstRunTourStep.Meet);
    }

    private void WaitForPermissions(FirstRunTourStep firstStep)
    {
        permissionWait?.Cancel();
        if (companion.AllPermissionsGranted)
        {
            GoTo(firstStep);
            return;
        }
        // The permission card owns the island until every permission is in.
        var cancellation = new CancellationTokenSource();
        permissionWait = cancellation;
        _ = WaitForPermissionsAsync(firstStep, cancellation.Token);
    }

    private async Task WaitForPermissionsAsync(FirstRunTourStep firstStep, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken); }
            catch (OperationCanceledException) { return; }
            if (!companion.AllPermissionsGranted) continue;
            GoTo(firstStep);
            return;
        }
    }

    // MARK: - What the tour watches for

    private void Observe()
    {
        if (isObserving) return;
        isObserving = true;
        companion.TakeFinished += TakeFinished;
        companion.TakeStateChanged += TakeStateChanged;
        companion.VoiceStateChanged += VoiceStateChanged;
        companion.AgentTaskStarted += TaskStarted;
        // A take that failed ("didn't catch that") never becomes a finished take; the orb says it.
        companion.Orb.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(OrbModel.Phase) && step == FirstRunTourStep.Dictate && companion.Orb.Phase.Kind == OrbPhaseKind.Failed)
            {
                companion.Services.MainThread.Post(CouldNotHear);
            }
            if (eventArgs.PropertyName == nameof(OrbModel.AudioLevel) && isWatchingForSilence)
            {
                loudestLevelThisTake = Math.Max(loudestLevelThisTake, companion.Orb.AudioLevel);
            }
        };
        companion.DictationSettings.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(DictationSettings.PreferredMicrophoneUID) or nameof(DictationSettings.DictationKey))
            {
                companion.Services.MainThread.Post(Publish);
            }
        };
        OpenClickyAuthSession.Shared.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(OpenClickyAuthSession.EmailFlow)) companion.Services.MainThread.Post(() => EmailFlowChanged(OpenClickyAuthSession.Shared.EmailFlow));
        };
        // Whether new accounts are taken decides if the account step is offered at all.
        _ = OpenClickyAuthSession.Shared.RefreshAccountsOpenAsync();
    }

    private void TakeFinished(TakeRecord take)
    {
        if (step != FirstRunTourStep.Dictate) return;
        var words = take.DisplayText.Trim();
        var wordCount = words.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        // A real first take: a sentence (one word like "no" is what silence turns into) from a microphone that heard a voice.
        if (take.Status == TakeStatus.Complete && wordCount >= 2 && loudestLevelThisTake >= VoiceLevel)
        {
            Succeed($"✓ heard you: “{(words.Length > 60 ? words[..60] + "…" : words)}”", RecordedVoice.Line.TourDictateDone);
        }
        else if (take.Status == TakeStatus.Complete && wordCount < 2 && loudestLevelThisTake >= VoiceLevel)
        {
            status = $"i only got “{words}”. say a whole sentence, like the one above.";
            statusIsGood = false;
            offersMicrophoneChange = false;
            Publish();
        }
        else
        {
            CouldNotHear();
        }
    }

    /// <summary>A take ended with nothing usable: most often the wrong microphone.</summary>
    private void CouldNotHear()
    {
        if (step != FirstRunTourStep.Dictate) return;
        status = $"i couldn’t hear you through {companion.MicrophoneName()}. choose the microphone you’re using.";
        statusIsGood = false;
        offersMicrophoneChange = true;
        Publish();
    }

    private void TakeStateChanged(DictationTakeController.TakeState takeState)
    {
        if (step != FirstRunTourStep.Dictate) return;
        silenceCheck?.Cancel();
        if (takeState == DictationTakeController.TakeState.Listening)
        {
            companion.RecordedVoice.Stop();
            isWatchingForSilence = true;
            loudestLevelThisTake = 0;
            var cancellation = new CancellationTokenSource();
            silenceCheck = cancellation;
            _ = CheckForSilenceLaterAsync(cancellation.Token);
        }
        else if (takeState == DictationTakeController.TakeState.Idle)
        {
            isWatchingForSilence = false;
        }
    }

    private async Task CheckForSilenceLaterAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken); }
        catch (OperationCanceledException) { return; }
        if (step != FirstRunTourStep.Dictate || !isWatchingForSilence || loudestLevelThisTake >= VoiceLevel) return;
        status = $"i can’t hear anything through {companion.MicrophoneName()}. choose the microphone you’re using.";
        statusIsGood = false;
        offersMicrophoneChange = true;
        Publish();
    }

    private void VoiceStateChanged(CompanionVoiceState voiceState)
    {
        if (voiceState == CompanionVoiceState.Listening) companion.RecordedVoice.Stop();
        // An answer was spoken and finished: that is asking Clicky, working.
        if (step == FirstRunTourStep.Talk && previousVoiceState == CompanionVoiceState.Responding && voiceState == CompanionVoiceState.Idle && !companion.LastQuestionFailed)
        {
            Succeed("✓ that’s asking clicky. it can point at things too.", RecordedVoice.Line.TourTalkDone);
        }
        previousVoiceState = voiceState;
    }

    private void EmailFlowChanged(OpenClickyAuthSession.EmailFlowState flow)
    {
        if (step != FirstRunTourStep.Account) return;
        switch (flow)
        {
            case OpenClickyAuthSession.EmailFlowState.SignedInState:
                FieldText = "";
                companion.Island.TourFieldText = "";
                Succeed("✓ saved. there’s a code in your inbox to confirm it whenever you like.");
                break;
            case OpenClickyAuthSession.EmailFlowState.NeedsCodeState needsCode:
                FieldText = "";
                companion.Island.TourFieldText = "";
                status = $"that email has an account. type the 6-digit code i sent to {needsCode.Email}.";
                statusIsGood = false;
                Publish();
                FocusField();
                break;
            case OpenClickyAuthSession.EmailFlowState.SendingState:
            case OpenClickyAuthSession.EmailFlowState.CheckingCodeState:
                status = "one moment…";
                statusIsGood = false;
                Publish();
                break;
            case OpenClickyAuthSession.EmailFlowState.FailedState failed:
                status = failed.Message;
                statusIsGood = false;
                Publish();
                break;
        }
    }

    private void TaskStarted()
    {
        if (step != FirstRunTourStep.Act) return;
        Succeed("✓ on it. results show up in the island.", RecordedVoice.Line.TourActDone);
    }

    /// <summary>The tour's last note before "all set", on a free account: questions are answered on OpenClicky's credits.</summary>
    private static bool NeedsFreeAccountNote =>
        ProviderRouter.Route(ProviderCapability.Claude, ProviderRouter.CurrentCredentials()).Kind == ProviderRouteKind.Backend;

    /// <summary>No key and no account (the guest start did not work): the account card asks for an email.</summary>
    private static bool AccountCardAsksForEmail => !OpenClickyConfiguration.IsConfigured;

    /// <summary>Runs <paramref name="action"/> once the mouse has stayed put for 2 s, starting <paramref name="delay"/> from now.</summary>
    private void WatchForIdleMouse(TimeSpan delay, Action action)
    {
        idleWatch?.Cancel();
        var cancellation = new CancellationTokenSource();
        idleWatch = cancellation;
        _ = WatchForIdleMouseAsync(delay, action, cancellation.Token);
    }

    private async Task WatchForIdleMouseAsync(TimeSpan delay, Action action, CancellationToken cancellationToken)
    {
        var lastLocation = companion.Services.Screens.CursorPosition();
        var stillSince = DateTimeOffset.UtcNow + delay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken); }
            catch (OperationCanceledException) { return; }
            var location = companion.Services.Screens.CursorPosition();
            if (Math.Abs(location.X - lastLocation.X) > 2 || Math.Abs(location.Y - lastLocation.Y) > 2)
            {
                lastLocation = location;
                if (stillSince < DateTimeOffset.UtcNow) stillSince = DateTimeOffset.UtcNow;
                continue;
            }
            if (DateTimeOffset.UtcNow - stillSince < TimeSpan.FromSeconds(2)) continue;
            action();
            return;
        }
    }

    private void FocusField() => _ = FocusFieldSoonAsync();

    private async Task FocusFieldSoonAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(0.2));
        companion.Island.FocusTourField();
    }

    // MARK: - Moving through the steps

    private void Succeed(string message, RecordedVoice.Line? line = null)
    {
        status = message;
        statusIsGood = true;
        offersMicrophoneChange = false;
        // The key worked: another one held from here on is no sign of a missing right Ctrl.
        companion.Shortcuts.ReportsOtherDictationKeys = false;
        if (line is { } recordedLine) companion.RecordedVoice.Play(recordedLine);
        Publish();
        ScheduleAdvance(TimeSpan.FromSeconds(2.6));
    }

    private void ScheduleAdvance(TimeSpan delay)
    {
        advanceCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        advanceCancellation = cancellation;
        _ = AdvanceLaterAsync(delay, cancellation.Token);
    }

    private async Task AdvanceLaterAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try { await Task.Delay(delay, cancellationToken); }
        catch (OperationCanceledException) { return; }
        Advance();
    }

    private void Advance()
    {
        switch (step)
        {
            case FirstRunTourStep.Meet: GoTo(FirstRunTourStep.Dictate); break;
            case FirstRunTourStep.Dictate: GoTo(FirstRunTourStep.Talk); break;
            case FirstRunTourStep.Talk: GoTo(FirstRunTourStep.Act); break;
            case FirstRunTourStep.Act: GoTo(NeedsFreeAccountNote ? FirstRunTourStep.Account : FirstRunTourStep.Done); break;
            case FirstRunTourStep.Account:
                var next = stepAfterAccount ?? FirstRunTourStep.Done;
                stepAfterAccount = null;
                GoTo(next);
                break;
            case FirstRunTourStep.OpenAtLogin: CloseOpenAtLoginOffer(); break;
            default: Finish(); break;
        }
    }

    private void GoTo(FirstRunTourStep newStep)
    {
        advanceCancellation?.Cancel();
        step = newStep;
        companion.SetHoldsTakesOnTheIsland(newStep == FirstRunTourStep.Dictate);
        companion.Shortcuts.ReportsOtherDictationKeys = newStep == FirstRunTourStep.Dictate;
        idleWatch?.Cancel();
        status = null;
        statusIsGood = false;
        offersMicrophoneChange = false;
        preferences.Set(StepKey, RawValue(newStep));
        isShowingDemo = false;
        AppLog.Write($"tour: {RawValue(newStep)}");
        switch (newStep)
        {
            case FirstRunTourStep.Meet:
                companion.HasCompletedOnboarding = true;
                companion.IntroducePointerForTour();
                // Still after the hello? The pointer goes and points at "let’s go".
                WatchForIdleMouse(TimeSpan.FromSeconds(4), () =>
                {
                    if (step == FirstRunTourStep.Meet) companion.PointAtTourPrimaryButton("tap let’s go");
                });
                // Nothing to type to get going: a PC with no key and no account starts as a guest.
                if (!OpenClickyConfiguration.IsConfigured) _ = StartGuestAsync();
                break;
            case FirstRunTourStep.Dictate:
                companion.RecordedVoice.Play(RecordedVoice.Line.TourDictate);
                break;
            case FirstRunTourStep.Talk:
                if (OpenClickyConfiguration.IsConfigured)
                {
                    // Watch first: Clicky points at something and says a few words, then it's your turn.
                    isShowingDemo = true;
                    companion.PerformTourDemo(() => companion.Services.MainThread.Post(() =>
                    {
                        if (step != FirstRunTourStep.Talk || !isShowingDemo) return;
                        isShowingDemo = false;
                        companion.RecordedVoice.Play(RecordedVoice.Line.TourTalk);
                        Publish();
                    }));
                }
                break;
            case FirstRunTourStep.Act:
                if (OpenClickyConfiguration.IsConfigured) companion.RecordedVoice.Play(RecordedVoice.Line.TourAct);
                break;
            case FirstRunTourStep.Account:
                FieldText = "";
                companion.Island.TourFieldText = "";
                if (AccountCardAsksForEmail)
                {
                    OpenClickyAuthSession.Shared.ForgetSettledFlow();
                    FocusField();
                }
                else
                {
                    companion.RecordedVoice.Play(RecordedVoice.Line.TourAccount);
                }
                break;
            case FirstRunTourStep.Done:
                companion.RecordedVoice.Play(RecordedVoice.Line.TourDone);
                break;
        }
        Publish();
    }

    private async Task StartGuestAsync()
    {
        await OpenClickyAuthSession.Shared.StartGuestWithoutEmailAsync();
        companion.Services.MainThread.Post(Publish);
    }

    // MARK: - Open at sign-in

    /// <summary>Whether this is the moment to ask: enough uses, setup done, not asked before, not already on.</summary>
    public static bool ShouldOfferOpenAtLogin(int useCount, bool tourIsFinished, bool alreadyAsked, bool isOpenAtLogin) =>
        useCount >= UsesBeforeOpenAtLoginOffer && tourIsFinished && !alreadyAsked && !isOpenAtLogin;

    /// <summary>Counts one use; on the fifth, once the take or answer is over, the island asks (once) about opening at sign-in.</summary>
    public void NoteUse()
    {
        var useCount = preferences.GetInt(UseCountKey) + 1;
        preferences.Set(UseCountKey, useCount);
        if (step is not null || !ShouldOfferOpenAtLogin(useCount, IsFinished, preferences.GetBool(OpenAtLoginAskedKey), companion.IsOpenAtLogin)) return;
        _ = OfferOpenAtLoginLaterAsync(useCount);
    }

    private async Task OfferOpenAtLoginLaterAsync(int useCount)
    {
        // A moment after the answer or the paste, not on top of it.
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (step is not null || companion.VoiceState != CompanionVoiceState.Idle) return;
        preferences.Set(OpenAtLoginAskedKey, true);
        AppLog.Write($"open at login: offered after {useCount} uses");
        step = FirstRunTourStep.OpenAtLogin;
        status = null;
        statusIsGood = false;
        Publish();
    }

    private void CloseOpenAtLoginOffer()
    {
        advanceCancellation?.Cancel();
        step = null;
        status = null;
        Publish();
    }

    private void Finish()
    {
        advanceCancellation?.Cancel();
        idleWatch?.Cancel();
        companion.SetHoldsTakesOnTheIsland(false);
        companion.Shortcuts.ReportsOtherDictationKeys = false;
        step = null;
        preferences.Set(DoneKey, true);
        preferences.Remove(StepKey);
        companion.DictationSettings.HasCompletedDictationOnboarding = true;
        AppLog.Write("tour: finished");
        Publish();
        // Setup is done: the models can download in the background now.
        companion.StartAutomaticModelDownloads(tourIsFinished: true);
    }

    // MARK: - The card's buttons

    public void PrimaryAction()
    {
        if (step is not { } currentStep) return;
        switch (currentStep)
        {
            case FirstRunTourStep.Meet:
                GoTo(FirstRunTourStep.Dictate);
                break;
            case FirstRunTourStep.Dictate:
                break;
            case FirstRunTourStep.Account:
                if (AccountCardAsksForEmail)
                {
                    SubmitField();
                }
                else
                {
                    // "add my key": Settings → Keys & Plans has the key fields; the tour carries on.
                    companion.ShowSettings(DictationSettingsPage.Keys);
                    Advance();
                }
                break;
            case FirstRunTourStep.Talk:
            case FirstRunTourStep.Act:
                // Tried it (or would rather not): "next" moves on, as the success would.
                if (OpenClickyConfiguration.IsConfigured)
                {
                    Advance();
                    return;
                }
                if (OpenClickyAuthSession.Shared.AccountsOpen != false)
                {
                    stepAfterAccount = currentStep;
                    GoTo(FirstRunTourStep.Account);
                }
                else
                {
                    companion.ShowSettings(DictationSettingsPage.Account);
                }
                break;
            case FirstRunTourStep.Done:
                companion.ShowSettings(DictationSettingsPage.General);
                Finish();
                break;
            case FirstRunTourStep.OpenAtLogin:
                var isOn = companion.SetOpenAtLogin(true);
                status = isOn ? "✓ done. i’ll be here whenever your pc starts." : "windows didn’t allow it. you can switch it on in settings → general.";
                statusIsGood = isOn;
                Publish();
                if (isOn) ScheduleAdvance(TimeSpan.FromSeconds(2.4));
                break;
        }
    }

    public void SecondaryAction()
    {
        if (step is not { } currentStep) return;
        switch (currentStep)
        {
            case FirstRunTourStep.Meet:
            case FirstRunTourStep.Done:
                Finish();
                break;
            case FirstRunTourStep.OpenAtLogin:
                CloseOpenAtLoginOffer();
                break;
            default:
                Advance();
                break;
        }
    }

    /// <summary>The account step's field: an email starts a free account (or asks for a code); a code finishes signing in.</summary>
    public void SubmitField()
    {
        var auth = OpenClickyAuthSession.Shared;
        var typed = FieldText.Trim();
        if (auth.EmailFlow is OpenClickyAuthSession.EmailFlowState.NeedsCodeState)
        {
            if (!EmailAccountFormRules.CanSubmitCode(typed))
            {
                status = "the code is the 6 digits in the email.";
                Publish();
                return;
            }
            _ = auth.SubmitCodeAsync(typed);
        }
        else
        {
            if (!EmailAccountFormRules.CanSubmitEmail(typed))
            {
                status = "that doesn’t look like an email yet.";
                Publish();
                return;
            }
            _ = auth.StartAsync(typed);
        }
    }

    /// <summary>On the dictate step, another key that could dictate was held alone: the keyboard most
    /// likely has no right Ctrl (many laptops don't), so that key dictates from now on. Right Alt, which
    /// the card offers, is taken on its press and that hold is already a take. Left Ctrl is not taken:
    /// it is the hand for copy, paste and selecting, so the card points at right Alt instead.</summary>
    public void AdoptDictationKey(DictationHotkey key)
    {
        if (step != FirstRunTourStep.Dictate || companion.DictationSettings.DictationKey == key) return;
        if (key == DictationHotkey.LeftControl)
        {
            AppLog.Write("tour: left Ctrl held alone on the dictate step; pointed at right Alt");
            status = "left ctrl stays free for copy and paste. no right ctrl? hold right alt, just right of the space bar.";
            statusIsGood = false;
            offersMicrophoneChange = false;
            Publish();
            return;
        }
        AppLog.Write($"tour: {key.RawValue()} held alone on the dictate step; it dictates now");
        companion.DictationSettings.DictationKey = key;
        // Right Alt is already recording; any other key is told to hold it again.
        status = key == DictationHotkey.RightAlt ? null : $"{key.KeycapLabel()} dictates now. hold it and say something.";
        statusIsGood = true;
        Publish();
    }

    /// <summary>The dictate step's "change microphone" link.</summary>
    public void ChangeMicrophone()
    {
        var microphoneBefore = companion.DictationSettings.PreferredMicrophoneUID;
        companion.PresentMicrophoneMenu(() =>
        {
            // A take still listening keeps the microphone it opened; end it so the next one uses the new one.
            if (companion.DictationSettings.PreferredMicrophoneUID != microphoneBefore) companion.CancelTakeInProgress();
            status = $"now listening through {companion.MicrophoneName()}. try again.";
            statusIsGood = false;
            offersMicrophoneChange = true;
            Publish();
        });
    }

    // MARK: - The card

    public void Publish()
    {
        Card = step is { } currentStep ? MakeCard(currentStep) : null;
        companion.Island.TourCard = Card;
    }

    private FirstRunTourCard MakeCard(FirstRunTourStep currentStep)
    {
        var needsCode = OpenClickyAuthSession.Shared.EmailFlow is OpenClickyAuthSession.EmailFlowState.NeedsCodeState;
        var inputs = new FirstRunTourCardInputs(
            companion.DictationSettings.DictationKey,
            CanAsk: OpenClickyConfiguration.IsConfigured,
            UsesAgent: AccountCapabilities.Current().UsesAgent,
            IsShowingDemo: isShowingDemo,
            AccountCardAsksForEmail: AccountCardAsksForEmail,
            NeedsCode: needsCode,
            Status: status,
            StatusIsGood: statusIsGood,
            MicrophoneName: currentStep == FirstRunTourStep.Dictate ? companion.MicrophoneName() : null,
            OffersMicrophoneChange: offersMicrophoneChange);
        return FirstRunTourCards.Make(currentStep, inputs);
    }

    public static string RawValue(FirstRunTourStep step) => step switch
    {
        FirstRunTourStep.Meet => "meet",
        FirstRunTourStep.Dictate => "dictate",
        FirstRunTourStep.Talk => "talk",
        FirstRunTourStep.Act => "act",
        FirstRunTourStep.Account => "account",
        FirstRunTourStep.Done => "done",
        _ => "openAtLogin",
    };

    public static FirstRunTourStep? StepFromRaw(string? raw) => raw switch
    {
        "meet" => FirstRunTourStep.Meet,
        "dictate" => FirstRunTourStep.Dictate,
        "talk" => FirstRunTourStep.Talk,
        "act" => FirstRunTourStep.Act,
        "account" => FirstRunTourStep.Account,
        "done" => FirstRunTourStep.Done,
        "openAtLogin" => FirstRunTourStep.OpenAtLogin,
        _ => null,
    };
}
