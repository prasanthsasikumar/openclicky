//
//  IslandReply.swift
//  OpenClicky
//
//  The reply card under the island. A typed question or a job handed to the agent gets its
//  answer in words as well as in voice: the question, a typing bubble that becomes the answer as
//  it arrives, an agent's milestones as steps that tick off and fold away when the answer is in,
//  and up to three next questions as pills. A spoken question keeps its spoken answer and gets
//  only the pills. The card leaves on a countdown ring once the answer is in (the pointer on the
//  island holds it), on its close button, on esc, or when another question is asked.
//

import AppKit
import Combine
import SwiftUI

struct IslandReplyState: Equatable {
    struct Step: Equatable, Identifiable {
        let id: Int
        var text: String
        var isDone: Bool
    }

    var question: String
    var text: String = ""
    var isFinished = false
    var steps: [Step] = []
    var nextQuestions: [String] = []
    /// The words of the answer are shown (a typed question, a job). False: only the next questions.
    var showsAnswer: Bool
    /// The agent's result can be opened as its card.
    var threadId: String?
    /// What is left of the countdown once the answer is in, 1…0.
    var fraction: Double = 1

    /// Nothing to draw yet: a spoken answer waiting for its next questions.
    var isEmpty: Bool { !showsAnswer && nextQuestions.isEmpty }
    /// The steps show while the job runs and fold away when the answer is in.
    var visibleSteps: [Step] { isFinished ? [] : Array(steps.suffix(IslandReplyLayout.maximumSteps)) }

    /// A milestone arrived: the one before it is done. The same line twice is one step.
    mutating func addStep(_ text: String) {
        let line = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !line.isEmpty, steps.last?.text != line else { return }
        if !steps.isEmpty { steps[steps.count - 1].isDone = true }
        steps.append(Step(id: steps.count, text: line, isDone: false))
    }
}

/// The card's size, worked out from its content so the island can open to it.
enum IslandReplyLayout {
    static let width: CGFloat = 512
    static let sidePadding: CGFloat = 18
    static let bubblePadding: CGFloat = 12
    static let textFontSize: CGFloat = 13
    static let maximumTextHeight: CGFloat = 176
    static let maximumSteps = 4
    static let stepHeight: CGFloat = 20
    static let pillRowHeight: CGFloat = 26

    static var textWidth: CGFloat { width - 2 * sidePadding - 2 * bubblePadding }

    static func textHeight(_ text: String) -> CGFloat {
        guard !text.isEmpty else { return 0 }
        let bounds = (text as NSString).boundingRect(
            with: CGSize(width: textWidth, height: .greatestFiniteMagnitude),
            options: [.usesLineFragmentOrigin, .usesFontLeading],
            attributes: [.font: NSFont.systemFont(ofSize: textFontSize)])
        return ceil(bounds.height)
    }

    /// How many rows the pills need: they wrap when three do not fit on one line.
    static func pillRows(_ questions: [String]) -> Int {
        guard !questions.isEmpty else { return 0 }
        var rows = 1
        var used: CGFloat = 0
        let available = width - 2 * sidePadding
        for question in questions {
            let pill = ceil((question as NSString).size(withAttributes: [.font: NSFont.systemFont(ofSize: 11.5, weight: .medium)]).width) + 22
            if used > 0, used + 6 + pill > available {
                rows += 1
                used = pill
            } else {
                used += (used > 0 ? 6 : 0) + pill
            }
        }
        return rows
    }

    /// The height under the island's top inset.
    static func contentHeight(for state: IslandReplyState) -> CGFloat {
        var height: CGFloat = 8
        if state.showsAnswer {
            height += 18 + 6                                              // the question
            height += CGFloat(state.visibleSteps.count) * stepHeight
            if !state.visibleSteps.isEmpty { height += 4 }
            let bubble = state.text.isEmpty ? 30 : min(textHeight(state.text), maximumTextHeight) + 2 * 9
            if state.text.isEmpty && !state.visibleSteps.isEmpty { /* the steps stand in for the typing bubble */ } else { height += bubble }
        } else {
            height += 18                                                  // "ask next"
        }
        let rows = pillRows(state.nextQuestions)
        if rows > 0 { height += 8 + CGFloat(rows) * pillRowHeight + CGFloat(rows - 1) * 6 }
        return height + 12
    }
}

@MainActor
final class IslandReplyController {
    private weak var companionManager: CompanionManager?
    private(set) var state: IslandReplyState?
    private var timer: Timer?
    private var remaining: Double = 0
    private var nextQuestionsTask: Task<Void, Never>?
    /// Bumped by every new question, so a late reply or late next questions land nowhere.
    private var generation = 0
    static let tick: TimeInterval = 0.1
    static let secondsAfterTheAnswer: Double = 22
    static let secondsForNextQuestionsOnly: Double = 30

