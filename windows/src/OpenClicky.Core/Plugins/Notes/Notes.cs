//
//  Notes.cs
//  OpenClicky (Windows port of Plugins/Notes/NotesStore.swift and NotesIslandView.swift's component)
//
//  The Notes plugin's text: one plain-text file on this PC, read once and written a moment after
//  typing pauses, so a keystroke never waits on the disk. The editor is
//  OpenClicky.Windows/Plugins/Notes.
//

using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Plugins.Notes;

/// <summary>The plugin's component: a scratch pad that is always one hover away.</summary>
public static class NotesPlugin
{
    public static IslandComponent Component { get; } = new(
        IslandPluginRegistry.NotesId, "Notes", "", 120, IsPlugin: true,
        SettingsDetail: "A quick notepad on the island. Click it to type; it saves on this PC as you go and never leaves it.");
}

/// <summary>
/// One per app. Use it on the UI thread: the delayed write comes back on the thread that typed, and
/// <see cref="Changed"/> is raised there.
/// </summary>
public sealed class NotesStore
{
    /// <summary>%LOCALAPPDATA%\OpenClicky\Notes\notes.txt</summary>
    public static string DefaultFilePath => Path.Combine(OpenClickyPaths.AppDataDirectory, "Notes", "notes.txt");

    public static readonly TimeSpan DefaultSaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly TimeSpan saveDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly object saveLock = new();
    private CancellationTokenSource? pendingSave;
    /// <summary>Typed since the last write; nothing is written (no empty file is made) until something is.</summary>
    private bool isDirty;

    public NotesStore(string filePath, TimeSpan? saveDelay = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        FilePath = filePath;
        this.saveDelay = saveDelay ?? DefaultSaveDelay;
        this.delay = delay ?? Task.Delay;
        try { Text = File.Exists(filePath) ? File.ReadAllText(filePath) : ""; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"notes not read: {error.Message}");
            Text = "";
        }
    }

    public string FilePath { get; }

    public string Text { get; private set; }

    /// <summary>The text changed (from the editor; nothing else writes it).</summary>
    public event Action? Changed;

    /// <summary>True while a write is waiting for typing to pause.</summary>
    public bool HasPendingSave
    {
        get { lock (saveLock) return pendingSave is not null; }
    }

    /// <summary>The editor's text changed: keep it and write it once typing pauses.</summary>
    public void Update(string newText)
    {
        if (newText == Text) return;
        CancellationTokenSource cancellation;
        lock (saveLock)
        {
            Text = newText;
            isDirty = true;
            pendingSave?.Cancel();
            cancellation = new CancellationTokenSource();
            pendingSave = cancellation;
        }
        Changed?.Invoke();
        _ = SaveLaterAsync(cancellation);
    }

    private async Task SaveLaterAsync(CancellationTokenSource cancellation)
    {
        try { await delay(saveDelay, cancellation.Token); }
        catch (OperationCanceledException) { return; }
        lock (saveLock)
        {
            if (pendingSave != cancellation) return;
        }
        SaveNow();
    }

    /// <summary>Writes the text now (the delay's end, quitting, or the editor going away).</summary>
    public void SaveNow()
    {
        lock (saveLock)
        {
            pendingSave?.Cancel();
            pendingSave = null;
            if (!isDirty) return;
            isDirty = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                AtomicFile.WriteAllText(FilePath, Text);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Kept dirty so the next pause or quitting tries again.
                isDirty = true;
                AppLog.Write($"notes not saved: {error.Message}");
            }
        }
    }
}
