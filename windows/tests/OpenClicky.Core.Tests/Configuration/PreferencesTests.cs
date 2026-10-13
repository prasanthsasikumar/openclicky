// New on Windows: the UserDefaults replacement keeps typed values, defaults, per-key change
// events, and survives a reload from its file.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Tests.Configuration;

public sealed class PreferencesTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"openclicky-preferences-test-{Guid.NewGuid()}");
    private string FilePath => Path.Combine(temporaryDirectory, "preferences.json");

    public void Dispose()
    {
        try { Directory.Delete(temporaryDirectory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TypedValuesRoundTripThroughTheFile()
    {
        var preferences = new Preferences(FilePath);
        preferences.Set("dictation.sounds", true);
        preferences.Set("dictation.timeout", 42);
        preferences.Set("orb.size", 1.5);
        preferences.Set("dictation.engine", "sarvam");
        preferences.Set("dictation.recent", ["a", "b"]);
        preferences.SetEnum("answer.source", AnswerSource.OpenAI);

        var reloaded = new Preferences(FilePath);
        Assert.True(reloaded.GetBool("dictation.sounds"));
        Assert.Equal(42, reloaded.GetInt("dictation.timeout"));
        Assert.Equal(42.0, reloaded.GetDouble("dictation.timeout"));
        Assert.Equal(1.5, reloaded.GetDouble("orb.size"));
        Assert.Equal("sarvam", reloaded.GetString("dictation.engine"));
        Assert.Equal(["a", "b"], reloaded.GetStringArray("dictation.recent"));
        Assert.Equal("openAI", reloaded.GetString("answer.source"));
        Assert.Equal(AnswerSource.OpenAI, reloaded.GetEnum("answer.source", AnswerSource.Anthropic));
    }

    [Fact]
    public void MissingOrWrongKindReadsTheDefault()
    {
        var preferences = new Preferences(FilePath);
        Assert.True(preferences.GetBool("missing", defaultValue: true));
        Assert.Equal(7, preferences.GetInt("missing", 7));
        Assert.Null(preferences.GetString("missing"));
        Assert.False(preferences.Contains("missing"));
        preferences.Set("text", "hello");
        Assert.Equal(3, preferences.GetInt("text", 3));
        Assert.Equal(AnswerSource.Groq, preferences.GetEnum("text", AnswerSource.Groq));
    }

    [Fact]
    public void ChangedNamesTheKeyAndObserversHearOnlyTheirs()
    {
        var preferences = new Preferences(FilePath);
        var changedKeys = new List<string>();
        var observed = 0;
        preferences.Changed += changedKeys.Add;
        using (preferences.Observe("a", () => observed++))
        {
            preferences.Set("a", 1);
            preferences.Set("a", 1); // unchanged: no event
            preferences.Set("b", 2);
        }
        preferences.Set("a", 3);
        Assert.Equal(["a", "b", "a"], changedKeys);
        Assert.Equal(1, observed);
    }

    [Fact]
    public void SettingNullRemovesTheKey()
    {
        var preferences = new Preferences(FilePath);
        preferences.Set("plan", "grant");
        preferences.Set("plan", (string?)null);
        Assert.False(new Preferences(FilePath).Contains("plan"));
    }

    [Fact]
    public void AnUnreadableFileStartsEmpty()
    {
        Directory.CreateDirectory(temporaryDirectory);
        File.WriteAllText(FilePath, "{ not json");
        var preferences = new Preferences(FilePath);
        Assert.Empty(preferences.Keys);
    }

    [Fact]
    public void ManyThreadsCanWriteAtOnce()
    {
        var preferences = new Preferences(FilePath);
        Parallel.For(0, 50, index => preferences.Set($"key.{index}", index));
        var reloaded = new Preferences(FilePath);
        for (var index = 0; index < 50; index++) Assert.Equal(index, reloaded.GetInt($"key.{index}", -1));
    }
}
