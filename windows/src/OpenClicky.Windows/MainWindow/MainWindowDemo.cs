// The window on its own, against real Core objects in a throwaway data root (OpenClickyPaths
// .OverrideRoots) with some seeded takes, words and snippets: for looking at the pages without the
// rest of the app. Integration: Program.cs calls MainWindowDemo.Run(args) for
// `--openclicky-window-demo`; `--shots <folder>` also saves a screenshot of every page, light and
// dark, then closes (windows/docs/screenshots/window-*.png were made this way).

using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Settings;
using OpenClicky.Windows.Platform;

namespace OpenClicky.Windows.AppWindow;

public static class MainWindowDemo
{
    /// <summary>Opens the window over a seeded temporary data root. Blocks until it closes when it starts its own Application.</summary>
    public static void Run(string[]? args = null)
    {
        args ??= [];
        var shotsIndex = Array.IndexOf(args, "--shots");
        var shotsFolder = shotsIndex >= 0 && shotsIndex + 1 < args.Length ? Path.GetFullPath(args[shotsIndex + 1]) : null;
        var pageIndex = Array.IndexOf(args, "--page");
        var onlyPage = pageIndex >= 0 && pageIndex + 1 < args.Length ? args[pageIndex + 1] : null;

        var root = Path.Combine(Path.GetTempPath(), "openclicky-window-demo-" + Guid.NewGuid().ToString("N")[..8]);
        OpenClickyPaths.OverrideRoots(Path.Combine(root, "home"), Path.Combine(root, "local"));
        Directory.CreateDirectory(OpenClickyPaths.SettingsDirectory);
        Directory.CreateDirectory(OpenClickyPaths.AppDataDirectory);
        AppLog.UseFile(Path.Combine(root, "app.log"));

        var ownsApplication = Application.Current is null;
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };

        var preferences = new Preferences(OpenClickyPaths.PreferencesFile);
        // The tour starts on its own the first time General opens; the demo starts it only when asked.
        preferences.Set(SettingsTourController.HasPlayedKey, !args.Contains("--tour"));
        var settings = new DictationSettings(preferences);
        // --speech-server <address>: Your models shows a speech server turned on, hearing and speaking.
        var speechServerIndex = Array.IndexOf(args, "--speech-server");
        var speechServer = speechServerIndex >= 0 && speechServerIndex + 1 < args.Length ? args[speechServerIndex + 1] : null;
        OpenClickyConfiguration.Update(shell =>
        {
            shell.OpenaiApiKey = "sk-demo-not-a-real-key-7f3a";
            shell.ElevenLabsApiKey = "sk_demo_voice_19c2";
            if (speechServer is null) return;
            shell.SpeechServerUrl = speechServer;
            shell.SpeechServerHearingModel = "gemma4:e2b-it-qat";
            shell.SpeechServerVoiceModel = "speaches-ai/Kokoro-82M-v1.0-ONNX";
            shell.SpeechServerVoice = "af_heart";
            shell.SpeechVoice = "server";
        });
        var spaceStore = new DictationSpaceStore(OpenClickyPaths.DictationDirectory);
        SeedSpace(spaceStore);
        TakeStore? takeStore = null;
        try
        {
            takeStore = new TakeStore(OpenClickyPaths.DictationDatabaseFile);
            SeedTakes(takeStore);
        }
        catch (TakeStoreException error)
        {
            AppLog.Write($"window demo: no history: {error.Message}");
        }
        var platform = new PlatformServices(application.Dispatcher);
        var services = MainWindowServices.Detached(platform, settings, spaceStore, takeStore, new TakeAudioStore(), preferences);
        var window = new DictationWindow(services) { HidesOnClose = false };
        window.Closed += (_, _) =>
        {
            takeStore?.Dispose();
            OpenClickyPaths.ResetOverrides();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
        if (shotsFolder is not null)
        {
            window.Loaded += async (_, _) => await TakeScreenshotsAsync(window, settings, shotsFolder, onlyPage);
        }
        window.Open(section: DictationSection.Words);
        if (ownsApplication) application.Run(window);
    }

    private static void SeedSpace(DictationSpaceStore spaceStore)
    {
        spaceStore.Update(space =>
        {
            space.Dictionary.Add(new DictionaryTerm("Siobhán", ["shivon", "shiv on"]));
            space.Dictionary.Add(new DictionaryTerm("OpenClicky", ["open clicky"]));
            space.Dictionary.Add(new DictionaryTerm("Kubernetes", ["cooper netties"]));
            space.Shortcuts.Add(new SpokenShortcut("my sign-off", "Thanks,\nPrasanth"));
            space.Shortcuts.Add(new SpokenShortcut("my address", "12 Example Street, Auckland 1010"));
        });
    }

