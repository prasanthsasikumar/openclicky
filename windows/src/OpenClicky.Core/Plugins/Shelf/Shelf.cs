//
//  Shelf.cs
//  OpenClicky (Windows port of Plugins/Shelf/ShelfStore.swift and the policies in ShelfIslandView.swift
//  and IslandDrop.swift)
//
//  The shelf: what a drop on the island becomes, and the store that keeps the items across launches.
//  Files are referenced, never copied: dragging one out moves or copies as the destination decides,
//  the shelf only hands the path over. Images dropped as data and the file icons rendered at drop
//  time are the only files written, under %LOCALAPPDATA%\OpenClicky\Shelf. Reading the drag data and
//  drawing the tiles is OpenClicky.Windows/Plugins/Shelf.
//

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Plugins.Shelf;

/// <summary>The plugin's component: drop things on the island, reach for them later.</summary>
public static class ShelfPlugin
{
    public static IslandComponent Component { get; } = new(
        IslandPluginRegistry.ShelfId, "Shelf", "", 84, IsPlugin: true,
        SettingsDetail: "Drop files, text or links on the island to keep them handy. Drag a tile out, or click it to copy.");
}

/// <summary>What a drop on the island turned out to be.</summary>
public abstract record ShelfDrop
{
    public sealed record File(string Path) : ShelfDrop;
    public sealed record Text(string Value) : ShelfDrop;
    public sealed record Link(Uri Url) : ShelfDrop;
    public sealed record Image(byte[] Png) : ShelfDrop
    {
        public bool Equals(Image? other) => other is not null && Png.AsSpan().SequenceEqual(other.Png);
        public override int GetHashCode() => Png.Length;
    }
}

public enum ShelfItemKind
{
    File,
    Text,
    Link,
    Image,
}

/// <summary>One tile on the shelf. <see cref="Value"/> is the file's path, the note, the link, or the saved image's path.</summary>
public sealed record ShelfItem(Guid Id, ShelfItemKind Kind, string Value, string Title, DateTimeOffset AddedAt, string? IconPath = null)
{
    /// <summary>A file's name, a link's host, a note's first line (40 characters), "image HH:mm".</summary>
    public static string TitleFor(ShelfDrop drop, DateTimeOffset now) => drop switch
    {
        ShelfDrop.File file => Path.GetFileName(file.Path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : file.Path,
        ShelfDrop.Link link => link.Url.Host.Length > 0 ? link.Url.Host : link.Url.ToString(),
        ShelfDrop.Text text => FirstLine(text.Value),
        _ => $"image {now.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}",
    };

    private static string FirstLine(string text)
    {
        var firstLine = text.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return firstLine.Length > 40 ? firstLine[..40] : firstLine;
    }

    /// <summary>A file or a saved image: the tile hands over a path.</summary>
    public bool IsFileOnDisk => Kind is ShelfItemKind.File or ShelfItemKind.Image;
}

/// <summary>
/// One per app: the items, newest first, at most 40, in index.json under <c>directory</c>. Call it on the
/// UI thread; <see cref="Changed"/> fires there.
/// </summary>
public sealed class ShelfStore
{
    public const int MaximumItems = 40;

    public static string DefaultDirectory => Path.Combine(OpenClickyPaths.AppDataDirectory, "Shelf");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string directory;
    private readonly Func<string, byte[]?>? iconRenderer;
    private readonly Func<DateTimeOffset> now;

    /// <param name="iconRenderer">A file's icon as PNG, rendered once at drop time so the tile never touches the file again.</param>
    public ShelfStore(string directory, Func<string, byte[]?>? iconRenderer = null, Func<DateTimeOffset>? now = null)
    {
        this.directory = directory;
        this.iconRenderer = iconRenderer;
        this.now = now ?? (() => DateTimeOffset.Now);
        Items = Load();
    }

    public IReadOnlyList<ShelfItem> Items { get; private set; }

    public event Action? Changed;

    private string IndexPath => Path.Combine(directory, "index.json");

    /// <summary>The drops become items ahead of everything already there, in the order given; what falls off
    /// the end of the 40 loses its files.</summary>
    public IReadOnlyList<ShelfItem> Add(IReadOnlyList<ShelfDrop> drops)
    {
        if (drops.Count == 0) return [];
        TryCreateDirectory();
        var added = drops.Select(MakeItem).ToList();
        var next = added.Concat(Items).ToList();
        if (next.Count > MaximumItems)
        {
            foreach (var item in next.Skip(MaximumItems)) DeleteFiles(item);
            next = next.Take(MaximumItems).ToList();
        }
        Items = next;
        Save();
        return added;
    }

    public void Remove(Guid id)
    {
        if (Items.FirstOrDefault(item => item.Id == id) is not { } item) return;
        DeleteFiles(item);
        Items = Items.Where(other => other.Id != id).ToList();
        Save();
    }

