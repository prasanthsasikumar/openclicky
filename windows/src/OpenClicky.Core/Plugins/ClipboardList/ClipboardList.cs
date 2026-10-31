//
//  ClipboardList.cs
//  OpenClicky (Windows port of Plugins/Clipboard/ClipboardHistory.swift and ClipboardStore.swift)
//
//  The Clipboard plugin: what was copied, newest first, at most 30, each copy once, kept in memory
//  only. The clipboard has a sequence number but no cheap change event a store can share, so it
//  is polled twice a second, and only while the plugin is switched on; switching it off stops the
//  polling and forgets the list. Reading and writing the clipboard itself is the platform's
//  (OpenClicky.Windows/Plugins/ClipboardList). The folder is not called "Clipboard" so the name
//  never hides WPF's Clipboard class elsewhere under Plugins.
//

using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Plugins.ClipboardList;

/// <summary>The plugin's component: what you copied lately, a click away from being copied again.</summary>
public static class ClipboardPlugin
{
    public static IslandComponent Component { get; } = new(
        IslandPluginRegistry.ClipboardId, "Clipboard", "", 130, IsPlugin: true,
        SettingsDetail: "Keeps the last 30 things you copied while OpenClicky runs, in memory only. Copies password managers mark as private are skipped.");
}

/// <summary>What one copy held, reduced to what can be shown in a row and put back on the clipboard.</summary>
public abstract record ClipboardContent
{
    private ClipboardContent() { }

    public sealed record Text(string Value) : ClipboardContent;

    public sealed record Link(Uri Url) : ClipboardContent;

    public sealed record Files(IReadOnlyList<string> Paths) : ClipboardContent
    {
        public bool Equals(Files? other) => other is not null && Paths.SequenceEqual(other.Paths, StringComparer.OrdinalIgnoreCase);
        public override int GetHashCode() => Paths.Count == 0 ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Paths[0]);
    }

    /// <summary>The image as PNG bytes, and its size in pixels.</summary>
    public sealed record Image(byte[] Png, int PixelWidth, int PixelHeight) : ClipboardContent
    {
        public bool Equals(Image? other) =>
            other is not null && PixelWidth == other.PixelWidth && PixelHeight == other.PixelHeight && Png.AsSpan().SequenceEqual(other.Png);
        public override int GetHashCode() => HashCode.Combine(PixelWidth, PixelHeight, Png.Length);
    }

    /// <summary>The row's icon, a Segoe Fluent Icons glyph.</summary>
    public string Glyph => this switch
    {
        Link => "",
        Files files => files.Paths.Count > 1 ? "" : "",
        Image => "",
        _ => "",
    };

    /// <summary>One line for the row: the text's first line, the link without its scheme, the file's name, or
    /// "Image 1200 × 800".</summary>
    public string Label
    {
        get
        {
            switch (this)
            {
                case Text text:
                {
                    var firstLine = text.Value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text.Value;
                    var collapsed = string.Join(" ", firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    return collapsed.Length == 0 ? "Text" : collapsed[..Math.Min(200, collapsed.Length)];
                }
                case Link link:
                {
                    var address = link.Url.OriginalString;
                    var schemeEnd = address.IndexOf("://", StringComparison.Ordinal);
                    var withoutScheme = schemeEnd > 0 ? address[(schemeEnd + 3)..] : address;
                    return withoutScheme[..Math.Min(200, withoutScheme.Length)];
                }
                case Files files:
                {
                    if (files.Paths.Count == 0) return "Files";
                    var first = Path.GetFileName(files.Paths[0].TrimEnd('\\', '/'));
                    return files.Paths.Count > 1 ? $"{first} and {files.Paths.Count - 1} more" : first;
                }
                case Image image:
                    return image.PixelWidth > 0 && image.PixelHeight > 0 ? $"Image {image.PixelWidth} × {image.PixelHeight}" : "Image";
                default:
                    return "";
            }
        }
    }

    /// <summary>The row's tooltip: the text itself (cut), else the label.</summary>
    public string Detail => this is Text text ? text.Value[..Math.Min(500, text.Value.Length)] : Label;

    /// <summary>Longer copies are kept cut to this many characters.</summary>
    public const int MaxTextLength = 20_000;

    /// <summary>The text as a link when it is a single http, https or mailto address; else null.</summary>
    public static Uri? LinkIn(string text)
    {
        if (text.Any(char.IsWhiteSpace) || !Uri.TryCreate(text, UriKind.Absolute, out var url)) return null;
        return url.Scheme.ToLowerInvariant() switch
        {
            "http" or "https" => url.Host.Length > 0 && text.Contains("://", StringComparison.Ordinal) ? url : null,
            "mailto" => url,
            _ => null,
        };
    }

    /// <summary>Copied text as a row: a link when it is one, the text (cut) otherwise, nothing when it is blank.</summary>
    public static ClipboardContent? FromText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        if (LinkIn(trimmed) is { } url) return new Link(url);
        return new Text(text.Length > MaxTextLength ? text[..MaxTextLength] : text);
    }
}

