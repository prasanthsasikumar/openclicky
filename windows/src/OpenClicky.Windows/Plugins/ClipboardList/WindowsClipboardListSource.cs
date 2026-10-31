//
//  WindowsClipboardListSource.cs
//  OpenClicky (Windows port of Plugins/Clipboard/ClipboardStore.swift's ClipboardPasteboard)
//
//  Turns what is on the clipboard into a row of the Clipboard plugin, and puts one back. The
//  sequence number and the password managers' "do not record" markers come from the raw Win32
//  clipboard (Platform/Win32Clipboard.cs, which the dictation paste also marks its own text with);
//  the contents through WPF's clipboard, so call it on the UI thread.
//

using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenClicky.Core.Plugins.ClipboardList;
using OpenClicky.Windows.Platform;

namespace OpenClicky.Windows.Plugins.ClipboardList;

internal sealed class WindowsClipboardListSource : IClipboardListSource
{
    /// <summary>A copied image bigger than this (a full-screen 8K capture) is not kept: one row would hold 100+ MB.</summary>
    private const long MaximumImagePixels = 40_000_000;

    private readonly Win32Clipboard clipboard = new();

    public uint SequenceNumber => clipboard.SequenceNumber;

    public bool ContainsTransientContent() => clipboard.ContainsTransientContent();

    /// <summary>Files first (Explorer also puts their names as text), then images, then text, which becomes a link when it
    /// is one web or mail address and nothing else.</summary>
    public ClipboardContent? Read()
    {
        if (Clipboard.ContainsFileDropList())
        {
            var paths = Clipboard.GetFileDropList().Cast<string>().ToList();
            if (paths.Count > 0) return new ClipboardContent.Files(paths);
        }
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image && (long)image.PixelWidth * image.PixelHeight <= MaximumImagePixels)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return new ClipboardContent.Image(stream.ToArray(), image.PixelWidth, image.PixelHeight);
        }
        if (Clipboard.ContainsText()) return ClipboardContent.FromText(Clipboard.GetText());
        return null;
    }

    public void Write(ClipboardContent content)
    {
        switch (content)
        {
            case ClipboardContent.Text text:
                Clipboard.SetText(text.Value);
                break;
            case ClipboardContent.Link link:
                Clipboard.SetText(link.Url.OriginalString);
                break;
            case ClipboardContent.Files files:
            {
                var list = new System.Collections.Specialized.StringCollection();
                list.AddRange([.. files.Paths]);
                Clipboard.SetFileDropList(list);
                break;
            }
            case ClipboardContent.Image image:
            {
                var decoder = new PngBitmapDecoder(new MemoryStream(image.Png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Clipboard.SetImage(decoder.Frames[0]);
                break;
            }
        }
    }
}
