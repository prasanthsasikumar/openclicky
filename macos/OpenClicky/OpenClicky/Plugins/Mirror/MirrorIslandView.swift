//
//  MirrorIslandView.swift
//  OpenClicky
//
//  The Mirror tool (id `mirror`): a quick look at yourself before a call. Its band button opens
//  the island with the camera's picture, flipped the way a mirror is. The camera runs only while
//  the picture is on the island: the ✕, the button again, or the island closing stops it at once,
//  so the camera light goes off with it. Nothing is recorded or sent anywhere. The first use asks
//  for camera permission through macOS; refused, the island says so with a way to System Settings.
//

import AVFoundation
import AppKit
import Combine
import SwiftUI

/// A plugin: the camera's picture on the island.
struct MirrorIslandComponent: IslandComponent {
    let id = IslandPluginRegistry.mirrorID
    let title = "Mirror"
    let symbolName = "camera"
    let preferredHeight: CGFloat = 196
    let isPlugin = true
    let settingsDetail: String? = "A quick look at yourself before a call, from the island's band. The camera runs only while the mirror is on the island, and nothing is recorded."
    func isAvailable(in context: IslandContext) -> Bool { true }
    @MainActor func makeView(companionManager: CompanionManager, model: NotchHUDModel) -> AnyView {
        AnyView(MirrorIslandView(camera: MirrorCamera.shared, model: model))
    }
}

/// What the island says instead of a picture when the camera is not allowed; nil when it is, or
/// when macOS has not asked yet (the picture view asks).
enum MirrorPermissionNote {
    static func text(for status: AVAuthorizationStatus) -> String? {
        switch status {
        case .authorized, .notDetermined: return nil
        case .denied: return "Camera access is off for OpenClicky. Turn it on in System Settings › Privacy & Security › Camera."
        case .restricted: return "The camera is not allowed on this Mac."
        @unknown default: return "The camera is not available."
        }
    }

    static let systemSettingsURL = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Camera")!
}

/// The capture session behind the mirror: started when the picture appears, stopped the moment
/// it goes. One per app, so a second appearance reuses the configured session.
@MainActor
final class MirrorCamera: ObservableObject {
    static let shared = MirrorCamera()

    let session = AVCaptureSession()
    @Published private(set) var status: AVAuthorizationStatus = AVCaptureDevice.authorizationStatus(for: .video)
    @Published private(set) var isRunning = false
    /// The person asked for the mirror (the band's button): home shows it in now playing's place.
    /// Cleared by the ✕, the button again, or the picture leaving the screen (the island closing).
    @Published private(set) var isWanted = false
    private var isConfigured = false
    private let sessionQueue = DispatchQueue(label: "org.openclicky.mirror")

    func toggle() {
        isWanted.toggle()
        if !isWanted { stop() }
    }

    func dismiss() {
        guard isWanted else { return }
        isWanted = false
        stop()
    }

    func start() {
        status = AVCaptureDevice.authorizationStatus(for: .video)
        switch status {
        case .authorized:
            startSession()
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .video) { [weak self] granted in
                Task { @MainActor in
                    guard let self else { return }
                    self.status = AVCaptureDevice.authorizationStatus(for: .video)
                    if granted { self.startSession() }
                }
            }
        default:
            break
        }
    }

    func stop() {
        guard isRunning else { return }
        isRunning = false
        let session = self.session
        sessionQueue.async { session.stopRunning() }
        AppLog.append("mirror: camera stopped")
    }

    private func startSession() {
        guard !isRunning else { return }
        isRunning = true
        let session = self.session
        let needsConfiguration = !isConfigured
        isConfigured = true
        sessionQueue.async {
            if needsConfiguration {
                session.beginConfiguration()
                session.sessionPreset = .high
                // The Mac's own camera first; any other video device when there is none.
                let device = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .front)
                    ?? AVCaptureDevice.default(for: .video)
                if let device, let input = try? AVCaptureDeviceInput(device: device), session.canAddInput(input) {
                    session.addInput(input)
                }
                session.commitConfiguration()
            }
            session.startRunning()
        }
        AppLog.append("mirror: camera started")
    }
}

struct MirrorIslandView: View {
    @ObservedObject var camera: MirrorCamera
    @ObservedObject var model: NotchHUDModel
    @State private var isHoveringClose = false

