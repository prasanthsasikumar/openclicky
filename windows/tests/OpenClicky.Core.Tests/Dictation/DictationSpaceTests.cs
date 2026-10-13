// Ports DictationSpaceTests.swift: styles, dictionary and shortcuts — seeded defaults, app
// assignment (by Windows process name), and the JSON files (the Mac's shape).

using System.Text.Json.Nodes;
using OpenClicky.Core.Dictation;

namespace OpenClicky.Core.Tests.Dictation;

public sealed class DictationSpaceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"openclicky-space-{Guid.NewGuid()}");

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void TheSeededStylesClaimKnownAppsAndFallBackToOther()
    {
        var space = DictationSpace.Empty;
        Assert.Equal("developer", space.Style("Code.exe").Id);
        Assert.Equal("work-messaging", space.Style("slack.exe").Id);
        Assert.Equal("work-messaging", space.Style("ms-teams.exe").Id);
        Assert.Equal("email", space.Style("OUTLOOK.EXE").Id);
        Assert.Equal("email", space.Style("olk.exe").Id);
        Assert.Equal("developer", space.Style("WindowsTerminal.exe").Id);
        Assert.Equal("other", space.Style("example.exe").Id);
        // Browsers carry Slack, GitHub and Notion as much as Gmail: they are "other apps", not email.
        Assert.Equal("other", space.Style("chrome.exe").Id);
        Assert.Equal("other", space.Style("msedge.exe").Id);
        Assert.Equal("other", space.Style(null).Id);
    }

    [Fact]
    public void ProcessNamesAreComparedWithoutCase()
    {
        Assert.Equal("email", DictationSpace.Empty.Style("outlook.exe").Id);
        Assert.Equal("developer", DictationSpace.Empty.Style("CODE.EXE").Id);
    }

    [Fact]
    public void FiveStylesAreSeededWithTheMacsWords()
    {
        var styles = DictationStyle.Seeded();
        Assert.Equal(["developer", "work-messaging", "personal-messaging", "email", "other"], styles.Select(style => style.Id));
        Assert.False(styles.First(style => style.Id == "personal-messaging").SentenceCase);
        Assert.Empty(styles.First(style => style.Id == "other").AppProcessNames);
    }

    [Fact]
    public void AssigningAnAppMovesItBetweenStyles()
    {
        var space = DictationSpace.Empty;
        space.Assign("Code.exe", "email");
        Assert.Equal("email", space.Style("Code.exe").Id);
        Assert.DoesNotContain("Code.exe", space.Styles.First(style => style.Id == "developer").AppProcessNames);
    }

    [Fact]
    public void TheStoreRoundTripsThroughItsFiles()
    {
        var store = new DictationSpaceStore(directory);
        Assert.Equal(5, store.Space.Styles.Count);
        store.Update(space =>
        {
            space.Dictionary.Add(new DictionaryTerm("FlowsXR", ["flows xr"]));
            space.Shortcuts.Add(new SpokenShortcut("my sign-off", "Warm regards"));
            space.Assign("example.exe", "email");
        });
        Assert.True(File.Exists(Path.Combine(directory, "dictionary.json")));
        var reloaded = new DictationSpaceStore(directory);
        Assert.Equal(store.Space, reloaded.Space);
        Assert.Equal(["flows xr"], reloaded.Space.Dictionary[0].HeardAs);
        Assert.Equal("email", reloaded.Space.Style("example.exe").Id);
    }

    [Fact]
    public void TheFilesHaveTheMacsShape()
    {
        var store = new DictationSpaceStore(directory);
        var id = Guid.NewGuid();
        store.Update(space => space.Dictionary.Add(new DictionaryTerm("FlowsXR", ["flows xr"], id, DateTimeOffset.FromUnixTimeSeconds(1_760_000_000))));
        var term = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "dictionary.json")))!.AsArray()[0]!.AsObject();
        Assert.Equal(["createdAt", "heardAs", "id", "written"], term.Select(pair => pair.Key));
        Assert.Equal(1_760_000_000, term["createdAt"]!.GetValue<long>());
        Assert.Equal(id.ToString().ToUpperInvariant(), term["id"]!.GetValue<string>());
        var style = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "styles.json")))!.AsArray()[0]!.AsObject();
        Assert.Contains("appBundleIDs", style.Select(pair => pair.Key));
        Assert.Contains("polishWithModel", style.Select(pair => pair.Key));
    }

    [Fact]
    public void AMacFileReads()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "dictionary.json"), """
        [
          {
            "createdAt" : 1760000000.5,
            "heardAs" : [ "flows xr" ],
            "id" : "0D3B3C3A-0B6E-4E58-9E3B-0C6F1A1B2C3D",
            "written" : "FlowsXR"
          }
        ]
        """);
        File.WriteAllText(Path.Combine(directory, "shortcuts.json"), """
        [ { "createdAt" : 1760000000, "id" : "1D3B3C3A-0B6E-4E58-9E3B-0C6F1A1B2C3D", "replacement" : "Warm regards", "trigger" : "my sign-off" } ]
        """);
        var space = new DictationSpaceStore(directory).Space;
        Assert.Equal("FlowsXR", space.Dictionary.Single().Written);
        Assert.Equal(Guid.Parse("0D3B3C3A-0B6E-4E58-9E3B-0C6F1A1B2C3D"), space.Dictionary.Single().Id);
        Assert.Equal("Warm regards", space.Shortcuts.Single().Replacement);
        // No styles file: the seeded styles.
        Assert.Equal(5, space.Styles.Count);
    }

    [Fact]
    public void AnUnreadableOrEmptyStylesFileFallsBackToTheSeededStyles()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "styles.json"), "[]");
        Assert.Equal(5, new DictationSpaceStore(directory).Space.Styles.Count);
        File.WriteAllText(Path.Combine(directory, "styles.json"), "{ not json");
        Assert.Equal(5, new DictationSpaceStore(directory).Space.Styles.Count);
    }

    [Fact]
    public async Task AnEditMadeByHandIsPickedUp()
    {
        using var store = new DictationSpaceStore(directory);
        store.Save();
        store.StartWatching();
        var changed = new TaskCompletionSource();
        store.PropertyChanged += (_, _) => changed.TrySetResult();
        var other = new DictationSpaceStore(directory);
        other.Update(space => space.Shortcuts.Add(new SpokenShortcut("brb", "be right back")));
        await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(changed.Task.IsCompleted);
        Assert.Equal("be right back", store.Space.Shortcuts.Single().Replacement);
    }
}