    private var idleSubscription: AnyCancellable?

    func start(companionManager: CompanionManager) {
        self.companionManager = companionManager
        // A question that ended without an answer (it failed, or was only an action that said
        // nothing) must not leave the typing bubble up for good.
        idleSubscription = companionManager.$voiceState
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.companionMaybeIdle() }
    }

    private func companionMaybeIdle() {
        guard companionManager?.voiceState == .idle, let current = state, current.showsAnswer, !current.isFinished else { return }
        let watched = generation
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) { [weak self] in
            guard let self, self.generation == watched, let current = self.state, !current.isFinished,
                  self.companionManager?.voiceState == .idle, self.companionManager?.agentActivityText == nil else { return }
            AppLog.append("reply card: withdrawn, the question ended without an answer")
            self.withdraw()
        }
    }

    /// A reply card is up with the answer's words (the job-done notice would say the same again).
    var isShowingAnAnswer: Bool { state?.showsAnswer == true }

    /// A typed question or a job begins: the card opens with the typing bubble.
    func begin(question: String) {
        withdraw()
        state = IslandReplyState(question: question.trimmingCharacters(in: .whitespacesAndNewlines), showsAnswer: true)
        publish()
    }

    /// The answer so far, as it streams.
    func receive(text: String) {
        guard var current = state, current.showsAnswer, !current.isFinished, current.text != text else { return }
        current.text = text
        state = current
        publish()
    }

    func addStep(_ text: String) {
        guard var current = state, current.showsAnswer, !current.isFinished else { return }
        current.addStep(text)
        state = current
        publish()
    }

    /// An exchange is over (any lane). The open card gets the whole answer and its countdown;
    /// then, where allowed, the next questions are asked for and offered.
    func finish(question: String, answer: String, threadId: String? = nil) {
        let shownAnswer = Self.shown(answer)
        if var current = state, current.showsAnswer {
            current.text = shownAnswer
            current.isFinished = true
            current.threadId = threadId
            for index in current.steps.indices { current.steps[index].isDone = true }
            state = current
            remaining = Self.secondsAfterTheAnswer
            publish()
            startTimer()
        } else {
            withdraw()
        }
        guard companionManager?.dictationSettings.nextQuestions ?? false else { return }
        let asked = generation
        nextQuestionsTask = Task { [weak self] in
            guard let questions = await NextQuestions.make(question: question, answer: answer), !Task.isCancelled else { return }
            self?.offer(questions, forGeneration: asked, question: question)
        }
    }

    private func offer(_ questions: [String], forGeneration asked: Int, question: String) {
        guard asked == generation else { return }
        if var current = state {
            current.nextQuestions = questions
            state = current
        } else {
            state = IslandReplyState(question: question, nextQuestions: questions, showsAnswer: false)
            remaining = Self.secondsForNextQuestionsOnly
            startTimer()
        }
        publish()
    }

    /// A pill: asked like a typed question.
    func ask(_ question: String) {
        companionManager?.submitTypedRequest(question)
    }

    func openResult() {
        guard let threadId = state?.threadId, !threadId.isEmpty else { return }
        withdraw()
        companionManager?.openAgentResultCard(threadId: threadId)
    }

    /// The close button, esc, a new question: the card goes and nothing late comes back.
    func withdraw() {
        generation += 1
        nextQuestionsTask?.cancel()
        nextQuestionsTask = nil
        timer?.invalidate()
        timer = nil
        guard state != nil else { return }
        state = nil
        publish()
    }

    /// What the card shows of an answer: its words without the tags the voice lanes carry, and
    /// not more than a screenful (a job's full report opens as its own card).
    static func shown(_ answer: String) -> String {
        var text = answer.replacingOccurrences(of: #"\[(POINT|ACTION|NEXT|UNSURE)[^\]]*\]"#, with: "", options: .regularExpression)
        // A tag still arriving at the end of a streamed answer ("[POI") is not shown half-written.
        text = text.replacingOccurrences(of: #"\[[A-Z]*(:[^\]]*)?$"#, with: "", options: .regularExpression)
        text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard text.count > 900 else { return text }
        return String(text.prefix(900)).trimmingCharacters(in: .whitespacesAndNewlines) + "…"
    }

    private func startTimer() {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: Self.tick, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tickOnce() }
        }
    }

    private func tickOnce() {
        guard var current = state, let hud = companionManager?.notchHUDManager else { return }
        // The clock runs only while the card is up, nobody is pointing at it, and nothing is being said.
        guard hud.isShowingReply, !hud.isPointerOnPrimaryIsland, companionManager?.voiceState == .idle else { return }
        remaining -= Self.tick
        if remaining <= 0 { return withdraw() }
        let total = current.showsAnswer ? Self.secondsAfterTheAnswer : Self.secondsForNextQuestionsOnly
        current.fraction = max(0, min(1, remaining / total))
        state = current
        publish()
    }

    private func publish() {
        companionManager?.notchHUDManager.setReply(state.flatMap { $0.isEmpty ? nil : $0 })
    }
}