    private static void SeedTakes(TakeStore takeStore)
    {
        var now = DateTimeOffset.Now;
        TakeRecord Take(double hoursAgo, string text, string processName, string appName, TakeMode mode = TakeMode.Dictate,
            TakeStatus status = TakeStatus.Complete, string? failure = null, bool pinned = false) => new()
        {
            CreatedAt = now.AddHours(-hoursAgo),
            Mode = mode,
            Status = status,
            RawText = text.ToLower(CultureInfo.InvariantCulture).TrimEnd('.'),
            FormattedText = text,
            AppProcessName = processName,
            AppName = appName,
            Engine = "offline",
            DurationSeconds = 4 + text.Length / 20.0,
            PasteOutcome = status == TakeStatus.Complete ? TakePasteOutcome.Verified : TakePasteOutcome.None,
            Pinned = pinned,
            FailureReason = failure,
        };
        var takes = new[]
        {
            Take(0.2, "Can we move the design review to Thursday afternoon? Friday is packed.", "slack.exe", "Slack"),
            Take(0.6, "Thanks for the notes, I'll fold them into the next draft and send it round tomorrow.", "OUTLOOK.EXE", "Outlook", pinned: true),
            Take(1.1, "", "chrome.exe", "Google Chrome", status: TakeStatus.Failed, failure: "no internet"),
            Take(1.5, "Make this more formal", "OUTLOOK.EXE", "Outlook", mode: TakeMode.Edit),
            Take(2.4, "rename the helper to settings tour targets and run the tests again", "Code.exe", "Visual Studio Code"),
            Take(26, "Remind me to call Siobhán about the Kubernetes migration.", "notepad.exe", "Notepad"),
            Take(28, "The orb should hide when idle, but the box stays open after a failed paste.", "Code.exe", "Visual Studio Code"),
            Take(50, "Dinner at seven? I'll bring dessert.", "WhatsApp.exe", "WhatsApp"),
            Take(75, "OpenClicky now runs on Windows too.", "chrome.exe", "Google Chrome"),
            Take(100, "Send the invoice for October before Friday.", "OUTLOOK.EXE", "Outlook"),
        };
        foreach (var take in takes) takeStore.Insert(take);
        takeStore.Revise(takes[0].Id, "Can we move the design review to Thursday afternoon? Friday is full.", "history");
    }

    // MARK: - screenshots

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    private const int ExtendedFrameBounds = 9;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    private const uint RenderFullContent = 2;

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    /// <summary>Every page in sidebar order, then the pages opened from rows, light then dark, plus the search results and the page ends.</summary>
    private static async Task TakeScreenshotsAsync(DictationWindow window, DictationSettings settings, string folder, string? onlyPage)
    {
        Directory.CreateDirectory(folder);
        window.Topmost = true;
        // Out of the window, so no row shows a hover.
        SetCursorPos(0, 0);
        await Delay(1200);
        foreach (var appearance in new[] { DictationAppearance.Light, DictationAppearance.Dark })
        {
            settings.Appearance = appearance;
            var themeName = appearance == DictationAppearance.Light ? "light" : "dark";
            await Delay(500);
            DictationDestination[] destinations =
            [
                .. DictationDestination.SidebarOrder,
                .. DictationSettingsPages.AllCases.Where(page => page.SidebarPage() != page).Select(DictationDestination.Of),
                .. DictationSettingsPages.SidebarPages.SelectMany(page => SettingsLayout.SectionsOf(page).Select(section => DictationDestination.Of(page, section.Title!))),
                .. DictationSections.AllCases.Select(DictationDestination.Of),
            ];
            foreach (var destination in destinations)
            {
                var name = destination.Page is { } page
                    ? destination.PageSection is { } pageSection ? $"{page.RawValue()}-{SettingsTourTargets.SectionSlug(pageSection)}" : page.RawValue()
                    : destination.Section!.Value.RawValue();
                if (onlyPage is not null && onlyPage != name) continue;
                window.Model.Select(destination);
                await Delay(700);
                FindScroller(window)?.ScrollToTop();
                await Delay(250);
                Capture(window, Path.Combine(folder, $"window-{name}-{themeName}.png"));
                if (FindScroller(window) is { } scroller && scroller.ScrollableHeight > 40)
                {
                    scroller.ScrollToEnd();
                    await Delay(400);
                    Capture(window, Path.Combine(folder, $"window-{name}-{themeName}-end.png"));
                }
            }
            if (onlyPage is null || onlyPage == "search")
            {
                window.Model.SearchText = "key";
                await Delay(700);
                Capture(window, Path.Combine(folder, $"window-search-{themeName}.png"));
                window.Model.SearchText = "";
            }
        }
        window.Close();
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is SettingsFormView or WordsPageView or WritingPageView) return (ScrollViewer)child;
            if (FindScroller(child) is { } found) return found;
        }
        return null;
    }

    private static Task Delay(int milliseconds)
    {
        var completion = new TaskCompletionSource();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            completion.SetResult();
        };
        timer.Start();
        return completion.Task;
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var handle = new WindowInteropHelper(window).Handle;
        if (DwmGetWindowAttribute(handle, ExtendedFrameBounds, out var bounds, Marshal.SizeOf<NativeRect>()) != 0) return;
        // The window draws itself (PrintWindow), so an always-on-top window in front of it never ends up in the picture
        // (a running OpenClicky's own did, leaving the shots blank); cut to its visible frame.
        if (!GetWindowRect(handle, out var outer)) return;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        using var whole = new System.Drawing.Bitmap(outer.Right - outer.Left, outer.Bottom - outer.Top);
        using (var graphics = System.Drawing.Graphics.FromImage(whole))
        {
            var deviceContext = graphics.GetHdc();
            try { PrintWindow(handle, deviceContext, RenderFullContent); }
            finally { graphics.ReleaseHdc(deviceContext); }
        }
        using var bitmap = whole.Clone(new System.Drawing.Rectangle(bounds.Left - outer.Left, bounds.Top - outer.Top, width, height), whole.PixelFormat);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
