//
//  IslandModel.cs
//  OpenClicky (Windows port of NotchHUD.swift's NotchHUDModel and the shared half of NotchHUDManager)
//
//  IslandModel is one monitor's island (NotchHUDModel): its expansion, its geometry and placement,
//  the cards it shows, hover and pinning, the 0.4 s grace before it settles. IslandCompanionModel
//  is what every island shares and reads (the voice state, the pointer, the cards, the typed
//  caption) and where the island's buttons are raised for the orchestrator to act on. The windows
//  are Island/IslandWindow.cs and Island/IslandWindowManager.cs.
//
//  Island geometry is in each monitor's own DIPs (ScreenInfo.BoundsInDips); the window converts.
//

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Shortcuts;

namespace OpenClicky.Core.Companion;

/// <summary>One monitor's island.</summary>
public sealed partial class IslandModel : ObservableObject
{
    [ObservableProperty] private IslandExpansion expansion = IslandExpansion.Collapsed;
    [ObservableProperty] private IslandGeometry geometry;
    [ObservableProperty] private HUDPlacement placement;

    [ObservableProperty] private AppConnectPrompt? connectPrompt;
    [ObservableProperty] private PermissionPrompt? permissionPrompt;
    [ObservableProperty] private FirstRunTourCard? tourCard;
    /// <summary>The tour just finished: the island settles back slowly, once.</summary>
    [ObservableProperty] private bool isSettlingGracefully;
    /// <summary>Text typed out in the compact strip (the goodbye); null = none.</summary>
    [ObservableProperty] private string? captionText;
    [ObservableProperty] private bool isComposerOpen;
    /// <summary>The busy strip has a line to show under its status.</summary>
    [ObservableProperty] private bool hasCompactDetailLine;
    /// <summary>The goodbye on quit: its caption shows even over a busy strip, and nothing opens after it.</summary>
    [ObservableProperty] private bool isSayingGoodbye;

    /// <summary>Called whenever the expansion or the island's size changes so the window can resize itself.</summary>
    public event Action? LayoutChanged;

    private bool isHovering;
    /// <summary>Something the panel opened takes over: stay closed until the pointer leaves and comes back.</summary>
    private bool ignoresHoverUntilExit;
    private bool isBusy;
    /// <summary>The user clicked the island: stay open until they click elsewhere or close it.</summary>
    private bool isPinnedOpen;
    private CancellationTokenSource? collapseCancellation;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    /// <summary>Clicky is speaking: the strip is a stop button, and hovering must not swap it for the panel.</summary>
    public bool IsSpeaking { get; private set; }
    public bool IsBusy => isBusy;
    public bool IsHovering => isHovering;
    public bool IsPinnedOpen => isPinnedOpen;

    /// <param name="componentSettings">The user's default component, order and plugins; none: every plugin off.</param>
    public IslandModel(IslandGeometry geometry, HUDPlacement placement = HUDPlacement.Notch, Func<TimeSpan, CancellationToken, Task>? delay = null,
                       IslandComponentSettings? componentSettings = null, IReadOnlyList<IslandComponent>? registry = null)
    {
        this.geometry = geometry;
        this.placement = placement;
        this.delay = delay ?? Task.Delay;
        this.componentSettings = componentSettings ?? new IslandComponentSettings(Configuration.Preferences.InMemory());
        Registry = registry ?? IslandComponentRegistry.All;
        Selection = ComputeSelection();
    }

    // MARK: - The slot

    private readonly IslandComponentSettings componentSettings;
    /// <summary>What the island knows about the moment.</summary>
    private IslandContext context = IslandContext.Idle;
    /// <summary>The user's flip in the capsule; cleared when the moment changes.</summary>
    private string? componentOverride;

    /// <summary>The components the slot can show: the built-ins, then the plugins.</summary>
    public IReadOnlyList<IslandComponent> Registry { get; }

