using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using OpenClicky.Core.Platform;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

/// <summary>The OS side of the fast-lane actions (MacActionRunner.live's closures).</summary>
public sealed partial class DesktopActionExecutor : IDesktopActionExecutor
{
    private static readonly string[] ShortcutExtensions = [".lnk", ".url", ".appref-ms"];

    public DesktopAppTarget? FindApplication(string name)
    {
        var wanted = name.Trim();
        if (wanted.Length == 0) return null;
        if (wanted.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) wanted = wanted[..^4];

        var shortcutMatch = BestMatch(wanted, StartMenuShortcuts());
        if (shortcutMatch is not null) return shortcutMatch;
        var appPath = AppPathsExecutable(wanted);
        if (appPath is not null) return new DesktopAppTarget(wanted, DesktopAppTargetKind.Executable, appPath);
        return BestMatch(wanted, PackagedApps());
    }

    public bool LaunchApplication(DesktopAppTarget target)
    {
        try
        {
            if (target.Kind == DesktopAppTargetKind.PackagedApp)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + target.Target) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(target.Target) { UseShellExecute = true });
            }
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    public bool OpenUrl(Uri url)
    {
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(url.Host)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public string? KnownFolderPath(string location) => location.Trim().ToLowerInvariant() switch
    {
        "desktop" => KnownFolder(FOLDERID_Desktop),
        "downloads" => KnownFolder(FOLDERID_Downloads),
        "documents" => KnownFolder(FOLDERID_Documents),
        "home" => KnownFolder(FOLDERID_Profile) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        _ => null,
    };

    public bool RevealInExplorer(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public bool SetVolume(int percent)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return false;
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var level = Math.Clamp(percent, 0, 100);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = level / 100f;
            if (level > 0 && device.AudioEndpointVolume.Mute) device.AudioEndpointVolume.Mute = false;
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    public int? GetVolume()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return (int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public bool SendMediaKey(string action)
    {
        int? virtualKey = action.Trim().ToLowerInvariant() switch
        {
            "playpause" => VirtualKeys.MediaPlayPause,
            "next" => VirtualKeys.MediaNextTrack,
            "previous" => VirtualKeys.MediaPreviousTrack,
            "mute" => VirtualKeys.VolumeMute,
            _ => null,
        };
        if (virtualKey is not { } key) return false;
        return Send(VirtualKeyInput(key, keyUp: false), VirtualKeyInput(key, keyUp: true)) == 2;
    }

    // ------------------------------------------------------------------ app lookup

    private static IEnumerable<DesktopAppTarget> StartMenuShortcuts()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
        };
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 };
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                if (!ShortcutExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                yield return new DesktopAppTarget(Path.GetFileNameWithoutExtension(file), DesktopAppTargetKind.Shortcut, file);
            }
        }
    }

    private static string? AppPathsExecutable(string name)
    {
        const string appPaths = @"Software\Microsoft\Windows\CurrentVersion\App Paths\";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(appPaths + name + ".exe");
            if (key?.GetValue(null) is string path)
            {
                var unquoted = path.Trim().Trim('"');
                if (File.Exists(unquoted)) return unquoted;
            }
        }
        return null;
    }

    /// <summary>shell:AppsFolder through the Shell.Application automation object (on an STA thread, as the shell requires).</summary>
    private static IReadOnlyList<DesktopAppTarget> PackagedApps()
    {
        var apps = new List<DesktopAppTarget>();
        var thread = new Thread(() =>
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is null) return;
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                if (folder is null) return;
                foreach (dynamic item in folder.Items())
                {
                    string displayName = item.Name;
                    string path = item.Path;
                    // Packaged apps' paths are AppUserModelIDs ("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App").
                    if (!string.IsNullOrEmpty(displayName) && path.Contains('!')) apps.Add(new DesktopAppTarget(displayName, DesktopAppTargetKind.PackagedApp, path));
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"apps folder: {exception.Message}");
            }
        })
        { IsBackground = true, Name = "OpenClicky apps folder" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(3));
        return apps;
    }

    /// <summary>Exact name, then letters-and-digits-only equal, then a name starting with it, then a name holding all its words; shortest wins.</summary>
    internal static DesktopAppTarget? BestMatch(string wanted, IEnumerable<DesktopAppTarget> candidates)
    {
        var usable = candidates
            .Where(candidate => !candidate.DisplayName.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var wantedKey = Normalized(wanted);
        if (wantedKey.Length == 0) return null;
        var wantedWords = Words(wanted);

        Func<DesktopAppTarget, bool>[] steps =
        [
            candidate => string.Equals(candidate.DisplayName, wanted, StringComparison.OrdinalIgnoreCase),
            candidate => Normalized(candidate.DisplayName) == wantedKey,
            candidate => wantedKey.Length >= 3 && Normalized(candidate.DisplayName).StartsWith(wantedKey, StringComparison.Ordinal),
            candidate => wantedKey.Length >= 3 && wantedWords.All(Words(candidate.DisplayName).Contains),
        ];
        foreach (var step in steps)
        {
            var match = usable.Where(step).OrderBy(candidate => candidate.DisplayName.Length).FirstOrDefault();
            if (match is not null) return match;
        }
        return null;
    }

    private static string Normalized(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static HashSet<string> Words(string text) =>
        text.ToLowerInvariant().Split(text.Where(character => !char.IsLetterOrDigit(character)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
}
