//
//  PlanToolProtocolTests.swift
//  OpenClickyTests
//
//  The [ACTION: …] text protocol the plan CLIs use for quick actions: the prompt block, the parser
//  that turns finished answers into tool calls, and the filter that keeps tags out of streamed text.
//

import Foundation
import Testing
@testable import OpenClicky

private let quickActionTools = MacAction.anthropicToolDefinitions().compactMap(ClaudeAPI.answerTool(fromAnthropicTool:))
private let quickActionToolNames = Set(quickActionTools.map(\.name))

private func parse(_ answerText: String) -> (text: String, toolCalls: [ClaudeToolCall]) {
    PlanToolProtocol.parse(answerText: answerText, knownToolNames: quickActionToolNames)
}

/// Streams `answerText` through a filter in pieces of `pieceLength` characters and joins what comes out.
private func filtered(_ answerText: String, pieceLength: Int) -> String {
    let filter = PlanActionLineFilter()
    var output = ""
    var remainingText = Substring(answerText)
    while !remainingText.isEmpty {
        output += filter.consume(String(remainingText.prefix(pieceLength)))
        remainingText = remainingText.dropFirst(pieceLength)
    }
    return output + filter.finish()
}

struct PlanToolProtocolTests {

    // MARK: the prompt block

    @Test func instructionBlockNamesEveryToolWithItsDescriptionAndSchema() {
        let block = PlanToolProtocol.instructionBlock(for: quickActionTools)
        #expect(quickActionTools.count == 17)
        for tool in quickActionTools {
            #expect(block.contains("- \(tool.name): \(tool.description)"))
            #expect(block.contains(PlanToolProtocol.compactSchemaText(from: tool.inputSchemaJSON)))
        }
        #expect(block.contains("[ACTION: <action name> <arguments as one JSON object>]"))
        #expect(block.contains("at most 2"))
        #expect(!block.contains("\u{2014}"))
    }

    @Test func instructionBlockIsEmptyWithoutToolsAndTheSystemPromptStaysAsItIs() {
        #expect(PlanToolProtocol.instructionBlock(for: []) == "")
        #expect(PlanToolProtocol.systemPrompt("answer in lowercase", offering: []) == "answer in lowercase")
        let withTools = PlanToolProtocol.systemPrompt("answer in lowercase", offering: quickActionTools)
        #expect(withTools.hasPrefix("answer in lowercase\n\nQuick actions:"))
    }

