// Port of macos/OpenClicky/OpenClickyTests/AppConnectPromptTests.swift: the "Connect <app> to
// OpenClicky" card's example prompts read from a skill body, and the remembered answers
// (Yes / No / Not now) that decide whether the card opens again. Not ported: the SwiftUI layout
// test (the card is drawn by OpenClicky.Windows). Added: "not now" per process, the card's lines,
// the front-app conversion, and the connect flow through the agent client.

using OpenClicky.Core.Agent;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Skills;

namespace OpenClicky.Core.Tests.Agent;

public class AppConnectPromptTests
{
    private const string Body = """
        ## Layout
        The header runs across the top.

        ## Common tasks
        - Play/pause: K or space; skip 10 seconds: J and L.
        - Captions: the CC button at the bottom-right of the player, or press C.
        - A task with no colon at all

        ## Pointing hints
        - "How do I change the speed / quality?" — point at the gear at the bottom-right corner of the player.
        - “How do I turn on subtitles?” — point at the CC button.
        - "how do i change the speed / quality?" — a duplicate in another case.
        - A hint without a question.
        """;

    private static SkillFile YouTube => SkillFile.Parse(
        $"---\nname: YouTube\ndescription: d\nsites: [youtube.com]\nintegration: youtube\nsurfaces: [talk]\n---\n{Body}", "youtube")!;

    private static SkillFile Terminal => SkillFile.Parse(
        $"---\nname: Terminal\ndescription: d\napps: [com.apple.Terminal, WindowsTerminal.exe]\nsurfaces: [talk]\n---\n{Body}", "terminal")!;

    private static readonly FrontAppContext ChromeOnYouTube = new()
    {
        BundleIdentifier = "com.google.Chrome",
        ProcessName = "chrome.exe",
        AppName = "Google Chrome",
        Url = new Uri("https://www.youtube.com/watch?v=x"),
        IsBrowser = true,
    };

    [Fact]
    public void ExtractsQuotedPointingHintsThenCommonTaskLeads()
    {
        Assert.Equal(
        [
            "How do I change the speed / quality?",
            "How do I turn on subtitles?",
            "Play/pause",
            "Captions",
        ], AppSkillExamples.Extract(Body));
    }

    [Fact]
    public void LimitsAndHandlesBodiesWithoutSections()
    {
        Assert.Equal(["How do I change the speed / quality?"], AppSkillExamples.Extract(Body, limit: 1));
        Assert.Empty(AppSkillExamples.Extract("Just prose, no bullets."));
        Assert.Equal(["Compose"], AppSkillExamples.Extract("## Common tasks\r\n- Compose: the pencil.\r\n"));
    }