    /// <summary>The active component and the ones the capsule can flip to.</summary>
    public IslandComponentSelection Selection { get; private set; }

    public IslandComponent ActiveComponent => Registry.FirstOrDefault(component => component.Id == Selection.Active) ?? Registry[0];

    /// <summary>The capsule shows in the band only when there is something to flip to.</summary>
    public bool ShowsCapsule => Selection.Available.Count > 1;

    public void SetContext(IslandContext newContext)
    {
        // The drag is this monitor's own; the shared context never carries it.
        newContext = newContext with { IsDragHovering = context.IsDragHovering };
        if (context == newContext) return;
        if (newContext.ContextualKey != context.ContextualKey) componentOverride = null;
        context = newContext;
        RecomputeSelection();
    }

    /// <summary>The capsule: show this component until the moment changes.</summary>
    public void SelectComponent(string id)
    {
        componentOverride = id;
        RecomputeSelection();
    }

    /// <summary>A plugin, the default or the order changed in Settings: pick again.</summary>
    public void RefreshComponentSettings() => RecomputeSelection();

    private IslandComponentSelection ComputeSelection() => IslandComponentSelector.Select(
        Registry, componentSettings.Order, componentSettings.DefaultId, componentOverride, context, componentSettings.EnabledIds(Registry));

    private void RecomputeSelection()
    {
        var selection = ComputeSelection();
        if (selection == Selection) return;
        var heightBefore = Layout.FullHeight;
        Selection = selection;
        OnPropertyChanged(nameof(Selection));
        if (Expansion == IslandExpansion.Full && Layout.FullHeight != heightBefore) LayoutChanged?.Invoke();
    }

    /// <summary>The permission cards or the tour are up: hovering or clicking must not open the panel over them.</summary>
    public bool IsSetupInProgress => TourCard is not null || PermissionPrompt is not null;

    /// <summary>The caption strip shows only while nothing busier needs the island (the goodbye excepted).</summary>
    public bool IsShowingCaption => CaptionText is not null && (!isBusy || IsSayingGoodbye);

    public IslandContentState ContentState => new(
        Expansion,
        IsShowingCaption,
        HasCompactDetailLine,
        ConnectHasExamplePrompts: ConnectPrompt is { ExamplePrompts.Count: > 0 },
        PermissionOffersTrustReset: PermissionPrompt?.OffersAccessibilityTrustReset == true,
        TourHasStatus: TourCard?.Status is not null,
        TourHasMicrophoneName: TourCard?.MicrophoneName is not null,
        TourHasField: TourCard?.Field is not null,
        // The shortcuts keep the panel's own body height (45, or 92 in a side drawer).
        SlotHeight: Selection.Active == IslandComponentRegistry.ShortcutsId ? null : ActiveComponent.PreferredHeight);

    /// <summary>Sizes and frames for the current state.</summary>
    public IslandLayout Layout => new(Geometry, Placement, ContentState);

    /// <summary>Hidden and idle, the island is nothing at all: no hover zone, no click target.</summary>
    public bool IsInvisible => Layout.IsInvisible;

    partial void OnGeometryChanged(IslandGeometry value) => LayoutChanged?.Invoke();
    partial void OnPlacementChanged(HUDPlacement value) => LayoutChanged?.Invoke();

    private IslandExpansionInputs Inputs() => new(
        IsSayingGoodbye: IsSayingGoodbye,
        IsComposerOpen: IsComposerOpen,
        HasPermissionPrompt: PermissionPrompt is not null,
        IsPinnedOpen: isPinnedOpen,
        HasTourCard: TourCard is not null,
        IsBusy: isBusy,
        HasConnectPrompt: ConnectPrompt is not null,
        IsHovering: isHovering,
        IsSpeaking: IsSpeaking,
        HasCaption: CaptionText is not null,
        IsSideDrawer: Placement.IsSideDrawer(),
        HoldsOpenForShelf: isDragHovering || isDraggingOut || isHoldingAfterDrop);

