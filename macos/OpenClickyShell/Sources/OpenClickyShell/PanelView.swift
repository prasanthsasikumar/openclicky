import SwiftUI

struct PanelView: View {
    @ObservedObject var runner: AgentRunner
    var onClose: () -> Void
    @State private var input = ""
    @FocusState private var focused: Bool

    var body: some View {
        VStack(spacing: 8) {
            header
            timeline
            composer
            footer
        }
        .padding(12)
        .frame(minWidth: 420, minHeight: 280)
        .onAppear { focused = true }
    }

    private var header: some View {
        HStack(spacing: 8) {
            Image(systemName: runner.isRunning ? "circle.dotted" : "waveform.circle.fill")
                .foregroundStyle(runner.isRunning ? .orange : .accentColor)
                .symbolEffect(.pulse, isActive: runner.isRunning)
            VStack(alignment: .leading, spacing: 1) {
                Text(runner.isRunning ? "Working…" : "OpenClicky").font(.headline)
                Text(runner.status).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
            }
            if let lane = runner.lane {
                Text(lane.uppercased())
                    .font(.caption2.weight(.semibold))
                    .padding(.horizontal, 6).padding(.vertical, 2)
                    .background(lane == "agent" ? Color.orange.opacity(0.2) : Color.blue.opacity(0.2))
                    .clipShape(Capsule())
            }
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
    }

    private var timeline: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 4) {
                    ForEach(runner.entries) { entry in
                        row(entry).id(entry.id)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(8)
            }
            .background(Color(nsColor: .textBackgroundColor).opacity(0.6))
            .clipShape(RoundedRectangle(cornerRadius: 8))
            .onChange(of: runner.entries.count) { _, _ in
                if let last = runner.entries.last { proxy.scrollTo(last.id, anchor: .bottom) }
            }
        }
    }

    @ViewBuilder
    private func row(_ entry: ActivityEntry) -> some View {
        switch entry.kind {
        case .user:
            Text(entry.text).font(.body.weight(.semibold)).padding(.top, 6)
        case .lane:
            Label(entry.text, systemImage: "arrow.triangle.branch").font(.caption).foregroundStyle(.secondary)
        case .event:
            Label(entry.text, systemImage: "circle.fill").font(.caption).foregroundStyle(.secondary)
                .labelStyle(TinyDotLabelStyle())
        case .agent:
            Text(entry.text).font(.body).textSelection(.enabled)
        case .artifact:
            Button {
                NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: entry.text)])
            } label: {
                Label(entry.text, systemImage: "doc").font(.caption)
            }.buttonStyle(.link).help("Reveal in Finder")
        case .system:
            Text(entry.text).font(.caption).foregroundStyle(.tertiary)
        case .error:
            Label(entry.text, systemImage: "exclamationmark.triangle.fill").font(.caption).foregroundStyle(.red)
        }
    }

    private var composer: some View {
        HStack {
            TextField("Ask or tell OpenClicky what to do…", text: $input)
                .textFieldStyle(.roundedBorder)
                .focused($focused)
                .onSubmit(send)
            Button("Send", action: send).keyboardShortcut(.return, modifiers: .command).disabled(runner.isRunning)
        }
    }

    private var footer: some View {
        HStack {
            if let t = runner.lastThreadId { Text("thread \(t.prefix(8))…").font(.caption2).foregroundStyle(.secondary) }
            if !runner.artifacts.isEmpty { Text("\(runner.artifacts.count) file(s)").font(.caption2).foregroundStyle(.secondary) }
            Spacer()
            Text("⌥Space toggles · ⌘↩ sends").font(.caption2).foregroundStyle(.secondary)
        }
    }

    private func send() {
        let text = input
        input = ""
        runner.submit(text)
    }
}

private struct TinyDotLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 6) {
            configuration.icon.font(.system(size: 5))
            configuration.title
        }
    }
}
