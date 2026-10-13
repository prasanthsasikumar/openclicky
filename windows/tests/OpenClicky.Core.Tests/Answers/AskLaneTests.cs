// Ports the logic parts of AskLaneFailureTests.swift, the ask-lane cases of PlanToolProtocolTests.swift
// (offersQuickActionsToAskLane) and WriteInPlaceTests.swift (theQuestionCarriesTheExactWords), and the
// routing case of ScreenElementGrounderTests.swift (grounding runs on every answer source but the plans).

using System.Net.Http;
using System.Text.Json.Nodes;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Tests.Answers;

/// <summary>What the Ctrl + Win ask lane says (or does not say) when an answer does not arrive normally.</summary>
public class AskLaneFailureTests
{
    // F-09: cancelling the task that reads the answer stream must read as an interruption, never
    // as "something went wrong".
    [Fact]
    public void ACancelledStreamIsAnInterruption()
    {
        Assert.True(AskLaneFailures.IsCancellation(new OperationCanceledException()));
        Assert.True(AskLaneFailures.IsCancellation(new TaskCanceledException()));
        // HttpClient's own timeout is a TaskCanceledException carrying a TimeoutException: a failure.
        Assert.False(AskLaneFailures.IsCancellation(new TaskCanceledException("timed out", new TimeoutException())));
        Assert.False(AskLaneFailures.IsCancellation(new TimeoutException()));
        Assert.False(AskLaneFailures.IsCancellation(new BackendHttpException(BackendHttpException.ServiceKind.Answers, 500, [])));
    }

    // F-34: a screenshot that cannot be taken is not a backend or token problem.
    [Fact]
    public void ACaptureFailureNamesTheScreenNotTheToken()
    {
        var captureError = new AskScreenCaptureException(new InvalidOperationException("BitBlt failed"));
        var permissionOff = AskLaneFailures.AskFailureSentence(captureError, screenRecordingAllowed: false);
        Assert.Contains("screen recording", permissionOff);
        Assert.DoesNotContain("token", permissionOff);
        var permissionOn = AskLaneFailures.AskFailureSentence(captureError);
        Assert.Contains("see your screen", permissionOn);
        Assert.DoesNotContain("token", permissionOn);
    }

    [Fact]
    public void TheTokenSentenceIsKeptForARefusedToken()
    {
        var forbidden = new BackendHttpException(BackendHttpException.ServiceKind.Answers, 403, []);
        Assert.Equal("something went wrong talking to the openclicky backend. check the backend and your token in the settings file.",
                     AskLaneFailures.AskFailureSentence(forbidden));
        var serverError = new BackendHttpException(BackendHttpException.ServiceKind.Answers, 500, []);
        Assert.Equal("something went wrong getting your answer. please ask again.", AskLaneFailures.AskFailureSentence(serverError));
        Assert.Contains("internet", AskLaneFailures.AskFailureSentence(new HttpRequestException(HttpRequestError.ConnectionError, "no route")));
        Assert.Contains("internet", AskLaneFailures.AskFailureSentence(new HttpRequestException(HttpRequestError.NameResolutionError, "no such host")));
        Assert.Contains("internet", AskLaneFailures.AskFailureSentence(new TimeoutException()));
        Assert.Equal(ProviderRouter.NeedsAnthropicKeySentence,
                     AskLaneFailures.AskFailureSentence(new ProviderRouteUnavailableException(ProviderCapability.Claude, ProviderRouter.NeedsAnthropicKeySentence)));
    }