    var body: some View {
        ZStack(alignment: .topTrailing) {
            if let note = MirrorPermissionNote.text(for: camera.status) {
                VStack(spacing: 10) {
                    Image(systemName: "camera.fill").font(.system(size: 20)).foregroundStyle(.white.opacity(0.6))
                    Text(note).font(.system(size: 12)).foregroundStyle(.white.opacity(0.8)).multilineTextAlignment(.center)
                    if camera.status == .denied {
                        Button("Open System Settings") { NSWorkspace.shared.open(MirrorPermissionNote.systemSettingsURL) }
                            .buttonStyle(IslandPillButtonStyle())
                            .pointerCursor()
                    }
                }
                .padding(.horizontal, 24)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                MirrorPreview(session: camera.session)
                    .frame(width: 300, height: 168)
                    .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
                    .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(.white.opacity(0.12), lineWidth: 1))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            Button {
                model.selectComponent(nil)
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 10, weight: .bold))
                    .foregroundStyle(.white)
                    .frame(width: 22, height: 22)
                    .background(Circle().fill(.black.opacity(isHoveringClose ? 0.75 : 0.55)))
            }
            .buttonStyle(.plain)
            .onHover { isHoveringClose = $0 }
            .pointerCursor()
            .padding(10)
            .help("Close the mirror")
            .accessibilityLabel("Close the mirror")
        }
        .padding(.vertical, 12)
        .onAppear { camera.start() }
        .onDisappear { camera.stop() }
    }
}

/// The mirror as a home column, in now playing's place: the picture fills the column with an ✕
/// at its corner. Appearing starts the camera; leaving the screen (the ✕, the island closing,
/// another component) stops it and lets the mirror go.
struct MirrorHomeColumn: View {
    @ObservedObject var camera: MirrorCamera
    @State private var isHoveringClose = false

    var body: some View {
        ZStack(alignment: .topTrailing) {
            if let note = MirrorPermissionNote.text(for: camera.status) {
                VStack(spacing: 8) {
                    Image(systemName: "camera.fill").font(.system(size: 16)).foregroundStyle(.white.opacity(0.6))
                    Text(note).font(.system(size: 11)).foregroundStyle(.white.opacity(0.8)).multilineTextAlignment(.center)
                    if camera.status == .denied {
                        Button("Open System Settings") { NSWorkspace.shared.open(MirrorPermissionNote.systemSettingsURL) }
                            .buttonStyle(IslandPillButtonStyle())
                            .pointerCursor()
                    }
                }
                .padding(.horizontal, 16)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.white.opacity(0.06)))
            } else {
                MirrorPreview(session: camera.session)
                    .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
                    .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(.white.opacity(0.12), lineWidth: 1))
            }
            Button {
                camera.dismiss()
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 9, weight: .bold))
                    .foregroundStyle(.white)
                    .frame(width: 20, height: 20)
                    .background(Circle().fill(.black.opacity(isHoveringClose ? 0.75 : 0.5)))
            }
            .buttonStyle(.plain)
            .onHover { isHoveringClose = $0 }
            .pointerCursor()
            .padding(8)
            .help("Close the mirror")
            .accessibilityLabel("Close the mirror")
        }
        .onAppear { camera.start() }
        .onDisappear { camera.dismiss() }
    }
}

/// The camera's picture, flipped left to right like a mirror.
struct MirrorPreview: NSViewRepresentable {
    let session: AVCaptureSession

    func makeNSView(context: Context) -> MirrorPreviewView {
        let view = MirrorPreviewView()
        view.previewLayer.session = session
        return view
    }

    func updateNSView(_ nsView: MirrorPreviewView, context: Context) {}
}

final class MirrorPreviewView: NSView {
    let previewLayer = AVCaptureVideoPreviewLayer()

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        previewLayer.videoGravity = .resizeAspectFill
        previewLayer.backgroundColor = NSColor.black.cgColor
        layer?.addSublayer(previewLayer)
    }

    required init?(coder: NSCoder) { nil }

    override func layout() {
        super.layout()
        previewLayer.frame = bounds
        // A mirror, not a camera: what is on your left stays on your left.
        if let connection = previewLayer.connection, connection.isVideoMirroringSupported {
            connection.automaticallyAdjustsVideoMirroring = false
            connection.isVideoMirrored = true
        }
    }
}