    public void SetHovering(bool hovering)
    {
        if (ignoresHoverUntilExit)
        {
            if (hovering) return;
            ignoresHoverUntilExit = false;
        }
        if (isHovering == hovering) return;
        isHovering = hovering;
        // A drop's hold lasts while the pointer stays; it ends when the pointer leaves.
        if (!hovering && isHoldingAfterDrop)
        {
            holdAfterDropCancellation?.Cancel();
            isHoldingAfterDrop = false;
        }
        Reconcile();
    }

    // MARK: - Drags (the Shelf plugin)

    private bool isDragHovering;
    /// <summary>A tile is being dragged out of the shelf: the pointer leaves, the island must not close.</summary>
    private bool isDraggingOut;
    /// <summary>A drop just landed: the island stays open a moment so the new tile is seen.</summary>
    private bool isHoldingAfterDrop;
    private CancellationTokenSource? holdAfterDropCancellation;

    /// <summary>A drag from another app is over this island.</summary>
    public bool IsDragHovering => isDragHovering;
    public bool IsDraggingOut => isDraggingOut;

    /// <summary>Whether a component is on: a built-in always, a plugin when the user turned it on.</summary>
    public bool IsComponentEnabled(string id) => componentSettings.IsEnabled(id, Registry);

    /// <summary>A drag from another app entered or left this island. Only with the shelf on: without it the island
    /// does not react to drags at all.</summary>
    public void SetDragHovering(bool hovering)
    {
        if (hovering && !IsComponentEnabled(Plugins.IslandPluginRegistry.ShelfId)) return;
        if (isDragHovering == hovering) return;
        isDragHovering = hovering;
        // The hover poll lags a drag by a tick; a drag that just left must not read as a hover.
        if (!hovering) isHovering = false;
        var newContext = context with { IsDragHovering = hovering };
        if (newContext.ContextualKey != context.ContextualKey) componentOverride = null;
        context = newContext;
        RecomputeSelection();
        OnPropertyChanged(nameof(IsDragHovering));
        Reconcile(immediately: true);
    }

    public void SetDragOut(bool dragging)
    {
        if (isDraggingOut == dragging) return;
        isDraggingOut = dragging;
        Reconcile(immediately: true);
    }

    /// <summary>Keeps the island open 1.5 s after a drop with the shelf in front (so the new tile is seen), longer
    /// while the pointer stays on it; the hold ends when the pointer leaves.</summary>
    public void HoldOpenAfterDrop()
    {
        holdAfterDropCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        holdAfterDropCancellation = cancellation;
        isHoldingAfterDrop = true;
        componentOverride = Plugins.IslandPluginRegistry.ShelfId;
        RecomputeSelection();
        Reconcile(immediately: true);
        _ = EndHoldAfterDropAsync(cancellation.Token);
    }

    private async Task EndHoldAfterDropAsync(CancellationToken cancellationToken)
    {
        try { await delay(TimeSpan.FromSeconds(1.5), cancellationToken); }
        catch (OperationCanceledException) { return; }
        if (cancellationToken.IsCancellationRequested || isHovering) return;
        isHoldingAfterDrop = false;
        Reconcile();
    }

    public void SetSpeaking(bool speaking)
    {
        if (IsSpeaking == speaking) return;
        IsSpeaking = speaking;
        Reconcile();
    }

    public void SetBusy(bool busy)
    {
        isBusy = busy;
        OnPropertyChanged(nameof(IsShowingCaption));
        Reconcile();
        // The compact strip changes size between the busy strip and the caption strip.
        if (CaptionText is not null) LayoutChanged?.Invoke();
    }