// MARK: - The card

struct IslandReplyView: View {
    let state: IslandReplyState
    let controller: IslandReplyController
    var topInset: CGFloat = 0
    var reduceMotion = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(state.showsAnswer ? state.question : "Ask next")
                    .font(.system(size: 11.5, weight: .medium))
                    .foregroundColor(DS.HUD.text3)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .frame(maxWidth: .infinity, alignment: .leading)
                if state.isFinished, let threadId = state.threadId, !threadId.isEmpty {
                    Button("Open") { controller.openResult() }.buttonStyle(IslandPillButtonStyle())
                }
                if state.isFinished || !state.showsAnswer {
                    IslandCountdownRing(fraction: state.fraction, action: { controller.withdraw() })
                } else {
                    Button(action: { controller.withdraw() }) {
                        Image(systemName: "xmark").font(.system(size: 8, weight: .bold)).foregroundColor(DS.HUD.text2)
                            .frame(width: 20, height: 20).background(Circle().fill(DS.HUD.surfaceRaised))
                    }
                    .buttonStyle(.plain).pointerCursor().accessibilityLabel("close")
                }
            }
            .frame(height: 18)

            if state.showsAnswer {
                if !state.visibleSteps.isEmpty {
                    VStack(alignment: .leading, spacing: 0) {
                        ForEach(state.visibleSteps) { step in
                            IslandReplyStepRow(step: step, reduceMotion: reduceMotion)
                                .frame(height: IslandReplyLayout.stepHeight)
                                .transition(.opacity.combined(with: .offset(y: 6)))
                        }
                    }
                    .padding(.top, 6)
                    .padding(.bottom, 4)
                    .transition(.opacity)
                }
                if !(state.text.isEmpty && !state.visibleSteps.isEmpty) {
                    bubble.padding(.top, 6)
                }
            }

            if !state.nextQuestions.isEmpty {
                IslandReplyPills(questions: state.nextQuestions, reduceMotion: reduceMotion) { controller.ask($0) }
                    .padding(.top, 8)
            }
        }
        .padding(.horizontal, IslandReplyLayout.sidePadding)
        .padding(.top, 8)
        .padding(.top, topInset)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .animation(Motion.settle(reduceMotion: reduceMotion), value: state.visibleSteps)
        .animation(Motion.settle(reduceMotion: reduceMotion), value: state.text.isEmpty)
        .accessibilityElement(children: .contain)
    }

    /// One bubble from first to last: three dots while the answer is on its way, then it widens
    /// and grows into the answer's words, which fade in a line at a time as they arrive.
    private var bubble: some View {
        Group {
            if state.text.isEmpty {
                IslandTypingDots(reduceMotion: reduceMotion)
                    .frame(width: 34, height: 12)
                    .transition(.opacity.animation(.easeIn(duration: 0.1)))
            } else {
                ScrollView(.vertical, showsIndicators: false) {
                    Text(state.text)
                        .font(.system(size: IslandReplyLayout.textFontSize))
                        .foregroundColor(DS.HUD.text)
                        .lineSpacing(0)
                        .multilineTextAlignment(.leading)
                        .fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .textSelection(.enabled)
                }
                .frame(height: min(IslandReplyLayout.textHeight(state.text), IslandReplyLayout.maximumTextHeight))
                // While the words are still coming, the newest line is soft at the bottom edge.
                .mask(LinearGradient(stops: [.init(color: .black, location: 0), .init(color: .black, location: state.isFinished ? 1 : 0.82), .init(color: .black.opacity(state.isFinished ? 1 : 0.35), location: 1)], startPoint: .top, endPoint: .bottom))
                .transition(.opacity.animation(Motion.swap.delay(0.08)))
            }
        }
        .padding(.horizontal, IslandReplyLayout.bubblePadding)
        .padding(.vertical, 9)
        .frame(maxWidth: state.text.isEmpty ? nil : .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 13, style: .continuous).fill(DS.HUD.surfaceRaised))
        .animation(Motion.SpringSpec(response: 0.35, damping: 0.85).animation, value: state.text.isEmpty)
        .animation(Motion.swap, value: IslandReplyLayout.textHeight(state.text))
    }
}

/// A step of a job: a box that is ticked when the next step begins, the current one with a dot.
private struct IslandReplyStepRow: View {
    let step: IslandReplyState.Step
    let reduceMotion: Bool

