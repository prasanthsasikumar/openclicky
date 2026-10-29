//
//  ShelfDataObjects.cs
//  OpenClicky (Windows port of the pasteboard half of Plugins/Shelf/ShelfStore.swift and
//  ShelfStore.finderIcon in IslandDrop.swift)
//
//  The shelf and Windows' drag data: what a drop on the island carries (files, a browser's link, text,
//  an image), what a tile hands over when dragged out or clicked (with the marker so the island refuses
//  its own tiles), and a file's icon rendered once at drop time.
//

using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenClicky.Core.Plugins.Shelf;

namespace OpenClicky.Windows.Plugins.Shelf;

internal static class ShelfDataObjects
{
    /// <summary>What a browser puts on a dragged link (the address, UTF-16, null-terminated).</summary>
    private const string UrlFormat = "UniformResourceLocatorW";

    /// <summary>The drops a drag carries: files first, else a link, else text, else an image.</summary>
    public static IReadOnlyList<ShelfDrop> Read(IDataObject data)
    {
        try
        {
            var files = data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null;
            var link = data.GetDataPresent(UrlFormat) ? ReadUnicodeStream(data.GetData(UrlFormat)) : null;
            var text = data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : null;
            var image = files is { Length: > 0 } || link is not null || !string.IsNullOrWhiteSpace(text) ? null : ReadImage(data);
            return ShelfDropReader.Drops(files, link, text, image);
        }
        catch (Exception error) when (error is COMException or ExternalException or InvalidOperationException or OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>The format names a drag offers, for the island's accept check.</summary>
    public static IReadOnlyList<string> Formats(IDataObject data)
    {
        try { return data.GetFormats(); }
        catch (Exception error) when (error is COMException or ExternalException) { return []; }
    }

    private static string? ReadUnicodeStream(object? value)
    {
        if (value is string text) return text.TrimEnd('\0');
        if (value is not MemoryStream stream) return null;
        return Encoding.Unicode.GetString(stream.ToArray()).TrimEnd('\0');
    }

    private static byte[]? ReadImage(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.Bitmap) || data.GetData(DataFormats.Bitmap) is not BitmapSource bitmap) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>What a tile hands over: the file (or saved image) as a file, the note or the link as text. Dragged out,
    /// it also carries the marker so the island refuses its own tiles.</summary>
    public static DataObject For(ShelfItem item, bool withMarker)
    {
        var data = new DataObject();
        if (item.IsFileOnDisk)
        {
            data.SetFileDropList(new StringCollection { item.Value });
        }
        else
        {
            data.SetText(item.Value);
        }
        if (withMarker) data.SetData(ShelfDragPolicy.MarkerFormat, "1");
        return data;
    }

    /// <summary>A file's icon as PNG (the one Explorer shows), rendered once at drop time; null for a folder or a file
    /// Windows has no icon for.</summary>
    public static byte[]? FileIcon(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
        {
            return null;
        }
    }
}
