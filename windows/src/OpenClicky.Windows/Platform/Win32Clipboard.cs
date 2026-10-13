using System.Runtime.InteropServices;
using System.Text;
using OpenClicky.Core.Platform;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// The raw Win32 clipboard (not the OLE one, so it works from any thread, STA or not). Opening it
/// is retried for ~250 ms because another app (often a clipboard manager reacting to the last
/// change) may hold it open.
/// </summary>
public sealed class Win32Clipboard : IClipboard
{
    /// <summary>Stops a snapshot from copying absurd amounts (a copied 4K bitmap is ~33 MB of DIB).</summary>
    private const long MaximumSnapshotBytes = 128L * 1024 * 1024;

    private static readonly string[] TransientMarkerFormats =
    [
        "ExcludeClipboardContentFromMonitorProcessing",
        "Clipboard Viewer Ignore",
    ];

    private static readonly object ClipboardLock = new();

    public uint SequenceNumber => GetClipboardSequenceNumber();

    public string? GetText()
    {
        lock (ClipboardLock)
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT) || !Open()) return null;
            try
            {
                var handle = GetClipboardData(CF_UNICODETEXT);
                if (handle == 0) return null;
                var pointer = GlobalLock(handle);
                if (pointer == 0) return null;
                try
                {
                    return Marshal.PtrToStringUni(pointer);
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
    }

    public bool SetText(string text, bool excludeFromHistory = false)
    {
        lock (ClipboardLock)
        {
            if (!Open()) return false;
            try
            {
                if (!EmptyClipboard()) return false;
                var bytes = Encoding.Unicode.GetBytes(text + "\0");
                if (!SetBytes(CF_UNICODETEXT, bytes)) return false;
                if (excludeFromHistory)
                {
                    // Keeps a transient paste out of Win+V history, cloud clipboard and well-behaved clipboard managers.
                    SetBytes(RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"), [0]);
                    SetBytes(RegisterClipboardFormat("CanIncludeInClipboardHistory"), BitConverter.GetBytes(0));
                    SetBytes(RegisterClipboardFormat("CanUploadToCloudClipboard"), BitConverter.GetBytes(0));
                }
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }
    }

    public ClipboardSnapshot Snapshot()
    {
        lock (ClipboardLock)
        {
            if (!Open()) return ClipboardSnapshot.Empty;
            try
            {
                var formats = new List<ClipboardFormatData>();
                long totalBytes = 0;
                uint format = 0;
                while ((format = EnumClipboardFormats(format)) != 0)
                {
                    if (!IsCopyableFormat(format)) continue;
                    var handle = GetClipboardData(format);
                    if (handle == 0) continue;
                    var size = (long)GlobalSize(handle);
                    if (size <= 0 || totalBytes + size > MaximumSnapshotBytes) continue;
                    var pointer = GlobalLock(handle);
                    if (pointer == 0) continue;
                    try
                    {
                        var data = new byte[size];
                        Marshal.Copy(pointer, data, 0, (int)size);
                        formats.Add(new ClipboardFormatData(format, FormatName(format), data));
                        totalBytes += size;
                    }
                    finally
                    {
                        GlobalUnlock(handle);
                    }
                }
                return new ClipboardSnapshot(formats);
            }
            finally
            {
                CloseClipboard();
            }
        }
    }

    public bool Restore(ClipboardSnapshot snapshot)
    {
        lock (ClipboardLock)
        {
            if (!Open()) return false;
            try
            {
                if (!EmptyClipboard()) return false;
                var allWritten = true;
                foreach (var format in snapshot.Formats)
                {
                    // Registered format ids are per session but stable; re-register by name to be safe.
                    var formatId = format.FormatName is { Length: > 0 } name && format.FormatId >= 0xC000
                        ? RegisterClipboardFormat(name)
                        : format.FormatId;
                    allWritten &= SetBytes(formatId, format.Data);
                }
                return allWritten;
            }
            finally
            {
                CloseClipboard();
            }
        }
    }

    public bool HasFormat(string registeredFormatName)
    {
        var formatId = RegisterClipboardFormat(registeredFormatName);
        return formatId != 0 && IsClipboardFormatAvailable(formatId);
    }

    public bool ContainsTransientContent()
    {
        foreach (var name in TransientMarkerFormats)
        {
            if (HasFormat(name)) return true;
        }
        // CanIncludeInClipboardHistory present with value 0 = "do not keep this".
        var historyFormat = RegisterClipboardFormat("CanIncludeInClipboardHistory");
        if (historyFormat == 0 || !IsClipboardFormatAvailable(historyFormat)) return false;
        lock (ClipboardLock)
        {
            if (!Open()) return false;
            try
            {
                var handle = GetClipboardData(historyFormat);
                if (handle == 0 || (long)GlobalSize(handle) < 4) return false;
                var pointer = GlobalLock(handle);
                if (pointer == 0) return false;
                try
                {
                    return Marshal.ReadInt32(pointer) == 0;
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
    }

    /// <summary>HGLOBAL formats only: bitmaps, palettes and metafiles are GDI handles, and CF_BITMAP is synthesised from CF_DIB anyway.</summary>
    private static bool IsCopyableFormat(uint format) => format switch
    {
        CF_BITMAP or CF_METAFILEPICT or CF_PALETTE or CF_ENHMETAFILE or CF_OWNERDISPLAY
            or CF_DSPBITMAP or CF_DSPMETAFILEPICT or CF_DSPENHMETAFILE => false,
        >= CF_GDIOBJFIRST and <= CF_GDIOBJLAST => false,
        _ => true,
    };

    private static string? FormatName(uint format)
    {
        if (format < 0xC000) return null;
        var builder = new StringBuilder(256);
        return GetClipboardFormatName(format, builder, builder.Capacity) > 0 ? builder.ToString() : null;
    }

    /// <summary>Copies bytes into a movable HGLOBAL the clipboard then owns.</summary>
    private static bool SetBytes(uint format, byte[] data)
    {
        if (format == 0) return false;
        var handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(data.Length, 1));
        if (handle == 0) return false;
        var pointer = GlobalLock(handle);
        if (pointer == 0)
        {
            GlobalFree(handle);
            return false;
        }
        Marshal.Copy(data, 0, pointer, data.Length);
        GlobalUnlock(handle);
        if (SetClipboardData(format, handle) == 0)
        {
            GlobalFree(handle);
            return false;
        }
        return true;
    }

    private static bool Open()
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            if (OpenClipboard(0)) return true;
            Thread.Sleep(10);
        }
        return false;
    }
}

/// <summary>AddClipboardFormatListener on a message-only window on its own thread.</summary>
public sealed class ClipboardChangeListener : IClipboardChangeListener
{
    private MessageLoopThread? loopThread;

    public event Action? ClipboardChanged;

    public void Start()
    {
        if (loopThread is not null) return;
        loopThread = new MessageLoopThread("OpenClicky clipboard listener", HandleMessage);
        var windowHandle = loopThread.WindowHandle;
        if (windowHandle == 0 || !loopThread.Invoke(() => AddClipboardFormatListener(windowHandle)))
        {
            Stop();
            throw new InvalidOperationException("couldn't listen to the clipboard");
        }
    }

    public void Stop()
    {
        if (loopThread is null) return;
        var windowHandle = loopThread.WindowHandle;
        if (windowHandle != 0) loopThread.Invoke(() => RemoveClipboardFormatListener(windowHandle));
        loopThread.Dispose();
        loopThread = null;
    }

    private nint? HandleMessage(nint windowHandle, uint message, nint wParam, nint lParam)
    {
        if (message != WM_CLIPBOARDUPDATE) return null;
        try
        {
            ClipboardChanged?.Invoke();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"clipboard listener handler failed: {exception}");
        }
        return 0;
    }

    public void Dispose() => Stop();
}
