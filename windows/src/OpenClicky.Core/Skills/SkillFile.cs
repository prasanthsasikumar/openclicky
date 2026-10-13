// Port of macos/OpenClicky/OpenClicky/SkillFile.swift
//
// A Hermes-style SKILL.md: YAML frontmatter (flat keys, inline `[a, b]` lists) followed by a
// Markdown body. Mirrors agent/src/skillMarkdown.ts so the app, the CLI, and the backend agree on
// what a skill is. Used for the app-teaching skills in `app-skills/` and the user library in
// `%USERPROFILE%\.openclicky\skills\library`.

using System.Text;
using System.Text.RegularExpressions;

namespace OpenClicky.Core.Skills;

public sealed record SkillFile(
    string Id,
    string Name,
    string Description,
    /// <summary>App identifiers this skill applies to (app-teaching skills only): Mac bundle ids
    /// (`com.figma.Desktop`) and/or Windows process image names (`Figma.exe`).</summary>
    IReadOnlyList<string> Apps,
    /// <summary>Browser host suffixes this skill applies to (app-teaching skills only).</summary>
    IReadOnlyList<string> Sites,
    /// <summary>Where the skill is injected: "talk" (voice / teacher prompts) and/or "agent" (Codex).</summary>
    IReadOnlySet<string> Surfaces,
    /// <summary>The Composio toolkit behind the app ("gmail", "youtube"); null for plain apps.</summary>
    string? Integration,
    string Body)
{
    public bool IsForTalk => Surfaces.Contains("talk");

    private static readonly Regex FrontmatterPattern =
        new(@"^---\r?\n([\s\S]*?)\r?\n---\r?\n?([\s\S]*)$", RegexOptions.CultureInvariant);

    private static readonly Regex KeyValuePattern =
        new(@"^([A-Za-z_][\w-]*):\s*(.*)$", RegexOptions.CultureInvariant);

    /// <summary>Parses one SKILL.md. Returns null without frontmatter or without `name` / `description`.</summary>
    public static SkillFile? Parse(string markdown, string id)
    {
        var text = markdown.StartsWith('﻿') ? markdown[1..] : markdown;
        var match = FrontmatterPattern.Match(text);
        if (!match.Success) return null;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in match.Groups[1].Value.Split('\n', '\r'))
        {
            var keyValueMatch = KeyValuePattern.Match(line);
            if (!keyValueMatch.Success) continue;
            fields[keyValueMatch.Groups[1].Value] = StripQuotes(keyValueMatch.Groups[2].Value.Trim(' ', '\t'));
        }
        if (!fields.TryGetValue("name", out var name) || name.Length == 0) return null;
        if (!fields.TryGetValue("description", out var description) || description.Length == 0) return null;

        var surfaces = fields.TryGetValue("surfaces", out var surfacesValue)
            ? ParseList(surfacesValue)
            : new List<string> { "talk", "agent" };
        fields.TryGetValue("integration", out var integration);
        return new SkillFile(
            Id: id,
            Name: name,
            Description: description,
            Apps: fields.TryGetValue("apps", out var apps) ? ParseList(apps) : new List<string>(),
            Sites: fields.TryGetValue("sites", out var sites) ? ParseList(sites) : new List<string>(),
            Surfaces: new HashSet<string>(surfaces, StringComparer.Ordinal),
            Integration: string.IsNullOrEmpty(integration) ? null : integration,
            Body: match.Groups[2].Value.Trim());
    }

    /// <summary>Every `&lt;directory&gt;/&lt;id&gt;/SKILL.md`, sorted by id. Missing directory → empty.</summary>
    public static IReadOnlyList<SkillFile> Load(string directory)
    {
        var skills = new List<SkillFile>();
        string[] entries;
        try { entries = Directory.GetDirectories(directory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return skills; }

        foreach (var entry in entries)
        {
            string markdown;
            try { markdown = File.ReadAllText(Path.Combine(entry, "SKILL.md"), Encoding.UTF8); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            var skill = Parse(markdown, Path.GetFileName(entry));
            if (skill is not null) skills.Add(skill);
        }
        skills.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return skills;
    }

    // MARK: - Helpers

    private static string StripQuotes(string value)
    {
        if (value.Length < 2) return value;
        var first = value[0];
        var last = value[^1];
        if (first != last || (first != '"' && first != '\'')) return value;
        return value[1..^1];
    }

    /// <summary>`[a, "b", 'c']` (or a bare comma list) → ["a", "b", "c"].</summary>
    private static List<string> ParseList(string value)
    {
        var inner = value.Trim(' ', '\t');
        if (inner.StartsWith('[')) inner = inner[1..];
        if (inner.EndsWith(']')) inner = inner[..^1];
        return inner.Split(',')
            .Select(item => StripQuotes(item.Trim(' ', '\t')))
            .Where(item => item.Length > 0)
            .ToList();
    }
}
