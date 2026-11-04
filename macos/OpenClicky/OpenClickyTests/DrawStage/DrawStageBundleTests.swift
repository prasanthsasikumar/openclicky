//
//  DrawStageBundleTests.swift
//  OpenClickyTests
//
//  The shared drawing stage (draw-stage/ at the repository root) is copied into the app by the
//  "Embed draw stage" build phase; these tests fail when the phase or a file is missing, and when
//  the worker's no-network fence loses one of its names.
//

import Foundation
import Testing
@testable import OpenClicky

@Suite struct DrawStageBundleTests {
    private var appBundle: Bundle { Bundle(for: CompanionManager.self) }

    @Test func theStagePageAndPromptsAreBundled() {
        for path in ["index.html", "stage.js", "stage-worker.js", "figure3d.js", "vendor/three/three.module.min.js",
                     "prompt/drawing-language.md", "prompt/follow-up.md", "prompt/question.md", "samples/rocket.js"] {
            #expect(appBundle.url(forResource: path, withExtension: nil, subdirectory: "draw-stage") != nil, "\(path)")
        }
    }

    @Test func theWorkerFencesNestedWorkersAndBlobs() throws {
        let workerURL = try #require(appBundle.url(forResource: "stage-worker.js", withExtension: nil, subdirectory: "draw-stage"))
        let source = try String(contentsOf: workerURL, encoding: .utf8)
        for name in ["'Worker'", "'SharedWorker'", "'Blob'", "URL.createObjectURL"] { #expect(source.contains(name), "\(name)") }
    }
}