    [Fact]
    public void PromptOpensOncePerSkillAndRemembersAnswers()
    {
        var preferencesPath = Path.Combine(Path.GetTempPath(), "app-connect-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var youtube = YouTube;
            var terminal = Terminal;
            var controller = new AppConnectPromptController(new Preferences(preferencesPath));
            var prompt = controller.Prompt(ChromeOnYouTube, [youtube]);
            Assert.NotNull(prompt);
            Assert.Equal("youtube", prompt.SkillId);
            Assert.Equal("YouTube", prompt.AppName);
            Assert.Equal("com.google.Chrome", prompt.FrontBundleIdentifier);
            Assert.Equal("chrome.exe", prompt.FrontProcessName);
            Assert.Equal("youtube", prompt.Integration);
            Assert.Equal("youtube", youtube.Integration);

            // An app skill without an `integration` (nothing to connect) never gets the card.
            Assert.Null(terminal.Integration);
            var terminalFront = new FrontAppContext { ProcessName = "WindowsTerminal.exe", AppName = "Terminal" };
            Assert.Null(controller.Prompt(terminalFront, [youtube, terminal]));
            Assert.Equal("How do I change the speed / quality?", prompt.ExamplePrompts[0]);

            // A site the skills do not cover never prompts.
            Assert.Null(controller.Prompt(ChromeOnYouTube with { Url = new Uri("https://example.com") }, [youtube]));

            // "Yes" without Composio turns into the notice; with Composio it hands the agent the connection task.
            controller.IsComposioConfigured = () => false;
            Assert.Equal(AppConnectPromptStage.ComposioMissing, controller.Apply(AppConnectAnswer.Yes, prompt)?.Stage);
            string? submittedTask = null;
            controller.IsComposioConfigured = () => true;
            controller.SubmitToAgent = task => submittedTask = task;
            Assert.Null(controller.Apply(AppConnectAnswer.Yes, prompt));
            Assert.Contains("toolkit \"youtube\"", submittedTask);
            Assert.Contains("YouTube", submittedTask);

            // "No" is remembered across launches and keeps the skill out of the talk prompts.
            controller.Apply(AppConnectAnswer.No, prompt);
            Assert.Equal(["youtube"], controller.DeclinedSkillIds);
            Assert.Null(controller.Prompt(ChromeOnYouTube, [youtube]));

            var relaunched = new AppConnectPromptController(new Preferences(preferencesPath));
            Assert.Equal(["youtube"], relaunched.DeclinedSkillIds);
            Assert.Null(relaunched.Prompt(ChromeOnYouTube, [youtube]));
            // Stored the Mac way: skill id → "connected" | "declined".
            Assert.Equal("declined", relaunched.Decisions["youtube"] == AppConnectDecision.Declined ? "declined" : "connected");
            Assert.Equal("""{"youtube":"declined"}""", new Preferences(preferencesPath).Object(AppConnectPromptController.DecisionsDefaultsKey)!.ToJsonString());
        }
        finally
        {
            File.Delete(preferencesPath);
        }
    }

    [Fact]
    public void NotNowHidesTheCardUntilTheAppIsLaunchedAgain()
    {
        var frontProcessIdentifier = 100;
        var controller = new AppConnectPromptController(Preferences.InMemory()) { FrontProcessIdentifier = () => frontProcessIdentifier };
        var prompt = controller.Prompt(ChromeOnYouTube, [YouTube])!;
        Assert.Null(controller.Apply(AppConnectAnswer.NotNow, prompt));
        Assert.Empty(controller.Decisions);
        Assert.Null(controller.Prompt(ChromeOnYouTube, [YouTube]));

        // The browser was closed and started again: a new process asks again.
        frontProcessIdentifier = 200;
        Assert.NotNull(controller.Prompt(ChromeOnYouTube, [YouTube]));
        frontProcessIdentifier = 100;
        Assert.NotNull(controller.Prompt(ChromeOnYouTube, [YouTube]));
    }

    [Fact]
    public void TheCardAnswersMoveTheCurrentPrompt()
    {
        var revealed = false;
        var controller = new AppConnectPromptController(Preferences.InMemory())
        {
            IsComposioConfigured = () => false,
            RevealSettingsFile = () => revealed = true,
        };
        var changed = new List<string?>();
        controller.PropertyChanged += (_, arguments) => changed.Add(arguments.PropertyName);

        controller.Answer(AppConnectAnswer.Yes);
        Assert.Null(controller.CurrentPrompt);

        // The poll would set it; set it the same way through Apply's result.
        var prompt = controller.Prompt(ChromeOnYouTube, [YouTube])!;
        controller.CurrentPrompt = prompt;
        controller.Answer(AppConnectAnswer.Yes);
        Assert.Equal(AppConnectPromptStage.ComposioMissing, controller.CurrentPrompt?.Stage);
        controller.Answer(AppConnectAnswer.OpenSettings);
        Assert.Null(controller.CurrentPrompt);
        Assert.True(revealed);
        Assert.Contains(nameof(AppConnectPromptController.CurrentPrompt), changed);
        Assert.Contains(nameof(AppConnectPromptController.DeclinedSkillIds), changed);
    }

