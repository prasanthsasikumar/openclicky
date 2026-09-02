import SwiftUI

struct PanelView: View {
    @ObservedObject var runner: AgentRunner
    var onClose: () -> Void
    @State private var input = ""
    @FocusState private var focused: Bool

    var body: some View {
        VStack(spacing: 8) {
            HStack(spacing: 8) {
                Image(systemName: runner.isRunning ? "circle.dotted" : "waveform.circle.fill")
                    .foregroundStyle(runner.isRunning ? .orange : .accentColor)
                    .symbolEffect(.pulse, isActive: runner.isRunning)
                Text(runner.isRunning ? "Working…" : "OpenClicky").font(.headline)
                Spacer()
                Toggle("Follow thread", isOn: $runner.followThread).toggleStyle(.checkbox).font(.caption)
                Button { runner.screenshotNext.toggle() } label: {
                    Image(systemName: runner.screenshotNext ? "camera.fill" : "camera")
                }.help("Attach a screenshot to the next request").buttonStyle(.borderless)
                Button { runner.voice() } label: { Image(systemName: "mic.fill") }
                    .help("Record \(runner.settings.voiceSeconds)s and run").buttonStyle(.borderless).disabled(runner.isRunning)
                Button { runner.talk() } label: { Image(systemName: "waveform") }
                    .help("Start an always-on voice conversation (OpenAI Realtime)").buttonStyle(.borderless).disabled(runner.isRunning)
                if runner.isRunning {
                    Button("Stop") { runner.cancel() }.controlSize(.small)
                }
                Button { onClose() } label: { Image(systemName: "xmark") }.buttonStyle(.borderless)
            }

            ScrollViewReader { proxy in
                ScrollView {
                    Text(runner.transcript)
                        .font(.system(.body, design: .monospaced))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(8)
                        .id("end")
                }
                .background(Color(nsColor: .textBackgroundColor).opacity(0.6))
                .clipShape(RoundedRectangle(cornerRadius: 8))
                .onChange(of: runner.transcript) { _, _ in proxy.scrollTo("end", anchor: .bottom) }
            }

            HStack {
                TextField("Ask or tell OpenClicky what to do…", text: $input)
                    .textFieldStyle(.roundedBorder)
                    .focused($focused)
                    .onSubmit(send)
                Button("Send", action: send).keyboardShortcut(.return, modifiers: .command).disabled(runner.isRunning)
            }
            HStack {
                if let t = runner.lastThreadId { Text("thread \(t.prefix(8))…").font(.caption2).foregroundStyle(.secondary) }
                Spacer()
                Text("⌥Space toggles · ⌘↩ sends").font(.caption2).foregroundStyle(.secondary)
            }
        }
        .padding(12)
        .frame(minWidth: 420, minHeight: 280)
        .onAppear { focused = true }
    }

    private func send() {
        let text = input
        input = ""
        runner.submit(text)
    }
}
