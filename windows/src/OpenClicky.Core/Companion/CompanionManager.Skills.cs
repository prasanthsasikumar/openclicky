//
//  CompanionManager.Skills.cs
//  OpenClicky (Windows port of CompanionManager.swift: skills in the talk lanes, the connect card)
//

using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using OpenClicky.Core.Agent;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Skills;

namespace OpenClicky.Core.Companion;

public sealed partial class CompanionManager
{
    /// <summary>The connect card is switched off in this build, as on the Mac (OpenClickyConfiguration.ConnectorsEnabled).</summary>
    private static bool ConnectorsAreEnabled => OpenClickyConfiguration.ConnectorsEnabled;

    /// <summary>
    /// The prompt block for the skills that apply to this turn: the activated talk skills plus the
    /// app-teaching skill for the app (or browser site) in front. Empty when none.
    /// </summary>
    public string TalkSkillsBlock()
    {
        var frontInfo = services.FrontmostApp.LastExternalForeground ?? services.FrontmostApp.CurrentWithoutUrl();
        var front = frontInfo is null ? new FrontAppContext() : AppConnectPromptController.Context(frontInfo);
        // Apps the person answered "never" to on the connect card keep their skill out of the prompt.
        var declinedSkillIds = AppConnectPromptController.DeclinedSkillIds;
        var appSkills = SkillLibraryStore.AppSkills.Where(skill => skill.IsForTalk && !declinedSkillIds.Contains(skill.Id)).ToList();
        var appSkill = AppSkillMatcher.Match(front, appSkills);
        var block = SkillPromptBuilder.Build(SkillLibraryStore.ActiveTalkSkills, appSkill, front);
        if (appSkill is not null) AppLog.Write($"skills: app skill \"{appSkill.Name}\" for {front.ProcessName ?? "?"}{(front.Url is { } url ? " / " + url.Host : "")}");
        return block;
    }

    // MARK: - The connect card

    private void StartAppConnectPrompts()
    {
        AppConnectPromptController.RevealSettingsFile = () => services.MainThread.Post(host.RevealSettingsFile);
        AppConnectPromptController.Start(services.FrontmostApp, () => SkillLibraryStore.AppSkills,
            task => services.MainThread.Post(() => _ = ConnectIntegrationThenRunAsync(task)));
    }

    /// <summary>The card's "Yes": make sure Codex is logged into Composio, then hand the agent the connection task.</summary>
    private Task ConnectIntegrationThenRunAsync(string task) =>
        AppConnectFlow.ConnectIntegrationThenRunAsync(openClickyAgentClient, task,
            submitTextToAgent: text => services.MainThread.Post(() => SubmitTextToAgent(text)),
            setActivityText: text => services.MainThread.Post(() => AgentActivityText = text));

    private void AnswerConnectCard(Companion.AppConnectAnswer answer) => AppConnectPromptController.Answer(answer switch
    {
        Companion.AppConnectAnswer.Yes => Agent.AppConnectAnswer.Yes,
        Companion.AppConnectAnswer.No => Agent.AppConnectAnswer.No,
        Companion.AppConnectAnswer.NotNow => Agent.AppConnectAnswer.NotNow,
        Companion.AppConnectAnswer.OpenSettings => Agent.AppConnectAnswer.OpenSettings,
        _ => Agent.AppConnectAnswer.DismissNotice,
    });

    /// <summary>The controller's card as the island draws it.</summary>
    public static Companion.AppConnectPrompt? IslandConnectCard(Agent.AppConnectPrompt? prompt) => prompt is null
        ? null
        : new Companion.AppConnectPrompt(prompt.SkillId, prompt.AppName, prompt.Integration, prompt.FrontExecutablePath, prompt.ExamplePrompts,
            prompt.Stage == Agent.AppConnectPromptStage.ComposioMissing ? Companion.AppConnectPromptStage.ComposioMissing : Companion.AppConnectPromptStage.Ask);

    // MARK: - Teaching a skill

    /// <summary>
    /// Drafts a SKILL.md for "create a skill": the own OpenAI key with the backend's drafting prompt,
    /// else the backend's /skills/create on the account.
    /// </summary>
    private static async Task<string> DraftSkillMarkdownAsync(SkillDraftRequest request, CancellationToken cancellationToken)
    {
        if (AccountCapabilities.Current().SkillTeachingUnavailableHint is { } hint) throw SkillLibraryException.NeedsOwnKey(hint);
        var route = ProviderRouter.Current(ProviderCapability.SkillDrafting);
        if (route.UnavailableSentence is { } sentence) throw SkillLibraryException.NeedsOwnKey(sentence);
        var capabilities = new List<string>();
        if (OpenClickyConfiguration.ActiveComposioMcpUrl is not null) capabilities.Add("composio");
        if (OpenClickyConfiguration.ResolvedCuaDriverBin is not null) capabilities.Add("computer-use");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        if (route.Kind == ProviderRouteKind.OpenAIDirect && ProviderRouter.CurrentCredentials().OpenAIKey is { } key)
        {
            using var openAIRequest = DirectProviderRequests.OpenAISkillDraft(key, request.Request, request.KnownSkills.Concat(capabilities).ToList());
            using var openAIResponse = await HttpClients.Shared.SendAsync(openAIRequest, timeout.Token);
            var status = (int)openAIResponse.StatusCode;
            if (status is < 200 or >= 300) throw SkillLibraryException.Backend($"openai answered {status}. check your openai key in shell.json.");
            var json = JsonNode.Parse(await openAIResponse.Content.ReadAsStringAsync(timeout.Token));
            var text = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(text)) throw SkillLibraryException.Backend("the model did not return a skill file.");
            return text;
        }
        using var backendRequest = new HttpRequestMessage(HttpMethod.Post, $"{OpenClickyConfiguration.BackendBaseUrl}/skills/create")
        {
            Content = new StringContent(SkillLibraryStore.BackendCreateRequestBody(request.Request, capabilities), Encoding.UTF8, "application/json"),
        };
        OpenClickyConfiguration.Authorize(backendRequest);
        using var response = await BackendSignIn.SendAsync(backendRequest, sent => HttpClients.Shared.SendAsync(sent, timeout.Token));
        return SkillLibraryStore.MarkdownFromBackendResponse((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(timeout.Token));
    }
}
