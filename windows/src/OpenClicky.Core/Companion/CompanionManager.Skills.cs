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
}