    /// <summary>Shows (or clears) the caption typed into the compact strip.</summary>
    public void SetCaption(string? text)
    {
        if (CaptionText == text) return;
        var wasShowingCaption = CaptionText is not null;
        CaptionText = text;
        OnPropertyChanged(nameof(IsShowingCaption));
        if (wasShowingCaption != (text is not null))
        {
            var expansionBefore = Expansion;
            Reconcile(immediately: text is not null);
            // Already compact (the goodbye over a busy strip): the caption strip is another size, so the window must
            // follow even though the expansion did not change.
            if (Expansion == expansionBefore && Expansion == IslandExpansion.Compact) LayoutChanged?.Invoke();
        }
    }

    public void OpenComposer()
    {
        if (IsComposerOpen) return;
        IsComposerOpen = true;
        Reconcile(immediately: true);
    }

    public void CloseComposer()
    {
        if (!IsComposerOpen) return;
        IsComposerOpen = false;
        isHovering = false;
        Reconcile(immediately: true);
    }

    public void SetCompactDetailLine(bool hasDetailLine)
    {
        if (HasCompactDetailLine == hasDetailLine) return;
        HasCompactDetailLine = hasDetailLine;
        if (Expansion == IslandExpansion.Compact) LayoutChanged?.Invoke();
    }

    public void TogglePinned()
    {
        if (IsSetupInProgress) return;
        isPinnedOpen = !isPinnedOpen;
        Reconcile();
    }

    /// <summary>Opens the full panel and keeps it open until a click elsewhere (the tray's "open the island").</summary>
    public void OpenPinned()
    {
        isPinnedOpen = true;
        Reconcile(immediately: true);
    }

    /// <summary>The goodbye caption takes the island over and nothing else opens until the app quits.</summary>
    public void BeginGoodbye()
    {
        IsSayingGoodbye = true;
        isPinnedOpen = false;
        isHovering = false;
        IsComposerOpen = false;
        Reconcile(immediately: true);
        LayoutChanged?.Invoke();
    }

    public void Close()
    {
        isPinnedOpen = false;
        isHovering = false;
        Reconcile(immediately: true);
    }

    /// <summary>Closes the panel for good this time: hovering reopens it only after the pointer has left.</summary>
    public void Dismiss()
    {
        ignoresHoverUntilExit = isHovering || Expansion == IslandExpansion.Full;
        Close();
    }

    /// <summary>Shows (or clears) the connect card; it takes the island over from the busy strip.</summary>
    public void SetConnectPrompt(AppConnectPrompt? prompt)
    {
        if (Equals(ConnectPrompt, prompt)) return;
        ConnectPrompt = prompt;
        Reconcile(immediately: prompt is not null);
    }

    /// <summary>Shows (or clears) the tour card. It yields to the busy strip while a take or a question is under way.</summary>
    public void SetTourCard(FirstRunTourCard? card)
    {
        if (Equals(TourCard, card)) return;
        if (TourCard is not null && card is null)
        {
            IsSettlingGracefully = true;
            _ = EndGracefulSettleAsync();
        }
        var sizeChanged = TourCard?.Status != card?.Status || TourCard?.Step != card?.Step
            || TourCard?.MicrophoneName != card?.MicrophoneName || TourCard?.Field != card?.Field;
        TourCard = card;
        Reconcile(immediately: true);
        if (sizeChanged && Expansion == IslandExpansion.Tour) LayoutChanged?.Invoke();
    }

    private async Task EndGracefulSettleAsync()
    {
        await delay(TimeSpan.FromSeconds(1.8), CancellationToken.None);
        IsSettlingGracefully = false;
    }

    /// <summary>Shows (or clears) the permission card; it outranks every other island state.</summary>
    public void SetPermissionPrompt(PermissionPrompt? prompt)
    {
        if (Equals(PermissionPrompt, prompt)) return;
        PermissionPrompt = prompt;
        Reconcile(immediately: true);
    }

