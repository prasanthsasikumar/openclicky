//
//  ExplorerReveal.cs
//  OpenClicky (Windows)
//
//  Shows a file selected in File Explorer, in the Explorer window already open on its folder when there is one.
//  `explorer.exe /select,<file>` opens a new window every time, so saving three drawings left three windows on
//  Videos\OpenClicky; SHOpenFolderAndSelectItems (the shell's own "show in folder") finds a window already showing the
//  folder, selects the file there and brings it forward, and only opens a window when none is. Falls back to
//  explorer.exe when the shell call fails.
//

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Windows.Platform;

internal static class ExplorerReveal
{
    /// <summary>Selects <paramref name="path"/> (a file or folder) in an Explorer window on its parent folder. False when
    /// it doesn't exist or neither the shell nor explorer.exe could show it.</summary>
    public static bool Select(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        if (RunOnStaThread(() => ShellSelect(path))) return true;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"explorer: couldn't show {Path.GetFileName(path)} ({error.Message})");
            return false;
        }
    }

    /// <summary>The shell call, on its own STA thread: it needs COM, and callers may be on a thread pool thread.</summary>
    private static bool RunOnStaThread(Func<bool> work)
    {
        var result = false;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception error) when (error is COMException or ArgumentException or ExternalException)
            {
                AppLog.Write($"explorer: the shell couldn't select the file ({error.GetType().Name})");
            }
        }) { IsBackground = true, Name = "Explorer reveal" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // The shell answers in well under a second; a hung Explorer must not hold the caller.
        return thread.Join(TimeSpan.FromSeconds(5)) && result;
    }

    private static bool ShellSelect(string path)
    {
        if (SHParseDisplayName(Path.GetFullPath(path), IntPtr.Zero, out var item, 0, out _) != 0 || item == IntPtr.Zero) return false;
        try
        {
            // An item's full PIDL with no children: the shell opens (or reuses) the window on its parent and selects it.
            return SHOpenFolderAndSelectItems(item, 0, null, 0) == 0;
        }
        finally
        {
            ILFree(item);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint attributesWanted, out uint attributes);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr[]? items, uint flags);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);
}
