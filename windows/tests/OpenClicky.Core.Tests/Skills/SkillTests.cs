// Port of OpenClickyTests/SkillTests.swift — SKILL.md parsing, frontmost-app matching, and prompt
// assembly for the talk lanes. Windows additions: process-name matching for `apps:`.

using OpenClicky.Core.Skills;

namespace OpenClicky.Core.Tests.Skills;

public class SkillTests
{
    private static string Markdown(string name, string extra = "", string body = "body") =>
        $"---\nname: {name}\ndescription: d {name}\n{extra}---\n\n# {name}\n{body}\n";

    private static SkillFile Parse(string name, string id, string extra = "", string body = "body") =>
        SkillFile.Parse(Markdown(name, extra, body), id)!;

    // MARK: - SkillFile.Parse

    [Fact]
    public void ParsesFrontmatterWithInlineLists()
    {
        var skill = SkillFile.Parse(
            Markdown("figma", "apps: [com.figma.Desktop, \"com.figma.Beta\"]\nsites: [figma.com]\nsurfaces: [talk]\n"),
            "figma");
        Assert.NotNull(skill);
        Assert.Equal("figma", skill!.Id);
        Assert.Equal("figma", skill.Name);
        Assert.Equal("d figma", skill.Description);
        Assert.Equal(new[] { "com.figma.Desktop", "com.figma.Beta" }, skill.Apps);
        Assert.Equal(new[] { "figma.com" }, skill.Sites);
        Assert.Equal(new HashSet<string> { "talk" }, skill.Surfaces);
        Assert.Contains("# figma", skill.Body);
        Assert.True(skill.IsForTalk);
    }

    [Fact]
    public void DefaultsSurfacesAndRejectsMissingFrontmatter()
    {
        var defaulted = SkillFile.Parse(Markdown("x"), "x");
        Assert.Equal(new HashSet<string> { "talk", "agent" }, defaulted?.Surfaces);
        Assert.Null(SkillFile.Parse("# no frontmatter", "x"));
        Assert.Null(SkillFile.Parse("---\nname: x\n---\n", "x")); // description required
        var agentOnly = SkillFile.Parse(Markdown("y", "surfaces: [agent]\n"), "y");
        Assert.False(agentOnly?.IsForTalk);
    }

    [Fact]
    public void ParsesCrlfAndByteOrderMark()
    {
        var skill = SkillFile.Parse("﻿---\r\nname: Win\r\ndescription: d\r\napps: [Code.exe]\r\n---\r\nbody\r\n", "win");
        Assert.Equal("Win", skill?.Name);
        Assert.Equal(new[] { "Code.exe" }, skill?.Apps);
        Assert.Equal("body", skill?.Body);
    }