    private void Reconcile(bool immediately = false)
    {
        collapseCancellation?.Cancel();
        collapseCancellation = null;
        var (target, applyNow) = IslandExpansionRules.Target(Inputs(), immediately);
        if (applyNow)
        {
            SetExpansion(target);
            return;
        }
        // Grace period so the panel does not flicker when the pointer skims the edge.
        var cancellation = new CancellationTokenSource();
        collapseCancellation = cancellation;
        _ = SettleAfterGraceAsync(cancellation.Token);
    }

    private async Task SettleAfterGraceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await delay(TimeSpan.FromSeconds(IslandExpansionRules.CollapseGraceSeconds), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cancellationToken.IsCancellationRequested) return;
        var inputs = Inputs();
        if (!IslandExpansionRules.SettlesAfterGrace(inputs)) return;
        SetExpansion(IslandExpansionRules.RestingExpansion(inputs));
    }

    private void SetExpansion(IslandExpansion newExpansion)
    {
        if (Expansion == newExpansion) return;
        Expansion = newExpansion;
        LayoutChanged?.Invoke();
    }
}

/// <summary>Where a button on the island sends the person in the window (CompanionManager.showSettings pages).</summary>
public enum IslandSettingsDestination
{
    General,
    Dictation,
    Keys,
    Account,
}