    // F-43: an `error` event after the stream started is a failure, not a finished answer.
    [Fact]
    public void AMidStreamErrorEventIsAFailure()
    {
        var errorEvent = JsonNode.Parse("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""")!.AsObject();
        var failure = ClaudeAPI.StreamFailure(errorEvent)!;
        Assert.Equal(ClaudeAPI.MidStreamErrorCode, failure.Code);
        Assert.Contains("overloaded_error", failure.Message);
        Assert.Contains("stopped partway", AskLaneFailures.AskFailureSentence(failure));

        var textEvent = JsonNode.Parse("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi"}}""")!.AsObject();
        Assert.Null(ClaudeAPI.StreamFailure(textEvent));
    }

    [Fact]
    public void ACutOffAnswerIsRecognised()
    {
        var cutOff = JsonNode.Parse("""{"type":"message_delta","delta":{"stop_reason":"max_tokens","stop_sequence":null},"usage":{"output_tokens":1024}}""")!.AsObject();
        Assert.Equal("max_tokens", ClaudeAPI.StopReason(cutOff));
        var finished = JsonNode.Parse("""{"type":"message_delta","delta":{"stop_reason":"end_turn"}}""")!.AsObject();
        Assert.Equal("end_turn", ClaudeAPI.StopReason(finished));
    }

    // F-35: an empty answer, or one that is only a tag, has nothing to speak.
    [Fact]
    public void AnAnswerThatIsOnlyATagHasNothingToSpeak()
    {
        var bareNone = PointTagParser.ParsePointingCoordinates("[POINT:none]");
        Assert.Empty(AskLaneFailures.SpokenReply(bareNone.SpokenText, []));
        Assert.Empty(AskLaneFailures.SpokenReply("", []));
        Assert.NotEmpty(AskLaneFailures.EmptyAnswerSentence);
        Assert.Equal("done. made the folder.", AskLaneFailures.SpokenReply("  done. ", ["", "made the folder."]));
    }
}

public class AskLanePromptsTests
{
    [Fact]
    public void TheAskLaneOffersQuickActionsWhenTheAgentLaneCantOrAPlanAnswers()
    {
        Assert.True(AskLanePrompts.OffersQuickActionsToAskLane(agentLaneRuns: false, AnswerSource.OpenClickyAccount));
        Assert.True(AskLanePrompts.OffersQuickActionsToAskLane(agentLaneRuns: false, AnswerSource.OpenAI));
        Assert.True(AskLanePrompts.OffersQuickActionsToAskLane(agentLaneRuns: true, AnswerSource.ClaudeCodePlan));
        Assert.True(AskLanePrompts.OffersQuickActionsToAskLane(agentLaneRuns: true, AnswerSource.CodexPlan));
        Assert.False(AskLanePrompts.OffersQuickActionsToAskLane(agentLaneRuns: true, AnswerSource.Anthropic));
    }

    [Fact]
    public void TheQuestionCarriesTheExactWords()
    {
        var target = new WritingTarget(WritingScope.WholeField, "Hi Ravi,\nsee you at 5.", "chrome.exe", "Google Chrome");
        var prompt = AskLanePrompts.AskUserPrompt("can you rephrase this email", target);
        Assert.StartsWith("can you rephrase this email", prompt);
        Assert.Contains("Hi Ravi,\nsee you at 5.", prompt);
        Assert.Contains("in Google Chrome", prompt);
        Assert.Contains(WriteInPlace.ToolName, prompt);
    }

    [Fact]
    public void AHighlightIsQuotedTheShortWayWithoutATarget()
    {
        Assert.Equal("what does this mean", AskLanePrompts.AskUserPrompt("what does this mean", null));
        var prompt = AskLanePrompts.AskUserPrompt("what does this mean", null, "ephemeral");
        Assert.StartsWith("what does this mean\n\n[The user has selected (highlighted) this text on screen: «ephemeral».", prompt);
        Assert.EndsWith("]", prompt);
    }

    [Fact]
    public void TheToolsAreTheQuickActionsThenWriteInPlace()
    {
        var quickActions = QuickActionToolDefinitions.All();
        Assert.Equal(quickActions.Select(tool => tool["name"]!.GetValue<string>()),
                     AskLanePrompts.AskTools(true, false, quickActions).Select(tool => tool["name"]!.GetValue<string>()));
        Assert.Equal(["write_in_place"], AskLanePrompts.AskTools(false, true, quickActions).Select(tool => tool["name"]!.GetValue<string>()));
        Assert.Equal(7, AskLanePrompts.AskTools(true, true, quickActions).Count);
        Assert.Empty(AskLanePrompts.AskTools(false, false, quickActions));
    }

    [Fact]
    public void TheSystemPromptEndsWithTheLanguageThenTheToolLinesThenTheSkills()
    {
        var prompt = AskLanePrompts.AskSystemPrompt(quickActionsOffered: true, hasWritingTarget: true, skillsBlock: "  skill block  ", languageCode: "en");
        Assert.StartsWith(AskLanePrompts.CompanionVoiceResponseSystemPrompt + "\n\n", prompt);
        var toolsAt = prompt.IndexOf(AskLanePrompts.AccountToolsInstruction, StringComparison.Ordinal);
        var writeAt = prompt.IndexOf(WriteInPlace.PromptInstruction, StringComparison.Ordinal);
        Assert.True(toolsAt > 0 && writeAt > toolsAt);
        Assert.EndsWith(WriteInPlace.PromptInstruction + "\n\nskill block", prompt);
        var bare = AskLanePrompts.AskSystemPrompt(false, false, " ", "en");
        Assert.Equal(AskLanePrompts.WithReplyLanguage(AskLanePrompts.CompanionVoiceResponseSystemPrompt, "en"), bare);
        Assert.DoesNotContain(AskLanePrompts.AccountToolsInstruction, bare);
    }

    [Fact]
    public void ThePromptsSayItTheWindowsWay()
    {
        var prompt = AskLanePrompts.CompanionVoiceResponseSystemPrompt;
        Assert.Contains("lives in the user's tray", prompt);
        Assert.DoesNotContain("menu bar", prompt);
        Assert.DoesNotContain("command option", prompt);
        Assert.Contains($"ending with {GuidedStepMarker.Marker}", prompt);
        Assert.Contains(GuidedStepRules.NextStepRequestOpening, prompt);
        Assert.DoesNotContain("dock", AskLanePrompts.OnboardingDemoSystemPrompt);
        Assert.Equal("Screen 1 (image dimensions: 1280x800 pixels)", AskLanePrompts.ImageLabelWithItsPixelSize("Screen 1", 1280, 800));
    }
}

public class ScreenElementGroundingTests
{
    // Another answer key used to make ClaudeAPI.analyzeImage throw, swallowed by `try?`, so
    // grounding never ran there. Keys with images run it; only the slow plans are skipped.
    [Fact]
    public void GroundingRunsOnEveryAnswerSourceButThePlans()
    {
        Assert.Null(ScreenElementGrounding.ReasonGroundingCannotRun(ProviderRoute.AnthropicDirect));
        Assert.Null(ScreenElementGrounding.ReasonGroundingCannotRun(ProviderRoute.Backend));
        foreach (var source in new[] { AnswerSource.OpenAI, AnswerSource.OpenRouter, AnswerSource.Gemini, AnswerSource.Groq, AnswerSource.Mistral, AnswerSource.Xai })
        {
            Assert.Null(ScreenElementGrounding.ReasonGroundingCannotRun(ProviderRoute.AnswerProvider(source)));
        }
        Assert.Equal("Claude Code (your Claude plan) is too slow to locate an element mid-sentence",
                     ScreenElementGrounding.ReasonGroundingCannotRun(ProviderRoute.AnswerProvider(AnswerSource.ClaudeCodePlan)));
        Assert.NotNull(ScreenElementGrounding.ReasonGroundingCannotRun(ProviderRoute.AnswerProvider(AnswerSource.CodexPlan)));
        Assert.NotNull(ScreenElementGrounding.ReasonGroundingCannotRun(ProviderRoute.Unavailable("add a key")));
    }

    [Fact]
    public async Task TheGroundingRequestGoesThroughClaudeApi()
    {
        var fake = new FakeAnswerProvider(["[POINT:516, 503:arXiv]"], []);
        var api = new ClaudeAPI(() => "https://backend.example/chat", ScreenElementGrounder.Model,
                                () => new ProviderCredentials(null, null, null, groqKey: "gsk"), makeAnswerProvider: (_, _, _) => Task.FromResult<IAnswerProvider>(fake),
                                warmUpConnection: false);
        var located = await ScreenElementGrounder.LocateAsync("paper link", "", "open this paper", [0xFF, 0xD8], 1280, 831,
            ScreenElementGrounding.GroundingRouteFor(ProviderRoute.AnswerProvider(AnswerSource.Groq)), ScreenElementGrounding.AnalyzeImageThrough(api));
        Assert.Equal(new ScreenElementGrounder.Located(new ScreenPoint(516, 503), "arXiv"), located);
        var request = fake.Requests.Single();
        Assert.Equal(ScreenElementGrounder.SystemPrompt, request.SystemPrompt);
        Assert.Equal("Screenshot of the user's screen, 1280×831 px, origin top-left.", request.Images.Single().Label);
        Assert.Equal("image/jpeg", request.Images.Single().MediaType);
        Assert.Contains("paper link", request.UserPrompt);
    }
}
