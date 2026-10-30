//
//  IslandPlugins.swift
//  OpenClicky
//
//  The island's plugins: optional extras, built for the Mac, each off until the user turns it on
//  in Settings → Plugins. Everything a plugin needs lives in its own folder under
//  `Plugins/` (its store, its view, its component; System, Shelf, CodingAgents, Media, Teacher), so the rest of the app, and the Windows and
//  Linux clients in their own trees, never depend on it. A plugin is an `IslandComponent` whose
//  `isPlugin` is true; the registry below is the only place that lists them. The Teacher also
//  changes what the voice lanes can do, so they call its one facade, `TeacherPlugin` (its tools,
//  its prompt line, running a tool call, clearing the board), and nothing else of it.
//

import Foundation

enum IslandPluginRegistry {
    static let systemID = "system"
    static let shelfID = "shelf"
    static let agentsID = "agents"
    static let mediaID = "media"
    static let teacherID = TeacherPlugin.id

    /// The tools: each gets a round button in the band's toolbar (in this order) instead of a
    /// place in the capsule.
    static let toolIDs: [String] = [shelfID, "clipboard", "notes", "timer"]

    /// In the order the capsule and Settings show them.
    @MainActor static var all: [any IslandComponent] {
        [SystemIslandComponent(), ShelfIslandComponent(), AgentsIslandComponent(), MediaIslandComponent(), TeacherIslandComponent()]
    }
}
