//
//  CompanionManager.AgentLane.cs
//  OpenClicky (Windows port of CompanionManager.swift: the OpenClicky agent lane)
//
//  Hands real work to a Codex thread through the `openclicky` CLI, attaching the cursor screen,
//  speaks the agent's final message, and fills the result card.
//

using OpenClicky.Core.Agent;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Companion;

public sealed partial class CompanionManager
{
    /// <summary>The Codex thread reused across agent requests this session, so follow-ups resume it.</summary>
    private string? lastAgentThreadId;

    /// <summary>
    /// Text entry point into the agent lane (the result card's follow-ups, the connect card). Captures
    /// the screen like the voice path does, resumes <paramref name="threadId"/> when given, speaks the result.
    /// </summary>
    public void SubmitTextToAgent(string text, string? threadId = null, bool startsNewThread = false)
    {
        var trimmedText = text.Trim();
        if (trimmedText.Length == 0 || !OpenClickyConfiguration.IsConfigured) return;
        // An account has no agent: the backend would refuse it with not_on_plan, so say that plainly.
        if (!AccountCapabilities.Current().UsesAgent)
        {
            SpeakWithSystemVoice(AccountLimitError.NotOnPlan.Message());
            return;
        }
        InterruptCurrentAnswer(includingAgentWork: true);
        if (threadId is not null) lastAgentThreadId = threadId;
        else if (startsNewThread) lastAgentThreadId = null;
        LastTranscript = trimmedText;
        var cancellation = new CancellationTokenSource();
        currentResponseCancellation = cancellation;
        _ = RunTextAgentTurnAsync(trimmedText, cancellation);
    }

    private async Task RunTextAgentTurnAsync(string text, CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        VoiceState = CompanionVoiceState.Processing;
        var screenCaptures = await CaptureScreensQuietlyAsync(cancellationToken);
        try
        {
            await RunOpenClickyAgentLaneAsync(text, screenCaptures, cancellationToken);
        }
        catch (Exception error) when (error is OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            AppLog.Write($"agent: follow-up failed ({error.GetType().Name})");
            AgentActivityText = null;
            SpeakCreditsErrorFallback();
        }
        await WaitForSystemVoiceNoticeToEndAsync();
        if (!cancellationToken.IsCancellationRequested)
        {
            VoiceState = CompanionVoiceState.Idle;
            Overlay.ScheduleTransientHideIfNeeded();
        }
        if (currentResponseCancellation == cancellation) currentResponseCancellation = null;
    }

    private async Task<IReadOnlyList<CompanionScreenCapture>> CaptureScreensQuietlyAsync(CancellationToken cancellationToken)
    {
        try { return await services.ScreenCapture.CaptureAllScreensAsync(cancellationToken: cancellationToken); }
        catch (Exception error)
        {
            AppLog.Write($"agent: no screenshot ({error.GetType().Name})");
            return [];
        }
    }

    /// <summary>The agent lane in the voice path: run the thread, then speak what it said.</summary>
    private async Task RunOpenClickyAgentLaneAsync(string transcript, IReadOnlyList<CompanionScreenCapture> screenCaptures, CancellationToken cancellationToken)
    {
        var spokenText = await PerformAgentTaskAsync(transcript, screenCaptures, cancellationToken);
        if (cancellationToken.IsCancellationRequested) return;
        await SpeakWholeReplyAsync(spokenText, cancellationToken);
    }