    public void RemoveAll()
    {
        foreach (var item in Items) DeleteFiles(item);
        Items = [];
        Save();
    }

    // MARK: Items

    private ShelfItem MakeItem(ShelfDrop drop)
    {
        var id = Guid.NewGuid();
        var addedAt = now();
        var title = ShelfItem.TitleFor(drop, addedAt);
        switch (drop)
        {
            case ShelfDrop.File file:
                string? iconPath = null;
                if (iconRenderer?.Invoke(file.Path) is { } icon)
                {
                    var path = Path.Combine(directory, $"{id}-icon.png");
                    if (TryWrite(path, icon)) iconPath = path;
                }
                return new ShelfItem(id, ShelfItemKind.File, file.Path, title, addedAt, iconPath);
            case ShelfDrop.Link link:
                return new ShelfItem(id, ShelfItemKind.Link, link.Url.ToString(), title, addedAt);
            case ShelfDrop.Image image:
                var imagePath = Path.Combine(directory, $"{id}.png");
                TryWrite(imagePath, image.Png);
                return new ShelfItem(id, ShelfItemKind.Image, imagePath, title, addedAt);
            default:
                return new ShelfItem(id, ShelfItemKind.Text, ((ShelfDrop.Text)drop).Value, title, addedAt);
        }
    }

    private static void DeleteFiles(ShelfItem item)
    {
        if (item.Kind == ShelfItemKind.Image) TryDelete(item.Value);
        if (item.IconPath is { } iconPath) TryDelete(iconPath);
    }

    // MARK: Disk

    /// <summary>A missing or broken index is an empty shelf. A file item's path is not checked: it is looked at
    /// when the tile is dragged.</summary>
    private IReadOnlyList<ShelfItem> Load()
    {
        try
        {
            var json = System.IO.File.ReadAllText(IndexPath);
            return JsonSerializer.Deserialize<List<ShelfItem>>(json, JsonOptions) ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    private void Save()
    {
        try
        {
            TryCreateDirectory();
            AtomicFile.WriteAllBytes(IndexPath, JsonSerializer.SerializeToUtf8Bytes(Items, JsonOptions));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"shelf: could not save ({error.GetType().Name})");
        }
        Changed?.Invoke();
    }

    private void TryCreateDirectory()
    {
        try { Directory.CreateDirectory(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static bool TryWrite(string path, byte[] data)
    {
        try
        {
            System.IO.File.WriteAllBytes(path, data);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { System.IO.File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// What a drag carries, as the island reads it: files first, else a web link, else text (a lone http(s)
/// URL is a link), else an image. The Windows side reads the data object into these pieces.
/// </summary>
public static class ShelfDropReader
{
    public static IReadOnlyList<ShelfDrop> Drops(IReadOnlyList<string>? files, string? link, string? text, byte[]? imagePng)
    {
        if (files is { Count: > 0 }) return files.Select(path => (ShelfDrop)new ShelfDrop.File(path)).ToList();
        if (WebLink(link) is { } dragged) return [new ShelfDrop.Link(dragged)];
        var trimmed = text?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            if (WebLink(trimmed) is { } inText) return [new ShelfDrop.Link(inText)];
            return [new ShelfDrop.Text(trimmed)];
        }
        if (imagePng is { Length: > 0 }) return [new ShelfDrop.Image(imagePng)];
        return [];
    }

    /// <summary>A string that is one http(s) URL with a host; anything else is a note.</summary>
    public static Uri? WebLink(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsWhiteSpace)) return null;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var url)) return null;
        return url.Scheme is "http" or "https" && url.Host.Length > 0 ? url : null;
    }
}

/// <summary>When the island takes a drop, and when a tile dragged out leaves the shelf.</summary>
public static class ShelfDragPolicy
{
    /// <summary>Rides along with every tile dragged out, so the island knows its own (dropping one back would
    /// re-add it and then delete it).</summary>
    public const string MarkerFormat = "OpenClicky.ShelfItem";

    /// <summary>Drops land only with the shelf switched on, and never the island's own tiles.</summary>
    public static bool Accepts(IReadOnlySet<string> enabledComponentIds, IEnumerable<string> formats) =>
        enabledComponentIds.Contains(IslandPluginRegistry.ShelfId) && !formats.Contains(MarkerFormat);

    /// <summary>The destination took it (a copy or a move): the tile goes. A refused drop keeps it.</summary>
    public static bool RemovesAfterDrag(bool dropLanded) => dropLanded;

    /// <summary>A file may be moved or copied as Explorer decides; everything else is copied.</summary>
    public static bool AllowsMove(ShelfItemKind kind) => kind == ShelfItemKind.File;
}
