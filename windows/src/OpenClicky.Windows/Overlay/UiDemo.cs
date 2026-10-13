//
//  UiDemo.cs
//  OpenClicky (Windows)
//
//  `--openclicky-ui-demo`: walks the companion UI through its states with no orchestrator behind
//  it (the pointer flying to a point, the caption, the waveform and spinner, the orb's phases, the
//  island's states and cards) and saves a screenshot of each. Capture exclusion is off in demo mode
//  so the windows show up in the screenshots. The Mac counterpart is the DEBUG UI tour.
//
//    UiDemo.Run()                      run, screenshots under %TEMP%\OpenClickyUiDemo
//    UiDemo.Run(@"windows\docs\screenshots")
//

using System.IO;
using System.Windows;
using System.Windows.Threading;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;
using OpenClicky.Windows.Island;
using OpenClicky.Windows.Orb;
using OpenClicky.Windows.Platform;

namespace OpenClicky.Windows.Overlay;

public static class UiDemo
{
    /// <summary>Runs the demo, saving screenshots to <paramref name="screenshotDirectory"/>. Starts a WPF application
    /// when none runs yet (the `--openclicky-ui-demo` flag branches off before the app starts) and shuts it down at
    /// the end; inside a running app it only opens and closes its own windows.</summary>
    public static void Run(string? screenshotDirectory = null)
    {
        var directory = screenshotDirectory ?? Path.Combine(Path.GetTempPath(), "OpenClickyUiDemo");
        Directory.CreateDirectory(directory);
        FloatingWindowSupport.AllowsScreenCapture = true;
        if (Application.Current is { } runningApplication)
        {
            runningApplication.Dispatcher.BeginInvoke(async () => await RunScriptAsync(directory));
            return;
        }
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Startup += (_, _) => application.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await RunScriptAsync(directory);
            }
            catch (Exception exception)
            {
                AppLog.Write($"ui demo: failed: {exception}");
                File.WriteAllText(Path.Combine(directory, "ui-demo-error.txt"), exception.ToString());
            }
            finally
            {
                application.Shutdown();
            }
        });
        application.Run();
    }

    private static async Task RunScriptAsync(string directory)
    {
        var screens = new Screens();
        var primary = screens.Primary() ?? screens.All().First();
        var preferences = Preferences.InMemory();
        var script = new DemoScript(directory, screens, primary, preferences);
        try
        {
            await script.RunAsync();
        }
        finally
        {
            script.Dispose();
            screens.Dispose();
        }
    }

    /// <summary>Lets screenshots see the floating windows from now on (they are left out of captures otherwise).</summary>
    public static void AllowScreenCapture() => FloatingWindowSupport.AllowsScreenCapture = true;

    /// <summary>Saves the pixels of <paramref name="region"/> (physical pixels) as a PNG, layered windows included.</summary>
    public static void SaveScreenshot(PixelRect region, string path)
    {
        using var bitmap = new System.Drawing.Bitmap(region.Width, region.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            // CAPTUREBLT so layered windows (all of ours) are in the picture; Graphics.CopyFromScreen refuses that flag.
            var destination = graphics.GetHdc();
            var screenDeviceContext = GetDC(0);
            try
            {
                BitBlt(destination, 0, 0, region.Width, region.Height, screenDeviceContext, region.Left, region.Top, SourceCopy | CaptureBlt);
            }
            finally
            {
                ReleaseDC(0, screenDeviceContext);
                graphics.ReleaseHdc(destination);
            }
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private const int SourceCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetDC(nint windowHandle);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(nint windowHandle, nint deviceContext);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int rasterOperation);

    private sealed class DemoScript : IDisposable
    {
        private readonly string directory;
        private readonly ScreenInfo primary;
        private readonly Preferences preferences;
        private readonly CompanionUi ui;
        private OverlayModel overlayModel => ui.Overlay;
        private OverlayWindowManager overlayManager => ui.OverlayWindows;
        private IslandCompanionModel islandModel => ui.Island;
        private IslandWindowManager islandManager => ui.IslandWindows;
        private OrbModel orbModel => ui.Orb;
        private OrbSettings orbSettings => ui.OrbSettings;
        private OrbWindow orbWindow => ui.OrbWindow;
        private AgentResultCardModel agentCardModel => ui.AgentCard;

        public DemoScript(string directory, Screens screens, ScreenInfo primary, Preferences preferences)
        {
            this.directory = directory;
            this.primary = primary;
            this.preferences = preferences;
            ui = new CompanionUi(screens, preferences);
        }

        private static Task Wait(double seconds) => Task.Delay(TimeSpan.FromSeconds(seconds));

        private void Shot(string name, PixelRect region)
        {
            var path = Path.Combine(directory, name + ".png");
            SaveScreenshot(region, path);
            AppLog.Write($"ui demo: saved {path}");
        }

        /// <summary>A region around a point given in the primary monitor's DIPs.</summary>
        private PixelRect AroundDips(double leftDips, double topDips, double widthDips, double heightDips) =>
            primary.DipsToPixels(new ScreenRect(primary.BoundsInDips.X + leftDips, primary.BoundsInDips.Y + topDips, widthDips, heightDips));

        private PixelRect IslandRegion(double heightDips = 220) =>
            AroundDips(primary.BoundsInDips.Width / 2 - 300, 0, 600, heightDips);

        private static void MoveMouse(PixelPoint point) => System.Windows.Forms.Cursor.Position = new System.Drawing.Point(point.X, point.Y);

        public async Task RunAsync()
        {
            var center = new PixelPoint(primary.Bounds.Left + primary.Bounds.Width / 2, primary.Bounds.Top + primary.Bounds.Height / 2);
            MoveMouse(new PixelPoint(center.X - (int)(300 * primary.Scale), center.Y));

            // The island, collapsed (the handle line).
            islandManager.Show();
            await Wait(1.0);
            Shot("island-collapsed", IslandRegion(60));

            // The pointer: following the mouse (and a quiet spell, for the idle cost of the overlay).
            overlayModel.ShowOverlayOnAllScreens();
            await Wait(6.0);
            var mouse = FloatingWindowSupport.CursorPixel();
            Shot("overlay-following", new PixelRect(mouse.X - 60, mouse.Y - 60, (int)(200 * primary.Scale), (int)(140 * primary.Scale)));

            // The caption beside it.
            overlayModel.StreamCursorCaption(IslandTexts.ExplainMessage(islandModel.DictationKey), 6);
            await Wait(7 * 0.03 * 10 + 6);
            Shot("overlay-caption", new PixelRect(mouse.X - 60, mouse.Y - 60, (int)(420 * primary.Scale), (int)(160 * primary.Scale)));
            overlayModel.DismissCursorCaption();
            await Wait(0.5);

            // Listening: the waveform, and the island's busy strip.
            overlayModel.VoiceState = CompanionVoiceState.Listening;
            islandModel.VoiceState = CompanionVoiceState.Listening;
            for (var frame = 0; frame < 40; frame++)
            {
                overlayModel.CurrentAudioPowerLevel = 0.15 + 0.15 * Math.Sin(frame / 3.0);
                islandModel.CurrentAudioPowerLevel = overlayModel.CurrentAudioPowerLevel;
                await Wait(0.05);
            }
            Shot("overlay-listening", new PixelRect(mouse.X - 40, mouse.Y - 40, (int)(120 * primary.Scale), (int)(100 * primary.Scale)));
            Shot("island-compact-listening", IslandRegion(90));

            // Thinking: the spinner.
            overlayModel.VoiceState = CompanionVoiceState.Processing;
            islandModel.VoiceState = CompanionVoiceState.Processing;
            await Wait(0.8);
            Shot("overlay-processing", new PixelRect(mouse.X - 40, mouse.Y - 40, (int)(120 * primary.Scale), (int)(100 * primary.Scale)));
            Shot("island-compact-processing", IslandRegion(90));

            // Speaking, then pointing at something.
            overlayModel.VoiceState = CompanionVoiceState.Responding;
            islandModel.VoiceState = CompanionVoiceState.Responding;
            islandModel.LastTranscript = "where do i change the repository visibility?";
            islandModel.AnsweringSourceName = "Claude";
            await Wait(0.6);
            Shot("island-compact-speaking", IslandRegion(110));
            var target = new ScreenPoint(primary.Bounds.Left + primary.Bounds.Width * 0.72, primary.Bounds.Top + primary.Bounds.Height * 0.62);
            overlayModel.PointAt(target, primary.FrameInPixels(), "change visibility here");
            await Wait(0.7);
            var targetRegion = new PixelRect((int)target.X - (int)(260 * primary.Scale), (int)target.Y - (int)(140 * primary.Scale), (int)(420 * primary.Scale), (int)(220 * primary.Scale));
            Shot("overlay-pointing-flight", targetRegion);
            await Wait(1.8);
            Shot("overlay-pointing", targetRegion);
            await Wait(4.5);
            overlayModel.VoiceState = CompanionVoiceState.Idle;
            islandModel.VoiceState = CompanionVoiceState.Idle;
            islandModel.LastTranscript = null;

            // The settings tour's click ripple (once the pointer is back at the mouse).
            await Wait(1.5);
            overlayModel.ShowPointerClick();
            await Wait(0.06);
            mouse = FloatingWindowSupport.CursorPixel();
            Shot("overlay-click-ripple", new PixelRect(mouse.X - 120, mouse.Y - 120, 300, 260));
            await Wait(0.6);

            // The full panel (hover).
            islandManager.OpenPanel();
            await Wait(0.3);
            Shot("island-full-opening", IslandRegion(130));
            await Wait(0.9);
            Shot("island-full", IslandRegion(130));

            // Dock into the island, and back out.
            islandManager.CloseAll();
            await Wait(0.5);
            overlayModel.SetCursorDocked(true);
            await Wait(0.75);
            Shot("overlay-docking-flight", IslandRegion(400));
            await Wait(2.5);
            islandManager.OpenPanel();
            await Wait(0.9);
            Shot("island-full-docked", IslandRegion(130));
            islandManager.CloseAll();
            await Wait(0.5);
            overlayModel.SetCursorDocked(false);
            await Wait(2.0);

            // The engine warning in place of the shortcuts.
            islandModel.EngineWarning = IslandEngineWarning.Make("Sarvam", "needs your sarvam key", "add a key");
            islandModel.EngineBlockedPillLabel = "add sarvam key ›";
            islandManager.OpenPanel();
            await Wait(0.9);
            Shot("island-full-engine-warning", IslandRegion(130));
            islandManager.CloseAll();
            islandModel.EngineWarning = null;
            islandModel.EngineBlockedPillLabel = null;
            await Wait(0.6);

            // The composer.
            islandModel.OpenTextComposer();
            await Wait(0.9);
            Shot("island-composer", IslandRegion(100));
            islandModel.CloseTextComposer();
            await Wait(0.6);

            // The permission card.
            var permissionController = new PermissionPromptController();
            permissionController.Update(new PermissionStatus(Microphone: false));
            islandModel.PermissionPrompt = permissionController.CurrentPrompt;
            await Wait(0.9);
            Shot("island-permission", IslandRegion(150));
            permissionController.Grant();
            islandModel.PermissionPrompt = permissionController.CurrentPrompt;
            await Wait(0.6);
            Shot("island-permission-waiting", IslandRegion(150));
            islandModel.PermissionPrompt = null;
            await Wait(0.6);

            // The connect card.
            islandModel.ConnectPrompt = new AppConnectPrompt("gmail", "Gmail", "gmail", null,
                new[] { "Compose an email", "Find the invoice from last week", "Archive newsletters", "Reply to Sam" });
            await Wait(0.9);
            Shot("island-connect", IslandRegion(190));
            islandModel.ConnectPrompt = null;
            await Wait(0.6);

            // The tour's cards.
            var inputs = new FirstRunTourCardInputs(islandModel.DictationKey, CanAsk: true, UsesAgent: true, MicrophoneName: "Microphone Array (Realtek)");
            islandModel.TourCard = FirstRunTourCards.Make(FirstRunTourStep.Meet, inputs);
            await Wait(0.9);
            Shot("island-tour-meet", IslandRegion(170));
            islandModel.TourCard = FirstRunTourCards.Make(FirstRunTourStep.Dictate, inputs);
            await Wait(1.6);
            Shot("island-tour-dictate", IslandRegion(190));
            islandModel.TourCard = FirstRunTourCards.Make(FirstRunTourStep.Talk, inputs with { Status = "✓ that’s asking clicky. it can point at things too.", StatusIsGood = true });
            await Wait(1.0);
            Shot("island-tour-talk", IslandRegion(190));
            islandModel.TourCard = FirstRunTourCards.Make(FirstRunTourStep.Account, inputs with { AccountCardAsksForEmail = true });
            await Wait(0.9);
            Shot("island-tour-account", IslandRegion(200));
            islandModel.TourCard = null;
            await Wait(2.2);

            // The agent result card.
            agentCardModel.BeginLoading("thread-demo");
            agentCardModel.Title = "Organize my downloads folder";
            agentCardModel.Summary = "Moved 214 files into Documents, Images, Installers and Archives. Nothing was deleted; duplicates are in Downloads\\Duplicates for you to check.";
            agentCardModel.Status = "completed";
            agentCardModel.IsLoading = false;
            await Wait(0.8);
            Shot("agent-result-card", AroundDips(primary.BoundsInDips.Width - 460, 0, 460, 300));
            agentCardModel.Close();

            // The orb's phases.
            await ShowOrbPhasesAsync();

            // The goodbye on quit, typed into the island.
            islandModel.ShowGoodbye("bye for now. i'll be right here when you need me.");
            await Wait(2.2);
            Shot("island-compact-goodbye", IslandRegion(110));

            overlayModel.HideOverlay();
            islandManager.Hide();
        }

        private async Task ShowOrbPhasesAsync()
        {
            orbWindow.Start();
            await Wait(0.8);
            var orbRegion = AroundDips(primary.BoundsInDips.Width / 2 - 150, primary.WorkAreaInDips.Height - 70, 300, 80);
            var orbBoxRegion = AroundDips(primary.BoundsInDips.Width / 2 - 220, primary.WorkAreaInDips.Height - 260, 440, 270);
            Shot("orb-idle", orbRegion);
            orbModel.Phase = OrbPhase.Listening;
            orbModel.Hint = OrbPhaseTexts.TapReleaseToTranscribe;
            for (var frame = 0; frame < 30; frame++)
            {
                orbModel.AudioLevel = 0.3 + 0.3 * Math.Sin(frame / 2.0);
                await Wait(0.05);
            }
            Shot("orb-listening-empty", orbRegion);
            orbModel.LiveTranscript = "hello openclicky, this is my first take";
            await Wait(0.5);
            Shot("orb-listening", orbRegion);
            orbModel.Phase = OrbPhase.Working(OrbPhaseTexts.MovingYourWords);
            await Wait(0.7);
            Shot("orb-working", orbRegion);
            orbModel.Phase = OrbPhase.Done(OrbPhaseTexts.MovedToTextBox);
            await Wait(0.6);
            Shot("orb-done", orbRegion);
            orbModel.Phase = OrbPhase.Failed(OrbPhaseTexts.DidNotCatchThat);
            await Wait(0.6);
            Shot("orb-failed", orbRegion);
            orbModel.Phase = OrbPhase.Done("click into the app you want it in, then paste");
            orbModel.BoxText = "Hello OpenClicky, this is my first take. It came out of the microphone and into the box because nothing was focused.";
            orbModel.BoxReason = "click into the app you want it in, then paste";
            orbModel.QuickRewrites = new[] { "formal", "casual", "shorter" };
            orbModel.OnPasteFromBox = () => { };
            orbModel.IsBoxOpen = true;
            await Wait(0.8);
            Shot("orb-box", orbBoxRegion);
            orbModel.IsBoxOpen = false;
            orbModel.Phase = OrbPhase.Idle;
            orbModel.Hint = null;
            // The three looks and themes.
            var looks = new[] { ("pill", "black"), ("classic", "coral"), ("pixel", "mist") };
            foreach (var (look, theme) in looks)
            {
                preferences.Set(OrbSettings.Keys.OrbLook, look);
                preferences.Set(OrbSettings.Keys.OrbTheme, theme);
                orbSettings.Reload();
                await Wait(0.6);
                Shot($"orb-look-{look}-{theme}", orbRegion);
            }
            orbWindow.Stop();
        }

        public void Dispose() => ui.Dispose();
    }
}
