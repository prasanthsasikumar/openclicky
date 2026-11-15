// The bundled drawing library: its manifest must point at files that are really there, and the prompt may only name
// library ids that exist (the prompt's hard-coded space list is kept in step with the manifest by this test).

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenClicky.Core.Plugins.DrawStage;

namespace OpenClicky.Core.Tests.Plugins;

public class LibraryManifestTests
{
    private static string LibraryFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenClicky.Windows.sln")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "OpenClicky.Windows", "Assets", "DrawStage", "library");
    }

    private static JsonObject Manifest() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(LibraryFolder(), "manifest.json")))!.AsObject();

    private static IEnumerable<string> Ids(JsonArray entries) => entries.Select(entry => entry!["id"]!.GetValue<string>());

    [Fact]
    public void EveryListedLibraryFileExistsAndEveryIdIsUnique()
    {
        var folder = LibraryFolder();
        var manifest = Manifest();
        var ids = new List<string>();
        foreach (var (section, field) in new[] { ("models", "file"), ("planets", "texture") })
        {
            foreach (var entry in manifest[section]!.AsArray())
            {
                var id = entry!["id"]!.GetValue<string>();
                var file = entry[field]!.GetValue<string>();
                ids.Add(id);
                Assert.True(File.Exists(Path.Combine(folder, file)), $"{section}/{id}: {file} is missing");
            }
        }
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryAnatomyStructureHasAGroupsArray()
    {
        // The drawing stage's anatomy-index.js finds "heart", "cranium" and the
        // like through these topic groups, so every generated entry needs one.
        var path = Path.Combine(LibraryFolder(), "anatomy", "index.json");
        var index = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var structures = index["structures"]!.AsArray();
        Assert.NotEmpty(structures);
        foreach (var structure in structures)
        {
            var node = structure!["node"]!.GetValue<string>();
            var groups = structure["groups"];
            Assert.NotNull(groups);
            foreach (var group in groups!.AsArray())
            {
                Assert.False(string.IsNullOrWhiteSpace(group!.GetValue<string>()), $"{node}: empty group name");
            }
        }
    }

    [Fact]
    public void TheSystemPromptNamesOnlyLibraryIds()
    {
        var prompt = DrawStageLesson.SystemPrompt(1280, 720);
        Assert.Contains("f.model(", prompt);
        Assert.Contains("f.planet(", prompt);
        var manifest = Manifest();
        AssertListsExactly(prompt, "models", Ids(manifest["models"]!.AsArray()));
        AssertListsExactly(prompt, "planets", Ids(manifest["planets"]!.AsArray()));
    }

    // "... (models: iss, hubble, …)" names each id; a note after ';' is not part of the list.
    private static void AssertListsExactly(string prompt, string heading, IEnumerable<string> ids)
    {
        var match = Regex.Match(prompt, heading + @": ([^)]+)");
        Assert.True(match.Success, $"the prompt has no '{heading}:' list");
        var named = match.Groups[1].Value.Split(';')[0]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        var expected = ids.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected.Order(), named.Order());
    }
}
