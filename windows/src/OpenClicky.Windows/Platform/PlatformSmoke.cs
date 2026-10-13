using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using OpenClicky.Core.Platform;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// <c>--openclicky-platform-smoke</c>: exercises the OS layer on the machine it runs on and prints
/// what it saw (monitors, microphones, the app in front, browser tabs, a capture + OCR, the keyboard
/// hook, voices, speech languages…). Read-only apart from a brief microphone open and one injected
/// F24 keystroke. Not wired into App yet; Program.cs calls <see cref="RunAsync"/> when it sees the flag.
/// </summary>
public static class PlatformSmoke
{
    public const string Flag = "--openclicky-platform-smoke";

    public static async Task<int> RunAsync(TextWriter output)
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        using var services = new PlatformServices(Dispatcher.CurrentDispatcher);
        var failures = 0;

        async Task Step(string name, Func<Task> body)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await body().ConfigureAwait(false);
                output.WriteLine($"  ({name}: {stopwatch.ElapsedMilliseconds} ms)");
            }
            catch (Exception exception)
            {
                failures++;
                output.WriteLine($"  !! {name} failed: {exception.GetType().Name}: {exception.Message}");
            }
        }

        output.WriteLine($"INPUT struct size: {INPUT.Size} bytes (expect 40 on 64-bit)");

        await Step("screens", () =>
        {
            foreach (var screen in services.Screens.All())
            {
                output.WriteLine($"screen {screen.DeviceName}: {screen.Bounds} work {screen.WorkArea} scale {screen.Scale:0.##} primary={screen.IsPrimary} cursor={screen.ContainsCursor}");
            }
            output.WriteLine($"cursor at {services.Screens.CursorPosition()}");
            return Task.CompletedTask;
        });

        await Step("microphones", () =>
        {
            foreach (var microphone in services.Microphones.Available())
            {
                output.WriteLine($"mic: {microphone.Name} default={microphone.IsDefault} communications={microphone.IsDefaultCommunications}");
            }
            output.WriteLine($"mic consent: {services.MicrophonePermission.ReadConsent()}");
            return Task.CompletedTask;
        });

        await Step("front app", async () =>
        {
            var front = await services.FrontmostApp.CurrentAsync().ConfigureAwait(false);
            output.WriteLine(front is null
                ? "front app: none"
                : $"front app: {front.ProcessName} \"{front.DisplayName}\" title \"{Shorten(front.WindowTitle)}\" browser={front.IsBrowser} url={front.Url ?? "-"}");
            if (front?.ExecutablePath is { } path)
            {
                var icon = services.FrontmostApp.AppIconPng(path, 32);
                output.WriteLine($"front app icon: {icon?.Length ?? 0} PNG bytes");
            }
        });

        await Step("windows", () =>
        {
            var windows = services.WindowList.VisibleWindows();
            output.WriteLine($"visible windows: {windows.Count}; top 5:");
            foreach (var window in windows.Take(5)) output.WriteLine($"  #{window.ZOrder} {window.ProcessName} \"{Shorten(window.Title)}\" {window.Bounds}");
            return Task.CompletedTask;
        });

        await Step("browser tabs", async () =>
        {
            var browsers = await services.BrowserTabs.EnumerateAsync().ConfigureAwait(false);
            output.WriteLine($"browser windows with tabs: {browsers.Count}");
            foreach (var browser in browsers.Take(3))
            {
                output.WriteLine($"  {browser.ProcessName} z{browser.ZOrder}: {browser.Tabs.Count} tabs, active url {browser.ActiveTabUrl ?? "-"}");
                foreach (var tab in browser.Tabs.Take(4)) output.WriteLine($"    [{tab.StripIndex}] \"{Shorten(tab.Title)}\" {tab.Bounds} selected={tab.IsSelected}");
            }
        });

        await Step("focused element", async () =>
        {
            var snapshot = await services.FocusedElements.SnapshotAsync().ConfigureAwait(false);
            output.WriteLine($"focused: {snapshot.AppProcessName} role {snapshot.Role ?? "-"} editable={snapshot.IsEditable} secure={snapshot.IsSecure} value {snapshot.Value?.Length ?? -1} chars");
            var controls = await services.FocusedElements.CaptionedControlsInForegroundWindowAsync().ConfigureAwait(false);
            output.WriteLine($"captioned controls in the front window: {controls.Count}");
            var terms = await services.FocusedElements.NearbyTermsAsync().ConfigureAwait(false);
            output.WriteLine($"nearby terms: {terms.Count}");
        });

        await Step("capture + OCR", async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            var captures = await services.ScreenCapture.CaptureAllScreensAsync().ConfigureAwait(false);
            output.WriteLine($"captured {captures.Count} screen(s) in {stopwatch.ElapsedMilliseconds} ms");
            foreach (var capture in captures)
            {
                output.WriteLine($"  \"{capture.Label}\" {capture.ScreenshotWidthInPixels}×{capture.ScreenshotHeightInPixels} {capture.ImageData.Length / 1024} KB frame {capture.DisplayFrame}");
            }
            var first = captures[0];
            stopwatch.Restart();
            var lines = await services.TextRecognizer.RecognizeAsync(first.ImageData).ConfigureAwait(false);
            output.WriteLine($"OCR: {lines.Count} lines in {stopwatch.ElapsedMilliseconds} ms; first few:");
            foreach (var line in lines.Take(5)) output.WriteLine($"  \"{Shorten(line.Text)}\" at ({line.Box.X:0},{line.Box.Y:0}) {line.Box.Width:0}×{line.Box.Height:0}");
            // Warm timings (the first call pays for GDI+, codec and OCR engine start-up).
            stopwatch.Restart();
            var again = await services.ScreenCapture.CaptureCursorScreenAsync().ConfigureAwait(false);
            var captureMilliseconds = stopwatch.ElapsedMilliseconds;
            stopwatch.Restart();
            await services.TextRecognizer.RecognizeAsync(again!.ImageData).ConfigureAwait(false);
            output.WriteLine($"warm: capture {captureMilliseconds} ms, OCR {stopwatch.ElapsedMilliseconds} ms");
        });

        await Step("keyboard hook", async () =>
        {
            var seen = new List<RawKeyEvent>();
            services.KeyboardMonitor.KeyEvent += keyEvent => { lock (seen) seen.Add(keyEvent); };
            services.KeyboardMonitor.Start();
            // A marked key (must be dropped) and an unmarked F24 (must be seen, flagged injected).
            services.KeyboardMonitor.SuppressMenuActivation();
            var f24Down = KeyboardInput(0x87, 0, 0);
            var f24Up = KeyboardInput(0x87, 0, KEYEVENTF_KEYUP);
            f24Down.U.ki.dwExtraInfo = 0;
            f24Up.U.ki.dwExtraInfo = 0;
            Send(f24Down, f24Up);
            await Task.Delay(300).ConfigureAwait(false);
            services.KeyboardMonitor.Stop();
            lock (seen)
            {
                output.WriteLine($"hook saw {seen.Count} events: {string.Join(", ", seen.Select(keyEvent => $"vk 0x{keyEvent.VirtualKey:X2} {(keyEvent.IsDown ? "down" : "up")} injected={keyEvent.IsInjected}"))}");
                if (seen.Any(keyEvent => keyEvent.VirtualKey == VirtualKeys.Unassigned)) throw new InvalidOperationException("a marked keystroke reached the hook's listeners");
                if (!seen.Any(keyEvent => keyEvent.VirtualKey == 0x87)) throw new InvalidOperationException("the hook did not see F24");
            }
        });

        await Step("clipboard", () =>
        {
            output.WriteLine($"clipboard sequence {services.Clipboard.SequenceNumber}, text {services.Clipboard.GetText()?.Length ?? -1} chars, transient={services.Clipboard.ContainsTransientContent()}");
            return Task.CompletedTask;
        });

        await Step("microphone capture", async () =>
        {
            if (services.Microphones.Default() is null)
            {
                output.WriteLine("no microphone on this machine: capture not exercised");
                return;
            }
            using var capture = services.CreateAudioCapture();
            var frames = 0;
            var samples = 0;
            capture.FramesAvailable += frame => { frames++; samples += frame.Samples.Length; };
            var stopwatch = Stopwatch.StartNew();
            var started = await capture.StartAsync(new AudioCaptureOptions { SampleRate = 16_000 }).ConfigureAwait(false);
            output.WriteLine($"capture started in {stopwatch.ElapsedMilliseconds} ms: {started.Description}");
            await Task.Delay(500).ConfigureAwait(false);
            capture.Stop();
            output.WriteLine($"0.5 s → {frames} frames, {samples} samples at 16 kHz, level {capture.CurrentLevel:0.000}");

            using var echoCancelled = services.CreateAudioCapture();
            var realtime = await echoCancelled.StartAsync(new AudioCaptureOptions { SampleRate = 24_000, PreferEchoCancellation = true }).ConfigureAwait(false);
            output.WriteLine($"realtime capture: {realtime.Description}");
            echoCancelled.Stop();
        });

        await Step("voices + speech", async () =>
        {
            var voices = services.SystemVoice.Voices("en");
            output.WriteLine($"system voices for en: {voices.Count} ({string.Join(", ", voices.Take(3).Select(voice => voice.Name))})");
            var wav = await services.SystemVoice.RenderWavAsync("Hello from this PC.", "en-US").ConfigureAwait(false);
            output.WriteLine($"rendered WAV: {wav.Length} bytes");
            output.WriteLine($"speech recognition languages: {string.Join(", ", services.OfflineSpeech.SupportedLanguages())}");
            output.WriteLine($"OCR available: {services.TextRecognizer.IsAvailable()}");
            try
            {
                await using var session = await services.OfflineSpeech.StartAsync(new OfflineSpeechOptions { LanguageTag = "en-US" }).ConfigureAwait(false);
                await Task.Delay(300).ConfigureAwait(false);
                output.WriteLine($"speech session started ({session.LanguageTag}), stopped with \"{await session.StopAsync().ConfigureAwait(false)}\"");
            }
            catch (OfflineSpeechException exception)
            {
                output.WriteLine($"speech session refused ({exception.Failure}): {exception.UserFacingSentence}");
            }
            using var listener = new ClipboardChangeListener();
            listener.Start();
            listener.Stop();
            output.WriteLine("clipboard listener started and stopped");
        });

        await Step("system", () =>
        {
            output.WriteLine($"network online={services.Network.IsOnline} metered={services.Network.IsMetered} constrained={services.Network.IsConstrained}");
            output.WriteLine($"login item enabled={services.LoginItem.IsEnabled} command {services.LoginItem.LaunchCommand}");
            output.WriteLine($"volume {services.DesktopActions.GetVolume()?.ToString() ?? "-"}%; downloads {services.DesktopActions.KnownFolderPath("downloads")}");
            foreach (var appName in new[] { "notepad", "calculator", "settings" })
            {
                var app = services.DesktopActions.FindApplication(appName);
                output.WriteLine($"find app \"{appName}\": {(app is null ? "-" : $"{app.DisplayName} ({app.Kind}) {app.Target}")}");
            }
            foreach (var command in new[] { "openclicky", "claude", "codex", "node" })
            {
                output.WriteLine($"resolve {command}: {services.Processes.ResolveExecutable(command) ?? "-"}");
            }
            return Task.CompletedTask;
        });

        await Step("process runner", async () =>
        {
            var result = await services.Processes.RunAsync(new ProcessLaunchRequest { Command = "cmd", Arguments = ["/c", "echo", "hello"] }, timeout: TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            output.WriteLine($"cmd /c echo hello → exit {result.ExitCode}, \"{result.StandardOutput.Trim()}\"");
        });

        output.WriteLine(failures == 0 ? "platform smoke: all steps ran" : $"platform smoke: {failures} step(s) failed");
        return failures == 0 ? 0 : 1;
    }

    private static string Shorten(string? text) =>
        text is null ? "-" : text.Length > 60 ? text[..60] + "…" : text;
}