    var body: some View {
        HStack(spacing: 8) {
            ZStack {
                RoundedRectangle(cornerRadius: 3.5, style: .continuous)
                    .stroke(step.isDone ? DS.HUD.live : DS.HUD.text3, lineWidth: 1.3)
                    .frame(width: 11, height: 11)
                if step.isDone {
                    Image(systemName: "checkmark")
                        .font(.system(size: 7, weight: .black))
                        .foregroundColor(DS.HUD.live)
                        .transition(reduceMotion ? .opacity : .scale(scale: 0.3).combined(with: .opacity))
                } else {
                    Circle().fill(DS.HUD.pointer).frame(width: 4, height: 4)
                }
            }
            .animation(Motion.bounce(reduceMotion: reduceMotion), value: step.isDone)
            Text(step.text)
                .font(.system(size: 12))
                .foregroundColor(step.isDone ? DS.HUD.text3 : DS.HUD.text)
                .strikethrough(step.isDone, color: DS.HUD.text3)
                .lineLimit(1)
                .animation(Motion.swap, value: step.isDone)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// Three dots that rise one after another while an answer is on its way; still under Reduce Motion.
struct IslandTypingDots: View {
    let reduceMotion: Bool
    @State private var isUp = false

    var body: some View {
        HStack(spacing: 5) {
            ForEach(0..<3, id: \.self) { index in
                Circle()
                    .fill(DS.HUD.text2)
                    .frame(width: 6, height: 6)
                    .offset(y: isUp ? -2.5 : 2.5)
                    .opacity(isUp ? 1 : 0.5)
                    .animation(reduceMotion ? nil : .easeInOut(duration: 0.45).repeatForever(autoreverses: true).delay(Double(index) * 0.15), value: isUp)
            }
        }
        .onAppear { if !reduceMotion { isUp = true } }
        .accessibilityLabel("answering")
    }
}

/// The next questions, arriving one after another, wrapping onto a second row when they must.
private struct IslandReplyPills: View {
    let questions: [String]
    let reduceMotion: Bool
    let onAsk: (String) -> Void
    @State private var shown = 0

    var body: some View {
        IslandWrapLayout(spacing: 6, rowSpacing: 6) {
            ForEach(Array(questions.enumerated()), id: \.offset) { index, question in
                Button(action: { onAsk(question) }) {
                    Text(question)
                        .font(.system(size: 11.5, weight: .medium))
                        .foregroundColor(DS.HUD.text)
                        .lineLimit(1)
                        .padding(.horizontal, 11)
                        .frame(height: IslandReplyLayout.pillRowHeight)
                        .background(Capsule().fill(Color.white.opacity(0.13)))
                        .overlay(Capsule().stroke(Color.white.opacity(0.14), lineWidth: 1))
                }
                .buttonStyle(.plain)
                .pointerCursor()
                .opacity(index < shown ? 1 : 0)
                .scaleEffect(index < shown || reduceMotion ? 1 : 0.9)
                .accessibilityHint("asks this question")
            }
        }
        .onAppear {
            for index in questions.indices {
                withAnimation(Motion.pop(reduceMotion: reduceMotion).delay(0.05 + Double(index) * 0.14)) { shown = index + 1 }
            }
        }
    }
}

/// Lays children out left to right, starting a new row when the next one does not fit.
struct IslandWrapLayout: Layout {
    var spacing: CGFloat
    var rowSpacing: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let rows = arrange(subviews: subviews, width: proposal.width ?? .greatestFiniteMagnitude)
        return CGSize(width: proposal.width ?? rows.map(\.width).max() ?? 0, height: rows.last.map { $0.y + $0.height } ?? 0)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        for row in arrange(subviews: subviews, width: bounds.width) {
            var x = bounds.minX
            for index in row.indices {
                let size = subviews[index].sizeThatFits(.unspecified)
                subviews[index].place(at: CGPoint(x: x, y: bounds.minY + row.y), proposal: ProposedViewSize(size))
                x += size.width + spacing
            }
        }
    }

    private struct Row { var indices: [Int]; var y: CGFloat; var height: CGFloat; var width: CGFloat }

    private func arrange(subviews: Subviews, width: CGFloat) -> [Row] {
        var rows: [Row] = []
        for index in subviews.indices {
            let size = subviews[index].sizeThatFits(.unspecified)
            if var last = rows.last, last.width + spacing + size.width <= width {
                last.indices.append(index)
                last.width += spacing + size.width
                last.height = max(last.height, size.height)
                rows[rows.count - 1] = last
            } else {
                let y = rows.last.map { $0.y + $0.height + rowSpacing } ?? 0
                rows.append(Row(indices: [index], y: y, height: size.height, width: size.width))
            }
        }
        return rows
    }
}
