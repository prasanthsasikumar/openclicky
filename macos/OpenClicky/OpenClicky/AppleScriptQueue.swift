//
//  AppleScriptQueue.swift
//  OpenClicky
//
//  Every NSAppleScript the app runs (the Media plugin's polling, Mail, Spotify) goes through this
//  one serial queue. NSAppleScript is not documented as safe across concurrent instances, and
//  three subsystems each with their own queue would run scripts at the same moment.
//

import Foundation

nonisolated enum AppleScriptQueue {
    static let shared = DispatchQueue(label: "openclicky.applescript", qos: .utility)
}