/// <summary>What every monitor's island shows and shares, and the island's buttons for the orchestrator to act on.</summary>
public sealed partial class IslandCompanionModel : ObservableObject
{
    // MARK: - What the islands read (mirrored from the orchestrator)

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsSpeaking), nameof(CompactStatusText), nameof(DetailLine), nameof(IsIdle))]
    private CompanionVoiceState voiceState = CompanionVoiceState.Idle;
    [ObservableProperty] private double currentAudioPowerLevel;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CompactStatusText), nameof(DetailLine))]
    private string? agentActivityText;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DetailLine))] private string? lastTranscript;
    /// <summary>Who is answering ("Claude Code", "free credits").</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CompactStatusText))] private string? answeringSourceName;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompactStatusText), nameof(ShortcutGrid))]
    private bool isAlwaysListening;
    /// <summary>The voice is a dictation into the front app, not a question.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CompactStatusText))] private bool isDictatingToFrontApp;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PointerStatusText))] private bool isClickyCursorEnabled = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PointerStatusText))] private PointerPresence pointerPresence = PointerPresence.Always;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PointerStatusText))] private bool isPointerAwake = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShortcutGrid))] private DictationHotkey dictationKey = DictationHotkeys.Default;
    /// <summary>The dictation engine's name, for the voice pill ("this pc", "Sarvam").</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VoicePillToolTip))] private string dictationEngineName = "this pc";
    /// <summary>Shown in place of the shortcut grid when the engine can't run; null = the grid.</summary>
    [ObservableProperty] private IslandEngineWarning? engineWarning;
    /// <summary>The voice pill's call to action when the engine can't run ("add sarvam key ›"); null = the green mic.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VoicePillToolTip))] private string? engineBlockedPillLabel;
    /// <summary>Why the engine can't run, the pill's hover text when blocked.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(VoicePillToolTip))] private string? engineUnavailableReason;
    [ObservableProperty]
    private HUDPlacement placement = HUDPlacement.Notch;

    // MARK: - Cards (every island shows the same one)

    [ObservableProperty] private AppConnectPrompt? connectPrompt;
    [ObservableProperty] private PermissionPrompt? permissionPrompt;
    [ObservableProperty] private FirstRunTourCard? tourCard;
    /// <summary>What is typed in the tour's account field.</summary>
    [ObservableProperty] private string tourFieldText = "";
    /// <summary>The caption typed into every island's compact strip; null = none.</summary>
    [ObservableProperty] private string? captionText;
    [ObservableProperty] private bool isSayingGoodbye;

    // MARK: - The slot's moment (mirrored from the orchestrator)

    /// <summary>What the islands know about the moment: a live or just-landed take, a working agent.</summary>
    [ObservableProperty] private IslandContext islandContext = IslandContext.Idle;
    /// <summary>The agent result card's title, status and thread, for the agent component.</summary>
    [ObservableProperty] private string agentCardTitle = "";
    [ObservableProperty] private string agentCardStatus = "";
    [ObservableProperty] private string agentCardThreadId = "";
    /// <summary>The thread a follow-up typed in the composer goes to; null: a new request.</summary>
    public string? ComposerThreadId { get; private set; }

    // MARK: - Derived

    public bool IsBusy => VoiceState != CompanionVoiceState.Idle || AgentActivityText is not null;
    public bool IsSpeaking => VoiceState == CompanionVoiceState.Responding;
    public bool IsIdle => VoiceState == CompanionVoiceState.Idle;
    public string CompactStatusText => IslandTexts.CompactStatus(VoiceState, IsDictatingToFrontApp, IsAlwaysListening, AgentActivityText, AnsweringSourceName);
    public string? DetailLine => IslandTexts.DetailLine(VoiceState, AgentActivityText, LastTranscript);
    public bool HasCompactDetailLine => DetailLine is not null;
    public string PointerStatusText => IslandTexts.PointerStatus(IsClickyCursorEnabled, PointerPresence, IsPointerAwake);
    public IReadOnlyList<IslandShortcut> ShortcutGrid => IslandTexts.ShortcutGrid(DictationKey, IsAlwaysListening);
    public string VoicePillToolTip => EngineBlockedPillLabel is null
        ? IslandTexts.VoicePillToolTip(DictationEngineName)
        : EngineUnavailableReason ?? EngineBlockedPillLabel;

    // MARK: - Requests to the island windows (raised by the orchestrator through the methods below)

    public event Action? OpenComposerRequested;
    public event Action? CloseComposerRequested;
    public event Action? OpenPanelRequested;
    public event Action? DismissPanelsRequested;
    public event Action? FocusTourFieldRequested;
    public event Action<HUDPlacement>? PlacementChangeRequested;

    /// <summary>Ctrl tapped twice: the one-line composer on the island under the pointer, with the keyboard.</summary>
    public void OpenTextComposer() => OpenTextComposer(threadId: null);

    /// <summary>The composer, its text going to <paramref name="threadId"/> when given (the agent component's
    /// "follow up"), else a new request.</summary>
    public void OpenTextComposer(string? threadId)
    {
        ComposerThreadId = string.IsNullOrEmpty(threadId) ? null : threadId;
        OpenComposerRequested?.Invoke();
    }
    /// <summary>Closes the composer everywhere; the window that had the keyboard gets it back.</summary>
    public void CloseTextComposer() => CloseComposerRequested?.Invoke();
    /// <summary>Opens the full panel on the island under the pointer and keeps it open.</summary>
    public void OpenPanel() => OpenPanelRequested?.Invoke();
    /// <summary>Closes every open panel because a window it opened is coming forward.</summary>
    public void DismissPanels() => DismissPanelsRequested?.Invoke();
    /// <summary>Gives the keyboard to the tour card's field (the account step's email or code).</summary>
    public void FocusTourField() => FocusTourFieldRequested?.Invoke();

    public void SetPlacement(HUDPlacement newPlacement)
    {
        if (Placement == newPlacement) return;
        Placement = newPlacement;
        PlacementChangeRequested?.Invoke(newPlacement);
    }

    // MARK: - The island's buttons (for the orchestrator)

    /// <summary>The composer's text, trimmed and non-empty (↵); the composer has already closed.</summary>
    public event Action<string>? ComposerSubmitted;
    /// <summary>The composer's text for an agent thread (text, thread id), opened from the agent component.</summary>
    public event Action<string, string>? ComposerFollowUpSubmitted;

    /// <summary>The (i): type out what OpenClicky does (the panel has closed).</summary>
    public event Action? ExplainRequested;
    public event Action<IslandSettingsDestination>? SettingsRequested;
    public event Action? QuitRequested;
    /// <summary>The busy strip was clicked while Clicky speaks.</summary>
    public event Action? StopSpeakingRequested;
    /// <summary>The voice pill or the engine warning's button: open what the engine needs.</summary>
    public event Action? EngineSetupRequested;
    public event Action? PermissionGrantRequested;
    public event Action? PermissionLaterRequested;
    public event Action? PermissionSettingsRequested;
    public event Action<AppConnectAnswer>? ConnectAnswered;
    public event Action? TourPrimaryRequested;
    public event Action? TourSecondaryRequested;
    public event Action? TourChangeMicrophoneRequested;
    /// <summary>The tour field's ↵ (email or code, untrimmed as typed).</summary>
    public event Action<string>? TourFieldSubmitted;

    [RelayCommand]
    private void SubmitComposer(string? text)
    {
        var threadId = ComposerThreadId;
        ComposerThreadId = null;
        CloseTextComposer();
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return;
        if (threadId is not null) ComposerFollowUpSubmitted?.Invoke(trimmed, threadId);
        else ComposerSubmitted?.Invoke(trimmed);
    }

    [RelayCommand] private void Explain() => ExplainRequested?.Invoke();
    [RelayCommand] private void OpenSettings(IslandSettingsDestination destination) => SettingsRequested?.Invoke(destination);
    [RelayCommand] private void Quit() => QuitRequested?.Invoke();
    [RelayCommand] private void StopSpeaking() => StopSpeakingRequested?.Invoke();
    [RelayCommand] private void OpenEngineSetup() => EngineSetupRequested?.Invoke();
    [RelayCommand] private void GrantPermission() => PermissionGrantRequested?.Invoke();
    [RelayCommand] private void PermissionLater() => PermissionLaterRequested?.Invoke();
    [RelayCommand] private void OpenPermissionSettings() => PermissionSettingsRequested?.Invoke();
    [RelayCommand] private void AnswerConnect(AppConnectAnswer answer) => ConnectAnswered?.Invoke(answer);
    [RelayCommand] private void TourPrimary() => TourPrimaryRequested?.Invoke();
    [RelayCommand] private void TourSecondary() => TourSecondaryRequested?.Invoke();
    [RelayCommand] private void TourChangeMicrophone() => TourChangeMicrophoneRequested?.Invoke();
    [RelayCommand] private void SubmitTourField() => TourFieldSubmitted?.Invoke(TourFieldText);

    // MARK: - The caption typed into the compact strip

    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Func<double> secondsClock;
    private CancellationTokenSource? captionCancellation;

    public IslandCompanionModel(Func<TimeSpan, CancellationToken, Task>? delay = null, Func<double>? secondsClock = null)
    {
        this.delay = delay ?? Task.Delay;
        this.secondsClock = secondsClock ?? Typewriter.MonotonicSeconds;
    }

    /// <summary>Types <paramref name="message"/> into every island's strip one character every 30 ms, holds it
    /// <paramref name="holdSeconds"/>, then lets the islands collapse again.</summary>
    public void ShowCaption(string message, double holdSeconds = 8)
    {
        captionCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        captionCancellation = cancellation;
        CaptionText = "";
        _ = TypeCaptionAsync(message, holdSeconds, cancellation.Token);
    }

    private async Task TypeCaptionAsync(string message, double holdSeconds, CancellationToken cancellationToken)
    {
        try
        {
            await Typewriter.TypeAsync(message, typed => CaptionText = typed, delay, cancellationToken, secondsClock);
            await delay(TimeSpan.FromSeconds(holdSeconds), cancellationToken);
            ClearCaption();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void ClearCaption()
    {
        captionCancellation?.Cancel();
        captionCancellation = null;
        CaptionText = null;
    }

    /// <summary>Types the goodbye into every island, over whatever it was showing.</summary>
    public void ShowGoodbye(string message)
    {
        IsSayingGoodbye = true;
        ShowCaption(message, holdSeconds: 30);
    }
}
