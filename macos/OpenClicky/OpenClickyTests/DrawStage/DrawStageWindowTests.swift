//
//  DrawStageWindowTests.swift
//  OpenClickyTests
//
//  The pure parts of the stage window: the URL scheme that serves the bundled page may never
//  reach outside its folder, and the page's figure boxes become the screen points where the
//  window takes the mouse.
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

@Suite struct DrawStageWindowTests {
    @Test func requestPathsStayInsideTheFolder() {
        let folder = URL(fileURLWithPath: "/tmp/stage")
        #expect(DrawStageScheme.fileURL(forRequestPath: "/index.html", in: folder, requireExists: false)?.path == "/tmp/stage/index.html")
        #expect(DrawStageScheme.fileURL(forRequestPath: "/vendor/three/three.module.min.js", in: folder, requireExists: false)?.path
                == "/tmp/stage/vendor/three/three.module.min.js")
        #expect(DrawStageScheme.fileURL(forRequestPath: "/samples/rocket.js?v=1", in: folder, requireExists: false)?.path == "/tmp/stage/samples/rocket.js")
        for escape in ["/../shell.json", "/vendor/../../x", "//etc/passwd", "/%2e%2e/x", "/vendor/..%2F..%2Fx", ""] {
            #expect(DrawStageScheme.fileURL(forRequestPath: escape, in: folder, requireExists: false) == nil, "\(escape)")
        }
    }

    @Test func mediaTypesForTheStageFiles() {
        #expect(DrawStageScheme.mediaType(forExtension: "js") == "text/javascript")
        #expect(DrawStageScheme.mediaType(forExtension: "html") == "text/html")
        #expect(DrawStageScheme.mediaType(forExtension: "css") == "text/css")
        #expect(DrawStageScheme.mediaType(forExtension: "png") == "image/png")
        #expect(DrawStageScheme.mediaType(forExtension: "md") == "text/plain")
        #expect(DrawStageScheme.mediaType(forExtension: "weird") == "application/octet-stream")
    }

    @Test func pageRectsBecomeScreenPointsForTheHitTest() {
        let pageSize = CGSize(width: 1440, height: 900)
        let panelFrame = CGRect(x: 0, y: 0, width: 1440, height: 900)
        let rects = DrawStageWindow.screenRects(from: [CGRect(x: 100, y: 50, width: 200, height: 100)], pageSize: pageSize, panelFrame: panelFrame)
        #expect(rects == [CGRect(x: 100, y: 750, width: 200, height: 100)])

        // A second display to the right, its page at half the panel's size (a retina page is measured in CSS pixels).
        let offsetFrame = CGRect(x: 1440, y: 100, width: 1000, height: 600)
        let scaled = DrawStageWindow.screenRects(from: [CGRect(x: 0, y: 0, width: 500, height: 300)], pageSize: CGSize(width: 500, height: 300), panelFrame: offsetFrame)
        #expect(scaled == [CGRect(x: 1440, y: 100, width: 1000, height: 600)])
    }
}
