// Port of OpenClickyTests/SkillLibraryStoreTests.swift — the on-disk skill library (library/,
// active/ entries, activations.json) shared with the CLI. On the Mac `active/<id>` is always a
// symlink; on Windows it is a symlink, a junction or a copy (see SkillLibraryStore), so the
// "is linked" checks accept any of the three.

using System.Text.Json.Nodes;
using OpenClicky.Core.Skills;

namespace OpenClicky.Core.Tests.Skills;

public sealed class SkillLibraryStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"oc-skills-{Guid.NewGuid()}");

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    private static string Markdown(string name, string? surfaces = null)
    {
        var surfacesLine = surfaces is null ? "" : $"surfaces: {surfaces}\n";
        return $"---\nname: {name}\ndescription: d {name}\n{surfacesLine}---\n\n# {name}\nbody\n";
    }

    private SkillLibraryStore MakeStore(Func<SkillDraftRequest, CancellationToken, Task<string>>? draft = null)
    {
        var appSkills = Path.Combine(root, "app-skills");
        Directory.CreateDirectory(appSkills);
        return new SkillLibraryStore(Path.Combine(root, "skills"), appSkills, watch: false,
            now: () => new DateTimeOffset(2026, 10, 13, 9, 30, 0, TimeSpan.Zero), draftSkillMarkdown: draft);
    }

    /// <summary>A link (symlink / junction) or a copied folder holding the skill.</summary>
    private static bool IsActiveEntry(string path) =>
        new DirectoryInfo(path).LinkTarget is not null || File.Exists(Path.Combine(path, "SKILL.md"));

    [Fact]
    public void ImportWritesFileActivatesAndLinks()
    {
        using var store = MakeStore();
        var skill = store.ImportSkill(Markdown("Write Like Me"));
        Assert.Equal("write-like-me", skill.Id);
        Assert.True(File.Exists(Path.Combine(store.LibraryDirectory, "write-like-me", "SKILL.md")));
        Assert.Equal(new[] { "write-like-me" }, store.LibrarySkills.Select(item => item.Id));
        Assert.Equal(new HashSet<string> { "write-like-me" }, store.ActiveIds);
        Assert.True(IsActiveEntry(Path.Combine(store.ActiveDirectory, "write-like-me")));

        var json = JsonNode.Parse(File.ReadAllText(store.ActivationsPath))!.AsObject();
        Assert.Equal(new[] { "write-like-me" }, json["active"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal("2026-10-13T09:30:00Z", json["updatedAt"]!.GetValue<string>());
    }

    [Fact]
    public void RemovingDeletesTheFolderTheLinkAndTheActivation()
    {
        using var store = MakeStore();
        store.ImportSkill(Markdown("Pirate"));
        store.ImportSkill(Markdown("Keep"));
        Assert.Equal(new[] { "keep", "pirate" }, store.ActiveIds.OrderBy(id => id, StringComparer.Ordinal));

        store.RemoveSkill("pirate");

        Assert.Equal(new[] { "keep" }, store.LibrarySkills.Select(item => item.Id));
        Assert.Equal(new HashSet<string> { "keep" }, store.ActiveIds);
        Assert.False(Directory.Exists(Path.Combine(store.LibraryDirectory, "pirate")));
        Assert.False(IsActiveEntry(Path.Combine(store.ActiveDirectory, "pirate")));
        Assert.Null(store.LastError);
    }

    [Fact]
    public void DeactivatingRemovesTheLinkAndKeepsTheFile()
    {
        using var store = MakeStore();
        store.ImportSkill(Markdown("Pirate"));
        store.SetActive("pirate", false);
        Assert.Empty(store.ActiveIds);
        Assert.False(IsActiveEntry(Path.Combine(store.ActiveDirectory, "pirate")));
        Assert.Equal(new[] { "pirate" }, store.LibrarySkills.Select(item => item.Id));
        store.SetActive("pirate", true);
        Assert.True(IsActiveEntry(Path.Combine(store.ActiveDirectory, "pirate")));
    }

    [Fact]
    public void RemovingALinkNeverTouchesTheLibraryFolder()
    {
        using var store = MakeStore();
        store.ImportSkill(Markdown("Pirate"));
        store.SetActive("pirate", false);
        Assert.True(File.Exists(Path.Combine(store.LibraryDirectory, "pirate", "SKILL.md")));
    }

    [Fact]
    public void StaleLinksAndUnknownActivationsAreDropped()
    {
        using var store = MakeStore();
        // The Mac plants a dangling symlink; creating one may need Developer Mode on Windows, so a
        // stale copied folder stands in when it cannot be made.
        var ghost = Path.Combine(store.ActiveDirectory, "ghost");
        try { Directory.CreateSymbolicLink(ghost, Path.Combine(root, "nowhere")); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Directory.CreateDirectory(ghost);
            File.WriteAllText(Path.Combine(ghost, "SKILL.md"), Markdown("Ghost"));
        }
        File.WriteAllText(store.ActivationsPath, "{ \"active\": [\"gone\", \"pirate\"] }");
        store.ImportSkill(Markdown("Pirate"));
        store.Reload();
        Assert.False(IsActiveEntry(ghost));
        Assert.False(Directory.Exists(ghost));
        Assert.Equal(new HashSet<string> { "pirate" }, store.ActiveIds);
    }

    [Fact]
    public void ActiveTalkSkillsExcludesAgentOnlySkills()
    {
        using var store = MakeStore();
        store.ImportSkill(Markdown("Deploy Flow", "[agent]"));
        store.ImportSkill(Markdown("Warm Tone", "[talk]"));
        store.ImportSkill(Markdown("Both"));
        Assert.Equal(new[] { "both", "warm-tone" }, store.ActiveTalkSkills.Select(item => item.Id));
    }

    [Fact]
    public void IdsAreDedupedAndSlugified()
    {
        using var store = MakeStore();
        Assert.Equal("same", store.ImportSkill(Markdown("Same")).Id);
        Assert.Equal("same-2", store.ImportSkill(Markdown("Same")).Id);
        Assert.Equal("same-3", store.ImportSkill(Markdown("Same")).Id);
        Assert.Equal("hello-world", SkillLibraryStore.Slugify("  Hello, World! "));
        Assert.Equal("skill", SkillLibraryStore.Slugify("日本語"));
    }

    [Fact]
    public void ImportNeverWritesIntoAFolderItDidNotCreate()
    {
        using var store = MakeStore();
        foreach (var existing in new[] { "foo", "foo-2" })
        {
            var folder = Path.Combine(store.LibraryDirectory, existing);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), Markdown($"Original {existing}"));
        }
        var imported = store.ImportSkill(Markdown("Foo"));
        Assert.Equal("foo-3", imported.Id);
        foreach (var existing in new[] { "foo", "foo-2" })
        {
            var text = File.ReadAllText(Path.Combine(store.LibraryDirectory, existing, "SKILL.md"));
            Assert.Contains($"name: Original {existing}", text);
        }
        Assert.Equal(new[] { "foo", "foo-2", "foo-3" }, store.LibrarySkills.Select(item => item.Id));
    }

    [Fact]
    public void FailedActivationSaveKeepsItsError()
    {
        using var store = MakeStore();
        store.ImportSkill(Markdown("Keep"));
        // Make the activations file unwritable: replace it with a directory of the same name.
        File.Delete(store.ActivationsPath);
        Directory.CreateDirectory(store.ActivationsPath);
        store.SetActive("keep", false);
        Assert.Contains("Could not save activations", store.LastError);
        store.Reload();
        Assert.Null(store.LastError);
    }

    [Fact]
    public void InvalidMarkdownIsRejected()
    {
        using var store = MakeStore();
        var error = Assert.Throws<SkillLibraryException>(() => store.ImportSkill("# no frontmatter"));
        Assert.Equal(SkillLibraryErrorKind.InvalidMarkdown, error.Kind);
        Assert.Empty(store.LibrarySkills);
    }

    [Fact]
    public void AppSkillsLoadFromTheirDirectory()
    {
        using var store = MakeStore();
        var figma = Path.Combine(root, "app-skills", "figma");
        Directory.CreateDirectory(figma);
        File.WriteAllText(Path.Combine(figma, "SKILL.md"), "---\nname: Figma\ndescription: d\napps: [com.figma.Desktop]\nsurfaces: [talk]\n---\nbody\n");
        store.Reload();
        Assert.Equal(new[] { "figma" }, store.AppSkills.Select(item => item.Id));
        Assert.Equal(new[] { "com.figma.Desktop" }, store.AppSkills.First().Apps);
    }

    // MARK: - Windows additions: create-skill through the injected drafter

    [Fact]
    public async Task CreateSkillStoresAndActivatesTheDraft()
    {
        SkillDraftRequest? seen = null;
        using var store = MakeStore((request, _) =>
        {
            seen = request;
            return Task.FromResult(Markdown("Morning Brief"));
        });
        var skill = await store.CreateSkillAsync("  summarise my morning  ");
        Assert.Equal("summarise my morning", seen?.Request);
        Assert.Equal("morning-brief", skill.Id);
        Assert.Contains("morning-brief", store.ActiveIds);
        Assert.False(store.IsCreating);
        Assert.Null(store.LastError);
    }

    [Fact]
    public async Task CreateSkillRecordsErrors()
    {
        using var store = MakeStore((_, _) => throw SkillLibraryException.Backend("backend 500: boom"));
        var empty = await Assert.ThrowsAsync<SkillLibraryException>(() => store.CreateSkillAsync("   "));
        Assert.Equal(SkillLibraryErrorKind.EmptyRequest, empty.Kind);
        Assert.Equal("Describe what the skill should do.", store.LastError);
        await Assert.ThrowsAsync<SkillLibraryException>(() => store.CreateSkillAsync("x"));
        Assert.Equal("backend 500: boom", store.LastError);
        Assert.False(store.IsCreating);
    }

    [Fact]
    public void BackendResponseIsReadLikeTheMac()
    {
        Assert.Equal("md", SkillLibraryStore.MarkdownFromBackendResponse(200, "{\"markdown\":\"md\"}"u8.ToArray()));
        Assert.Equal("nope", Assert.Throws<SkillLibraryException>(() => SkillLibraryStore.MarkdownFromBackendResponse(400, "{\"error\":\"nope\"}"u8.ToArray())).Message);
        Assert.Equal("backend 502: bad gateway", Assert.Throws<SkillLibraryException>(() => SkillLibraryStore.MarkdownFromBackendResponse(502, "bad gateway"u8.ToArray())).Message);
        Assert.Equal("backend returned 500", Assert.Throws<SkillLibraryException>(() => SkillLibraryStore.MarkdownFromBackendResponse(500, Array.Empty<byte>())).Message);
        Assert.Equal("no markdown in response", Assert.Throws<SkillLibraryException>(() => SkillLibraryStore.MarkdownFromBackendResponse(200, "{}"u8.ToArray())).Message);
        Assert.Equal("{\"request\":\"r\",\"capabilities\":[\"composio\"]}", SkillLibraryStore.BackendCreateRequestBody("r", new[] { "composio" }));
    }
}
