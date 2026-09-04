//
//  AppSkillMatcher.swift
//  OpenClicky
//
//  Picks the app-teaching skill for whatever is in front of the user. Pure function so it is
//  testable; FrontmostAppObserver produces the context from NSWorkspace + Accessibility.
//

import Foundation

struct FrontAppContext: Equatable {
    var bundleIdentifier: String?
    var appName: String?
    /// The front browser tab's URL when it could be read (Accessibility), else nil.
    var url: URL?
    var windowTitle: String?
}

enum AppSkillMatcher {

    /// Order: a site skill matching the URL host (exact or `.suffix`), then a site skill named in
    /// the window title, then the app skill for the bundle identifier. Among site matches the most
    /// specific site wins (`mail.google.com` beats `google.com`); otherwise the first listed skill.
    static func match(_ ctx: FrontAppContext, in skills: [SkillFile]) -> SkillFile? {
        if let host = ctx.url?.host?.lowercased(),
           let bySite = bestSiteMatch(in: skills, where: { hostMatches(host, site: $0) }) {
            return bySite
        }
        if let title = ctx.windowTitle?.lowercased(), !title.isEmpty,
           let byTitle = bestSiteMatch(in: skills, where: { title.contains($0.lowercased()) }) {
            return byTitle
        }
        if let bundleIdentifier = ctx.bundleIdentifier,
           let byApp = skills.first(where: { $0.apps.contains(bundleIdentifier) }) {
            return byApp
        }
        return nil
    }

    /// The skill whose longest matching site is the longest overall; ties keep list order.
    private static func bestSiteMatch(in skills: [SkillFile], where matches: (String) -> Bool) -> SkillFile? {
        var best: (skill: SkillFile, length: Int)?
        for skill in skills {
            guard let longest = skill.sites.filter(matches).map(\.count).max() else { continue }
            if best == nil || longest > best!.length { best = (skill, longest) }
        }
        return best?.skill
    }

    static func hostMatches(_ host: String, site: String) -> Bool {
        let site = site.lowercased()
        return host == site || host.hasSuffix("." + site)
    }
}
