// Ports TakeStoreTests.swift and TakeAudioStoreTests.swift: the SQLite take store in a temp file
// (inserts, search, revisions, stats, another process's lock) and the retained audio of failed takes.

using System.Text;
using Microsoft.Data.Sqlite;
using OpenClicky.Core.Dictation;

namespace OpenClicky.Core.Tests.Dictation;

public sealed class TakeStoreTests : IDisposable
{
    private readonly List<TakeStore> stores = [];

    private TakeStore MakeStore()
    {
        var store = new TakeStore(Path.Combine(Path.GetTempPath(), $"openclicky-takes-{Guid.NewGuid()}.sqlite"));
        stores.Add(store);
        return store;
    }

    public void Dispose()
    {
        foreach (var store in stores)
        {
            store.Dispose();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(store.FilePath + suffix); } catch (IOException) { }
            }
        }
    }

    [Fact]
    public void InsertedTakesComeBackNewestFirst()
    {
        var store = MakeStore();
        var older = new TakeRecord
        {
            CreatedAt = DateTimeOffset.Now.AddSeconds(-100), RawText = "tech week starts today", FormattedText = "Tech Week starts in San Francisco today.",
            AppProcessName = "chrome.exe", AppName = "Google Chrome", Engine = "offline",
        };
        var newer = new TakeRecord { RawText = "three kilograms of atta", FormattedText = "3 kg of atta.", AppProcessName = "Notepad.exe", AppName = "Notepad", Engine = "sarvam" };
        store.Insert(older);
        store.Insert(newer);
        var rows = store.Recent();
        Assert.Equal([newer.Id, older.Id], rows.Select(row => row.Id));
        Assert.Equal("sarvam", rows[0].Engine);
        Assert.Equal("chrome.exe", rows[1].AppProcessName);
    }

    [Fact]
    public void EveryColumnRoundTrips()
    {
        var store = MakeStore();
        var take = new TakeRecord
        {
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_250), Mode = TakeMode.Edit, Status = TakeStatus.Failed,
            RawText = "raw", FormattedText = "Formatted.", AppProcessName = "Code.exe", AppName = "Visual Studio Code", Language = "hi-IN",
            Engine = "sarvam", DurationSeconds = 2.5, PasteOutcome = TakePasteOutcome.LeftInOrb, Pinned = true, FailureReason = "no key",
        };
        store.Insert(take);
        var fetched = store.Fetch(take.Id)!;
        Assert.Equal(take with { CreatedAt = fetched.CreatedAt }, fetched);
        Assert.Equal(take.CreatedAt.ToUnixTimeMilliseconds(), fetched.CreatedAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void TheSchemaIsTheMacs()
    {
        var store = MakeStore();
        var take = new TakeRecord { RawText = "a", FormattedText = "A.", PasteOutcome = TakePasteOutcome.LeftOnPasteboard };
        store.Insert(take);
        using var connection = new SqliteConnection($"Data Source={store.FilePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, mode, status, paste_outcome, typeof(created_at) FROM takes";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(take.Id.ToString().ToUpperInvariant(), reader.GetString(0));
        Assert.Equal("dictate", reader.GetString(1));
        Assert.Equal("complete", reader.GetString(2));
        Assert.Equal("leftOnPasteboard", reader.GetString(3));
        Assert.Equal("real", reader.GetString(4));
    }

    [Fact]
    public void SearchMatchesWordsInEitherText()
    {
        var store = MakeStore();
        store.Insert(new TakeRecord { RawText = "tech week starts today", FormattedText = "Tech Week starts in San Francisco today." });
        store.Insert(new TakeRecord { RawText = "three kilograms of atta", FormattedText = "3 kg of atta." });
        Assert.Single(store.Recent(query: "francisco"));
        Assert.Single(store.Recent(query: "kilograms"));
        Assert.Single(store.Recent(query: "today week"));
        Assert.Empty(store.Recent(query: "nothing%"));
    }

    [Fact]
    public void ModeAndAppNarrowTheList()
    {
        var store = MakeStore();
        store.Insert(new TakeRecord { RawText = "a", AppProcessName = "Code.exe" });
        store.Insert(new TakeRecord { RawText = "b", Mode = TakeMode.Clipboard });
        Assert.Single(store.Recent(mode: TakeMode.Clipboard));
        Assert.Single(store.Recent(appProcessName: "Code.exe"));
        Assert.Equal([new AppUsage("Code.exe", null, 1)], store.AppsUsed());
    }

    [Fact]
    public void RevisionsKeepThePreviousTextAndUpdateTheTake()
    {
        var store = MakeStore();
        var take = new TakeRecord { RawText = "hello", FormattedText = "Hello." };
        store.Insert(take);
        store.Revise(take.Id, "Hello there.", "history");
        Assert.Equal("Hello there.", store.Fetch(take.Id)?.FormattedText);
    }

    [Fact]
    public void RevisedTakesAreListedOnce()
    {
        var store = MakeStore();
        var edited = new TakeRecord { RawText = "hello", FormattedText = "Hello." };
        var untouched = new TakeRecord { RawText = "bye", FormattedText = "Bye." };
        store.Insert(edited);
        store.Insert(untouched);
        store.Revise(edited.Id, "Hello there.", "history");
        store.Revise(edited.Id, "Hello there!", "history");
        Assert.Equal([edited.Id], store.RevisedTakeIds());
    }

    [Fact]
    public void PinningAndDeletingTakeEffect()
    {
        var store = MakeStore();
        var take = new TakeRecord { RawText = "a", FormattedText = "A." };
        store.Insert(take);
        store.SetPinned(true, take.Id);
        Assert.True(store.Fetch(take.Id)!.Pinned);
        store.Delete(take.Id);
        Assert.Null(store.Fetch(take.Id));
    }

    [Fact]
    public void StatsCountWordsTodayAndTheBusiestApp()
    {
        var store = MakeStore();
        store.Insert(new TakeRecord { FormattedText = "one two three", AppProcessName = "chrome.exe", AppName = "Google Chrome" });
        store.Insert(new TakeRecord { FormattedText = "four five", AppProcessName = "chrome.exe", AppName = "Google Chrome" });
        store.Insert(new TakeRecord { FormattedText = "six", AppProcessName = "Notepad.exe", AppName = "Notepad" });
        store.Insert(new TakeRecord { CreatedAt = DateTimeOffset.Now.AddDays(-3), FormattedText = "old words here" });
        store.Insert(new TakeRecord { Mode = TakeMode.Clipboard, FormattedText = "copied text not counted" });
        var stats = store.Stats();
        Assert.Equal(6, stats.WordsToday);
        Assert.Equal(3, stats.TakesToday);
        Assert.Equal(9, stats.WordsThisWeek);
        Assert.Equal("Google Chrome", stats.MostUsedAppToday);
        Assert.Equal(4, stats.AllTimeTakes);
    }

    [Fact]
    public void WordsPerDayCoversSevenDaysWithTodayLast()
    {
        var store = MakeStore();
        store.Insert(new TakeRecord { FormattedText = "one two" });
        store.Insert(new TakeRecord { CreatedAt = DateTimeOffset.Now.AddDays(-2), FormattedText = "three" });
        var days = store.WordsPerDay();
        Assert.Equal(7, days.Count);
        Assert.Equal(2, days[^1].Words);
        Assert.Equal(1, days[4].Words);
        Assert.Equal(3, days.Sum(day => day.Words));
    }

    [Fact]
    public void DeleteAllEmptiesTheStore()
    {
        var store = MakeStore();
        store.Insert(new TakeRecord { RawText = "a", FormattedText = "A." });
        store.DeleteAll();
        Assert.Empty(store.Recent());
    }

    /// <summary>
    /// Two OpenClicky processes (the installed app and a Debug build) share one file: a write that
    /// meets the other's lock waits for it instead of failing at once with SQLITE_BUSY (F-33).
    /// </summary>
    [Fact]
    public void AWriteWaitsOutAnotherProcesssLock()
    {
        var store = MakeStore();
        using var otherConnection = new SqliteConnection($"Data Source={store.FilePath};Pooling=False");
        otherConnection.Open();
        using (var begin = otherConnection.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            begin.ExecuteNonQuery();
        }
        // A thread of its own: on a busy runner a pool thread could start after the store's 2 s
        // busy timeout ran out, and the insert would fail for the test's sake.
        var releaser = new Thread(() =>
        {
            Thread.Sleep(300);
            using var commit = otherConnection.CreateCommand();
            commit.CommandText = "COMMIT";
            commit.ExecuteNonQuery();
        });
        releaser.Start();
        store.Insert(new TakeRecord { RawText = "while locked", FormattedText = "While locked." });
        releaser.Join();
        Assert.Equal("while locked", store.Recent()[0].RawText);
    }
}

public sealed class TakeAudioStoreTests : IDisposable
{
    private readonly TakeAudioStore store = new(Path.Combine(Path.GetTempPath(), $"openclicky-audio-{Guid.NewGuid()}"));

    public void Dispose() => store.DiscardAll();

    [Fact]
    public void RetainedAudioIsAWavThatCanBeFoundAndDiscarded()
    {
        var id = Guid.NewGuid();
        store.Retain(id, Enumerable.Repeat((byte)1, 3200).ToArray());
        Assert.True(store.HasAudio(id));
        var wav = File.ReadAllBytes(store.PathFor(id));
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(44 + 3200, wav.Length);
        Assert.Equal(3200, store.ReadPcm16(id)!.Length);
        store.Discard(id);
        Assert.False(store.HasAudio(id));
    }

    [Fact]
    public void PruningKeepsTheNewestTakes()
    {
        var ids = new List<Guid>();
        for (var index = 0; index < TakeAudioStore.MaxRetainedTakes + 5; index++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            store.Retain(id, Enumerable.Repeat((byte)(index % 7), 64).ToArray());
            // Distinct modification dates, oldest first.
            File.SetLastWriteTimeUtc(store.PathFor(id), DateTime.UnixEpoch.AddSeconds(1_000 + index));
        }
        store.PruneIfNeeded();
        Assert.False(store.HasAudio(ids[0]));
        Assert.False(store.HasAudio(ids[4]));
        Assert.True(store.HasAudio(ids[5]));
        Assert.True(store.HasAudio(ids[^1]));
    }

    [Fact]
    public void CaptureCollectsThenForgets()
    {
        store.BeginCapture();
        Assert.Empty(store.EndCapture());
        store.BeginCapture();
        store.Append([1, 2, 3, 4]);
        Assert.Equal([1, 2, 3, 4], store.EndCapture());
        Assert.Empty(store.EndCapture());
    }

    [Fact]
    public void SlicesCoverTheRecordingInOrder()
    {
        var pcm = Enumerable.Range(0, 16_000 * 2 + 10).Select(index => (byte)index).ToArray();
        var slices = TakeAudioStore.Slices(pcm).ToList();
        Assert.Equal(5, slices.Count);
        Assert.Equal(8_000, slices[0].Length);
        Assert.Equal(10, slices[^1].Length);
        Assert.Equal(pcm, slices.SelectMany(slice => slice.ToArray()));
    }
}