    [Fact]
    public void TheCardSaysWhatItMeans()
    {
        var prompt = new AppConnectPrompt
        {
            SkillId = "gmail", AppName = "Gmail", Integration = "gmail",
            ExamplePrompts = ["How do I write an email?", "Where is search?", "How do I filter / advanced search?", "Where are my drafts?", "Fifth"],
        };
        Assert.Equal("connect gmail to openclicky?", prompt.Headline);
        Assert.Equal("clicky could act in your gmail account when you ask.", prompt.Detail);
        Assert.Equal("never for gmail", prompt.NeverButtonTitle);
        Assert.Equal("don't ask again; Gmail's skill is left out of what clicky knows", prompt.NeverButtonHelp);
        Assert.Equal(4, prompt.ExampleChips.Count);
        Assert.Equal("“How do I write an email?”", prompt.ExampleChips[0]);

        var notice = prompt with { Stage = AppConnectPromptStage.ComposioMissing };
        Assert.Equal("gmail skill is on", notice.Headline);
        Assert.Equal("account actions need composio: set COMPOSIO_MCP_URL in shell.json.", notice.Detail);
        Assert.NotEqual(prompt, notice);
        Assert.Equal(prompt, prompt with { ExamplePrompts = prompt.ExamplePrompts.ToList() });
    }

    [Fact]
    public void TheConnectionTaskNamesTheToolkitAndTheComposioSteps()
    {
        var task = AppConnectPromptController.ConnectionTask(new AppConnectPrompt { SkillId = "youtube", AppName = "YouTube", Integration = "youtube" });
        Assert.StartsWith("Connect my YouTube account through the composio MCP tools (toolkit \"youtube\"). ", task);
        Assert.Contains("COMPOSIO_MANAGE_CONNECTIONS", task);
        Assert.Contains("COMPOSIO_WAIT_FOR_CONNECTIONS", task);
        Assert.EndsWith("what you can now do in YouTube.", task);
    }

    [Fact]
    public void TheObserversFrontAppBecomesTheMatchersContext()
    {
        var context = AppConnectPromptController.Context(new FrontAppInfo
        {
            ProcessName = "msedge.exe", DisplayName = "Microsoft Edge", Url = "www.youtube.com/watch?v=x", WindowTitle = "YouTube", IsBrowser = true,
        });
        Assert.Equal("www.youtube.com", context.Url?.Host);
        Assert.Equal("msedge.exe", context.ProcessName);
        Assert.True(context.IsBrowser);
        Assert.Equal("youtube", AppSkillMatcher.Match(context, [YouTube])?.Id);
        Assert.Null(AppConnectPromptController.Context(new FrontAppInfo { ProcessName = "notepad.exe" }).Url);
    }

    [Fact]
    public async Task ConnectingLogsIntoComposioFirstWhenItIsNotLoggedIn()
    {
        var runner = new ScriptedProcessRunner
        {
            Script = request => request.Arguments[1] == "status"
                ? (["""{"configured":true,"composio":null,"loggedIn":false}"""], [], 0)
                : ([], [], 0),
        };
        var client = new OpenClickyAgentClient(runner, () => ["openclicky"], () => new Dictionary<string, string>(),
                                               () => Path.GetTempPath(), () => null);
        var activity = new List<string?>();
        string? submitted = null;
        await AppConnectFlow.ConnectIntegrationThenRunAsync(client, "connect it", task => submitted = task, activity.Add);
        Assert.Equal(["integrations status --json", "integrations login composio"], runner.Launched.Select(request => string.Join(" ", request.Arguments)));
        Assert.Equal([AppConnectFlow.SigningInActivityText, null], activity);
        Assert.Equal("connect it", submitted);
    }

    [Fact]
    public async Task AFailedStatusCheckStillHandsTheTaskToTheAgent()
    {
        var runner = new ScriptedProcessRunner { Script = _ => ([], ["codex not found"], 1) };
        var client = new OpenClickyAgentClient(runner, () => ["openclicky"], () => new Dictionary<string, string>(),
                                               () => Path.GetTempPath(), () => null);
        string? submitted = null;
        await AppConnectFlow.ConnectIntegrationThenRunAsync(client, "connect it", task => submitted = task);
        Assert.Equal("connect it", submitted);
        Assert.Single(runner.Launched);
    }
}
