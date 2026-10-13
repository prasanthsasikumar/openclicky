//
//  SkillLibraryStoreTests.swift
//  OpenClickyTests
//
//  The on-disk skill library (library/, active/ symlinks, activations.json) shared with the CLI.
//

import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct SkillLibraryStoreTests {

    private func markdown(name: String, surfaces: String? = nil) -> String {
        let surfacesLine = surfaces.map { "surfaces: \($0)\n" } ?? ""
        return "---\nname: \(name)\ndescription: d \(name)\n\(surfacesLine)---\n\n# \(name)\nbody\n"
    }

    private func makeStore() throws -> (SkillLibraryStore, URL) {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("oc-skills-\(UUID().uuidString)", isDirectory: true)
        let appSkills = root.appendingPathComponent("app-skills", isDirectory: true)
        try FileManager.default.createDirectory(at: appSkills, withIntermediateDirectories: true)
        let store = SkillLibraryStore(userSkillsDirectory: root.appendingPathComponent("skills", isDirectory: true),
                                      appSkillsDirectory: appSkills,
                                      watch: false)
        return (store, root)
    }

    private func isSymlink(_ url: URL) -> Bool {
        (try? FileManager.default.destinationOfSymbolicLink(atPath: url.path)) != nil
    }

    @Test func importWritesFileActivatesAndLinks() throws {
        let (store, _) = try makeStore()
        let skill = try store.importSkill(markdown: markdown(name: "Write Like Me"))
        #expect(skill.id == "write-like-me")
        #expect(FileManager.default.fileExists(atPath: store.libraryDirectory.appendingPathComponent("write-like-me/SKILL.md").path))
        #expect(store.librarySkills.map(\.id) == ["write-like-me"])
        #expect(store.activeIds == ["write-like-me"])
        #expect(isSymlink(store.activeDirectory.appendingPathComponent("write-like-me")))

        let data = try Data(contentsOf: store.activationsURL)
        let object = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
        #expect(object["active"] as? [String] == ["write-like-me"])
        #expect((object["updatedAt"] as? String)?.isEmpty == false)
    }

    @Test func removingDeletesTheFolderTheLinkAndTheActivation() throws {
        let (store, _) = try makeStore()
        try store.importSkill(markdown: markdown(name: "Pirate"))
        try store.importSkill(markdown: markdown(name: "Keep"))
        #expect(store.activeIds.sorted() == ["keep", "pirate"])

        store.removeSkill("pirate")

        #expect(store.librarySkills.map(\.id) == ["keep"])
        #expect(store.activeIds == ["keep"])
        #expect(!FileManager.default.fileExists(atPath: store.libraryDirectory.appendingPathComponent("pirate").path))
        #expect(!isSymlink(store.activeDirectory.appendingPathComponent("pirate")))
        #expect(store.lastError == nil)
    }

    @Test func deactivatingRemovesTheLinkAndKeepsTheFile() throws {
        let (store, _) = try makeStore()
        try store.importSkill(markdown: markdown(name: "Pirate"))
        store.setActive("pirate", false)
        #expect(store.activeIds.isEmpty)
        #expect(!isSymlink(store.activeDirectory.appendingPathComponent("pirate")))
        #expect(store.librarySkills.map(\.id) == ["pirate"])
        store.setActive("pirate", true)
        #expect(isSymlink(store.activeDirectory.appendingPathComponent("pirate")))
    }

    @Test func staleLinksAndUnknownActivationsAreDropped() throws {
        let (store, _) = try makeStore()
        try FileManager.default.createSymbolicLink(atPath: store.activeDirectory.appendingPathComponent("ghost").path,
                                                   withDestinationPath: "/nowhere")
        try "{ \"active\": [\"gone\", \"pirate\"] }".write(to: store.activationsURL, atomically: true, encoding: .utf8)
        try store.importSkill(markdown: markdown(name: "Pirate"))
        store.reload()
        #expect(!isSymlink(store.activeDirectory.appendingPathComponent("ghost")))
        #expect(store.activeIds == ["pirate"])
    }

    @Test func activeTalkSkillsExcludesAgentOnlySkills() throws {
        let (store, _) = try makeStore()
        try store.importSkill(markdown: markdown(name: "Deploy Flow", surfaces: "[agent]"))
        try store.importSkill(markdown: markdown(name: "Warm Tone", surfaces: "[talk]"))
        try store.importSkill(markdown: markdown(name: "Both"))
        #expect(store.activeTalkSkills.map(\.id) == ["both", "warm-tone"])
    }

    @Test func idsAreDedupedAndSlugified() throws {
        let (store, _) = try makeStore()
        #expect(try store.importSkill(markdown: markdown(name: "Same")).id == "same")
        #expect(try store.importSkill(markdown: markdown(name: "Same")).id == "same-2")
        #expect(try store.importSkill(markdown: markdown(name: "Same")).id == "same-3")
        #expect(SkillLibraryStore.slugify("  Hello, World! ") == "hello-world")
        #expect(SkillLibraryStore.slugify("日本語") == "skill")
    }

    @Test func importNeverWritesIntoAFolderItDidNotCreate() throws {
        let (store, _) = try makeStore()
        for existing in ["foo", "foo-2"] {
            let folder = store.libraryDirectory.appendingPathComponent(existing, isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            try markdown(name: "Original \(existing)").write(to: folder.appendingPathComponent("SKILL.md"), atomically: true, encoding: .utf8)
        }
        let imported = try store.importSkill(markdown: markdown(name: "Foo"))
        #expect(imported.id == "foo-3")
        for existing in ["foo", "foo-2"] {
            let text = try String(contentsOf: store.libraryDirectory.appendingPathComponent("\(existing)/SKILL.md"), encoding: .utf8)
            #expect(text.contains("name: Original \(existing)"))
        }
        #expect(store.librarySkills.map(\.id) == ["foo", "foo-2", "foo-3"])
    }

    @Test func failedActivationSaveKeepsItsError() throws {
        let (store, root) = try makeStore()
        _ = try store.importSkill(markdown: markdown(name: "Keep"))
        // Make the activations file unwritable: replace it with a directory of the same name.
        try FileManager.default.removeItem(at: store.activationsURL)
        try FileManager.default.createDirectory(at: store.activationsURL, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: root) }
        store.setActive("keep", false)
        #expect(store.lastError?.contains("Could not save activations") == true)
        store.reload()
        #expect(store.lastError == nil)
    }

    @Test func invalidMarkdownIsRejected() throws {
        let (store, _) = try makeStore()
        #expect(throws: SkillLibraryError.self) { try store.importSkill(markdown: "# no frontmatter") }
        #expect(store.librarySkills.isEmpty)
    }

    @Test func appSkillsLoadFromTheirDirectory() throws {
        let (store, root) = try makeStore()
        let figma = root.appendingPathComponent("app-skills/figma", isDirectory: true)
        try FileManager.default.createDirectory(at: figma, withIntermediateDirectories: true)
        try "---\nname: Figma\ndescription: d\napps: [com.figma.Desktop]\nsurfaces: [talk]\n---\nbody\n"
            .write(to: figma.appendingPathComponent("SKILL.md"), atomically: true, encoding: .utf8)
        store.reload()
        #expect(store.appSkills.map(\.id) == ["figma"])
        #expect(store.appSkills.first?.apps == ["com.figma.Desktop"])
    }

    private func writeAppSkill(in directory: URL, id: String, name: String, bundleIdentifier: String) throws {
        let folder = directory.appendingPathComponent(id, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try "---\nname: \(name)\ndescription: d\napps: [\(bundleIdentifier)]\nsurfaces: [talk]\n---\nbody\n"
            .write(to: folder.appendingPathComponent("SKILL.md"), atomically: true, encoding: .utf8)
    }

    /// The skills inside the app reach every install; a local folder (a checkout or
    /// ~/.openclicky/app-skills) adds to them, and its skill wins over the bundled one with the same id.
    @Test func bundledAppSkillsLoadAndALocalFolderOverridesThemById() throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("oc-skills-\(UUID().uuidString)", isDirectory: true)
        let bundled = root.appendingPathComponent("bundled-app-skills", isDirectory: true)
        let local = root.appendingPathComponent("app-skills", isDirectory: true)
        try writeAppSkill(in: bundled, id: "figma", name: "Figma (bundled)", bundleIdentifier: "com.figma.Desktop")
        try writeAppSkill(in: bundled, id: "notes", name: "Notes", bundleIdentifier: "com.apple.Notes")
        try writeAppSkill(in: local, id: "figma", name: "Figma (mine)", bundleIdentifier: "com.figma.Desktop")
        try writeAppSkill(in: local, id: "canva", name: "Canva", bundleIdentifier: "com.canva.CanvaDesktop")

        let store = SkillLibraryStore(userSkillsDirectory: root.appendingPathComponent("skills", isDirectory: true),
                                      appSkillsDirectory: local,
                                      bundledAppSkillsDirectory: bundled,
                                      watch: false)

        #expect(store.appSkills.map(\.id) == ["canva", "figma", "notes"])
        #expect(store.appSkills.first { $0.id == "figma" }?.name == "Figma (mine)")
    }

    /// The release build copies the repository's app-skills/ into Contents/Resources (the
    /// "Embed app skills" build phase); the tests run inside that app.
    @Test func theAppCarriesItsAppSkillsIncludingDaVinciResolve() throws {
        let bundled = try #require(OpenClickyConfiguration.bundledAppSkillsDirectory)
        let skills = SkillFile.load(directory: bundled)
        #expect(skills.contains { $0.id == "figma" })
        let resolve = try #require(skills.first { $0.id == "davinci-resolve" })
        #expect(resolve.isForTalk)
        for bundleIdentifier in ["com.blackmagic-design.DaVinciResolve", "com.blackmagic-design.DaVinciResolveLite"] {
            let front = FrontAppContext(bundleIdentifier: bundleIdentifier, appName: "DaVinci Resolve")
            #expect(AppSkillMatcher.match(front, in: skills)?.id == "davinci-resolve")
        }
    }
}