    [Fact]
    public void LoadsEverySkillDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skilltests-{Guid.NewGuid()}");
        try
        {
            foreach (var id in new[] { "beta", "alpha" })
            {
                var directory = Path.Combine(root, id);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "SKILL.md"), Markdown(char.ToUpperInvariant(id[0]) + id[1..]));
            }
            Directory.CreateDirectory(Path.Combine(root, "empty"));
            var loaded = SkillFile.Load(root);
            Assert.Equal(new[] { "alpha", "beta" }, loaded.Select(skill => skill.Id));
            Assert.Equal("Alpha", loaded.First().Name);
            Assert.Empty(SkillFile.Load(Path.Combine(root, "missing")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // MARK: - AppSkillMatcher

    private static SkillFile Gmail => Parse("Gmail", "gmail", "sites: [mail.google.com]\nsurfaces: [talk]\n");
    private static SkillFile Google => Parse("Google", "google", "sites: [google.com]\nsurfaces: [talk]\n");
    private static SkillFile Chrome => Parse("Google Chrome", "google-chrome", "apps: [com.google.Chrome, chrome.exe]\nsites: [chrome.google.com]\nsurfaces: [talk]\n");
    private static SkillFile Xcode => Parse("Xcode", "xcode", "apps: [com.apple.dt.Xcode]\nsurfaces: [talk]\n");
    private static IReadOnlyList<SkillFile> Skills => new[] { Chrome, Xcode, Google, Gmail };

    [Fact]
    public void PrefersSiteSkillOverBrowserAppSkill()
    {
        var context = new FrontAppContext { BundleIdentifier = "com.google.Chrome", AppName = "Google Chrome", Url = new Uri("https://mail.google.com/mail/u/0/#inbox"), WindowTitle = "Inbox" };
        Assert.Equal("gmail", AppSkillMatcher.Match(context, Skills)?.Id);
    }

    [Fact]
    public void HostSuffixMatchesSubdomainsOnly()
    {
        var subdomain = new FrontAppContext { BundleIdentifier = "com.google.Chrome", Url = new Uri("https://docs.google.com/x") };
        Assert.Equal("google", AppSkillMatcher.Match(subdomain, new[] { Xcode, Google })?.Id);
        var lookalike = new FrontAppContext { BundleIdentifier = "com.other.Browser", Url = new Uri("https://notgoogle.com/") };
        Assert.Null(AppSkillMatcher.Match(lookalike, new[] { Xcode, Google }));
    }

    [Fact]
    public void FallsBackToWindowTitleThenBundleIdentifier()
    {
        var titleOnly = new FrontAppContext { BundleIdentifier = "com.google.Chrome", WindowTitle = "Inbox (3) - MAIL.GOOGLE.COM", IsBrowser = true };
        Assert.Equal("gmail", AppSkillMatcher.Match(titleOnly, Skills)?.Id);
        var plainBrowser = new FrontAppContext { BundleIdentifier = "com.google.Chrome", Url = new Uri("https://example.org/"), WindowTitle = "Example" };
        Assert.Equal("google-chrome", AppSkillMatcher.Match(plainBrowser, Skills)?.Id);
        var ide = new FrontAppContext { BundleIdentifier = "com.apple.dt.Xcode", AppName = "Xcode", WindowTitle = "Project" };
        Assert.Equal("xcode", AppSkillMatcher.Match(ide, Skills)?.Id);
    }

    [Fact]
    public void MatchesWindowsProcessNameCaseInsensitively()
    {
        var chromeOnWindows = new FrontAppContext { ProcessName = "CHROME.EXE", AppName = "Google Chrome", Url = new Uri("https://example.org/") };
        Assert.Equal("google-chrome", AppSkillMatcher.Match(chromeOnWindows, Skills)?.Id);
        var bundleDifferentCase = new FrontAppContext { BundleIdentifier = "com.apple.DT.xcode" };
        Assert.Equal("xcode", AppSkillMatcher.Match(bundleDifferentCase, Skills)?.Id);
        var unknownExe = new FrontAppContext { ProcessName = "notepad.exe" };
        Assert.Null(AppSkillMatcher.Match(unknownExe, Skills));
    }

    [Fact]
    public void WindowTitleFallbackIsBrowsersOnly()
    {
        var github = Parse("GitHub", "github", "sites: [github.com]\nsurfaces: [talk]\n");
        var terminal = Parse("Terminal", "terminal", "apps: [com.apple.Terminal, WindowsTerminal.exe]\nsurfaces: [talk]\n");
        var ssh = new FrontAppContext { BundleIdentifier = "com.apple.Terminal", AppName = "Terminal", WindowTitle = "ssh — github.com", IsBrowser = false };
        Assert.Equal("terminal", AppSkillMatcher.Match(ssh, new[] { github, terminal })?.Id);
        var sshOnWindows = new FrontAppContext { ProcessName = "WindowsTerminal.exe", WindowTitle = "ssh — github.com" };
        Assert.Equal("terminal", AppSkillMatcher.Match(sshOnWindows, new[] { github, terminal })?.Id);
        var browserTitle = new FrontAppContext { BundleIdentifier = "com.google.Chrome", WindowTitle = "openclicky - github.com", IsBrowser = true };
        Assert.Equal("github", AppSkillMatcher.Match(browserTitle, new[] { github, terminal, Chrome })?.Id);
    }

    [Fact]
    public void ReturnsNullWhenNothingMatches()
    {
        var context = new FrontAppContext { BundleIdentifier = "com.apple.finder", AppName = "Finder", WindowTitle = "Desktop" };
        Assert.Null(AppSkillMatcher.Match(context, Skills));
        Assert.Null(AppSkillMatcher.Match(new FrontAppContext(), Skills));
    }

    // MARK: - SkillPromptBuilder

    [Fact]
    public void BuildsSkillAndAppBlocks()
    {
        var style = Parse("Pirate", "pirate", body: "talk like a pirate");
        var context = new FrontAppContext { BundleIdentifier = "com.google.Chrome", AppName = "Google Chrome", Url = new Uri("https://mail.google.com/") };
        var output = SkillPromptBuilder.Build(new[] { style }, Gmail, context);
        Assert.StartsWith("## Skill: Pirate\n# Pirate\ntalk like a pirate", output);
        Assert.Contains("\n\n## The app in front: Google Chrome (com.google.Chrome, mail.google.com)\n# Gmail\nbody", output);
    }

    [Fact]
    public void AppHeadingNamesTheProcessWhenThereIsNoBundleIdentifier()
    {
        var context = new FrontAppContext { ProcessName = "chrome.exe", AppName = "Google Chrome", Url = new Uri("https://mail.google.com/") };
        var output = SkillPromptBuilder.Build(Array.Empty<SkillFile>(), Gmail, context);
        Assert.StartsWith("## The app in front: Google Chrome (chrome.exe, mail.google.com)\n", output);
    }

    [Fact]
    public void TruncatesAppSkillToBudget()
    {
        var longSkill = Parse("Big", "big", body: new string('x', 500));
        var output = SkillPromptBuilder.Build(Array.Empty<SkillFile>(), longSkill, new FrontAppContext { BundleIdentifier = "com.big", AppName = "Big" }, appBudget: 100);
        var body = string.Join("\n", output.Split('\n').Skip(1));
        Assert.EndsWith("…", body);
        Assert.True(body.Length <= 101);
        Assert.StartsWith("## The app in front: Big (com.big)\n", output);
    }

    [Fact]
    public void DropsWholeActiveSkillsBeyondBudgetAndNonTalkSkills()
    {
        var skillA = Parse("A", "a", body: new string('a', 60));
        var skillB = Parse("B", "b", body: new string('b', 60));
        var agentOnly = Parse("Ops", "ops", "surfaces: [agent]\n");
        var output = SkillPromptBuilder.Build(new[] { agentOnly, skillA, skillB }, null, null, activeBudget: 100);
        Assert.Contains("## Skill: A", output);
        Assert.DoesNotContain("## Skill: B", output);
        Assert.DoesNotContain("## Skill: Ops", output);
    }

    [Fact]
    public void EmptyWhenNothingToInject()
    {
        var agentOnly = Parse("Ops", "ops", "surfaces: [agent]\n");
        Assert.Equal("", SkillPromptBuilder.Build(Array.Empty<SkillFile>(), null, null));
        Assert.Equal("", SkillPromptBuilder.Build(new[] { agentOnly }, null, new FrontAppContext { BundleIdentifier = "x" }));
    }
}
