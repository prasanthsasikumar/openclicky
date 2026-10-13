// Port of macos/OpenClicky/OpenClicky/SkillPromptBuilder.swift
//
// Assembles the skills block appended to the Realtime instructions and the Claude teacher prompt:
// the user's activated talk skills first, then the teaching skill for the app in front.
// Budgets keep the Realtime session instructions small (spec: app ≤ 4,000 chars, active ≤ 6,000).

namespace OpenClicky.Core.Skills;

public static class SkillPromptBuilder
{
    public static string Build(
        IReadOnlyList<SkillFile> activeSkills,
        SkillFile? appSkill,
        FrontAppContext? front,
        int activeBudget = 6000,
        int appBudget = 4000)
    {
        var sections = new List<string>();

        var remaining = activeBudget;
        foreach (var skill in activeSkills.Where(skill => skill.IsForTalk))
        {
            var section = $"## Skill: {skill.Name}\n{skill.Body}";
            // Whole skills only: a truncated style guide is worse than none.
            if (section.Length > remaining)
            {
                // TODO(integration): AppLog.Write once Configuration's AppLog lands (Mac prints this line).
                System.Diagnostics.Debug.WriteLine($"Skills: skipping \"{skill.Name}\" ({section.Length} chars over the talk budget)");
                continue;
            }
            remaining -= section.Length;
            sections.Add(section);
        }

        if (appSkill is not null)
        {
            var heading = $"## The app in front: {front?.AppName ?? appSkill.Name}";
            var details = new List<string>();
            // The Mac names the bundle id; on Windows (no bundle id) the process name takes its place.
            var appIdentifier = front?.BundleIdentifier ?? front?.ProcessName;
            if (appIdentifier is not null) details.Add(appIdentifier);
            var host = front?.Url?.Host;
            if (!string.IsNullOrEmpty(host)) details.Add(host);
            if (details.Count > 0) heading += $" ({string.Join(", ", details)})";
            sections.Add($"{heading}\n{Truncate(appSkill.Body, appBudget)}");
        }

        return string.Join("\n\n", sections);
    }

    /// <summary>
    /// Joins the skills block onto a base prompt after a blank line; an empty block leaves the base
    /// untouched. (Swift: <c>CompanionManager.composeTalkInstructions(base:skillsBlock:)</c>; it lives
    /// here so the Companion port can call it.)
    /// </summary>
    public static string ComposeTalkInstructions(string basePrompt, string skillsBlock)
    {
        var trimmed = skillsBlock.Trim();
        return trimmed.Length == 0 ? basePrompt : basePrompt + "\n\n" + trimmed;
    }

    private static string Truncate(string text, int budget)
    {
        if (text.Length <= budget) return text;
        return text[..Math.Max(budget - 1, 0)] + "…";
    }
}
