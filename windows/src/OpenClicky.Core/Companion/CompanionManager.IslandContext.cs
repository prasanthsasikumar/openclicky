//
//  CompanionManager.IslandContext.cs
//  OpenClicky (Windows port of NotchHUDManager.observeIslandContext in NotchHUD.swift)
//
//  What the island's slot knows about the moment, built from what the companion already publishes:
//  the take controller's state and last take, the orb's live words, the agent's activity line, and
//  whether its result card is up. The island windows push it to every island and keep a landed
//  take's eight seconds ticking (IslandWindowManager).
//

namespace OpenClicky.Core.Companion;

public sealed partial class CompanionManager
{
    private bool isTakeLiveForIsland;
    private DateTimeOffset? islandTakeLandedAt;
    private string islandLandedTranscript = "";

    /// <summary>Follows the take, the orb, the agent and its card into <see cref="IslandCompanionModel.IslandContext"/>.</summary>
    private void ObserveIslandContext()
    {
        TakeStateChanged += state => services.MainThread.Post(() =>
        {
            var isLive = state != Dictation.DictationTakeController.TakeState.Idle;
            islandTakeLandedAt = IslandContext.LandingTime(isTakeLiveForIsland, islandTakeLandedAt, isLive, DateTimeOffset.Now);
            isTakeLiveForIsland = isLive;
            UpdateIslandContext();
        });
        TakeFinished += take => services.MainThread.Post(() =>
        {
            islandLandedTranscript = take.DisplayText;
            UpdateIslandContext();
        });
        Orb.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(OrbModel.LiveTranscript)) services.MainThread.Post(UpdateIslandContext);
        };
        AgentCard.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(AgentResultCardModel.IsVisible) or nameof(AgentResultCardModel.Title)
                or nameof(AgentResultCardModel.Status) or nameof(AgentResultCardModel.ThreadId))
            {
                services.MainThread.Post(() =>
                {
                    Island.AgentCardTitle = AgentCard.Title;
                    Island.AgentCardStatus = AgentCard.Status;
                    Island.AgentCardThreadId = AgentCard.ThreadId;
                    UpdateIslandContext();
                });
            }
        };
        // "follow up" on the agent component: the composer's words go to that agent thread.
        Island.ComposerFollowUpSubmitted += (text, threadId) => SubmitTextToAgent(text, threadId);
    }

    private void UpdateIslandContext()
    {
        Island.IslandContext = Island.IslandContext with
        {
            IsTakeLive = isTakeLiveForIsland,
            TakeLandedAt = islandTakeLandedAt,
            LiveTranscript = Orb.LiveTranscript,
            LandedTranscript = islandLandedTranscript,
            AgentActivityText = AgentActivityText,
            IsResultCardUp = AgentCard.IsVisible,
            Now = DateTimeOffset.Now,
        };
    }
}
