// The entry point (OpenClickyApp.swift's OpenClickyMain + CompanionAppDelegate's launch flags).
// In order: Velopack's install/update hooks; the Kokoro and Parakeet helper processes (the same exe
// with a flag, before any UI); the headless smoke and demo flags; one OpenClicky per session (a
// second launch opens the window of the first); then the WPF app with no main window, built by
// AppComposition. Unhandled exceptions are written to app.log and the app keeps running.

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using OpenClicky.Core.Configuration;
using OpenClicky.Windows.AppWindow;
using OpenClicky.Windows.Overlay;
using OpenClicky.Windows.Platform;
using OpenClicky.Windows.Speech;
using Velopack;

namespace OpenClicky.Windows;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack first: an install, update or uninstall hook runs here and exits.
        VelopackApp.Build().Run();

        if (SherpaHelperMain.IsHelperInvocation(args)) return SherpaHelperMain.Run(args);

        if (SmokeCommands.TryRun(args) is { } smokeExitCode) return smokeExitCode;

        if (args.Contains("--openclicky-ui-demo"))
        {
            var folderIndex = Array.IndexOf(args, "--shots");
            UiDemo.Run(folderIndex >= 0 && folderIndex + 1 < args.Length ? args[folderIndex + 1] : null);
            return 0;
        }
        if (args.Contains("--openclicky-window-demo"))
        {
            MainWindowDemo.Run(args);
            return 0;
        }

        AppLog.Write($"launch: OpenClicky {typeof(Program).Assembly.GetName().Version} ({RuntimeInformation.ProcessArchitecture}, {Environment.OSVersion.VersionString})");
        using var singleInstance = new SingleInstance();
        if (!singleInstance.TryBecomePrimary(args))
        {
            // Another OpenClicky runs: it got our arguments and opens its window.
            return 0;
        }

        // Debug: let screenshots (and so the person's own screen captures) see the floating windows,
        // which are otherwise left out of every capture (WDA_EXCLUDEFROMCAPTURE).
        if (args.Contains("--openclicky-capturable-windows") || Environment.GetEnvironmentVariable("OPENCLICKY_CAPTURABLE_WINDOWS") == "1")
        {
            UiDemo.AllowScreenCapture();
        }

        var application = new App();
        application.InitializeComponent();
        InstallExceptionHandlers(application);
        AppComposition? composition = null;
        application.Startup += (_, _) =>
        {
            try
            {
                composition = new AppComposition(application, singleInstance, args);
                composition.Start();
            }
            catch (Exception error)
            {
                AppLog.Write($"launch: failed: {error}");
                application.Shutdown(1);
            }
        };
        application.Exit += (_, _) => composition?.Dispose();
        return application.Run();
    }

    private static void InstallExceptionHandlers(Application application)
    {
        application.DispatcherUnhandledException += (_, eventArgs) =>
        {
            AppLog.Write($"error: unhandled on the UI thread: {eventArgs.Exception}");
            eventArgs.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            AppLog.Write($"error: unobserved task exception: {eventArgs.Exception}");
            eventArgs.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            AppLog.Write($"error: unhandled (terminating: {eventArgs.IsTerminating}): {eventArgs.ExceptionObject}");
    }
}

/// <summary>Writes to the console a smoke flag was started from (a WinExe has none of its own).</summary>
internal static class ConsoleAttachment
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    public static void Attach()
    {
        if (!AttachConsole(AttachParentProcess)) AllocConsole();
        var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(output);
        Console.SetError(output);
        Console.WriteLine();
    }
}