    /// <summary>
    /// The agent lane core, shared by the voice path and the Realtime `send_to_agent` tool: attach the
    /// cursor screen, run the Codex thread (resumed across turns), publish milestones and the result
    /// card, and return the sentence to speak.
    /// </summary>
    private async Task<string> PerformAgentTaskAsync(string transcript, IReadOnlyList<CompanionScreenCapture> screenCaptures, CancellationToken cancellationToken = default)
    {
        string? screenshotPath = null;
        if ((screenCaptures.FirstOrDefault(capture => capture.IsCursorScreen) ?? screenCaptures.FirstOrDefault()) is { } cursorScreenCapture)
        {
            var path = Path.Combine(Path.GetTempPath(), $"openclicky-{Guid.NewGuid()}.jpg");
            try
            {
                await File.WriteAllBytesAsync(path, cursorScreenCapture.ImageData, CancellationToken.None);
                screenshotPath = path;
            }
            catch (IOException error)
            {
                AppLog.Write($"agent: couldn't save the screenshot ({error.Message})");
            }
        }

        AgentActivityText = "starting agent…";
        AppLog.Write($"agent: task ({transcript.Length} chars)");
        var startedAt = DateTimeOffset.UtcNow;
        var result = await openClickyAgentClient.RunAgentAsync(
            transcript, screenshotPath, lastAgentThreadId,
            onEvent: milestone => services.MainThread.Post(() => AgentActivityText = milestone),
            cancellationToken);
        AppLog.Write($"agent task finished in {(DateTimeOffset.UtcNow - startedAt).TotalSeconds:0.0} s ({result.Status})");

        if (result.ThreadId is { } threadId) lastAgentThreadId = threadId;
        LastAgentArtifacts = result.Artifacts;
        AgentActivityText = null;
        var summary = new OpenClickyAgentResultSummary(
            result.ThreadId ?? lastAgentThreadId ?? "",
            transcript.Length > 60 ? transcript[..60] : transcript,
            result.Text.Trim(),
            result.Artifacts,
            result.Status,
            DateTimeOffset.Now);
        LastAgentResult = summary;
        ShowAgentResult(summary);

        var spokenText = result.Text.Trim();
        if (spokenText.Length == 0)
        {
            spokenText = result.Status == "completed" ? "done." : $"the agent stopped early: {result.ErrorMessage ?? result.Status}";
        }
        if (result.Artifacts.Count > 0)
        {
            var fileCount = result.Artifacts.Count;
            spokenText += $" i saved {fileCount} {(fileCount == 1 ? "file" : "files")}.";
        }
        // Keep the ask lane aware of what the agent did.
        RememberExchange(transcript, spokenText);
        return spokenText;
    }

    /// <summary>The result card (top-right) with the finished turn.</summary>
    private void ShowAgentResult(OpenClickyAgentResultSummary summary)
    {
        AgentCard.ThreadId = summary.ThreadId;
        AgentCard.Title = summary.Title;
        AgentCard.Summary = summary.Text;
        AgentCard.LiveResultText = summary.Text;
        AgentCard.Status = summary.Status;
        AgentCard.IsLoading = false;
        AgentCard.IsVisible = true;
    }

    /// <summary>Opens the result card for a thread and reads it through the CLI (the Mac's Agents tab → Open Agent).</summary>
    public void OpenAgentResultCard(string threadId)
    {
        AgentCard.BeginLoading(threadId);
        if (LastAgentResult is { } last && last.ThreadId == threadId && DateTimeOffset.Now - last.FinishedAt < TimeSpan.FromHours(1))
        {
            AgentCard.LiveResultText = last.Text;
        }
        _ = LoadAgentThreadAsync(threadId);
    }

    private async Task LoadAgentThreadAsync(string threadId)
    {
        try
        {
            var detail = await openClickyAgentClient.ReadThreadAsync(threadId);
            if (AgentCard.ThreadId != threadId) return;
            AgentCard.Title = detail.Thread.Preview.Length > 60 ? detail.Thread.Preview[..60] : detail.Thread.Preview;
            AgentCard.Summary = detail.LastAgentMessage ?? "";
            AgentCard.Status = detail.Thread.Status;
        }
        catch (Exception error)
        {
            AppLog.Write($"agent: couldn't read the thread ({error.GetType().Name})");
            if (AgentCard.ThreadId == threadId) AgentCard.Status = "error";
        }
        finally
        {
            if (AgentCard.ThreadId == threadId) AgentCard.IsLoading = false;
        }
    }
}
