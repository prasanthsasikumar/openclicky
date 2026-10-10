//
//  OnboardingWindow.swift
//  OpenClicky
//
//  The first run, in six short chapters: welcome, the two permissions, the key, the engine, a
//  first take that lands in the window itself, and the one email field that makes a free account (or your own key,
//  or neither). Every chapter can be skipped; the whole thing can be replayed from Settings → general.
//

import AppKit
import Combine
import AVFoundation
import Speech
import SwiftUI

enum OnboardingChapter: Int, CaseIterable {
    case welcome, permissions, key, engine, firstTake, account

    var title: String {
        switch self {
        case .welcome: return "you talk, openclicky writes."
        case .permissions: return "two switches, once."
        case .key: return "the key you'll press a hundred times a day."
        case .engine: return "who hears you."
        case .firstTake: return "your first take."
        case .account: return "use clicky's brain."
        }
    }
}

@MainActor
final class OnboardingWindowController: NSWindowController, NSWindowDelegate {
    private let companionManager: CompanionManager
    let model = OnboardingModel()

    init(companionManager: CompanionManager) {
        self.companionManager = companionManager
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 760, height: 560), styleMask: [.titled, .closable, .fullSizeContentView], backing: .buffered, defer: false)
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.isReleasedWhenClosed = false
        window.backgroundColor = NSColor(Paper.background)
        super.init(window: window)
        window.delegate = self
        let hosting = NSHostingView(rootView: OnboardingView(model: model, companionManager: companionManager, finish: { [weak self] in self?.finish() }))
        // The window keeps its own size; the view fills it (SwiftUI would otherwise grow the window to its ideal height).
        hosting.sizingOptions = []
        window.contentView = hosting
        window.center()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { nil }

    func show() {
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        showWindow(nil)
        window?.makeKeyAndOrderFront(nil)
    }

    private func finish() {
        companionManager.dictationSettings.hasCompletedDictationOnboarding = true
        companionManager.dictationTakeController.onTakeFinished = nil
        close()
        companionManager.showDictationWindow(section: .record)
    }

    func windowWillClose(_ notification: Notification) {
        companionManager.dictationSettings.hasCompletedDictationOnboarding = true
        companionManager.dictationTakeController.onTakeFinished = nil
        DispatchQueue.main.async {
            if !(NSApp.windows.contains { $0.isVisible && !($0 is NSPanel) }) { NSApp.setActivationPolicy(.accessory) }
        }
    }
}

@MainActor
final class OnboardingModel: ObservableObject {
    @Published var chapter: OnboardingChapter = .welcome
    @Published var firstTakeText = ""
}

struct OnboardingView: View {
    @ObservedObject var model: OnboardingModel
    let companionManager: CompanionManager
    let finish: () -> Void
    @ObservedObject private var settings: DictationSettings
    @ObservedObject private var authSession = OpenClickyAuthSession.shared

    init(model: OnboardingModel, companionManager: CompanionManager, finish: @escaping () -> Void) {
        self.model = model
        self.companionManager = companionManager
        self.finish = finish
        self.settings = companionManager.dictationSettings
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                OpenClickyWordmark()
                Spacer()
                HStack(spacing: 6) {
                    ForEach(OnboardingChapter.allCases, id: \.rawValue) { chapter in
                        Capsule().fill(chapter.rawValue <= model.chapter.rawValue ? Paper.accent : Paper.hairline).frame(width: chapter == model.chapter ? 22 : 10, height: 4)
                    }
                }
                Button("skip this chapter") { next() }.buttonStyle(.plain).font(Paper.body(11)).foregroundStyle(Paper.inkTertiary).pointerCursor()
            }
            .padding(.horizontal, 36).padding(.top, 40)

            Text(model.chapter.title).font(Paper.heading(34)).foregroundStyle(Paper.ink).padding(.horizontal, 36).padding(.top, 36)

