import Foundation
import Testing
@testable import OpenClicky

struct ClaudeToolStreamTests {
    @Test func anthropicToolsMirrorTheRealtimeOnes() {
        let tools = RealtimeVoiceClient.anthropicToolDefinitions()
        let names = tools.compactMap { $0["name"] as? String }
        #expect(names == ["open_app", "open_url", "create_folder", "reveal_in_finder", "set_volume", "media_control"])
        #expect(tools.allSatisfy { $0["input_schema"] is [String: Any] && $0["type"] == nil })
    }

    @Test func parsesAToolCallStreamedInPieces() {
        let lines = [
            #"data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"create_folder","input":{}}}"#,
            #"data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"name\": \"Launch"}}"#,
            #"data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":" Ideas\", \"location\": \"desktop\"}"}}"#,
            #"data: {"type":"content_block_stop","index":1}"#,
        ]
        let calls = ClaudeAPI.parseToolCalls(fromSSELines: lines)
        #expect(calls == [ClaudeToolCall(id: "toolu_1", name: "create_folder", arguments: ["name": "Launch Ideas", "location": "desktop"])])
    }

    @Test func numbersBecomeStringsAndParseStillAccepts() {
        let lines = [
            #"data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"t","name":"set_volume","input":{}}}"#,
            #"data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"level\": 30}"}}"#,
            #"data: {"type":"content_block_stop","index":0}"#,
        ]
        let call = ClaudeAPI.parseToolCalls(fromSSELines: lines)[0]
        #expect(call.arguments["level"] == "30")
        #expect(MacAction.parse(toolName: call.name, arguments: call.arguments) == .action(.setVolume(level: 30)))
    }

    @Test func invalidJSONYieldsNoCall() {
        let lines = [
            #"data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"t","name":"open_app","input":{}}}"#,
            #"data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"name\": "}}"#,
            #"data: {"type":"content_block_stop","index":0}"#,
        ]
        #expect(ClaudeAPI.parseToolCalls(fromSSELines: lines).isEmpty)
    }
}
