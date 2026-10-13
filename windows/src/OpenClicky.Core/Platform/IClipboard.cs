namespace OpenClicky.Core.Platform;

/// <summary>One clipboard format's bytes (HGLOBAL-backed formats only; GDI handle formats are not copied).</summary>
public sealed record ClipboardFormatData(uint FormatId, string? FormatName, byte[] Data);

/// <summary>Everything on the clipboard that could be copied, to put back after a paste.</summary>
public sealed class ClipboardSnapshot
{
    public static readonly ClipboardSnapshot Empty = new(Array.Empty<ClipboardFormatData>());

    public ClipboardSnapshot(IReadOnlyList<ClipboardFormatData> formats) => Formats = formats;

    public IReadOnlyList<ClipboardFormatData> Formats { get; }

    public bool IsEmpty => Formats.Count == 0;
}

/// <summary>The Win32 clipboard. Safe to call from any thread (it retries while another app holds the clipboard open).</summary>
public interface IClipboard
{
    /// <summary>GetClipboardSequenceNumber: changes on every clipboard change (NSPasteboard.changeCount).</summary>
    uint SequenceNumber { get; }

    string? GetText();

    /// <summary>
    /// Replaces the clipboard with text. <paramref name="excludeFromHistory"/> adds the
    /// "ExcludeClipboardContentFromMonitorProcessing" / "CanIncludeInClipboardHistory = 0" markers so
    /// a transient paste does not land in Win+V history or other clipboard monitors.
    /// </summary>
    bool SetText(string text, bool excludeFromHistory = false);

    ClipboardSnapshot Snapshot();

    bool Restore(ClipboardSnapshot snapshot);

    /// <summary>Whether a registered format (by name) is on the clipboard now.</summary>
    bool HasFormat(string registeredFormatName);

    /// <summary>
    /// Password managers' "do not record this" markers are on the clipboard
    /// (ExcludeClipboardContentFromMonitorProcessing, Clipboard Viewer Ignore, CanIncludeInClipboardHistory = 0):
    /// the Windows equivalents of org.nspasteboard.TransientType / ConcealedType.
    /// </summary>
    bool ContainsTransientContent();
}

/// <summary>Clipboard-change notifications (AddClipboardFormatListener on a message-only window). Raised on the listener's own thread.</summary>
public interface IClipboardChangeListener : IDisposable
{
    event Action? ClipboardChanged;

    void Start();

    void Stop();
}