            Group {
                switch model.chapter {
                case .welcome: welcome
                case .permissions: permissions
                case .key: key
                case .engine: engine
                case .firstTake: firstTake
                case .account: account
                }
            }
            .padding(.horizontal, 36).padding(.top, 18)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)

            HStack {
                if model.chapter != .welcome { Button("back") { previous() }.buttonStyle(PaperPillButtonStyle()) }
                Spacer()
                Button(model.chapter == OnboardingChapter.allCases.last ? "come back to openclicky" : "continue") { next() }.buttonStyle(PaperPillButtonStyle(prominent: true))
            }
            .padding(36)
        }
        .background(Paper.background)
        .onAppear {
            companionManager.dictationTakeController.onTakeFinished = { take in
                model.firstTakeText = take.displayText
            }
        }
    }

    private var welcome: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("hold one key anywhere on your mac, say what you mean, let go. the words are cleaned up in the style of the app in front and land where your cursor is.")
                .font(Paper.body(15)).foregroundStyle(Paper.inkSecondary).fixedSize(horizontal: false, vertical: true)
            Text("with the offline engine your voice never leaves this mac. add a sarvam key and it hears eleven indian languages.")
                .font(Paper.body(15)).foregroundStyle(Paper.inkSecondary).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 16) {
                feature("mic", "dictate anywhere")
                feature("pencil.line", "styles per app")
                feature("sparkles", "names spelled right")
                feature("lock", "offline by default")
            }
            .padding(.top, 10)
        }
    }

    private func feature(_ symbol: String, _ text: String) -> some View {
        VStack(spacing: 8) {
            Image(systemName: symbol).font(.system(size: 18)).foregroundStyle(Paper.accent)
            Text(text).font(Paper.body(12)).foregroundStyle(Paper.ink)
        }
        .frame(width: 130, height: 80)
        .background(RoundedRectangle(cornerRadius: 12).fill(Paper.card))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Paper.hairline))
    }

    private var permissions: some View {
        OnboardingPermissionsView()
    }

    private var key: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("hold it to dictate, tap it to start a take and tap again to finish. choose wisely.").font(Paper.body(14)).foregroundStyle(Paper.inkSecondary)
            HStack(spacing: 12) {
                ForEach(DictationHotkey.allCases) { key in
                    Button(action: { settings.dictationKey = key }) {
                        VStack(spacing: 10) {
                            Keycap(text: key.keycapLabel)
                            Text(key.displayName).font(Paper.body(12)).foregroundStyle(Paper.ink)
                        }
                        .frame(width: 130, height: 86)
                        .background(RoundedRectangle(cornerRadius: 12).fill(Paper.card))
                        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(settings.dictationKey == key ? Paper.success : Paper.hairline, lineWidth: settings.dictationKey == key ? 2 : 1))
                    }
                    .buttonStyle(.plain).pointerCursor()
                }
            }
            if settings.dictationKey == .fn, !FnKeyGuard.isFnFree {
                HStack(spacing: 10) {
                    Text("macOS also uses fn to \(FnKeyGuard.currentUsage()?.description ?? "do something"). let openclicky set it to do nothing?").font(Paper.body(12)).foregroundStyle(Paper.inkSecondary)
                    Button("free the fn key") { FnKeyGuard.freeFn(); settings.objectWillChange.send() }.buttonStyle(PaperPillButtonStyle(prominent: true))
                }
                .padding(14).background(RoundedRectangle(cornerRadius: 10).fill(Paper.highlighter.opacity(0.4)))
            }
        }
    }

    private var engine: some View {
        VStack(alignment: .leading, spacing: 12) {
            ForEach(DictationEngineChoice.allCases) { choice in
                let reason = DictationEngineResolver.unavailableReason(for: choice)
                Button(action: { settings.engine = choice }) {
                    HStack(spacing: 12) {
                        Image(systemName: settings.engine == choice ? "largecircle.fill.circle" : "circle").foregroundStyle(settings.engine == choice ? Paper.success : Paper.inkTertiary)
                        VStack(alignment: .leading, spacing: 3) {
                            HStack(spacing: 8) {
                                Text(choice.displayName).font(Paper.body(14, weight: .medium)).foregroundStyle(Paper.ink)
                                if let reason { Text(reason).font(Paper.body(11)).foregroundStyle(Paper.danger) }
                            }
                            Text(choice.detail).font(Paper.body(12)).foregroundStyle(Paper.inkSecondary)
                        }
                        Spacer()
                    }
                    .padding(14)
                    .background(RoundedRectangle(cornerRadius: 10).fill(Paper.card))
                    .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(settings.engine == choice ? Paper.success : Paper.hairline))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain).pointerCursor()
            }
            if settings.engine == .sarvam { OnboardingSarvamKeyField() }
            Text("you can change this any time under settings → engine.").font(Paper.body(11)).foregroundStyle(Paper.inkTertiary)
        }
    }

    private var firstTake: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 6) {
                Text("press").font(Paper.body(14)).foregroundStyle(Paper.inkSecondary)
                Keycap(text: settings.dictationKey.keycapLabel)
                Text("anywhere, say something, let go. it lands here.").font(Paper.body(14)).foregroundStyle(Paper.inkSecondary)
            }
            PaperCard {
                Text(model.firstTakeText.isEmpty ? "…" : model.firstTakeText)
                    .font(.system(size: 20, design: .serif)).foregroundStyle(model.firstTakeText.isEmpty ? Paper.inkTertiary : Paper.ink)
                    .frame(maxWidth: .infinity, minHeight: 140, alignment: .topLeading)
                    .padding(18)
            }
            if !model.firstTakeText.isEmpty {
                Text("that's it. the orb at the bottom of your screen shows every take; the window keeps your history, dictionary, shortcuts and styles.").font(Paper.body(12)).foregroundStyle(Paper.inkSecondary)
            }
        }
    }

    private var account: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("your email gets you a free openclicky account — polished dictation and spoken answers, nothing to set up.")
                .font(Paper.body(14)).foregroundStyle(Paper.inkSecondary).fixedSize(horizontal: false, vertical: true)
            if let accountEmail = authSession.accountEmail {
                HStack(spacing: 8) {
                    Image(systemName: "checkmark").font(.system(size: 12, weight: .bold))
                    Text(authSession.emailFlow == .signedIn(confirmed: false)
                         ? "you're set, \(accountEmail). check your inbox to unlock the full free allowance."
                         : "you're set, \(accountEmail).").font(Paper.body(14, weight: .medium))
                }
                .foregroundStyle(Paper.success).padding(.top, 6)
            } else {
                EmailAccountForm(onSignedIn: {}, onUseOwnKey: { companionManager.showDictationWindow(settingsPage: .account) })
                if let note = OpenClickyAuthSession.signUpClosedNote(accountsOpen: authSession.accountsOpen) {
                    Text(note).font(Paper.caption).foregroundStyle(Paper.inkSecondary).fixedSize(horizontal: false, vertical: true)
                }
            }
            if authSession.accountEmail == nil {
                Button("skip — keep everything on this mac") { finish() }
                    .buttonStyle(.plain).font(Paper.body(12)).foregroundStyle(Paper.inkTertiary).pointerCursor().padding(.top, 2)
            }
        }
        .task { await authSession.refreshAccountsOpen() }
    }

    private func next() {
        if let following = OnboardingChapter(rawValue: model.chapter.rawValue + 1) { model.chapter = following } else { finish() }
    }

    private func previous() {
        if let earlier = OnboardingChapter(rawValue: model.chapter.rawValue - 1) { model.chapter = earlier }
    }
}

