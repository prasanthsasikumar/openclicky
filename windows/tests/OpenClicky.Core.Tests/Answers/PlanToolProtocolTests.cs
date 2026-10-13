// Port of macos/OpenClicky/OpenClickyTests/PlanToolProtocolTests.swift.
// The Swift tests build their tools from MacAction.anthropicToolDefinitions(); the Actions folder is
// not ported yet, so the six quick-action tools are stood in by name and a small schema here.
// Not ported: the MacAction.parse round trip (Actions folder). offersQuickActionsToAskLane is in
// AskLaneTests.cs (AskLanePrompts.OffersQuickActionsToAskLane).

using OpenClicky.Core.Answers;

namespace OpenClicky.Core.Tests.Answers;

public class PlanToolProtocolTests
{
    private static readonly IReadOnlyList<AnswerRequest.Tool> QuickActionTools = new[]
    {
        new AnswerRequest.Tool("open_app", "Open an app on the person's PC.", """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}"""),
        new AnswerRequest.Tool("open_url", "Open a web address.", """{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}"""),
        new AnswerRequest.Tool("create_folder", "Make a folder.", """{"type":"object","properties":{"name":{"type":"string"},"location":{"type":"string","enum":["desktop","documents","downloads"]}},"required":["name"]}"""),
        new AnswerRequest.Tool("reveal_in_explorer", "Show a file in File Explorer.", """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
        new AnswerRequest.Tool("set_volume", "Set the volume.", """{"type":"object","properties":{"level":{"type":"integer"}},"required":["level"]}"""),
        new AnswerRequest.Tool("media_control", "Play, pause or skip.", """{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}"""),
    };
    private static readonly IReadOnlySet<string> QuickActionToolNames = QuickActionTools.Select(tool => tool.Name).ToHashSet();

    private static (string Text, IReadOnlyList<ClaudeToolCall> ToolCalls) Parse(string answerText) =>
        PlanToolProtocol.Parse(answerText, QuickActionToolNames);

    /// <summary>Streams `answerText` through a filter in pieces of `pieceLength` characters and joins what comes out.</summary>
    private static string Filtered(string answerText, int pieceLength)
    {
        var filter = new PlanActionLineFilter();
        var output = "";
        for (var start = 0; start < answerText.Length; start += pieceLength)
        {
            output += filter.Consume(answerText.Substring(start, Math.Min(pieceLength, answerText.Length - start)));
        }
        return output + filter.Finish();
    }

    private static Dictionary<string, string> Arguments(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value);

    // MARK: the prompt block

    [Fact]
    public void InstructionBlockNamesEveryToolWithItsDescriptionAndSchema()
    {
        var block = PlanToolProtocol.InstructionBlock(QuickActionTools);
        Assert.Equal(6, QuickActionTools.Count);
        foreach (var tool in QuickActionTools)
        {
            Assert.Contains($"- {tool.Name}: {tool.Description}", block);
            Assert.Contains(PlanToolProtocol.CompactSchemaText(tool.InputSchemaJson), block);
        }
        Assert.Contains("[ACTION: <action name> <arguments as one JSON object>]", block);
        Assert.Contains("at most 2", block);
        Assert.DoesNotContain("—", block);
    }

    [Fact]
    public void InstructionBlockIsEmptyWithoutToolsAndTheSystemPromptStaysAsItIs()
    {
        Assert.Equal("", PlanToolProtocol.InstructionBlock(Array.Empty<AnswerRequest.Tool>()));
        Assert.Equal("answer in lowercase", PlanToolProtocol.SystemPrompt("answer in lowercase", Array.Empty<AnswerRequest.Tool>()));
        var withTools = PlanToolProtocol.SystemPrompt("answer in lowercase", QuickActionTools);
        Assert.StartsWith("answer in lowercase\n\nQuick actions:", withTools);
    }

    [Fact]
    public void CompactSchemaHasSortedKeysAndNoSpaces()
    {
        var schemaJson = """{ "type": "object", "required": ["name"], "properties": { "name": { "type": "string" } } }""";
        Assert.Equal("""{"properties":{"name":{"type":"string"}},"required":["name"],"type":"object"}""", PlanToolProtocol.CompactSchemaText(schemaJson));
    }

    [Fact]
    public void ASchemaThatDoesNotParseIsPassedAsItIs()
    {
        Assert.Equal("not json", PlanToolProtocol.CompactSchemaText("not json"));
    }

    // MARK: the parser

    [Fact]
    public void OneActionBecomesAToolCallAndLeavesTheSpokenWords()
    {
        var result = Parse("making that folder on your desktop now.\n[ACTION: create_folder {\"name\":\"test-plan-actions\",\"location\":\"desktop\"}]");
        Assert.Equal("making that folder on your desktop now.", result.Text);
        Assert.Equal(new[] { new ClaudeToolCall("plan-action-1", "create_folder", Arguments(("name", "test-plan-actions"), ("location", "desktop"))) }, result.ToolCalls);
    }

    [Fact]
    public void TwoActionsAreKeptInOrder()
    {
        var result = Parse("opening both.\n[ACTION: open_app {\"name\":\"Spotify\"}]\n[ACTION: set_volume {\"level\":40}]");
        Assert.Equal("opening both.", result.Text);
        Assert.Equal(new[] { "open_app", "set_volume" }, result.ToolCalls.Select(call => call.Name));
        Assert.Equal(Arguments(("level", "40")), result.ToolCalls[^1].Arguments);
        Assert.Equal(new[] { "plan-action-1", "plan-action-2" }, result.ToolCalls.Select(call => call.Id));
    }

    [Fact]
    public void AThirdActionIsDroppedButItsTagIsStillRemoved()
    {
        var result = Parse("on it.\n[ACTION: open_app {\"name\":\"Safari\"}]\n[ACTION: open_app {\"name\":\"Notes\"}]\n[ACTION: open_app {\"name\":\"Mail\"}]");
        Assert.Equal(new[] { "Safari", "Notes" }, result.ToolCalls.Select(call => call.Arguments["name"]));
        Assert.Equal("on it.", result.Text);
    }

    [Fact]
    public void AFencedActionIsReadAndTheFenceGoes()
    {
        var result = Parse("opening safari.\n```json\n[ACTION: open_app {\"name\":\"Safari\"}]\n```");
        Assert.Equal("opening safari.", result.Text);
        Assert.Equal(new[] { "open_app" }, result.ToolCalls.Select(call => call.Name));
    }

    [Fact]
    public void ToleratesNoColonNoSpaceTrailingPunctuationAndAMissingBracket()
    {
        Assert.Equal(Arguments(("name", "Safari")), Parse("""[ACTION:open_app{"name":"Safari"}]""").ToolCalls[0].Arguments);
        Assert.Equal("open_app", Parse("""[action open_app {"name":"Safari"}]""").ToolCalls[0].Name);
        var punctuated = Parse("""done. [ACTION: open_app {"name":"Safari"}].""");
        Assert.Equal("done.", punctuated.Text);
        Assert.Single(punctuated.ToolCalls);
        var unclosed = Parse("opening it.\n[ACTION: open_app {\"name\":\"Safari\"}\nanything else?");
        Assert.Equal(Arguments(("name", "Safari")), unclosed.ToolCalls[0].Arguments);
        Assert.Equal("opening it.\n\nanything else?", unclosed.Text);
    }

    [Fact]
    public void ABracketInsideTheJsonDoesNotEndTheTag()
    {
        var result = Parse("""[ACTION: open_url {"url":"https://example.com/a]b"}] there you go""");
        Assert.Equal(Arguments(("url", "https://example.com/a]b")), result.ToolCalls[0].Arguments);
        Assert.Equal("there you go", result.Text);
    }

    [Fact]
    public void BadJsonAndUnknownNamesAreDroppedAndTheirTagsRemoved()
    {
        var badJson = Parse("sure.\n[ACTION: open_app {name: Safari}]");
        Assert.Empty(badJson.ToolCalls);
        Assert.Equal("sure.", badJson.Text);
        var unknownName = Parse("sure.\n[ACTION: delete_everything {\"path\":\"/\"}]");
        Assert.Empty(unknownName.ToolCalls);
        Assert.Equal("sure.", unknownName.Text);
        var notAnObject = Parse("""[ACTION: open_app "Safari"]""");
        Assert.Empty(notAnObject.ToolCalls);
        Assert.Equal("", notAnObject.Text);
    }

    [Fact]
    public void AnAnswerWithoutActionsIsUnchangedApartFromTrimming()
    {
        var result = Parse("  [POINT:120,40:save button] the save button is up there.  ");
        Assert.Equal("[POINT:120,40:save button] the save button is up there.", result.Text);
        Assert.Empty(result.ToolCalls);
    }

    // MARK: the streaming filter

    [Fact]
    public void FilterHoldsTheTagBackAtEveryPieceSize()
    {
        var answer = "[POINT:none] making it now.\n[ACTION: create_folder {\"name\":\"x\"}]\n";
        foreach (var pieceLength in new[] { 1, 2, 3, 5, 8, 100 })
        {
            Assert.Equal("[POINT:none] making it now.\n\n", Filtered(answer, pieceLength));
        }
    }

    [Fact]
    public void FilterLetsOrdinaryBracketsAndBackticksThrough()
    {
        var answer = "press [cmd] and [a]; the `ls` command lists files.\n[[ACTION: open_app {\"name\":\"Finder\"}]";
        foreach (var pieceLength in new[] { 1, 4, 100 })
        {
            Assert.Equal("press [cmd] and [a]; the `ls` command lists files.\n[", Filtered(answer, pieceLength));
        }
    }

    [Fact]
    public void FilterDropsFenceLinesTrailingPunctuationAndAnUnclosedTag()
    {
        Assert.Equal("ok.\n\n", Filtered("ok.\n```json\n[ACTION: open_app {\"name\":\"Safari\"}]\n```\n", 3));
        Assert.Equal("done  bye", Filtered("done [ACTION: open_app {\"name\":\"Safari\"}]. bye", 2));
        Assert.Equal("ok.\n", Filtered("ok.\n[ACTION: open_app {\"name\":\"Safari\"}", 4));
        Assert.Equal("ok.\n\nmore", Filtered("ok.\n[ACTION: open_app {\"name\":\"Safari\"}\nmore", 4));
    }

    [Fact]
    public void FilterReleasesAHeldStartThatNeverBecameATag()
    {
        Assert.Equal("look at [ACT", Filtered("look at [ACT", 1));
    }
}
