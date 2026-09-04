//
//  SkillPromptBuilder.swift
//  OpenClicky
//
//  Assembles the skills block appended to the Realtime instructions and the Claude teacher prompt:
//  the user's activated talk skills first, then the teaching skill for the app in front.
//  Budgets keep the Realtime session instructions small (spec: app ≤ 4,000 chars, active ≤ 6,000).
//

import Foundation

enum SkillPromptBuilder {

    static func build(
        activeSkills: [SkillFile],
        appSkill: SkillFile?,
        front: FrontAppContext?,
        activeBudget: Int = 6000,
        appBudget: Int = 4000
    ) -> String {
        var sections: [String] = []

        var remaining = activeBudget
        for skill in activeSkills where skill.isForTalk {
            let section = "## Skill: \(skill.name)\n\(skill.body)"
            // Whole skills only: a truncated style guide is worse than none.
            guard section.count <= remaining else {
                print("🧩 Skills: skipping \"\(skill.name)\" (\(section.count) chars over the talk budget)")
                continue
            }
            remaining -= section.count
            sections.append(section)
        }

        if let appSkill {
            var heading = "## The app in front: \(front?.appName ?? appSkill.name)"
            var details: [String] = []
            if let bundleIdentifier = front?.bundleIdentifier { details.append(bundleIdentifier) }
            if let host = front?.url?.host { details.append(host) }
            if !details.isEmpty { heading += " (\(details.joined(separator: ", ")))" }
            sections.append("\(heading)\n\(truncate(appSkill.body, to: appBudget))")
        }

        return sections.joined(separator: "\n\n")
    }

    private static func truncate(_ text: String, to budget: Int) -> String {
        guard text.count > budget else { return text }
        return String(text.prefix(max(budget - 1, 0))) + "…"
    }
}
