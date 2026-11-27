import Testing
@testable import OpenClicky

@Suite("Command center status")
struct CommandCenterStatusTests {
    @Test func idleIsReady() {
        let status = CommandCenterStatus.current(voiceState: .idle, agentActivity: nil)
        #expect(status.title == "Ready")
        #expect(status.symbol == "checkmark.circle.fill")
    }

    @Test func agentWorkTakesPriorityOverVoiceState() {
        let status = CommandCenterStatus.current(voiceState: .listening, agentActivity: "running tests")
        #expect(status.title == "Working")
        #expect(status.detail == "running tests")
    }

    @Test func voiceStatesHaveSpecificLabels() {
        #expect(CommandCenterStatus.current(voiceState: .listening, agentActivity: nil).title == "Listening")
        #expect(CommandCenterStatus.current(voiceState: .processing, agentActivity: nil).title == "Thinking")
        #expect(CommandCenterStatus.current(voiceState: .responding, agentActivity: nil).title == "Speaking")
    }
}