    @Test func compactSchemaHasSortedKeysAndNoSpaces() {
        let schemaJSON = Data(#"{ "type": "object", "required": ["name"], "properties": { "name": { "type": "string" } } }"#.utf8)
        #expect(PlanToolProtocol.compactSchemaText(from: schemaJSON) == #"{"properties":{"name":{"type":"string"}},"required":["name"],"type":"object"}"#)
    }

    // MARK: the parser

    @Test func oneActionBecomesAToolCallAndLeavesTheSpokenWords() throws {
        let result = parse("making that folder on your desktop now.\n[ACTION: create_folder {\"name\":\"test-plan-actions\",\"location\":\"desktop\"}]")
        #expect(result.text == "making that folder on your desktop now.")
        #expect(result.toolCalls == [ClaudeToolCall(id: "plan-action-1", name: "create_folder", arguments: ["name": "test-plan-actions", "location": "desktop"])])
        // The call reads exactly as MacAction expects.
        let toolCall = try #require(result.toolCalls.first)
        guard case .action = MacAction.parse(toolName: toolCall.name, arguments: toolCall.arguments) else {
            Issue.record("create_folder did not parse as a Mac action")
            return
        }
    }

    @Test func twoActionsAreKeptInOrder() {
        let result = parse("opening both.\n[ACTION: open_app {\"name\":\"Spotify\"}]\n[ACTION: set_volume {\"level\":40}]")
        #expect(result.text == "opening both.")
        #expect(result.toolCalls.map(\.name) == ["open_app", "set_volume"])
        #expect(result.toolCalls.last?.arguments == ["level": "40"])
        #expect(result.toolCalls.map(\.id) == ["plan-action-1", "plan-action-2"])
    }

    @Test func aThirdActionIsDroppedButItsTagIsStillRemoved() {
        let result = parse("""
        on it.
        [ACTION: open_app {"name":"Safari"}]
        [ACTION: open_app {"name":"Notes"}]
        [ACTION: open_app {"name":"Mail"}]
        """)
        #expect(result.toolCalls.map { $0.arguments["name"] } == ["Safari", "Notes"])
        #expect(result.text == "on it.")
    }

    @Test func aFencedActionIsReadAndTheFenceGoes() {
        let result = parse("opening safari.\n```json\n[ACTION: open_app {\"name\":\"Safari\"}]\n```")
        #expect(result.text == "opening safari.")
        #expect(result.toolCalls.map(\.name) == ["open_app"])
    }

    @Test func toleratesNoColonNoSpaceTrailingPunctuationAndAMissingBracket() {
        #expect(parse(#"[ACTION:open_app{"name":"Safari"}]"#).toolCalls.first?.arguments == ["name": "Safari"])
        #expect(parse(#"[action open_app {"name":"Safari"}]"#).toolCalls.first?.name == "open_app")
        let punctuated = parse(#"done. [ACTION: open_app {"name":"Safari"}]."#)
        #expect(punctuated.text == "done.")
        #expect(punctuated.toolCalls.count == 1)
        let unclosed = parse("opening it.\n[ACTION: open_app {\"name\":\"Safari\"}\nanything else?")
        #expect(unclosed.toolCalls.first?.arguments == ["name": "Safari"])
        #expect(unclosed.text == "opening it.\n\nanything else?")
    }

    @Test func aBracketInsideTheJSONDoesNotEndTheTag() {
        let result = parse(#"[ACTION: open_url {"url":"https://example.com/a]b"}] there you go"#)
        #expect(result.toolCalls.first?.arguments == ["url": "https://example.com/a]b"])
        #expect(result.text == "there you go")
    }

    @Test func badJSONAndUnknownNamesAreDroppedAndTheirTagsRemoved() {
        let badJSON = parse("sure.\n[ACTION: open_app {name: Safari}]")
        #expect(badJSON.toolCalls.isEmpty)
        #expect(badJSON.text == "sure.")
        let unknownName = parse("sure.\n[ACTION: delete_everything {\"path\":\"/\"}]")
        #expect(unknownName.toolCalls.isEmpty)
        #expect(unknownName.text == "sure.")
        let notAnObject = parse(#"[ACTION: open_app "Safari"]"#)
        #expect(notAnObject.toolCalls.isEmpty)
        #expect(notAnObject.text == "")
    }

    @Test func anAnswerWithoutActionsIsUnchangedApartFromTrimming() {
        let result = parse("  [POINT:120,40:save button] the save button is up there.  ")
        #expect(result.text == "[POINT:120,40:save button] the save button is up there.")
        #expect(result.toolCalls.isEmpty)
    }

    // MARK: the streaming filter

    @Test func filterHoldsTheTagBackAtEveryPieceSize() {
        let answer = "[POINT:none] making it now.\n[ACTION: create_folder {\"name\":\"x\"}]\n"
        for pieceLength in [1, 2, 3, 5, 8, 100] {
            #expect(filtered(answer, pieceLength: pieceLength) == "[POINT:none] making it now.\n\n", "piece length \(pieceLength)")
        }
    }

    @Test func filterLetsOrdinaryBracketsAndBackticksThrough() {
        let answer = "press [cmd] and [a]; the `ls` command lists files.\n[[ACTION: open_app {\"name\":\"Finder\"}]"
        for pieceLength in [1, 4, 100] {
            #expect(filtered(answer, pieceLength: pieceLength) == "press [cmd] and [a]; the `ls` command lists files.\n[", "piece length \(pieceLength)")
        }
    }

    @Test func filterDropsFenceLinesTrailingPunctuationAndAnUnclosedTag() {
        #expect(filtered("ok.\n```json\n[ACTION: open_app {\"name\":\"Safari\"}]\n```\n", pieceLength: 3) == "ok.\n\n")
        #expect(filtered("done [ACTION: open_app {\"name\":\"Safari\"}]. bye", pieceLength: 2) == "done  bye")
        #expect(filtered("ok.\n[ACTION: open_app {\"name\":\"Safari\"}", pieceLength: 4) == "ok.\n")
        #expect(filtered("ok.\n[ACTION: open_app {\"name\":\"Safari\"}\nmore", pieceLength: 4) == "ok.\n\nmore")
    }

    @Test func filterReleasesAHeldStartThatNeverBecameATag() {
        #expect(filtered("look at [ACT", pieceLength: 1) == "look at [ACT")
    }

    // MARK: the ask lane

    @MainActor
    @Test func theAskLaneOffersQuickActionsWhenTheAgentLaneCantOrAPlanAnswers() {
        #expect(CompanionManager.offersQuickActionsToAskLane(agentLaneRuns: false, answeringSource: .openClickyAccount))
        #expect(CompanionManager.offersQuickActionsToAskLane(agentLaneRuns: false, answeringSource: .openAI))
        #expect(CompanionManager.offersQuickActionsToAskLane(agentLaneRuns: true, answeringSource: .claudeCodePlan))
        #expect(CompanionManager.offersQuickActionsToAskLane(agentLaneRuns: true, answeringSource: .codexPlan))
        #expect(!CompanionManager.offersQuickActionsToAskLane(agentLaneRuns: true, answeringSource: .anthropic))
    }
}