public sealed record ClipboardEntry(Guid Id, ClipboardContent Content, DateTimeOffset CopiedAt);

/// <summary>The list: newest first, at most <see cref="Capacity"/>, each copy once.</summary>
public sealed class ClipboardHistory
{
    public const int Capacity = 30;

    private readonly List<ClipboardEntry> entries = [];

    public IReadOnlyList<ClipboardEntry> Entries => entries;

    /// <summary>Keeps a copy at the top. The same content copied again moves up, keeping its id (so a row copied back
    /// from the list stays the same row), rather than repeating; past the capacity the oldest goes. Returns false when the
    /// copy was not kept (a password manager's private copy).</summary>
    public bool Add(ClipboardContent content, bool isTransient = false, DateTimeOffset? at = null, Guid? id = null)
    {
        if (isTransient) return false;
        var existing = entries.FindIndex(entry => entry.Content.Equals(content));
        var keptId = existing >= 0 ? entries[existing].Id : id ?? Guid.NewGuid();
        if (existing >= 0) entries.RemoveAt(existing);
        entries.Insert(0, new ClipboardEntry(keptId, content, at ?? DateTimeOffset.Now));
        if (entries.Count > Capacity) entries.RemoveRange(Capacity, entries.Count - Capacity);
        return true;
    }

    public void RemoveAll() => entries.Clear();
}

/// <summary>The clipboard itself: Windows', or a test double.</summary>
public interface IClipboardListSource
{
    /// <summary>Changes on every clipboard change (GetClipboardSequenceNumber).</summary>
    uint SequenceNumber { get; }

    /// <summary>A password manager marked the copy as private (ExcludeClipboardContentFromMonitorProcessing, …).</summary>
    bool ContainsTransientContent();

    /// <summary>The copy on the clipboard, or null for nothing worth a row. Files first, then images, then text.</summary>
    ClipboardContent? Read();

    void Write(ClipboardContent content);
}

/// <summary>
/// The Clipboard plugin's one live instance. Polls only while switched on; use it on the UI thread (the poll is a delay
/// loop, and the WPF clipboard wants the UI thread).
/// </summary>
public sealed class ClipboardStore(IClipboardListSource source, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(0.5);

    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;
    private CancellationTokenSource? polling;
    private uint lastSequenceNumber;

    public ClipboardHistory History { get; } = new();

    public bool IsPolling => polling is not null;

    /// <summary>The list changed.</summary>
    public event Action? Changed;

    /// <summary>On: poll from the current clipboard (what was copied before is not news). Off: stop and forget everything
    /// kept.</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled == IsPolling) return;
        if (enabled)
        {
            lastSequenceNumber = source.SequenceNumber;
            var cancellation = new CancellationTokenSource();
            polling = cancellation;
            _ = PollLoopAsync(cancellation.Token);
        }
        else
        {
            polling!.Cancel();
            polling = null;
            if (History.Entries.Count == 0) return;
            History.RemoveAll();
            Changed?.Invoke();
        }
    }

    /// <summary>Reads the clipboard if it changed since the last look.</summary>
    public void Poll()
    {
        var sequenceNumber = source.SequenceNumber;
        if (sequenceNumber == lastSequenceNumber) return;
        lastSequenceNumber = sequenceNumber;
        // A dictated take is pasted through the clipboard marked private, then the copy before it is put back: neither is
        // something the user copied, and the first is skipped here.
        ClipboardContent? content;
        try
        {
            if (source.ContainsTransientContent()) return;
            content = source.Read();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"clipboard: read failed ({error.GetType().Name})");
            return;
        }
        if (content is null) return;
        if (History.Add(content)) Changed?.Invoke();
    }

    /// <summary>Puts a kept copy back on the clipboard. The next poll sees it and moves it to the top.</summary>
    public void CopyBack(ClipboardEntry entry)
    {
        try { source.Write(entry.Content); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"clipboard: copy back failed ({error.GetType().Name})");
        }
    }

    public void Clear()
    {
        if (History.Entries.Count == 0) return;
        History.RemoveAll();
        Changed?.Invoke();
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await delay(PollInterval, cancellationToken); }
            catch (OperationCanceledException) { return; }
            if (cancellationToken.IsCancellationRequested) return;
            Poll();
        }
    }
}