private struct OnboardingPermissionsView: View {
    @State private var microphone = AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
    @State private var accessibility = AXIsProcessTrusted()
    @State private var speech = SFSpeechRecognizer.authorizationStatus() == .authorized
    private let timer = Timer.publish(every: 1.0, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            row("microphone", "openclicky listens only while you hold the key.", granted: microphone) {
                AVCaptureDevice.requestAccess(for: .audio) { _ in }
            }
            row("accessibility", "pastes your words into any app and reads nearby names for spelling.", granted: accessibility) {
                WindowPositionManager.requestAccessibilityPermission()
            }
            row("speech recognition", "apple's on-device recogniser, for the offline engine.", granted: speech) {
                SFSpeechRecognizer.requestAuthorization { _ in }
            }
            Text("openclicky needs the first two before dictation can paste. macOS can take a moment to show a fresh grant; the island's permission card can drag openclicky into the list if it never appears.").font(Paper.body(11)).foregroundStyle(Paper.inkTertiary)
        }
        .onReceive(timer) { _ in
            microphone = AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
            accessibility = AXIsProcessTrusted()
            speech = SFSpeechRecognizer.authorizationStatus() == .authorized
        }
    }

    private func row(_ title: String, _ detail: String, granted: Bool, action: @escaping () -> Void) -> some View {
        HStack(spacing: 14) {
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(Paper.body(14, weight: .medium)).foregroundStyle(Paper.ink)
                Text(detail).font(Paper.body(12)).foregroundStyle(Paper.inkSecondary)
            }
            Spacer()
            if granted {
                HStack(spacing: 4) { Image(systemName: "checkmark").font(.system(size: 10, weight: .bold)); Text("granted").font(Paper.body(12)) }.foregroundStyle(Paper.success)
            } else {
                Button("grant", action: action).buttonStyle(PaperPillButtonStyle(prominent: true))
            }
        }
        .padding(16)
        .background(RoundedRectangle(cornerRadius: 10).fill(Paper.card))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(Paper.hairline))
    }
}

private struct OnboardingSarvamKeyField: View {
    @State private var key = OpenClickyConfiguration.settings.sarvamKey ?? ""
    @State private var status: String?

    var body: some View {
        HStack(spacing: 10) {
            SecureField("paste your sarvam key (dashboard.sarvam.ai)", text: $key).textFieldStyle(.roundedBorder)
            Button("save") {
                let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines)
                OpenClickyConfiguration.update { $0.sarvamKey = trimmed.isEmpty ? nil : trimmed }
                status = trimmed.isEmpty ? "removed" : "saved — your voice will go to sarvam as audio"
            }
            .buttonStyle(PaperPillButtonStyle(prominent: true))
            if let status { Text(status).font(Paper.body(11)).foregroundStyle(Paper.inkSecondary) }
        }
        .padding(14).background(RoundedRectangle(cornerRadius: 10).fill(Paper.highlighter.opacity(0.4)))
    }
}
