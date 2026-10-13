// Ports Dictation/UI/AppIconView.swift: an app's icon and name, as the window's pages show them next
// to a take or a writing style, cached because a list redraws them on every scroll. The Mac looked an
// app up by bundle id; on Windows a take and a style name the app by its process image name
// ("chrome.exe"), which is found among the running processes, then in App Paths, then in System32,
// and its icon comes from IFrontmostAppObserver.AppIconPng.

using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenClicky.Core.Platform;

namespace OpenClicky.Windows.AppWindow;

internal sealed class AppIconCache(IFrontmostAppObserver frontmostApp)
{
    private readonly Dictionary<string, string?> executablePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource?> icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers where an app was found (the app picker knows the path already).</summary>
    public void Remember(string processName, string executablePath) => executablePaths[processName] = executablePath;

    public string? ExecutablePath(string processName)
    {
        if (executablePaths.TryGetValue(processName, out var cached)) return cached;
        var found = FindExecutable(processName);
        executablePaths[processName] = found;
        return found;
    }

    private static string? FindExecutable(string processName)
    {
        var bareName = Path.GetFileNameWithoutExtension(processName);
        try
        {
            foreach (var process in Process.GetProcessesByName(bareName))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName is { } fileName) return fileName;
                    }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        // An elevated or exiting process: try the next one.
                    }
                }
            }
        }
        catch (InvalidOperationException) { }
        var imageName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName : processName + ".exe";
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{imageName}");
                if (key?.GetValue(null) is string registeredPath && File.Exists(registeredPath.Trim('"'))) return registeredPath.Trim('"');
            }
            catch (System.Security.SecurityException) { }
        }
        foreach (var folder in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
        {
            var candidate = Path.Combine(folder, imageName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public ImageSource? Icon(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return null;
        if (icons.TryGetValue(processName, out var cached)) return cached;
        ImageSource? image = null;
        if (ExecutablePath(processName) is { } path && frontmostApp.AppIconPng(path, 32) is { Length: > 0 } png)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(png);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            catch (Exception error) when (error is NotSupportedException or FileFormatException) { }
        }
        icons[processName] = image;
        return image;
    }

    /// <summary>"Google Chrome" for chrome.exe (the file's description), or null when the app isn't on this PC.</summary>
    public string? Name(string processName)
    {
        if (names.TryGetValue(processName, out var cached)) return cached;
        string? name = null;
        if (ExecutablePath(processName) is { } path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription.Trim()
                    : !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName.Trim()
                    : Path.GetFileNameWithoutExtension(path);
            }
            catch (FileNotFoundException) { }
        }
        names[processName] = name;
        return name;
    }

    /// <summary>Whether the app is on this PC (a style lists only those).</summary>
    public bool IsOnThisPC(string processName) => ExecutablePath(processName) is not null;
}

internal static class AppIconView
{
    /// <summary>The icon of an app, or a quiet placeholder for an app that is not on this PC (or a take that recorded none).</summary>
    public static FrameworkElement Make(AppIconCache cache, string? processName, double size = 18)
    {
        if (cache.Icon(processName) is { } icon)
        {
            var image = new Image { Source = icon, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }
        var placeholder = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.22), VerticalAlignment = VerticalAlignment.Center };
        placeholder.SetResourceReference(Border.BackgroundProperty, "ControlStrongFillColorDisabledBrush");
        return placeholder;
    }

    /// <summary>The clipboard's own icon, for a clipboard take.</summary>
    public static FrameworkElement ClipboardIcon(double size = 18)
    {
        var glyph = Ui.Text("", size - 2, SettingsLook.TextSecondary, wrap: false);
        glyph.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        glyph.Width = size;
        glyph.TextAlignment = TextAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        return glyph;
    }
}
