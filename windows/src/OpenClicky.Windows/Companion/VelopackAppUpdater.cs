// AppUpdater.swift's job (Sparkle on the Mac) through Velopack, against the GitHub Releases of the
// OpenClicky repository. A development build (not installed by Setup.exe) has no feed: IsConfigured
// is false and the row says so. Automatic checks run at launch and once a day: an update found then
// downloads in the background, unless the person skipped that version, and a card says it is ready
// (UpdateReady → UpdateWindows: Restart now / Later). Restart now shows the update window and restarts
// into the new version; otherwise Velopack applies it the next time OpenClicky starts (its auto-apply on
// startup; the old WaitExitThenApplyUpdates gave up after 60 s, long before a tray app is quit). "Check
// now" asks first, as Sparkle does (Install Update / Skip This Version / Remind Me Later); Install
// downloads it in the update window and restarts. It also says when OpenClicky is up to date or the
// check failed. The flow's words and rules are Core's (Updates/UpdateFlow).

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Settings;
using OpenClicky.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace OpenClicky.Windows.Companion;

public sealed class VelopackAppUpdater : ObservableObject, IAppUpdater, IDisposable
{
    public const string RepositoryUrl = "https://github.com/prasanthsasikumar/openclicky";
    public const string AutomaticallyChecksKey = "openclicky.updates.automaticallyChecks";
    public const string LastCheckKey = "openclicky.updates.lastCheck";
    /// <summary>The version "Skip This Version" was pressed for (Sparkle's SUSkippedVersion).</summary>
    public const string SkippedVersionKey = "openclicky.updates.skippedVersion";

    private readonly Preferences preferences;
    private readonly UpdatePromptSheets? prompts;
    private readonly UpdateWindows? windows;
    private readonly UpdateManager? updateManager;
    /// <summary>The update downloaded in the background, waiting for Restart now (or the next start).</summary>
    private VelopackAsset? downloadedUpdate;
    private Timer? dailyTimer;
    private bool isChecking;

    /// <param name="prompts">The sheets "Check Now" asks with; null → it installs without asking (tests, no UI).</param>
    /// <param name="windows">The ready card and the update window; null → nothing is shown.</param>
    public VelopackAppUpdater(Preferences preferences, UpdatePromptSheets? prompts = null, UpdateWindows? windows = null)
    {
        this.preferences = preferences;
        this.prompts = prompts;
        this.windows = windows;
        try
        {
            var manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false));
            updateManager = manager.IsInstalled ? manager : null;
        }
        catch (Exception error)
        {
            AppLog.Write($"updates: no update manager ({error.GetType().Name})");
        }
    }

    public bool IsConfigured => updateManager is not null;

    /// <summary>The installed version ("0.12.2"); null for a development build.</summary>
    public string? CurrentVersion => updateManager?.CurrentVersion?.ToString();

    /// <summary>An update finished downloading in the background (its version): the card can say it is ready.</summary>
    public event Action<string>? UpdateReady;

    /// <summary>Restart now: the update window shows Install, and OpenClicky restarts into the downloaded version, which
    /// opens with the window on Open (UpdateFlow.RestartedArgument).</summary>
    public void RestartToUpdate()
    {
        if (updateManager is null || downloadedUpdate is not { } update) return;
        var version = update.Version.ToString();
        AppLog.Write($"updates: restarting into {version}");
        _ = InstallAndRestartAsync(update, version);
    }

    private async Task InstallAndRestartAsync(VelopackAsset update, string version)
    {
        windows?.ShowProgress(UpdateProgress.Installing(version));
        // A moment for the window to be seen before OpenClicky exits.
        await Task.Delay(TimeSpan.FromSeconds(1.2)).ConfigureAwait(false);
        try
        {
            updateManager!.ApplyUpdatesAndRestart(update, [UpdateFlow.RestartedArgument]);
        }
        catch (Exception error)
        {
            AppLog.Write($"updates: couldn't install ({error.GetType().Name}: {error.Message})");
            windows?.CloseProgress();
            if (prompts is not null) await prompts.TellAsync("Update failed", "OpenClicky couldn't install the update. It will try again the next time it starts.").ConfigureAwait(false);
        }
    }

    public bool AutomaticallyChecks
    {
        get => preferences.GetOptionalBool(AutomaticallyChecksKey) ?? true;
        set
        {
            preferences.Set(AutomaticallyChecksKey, value);
            OnPropertyChanged();
        }
    }

    public string LastCheckDescription
    {
        get
        {
            if (!IsConfigured) return "updates come with the installed app";
            if (preferences.GetString(LastCheckKey) is not { } raw || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastCheck)) return "never checked";
            var local = lastCheck.ToLocalTime();
            var day = local.Date == DateTime.Today ? "today" : local.Date == DateTime.Today.AddDays(-1) ? "yesterday" : local.ToString("d MMM", CultureInfo.CurrentCulture);
            return $"last checked {day} at {local:H:mm}";
        }
    }

    /// <summary>Starts the launch check and the daily one (when automatic checks are on).</summary>
    public void Start()
    {
        if (!IsConfigured) return;
        dailyTimer = new Timer(_ =>
        {
            if (AutomaticallyChecks) _ = CheckAsync(applyNow: false);
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromDays(1));
    }

    public void CheckNow() => _ = CheckAsync(applyNow: true);

    private async Task CheckAsync(bool applyNow)
    {
        if (updateManager is null || isChecking) return;
        isChecking = true;
        var showsProgress = false;
        try
        {
            var update = await updateManager.CheckForUpdatesAsync().ConfigureAwait(false);
            preferences.Set(LastCheckKey, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            OnPropertyChanged(nameof(LastCheckDescription));
            if (update is null)
            {
                AppLog.Write("updates: up to date");
                if (applyNow && prompts is not null)
                {
                    await prompts.TellAsync("You're up to date!", $"OpenClicky {updateManager.CurrentVersion} is the newest version available.").ConfigureAwait(false);
                }
                return;
            }
            var version = update.TargetFullRelease.Version.ToString();
            if (applyNow)
            {
                // Asked even about a skipped version: they pressed Check Now (Sparkle's rule).
                if (prompts is not null)
                {
                    var choice = await prompts.AskToInstallAsync(new UpdateOffer(version, updateManager.CurrentVersion?.ToString() ?? "an older version",
                                                                                 update.TargetFullRelease.NotesMarkdown ?? "")).ConfigureAwait(false);
                    AppLog.Write($"updates: {version} available; {choice.ToString().ToLowerInvariant()}");
                    if (choice == UpdateChoice.Skip) preferences.Set(SkippedVersionKey, version);
                    if (choice != UpdateChoice.Install) return;
                }
                showsProgress = true;
                windows?.ShowProgress(UpdateProgress.Downloading(version, 0));
                var lastPercent = -1;
                await updateManager.DownloadUpdatesAsync(update, percent =>
                {
                    if (percent == lastPercent) return;
                    lastPercent = percent;
                    windows?.ShowProgress(UpdateProgress.Downloading(version, percent / 100.0));
                }).ConfigureAwait(false);
                downloadedUpdate = update.TargetFullRelease;
                await InstallAndRestartAsync(update.TargetFullRelease, version).ConfigureAwait(false);
            }
            else
            {
                if (preferences.GetString(SkippedVersionKey) == version)
                {
                    AppLog.Write($"updates: {version} available but skipped");
                    return;
                }
                AppLog.Write($"updates: {version} available; downloading");
                await updateManager.DownloadUpdatesAsync(update).ConfigureAwait(false);
                // Ready: the card offers Restart now; otherwise it is applied the next time OpenClicky starts.
                downloadedUpdate = update.TargetFullRelease;
                AppLog.Write($"updates: {version} downloaded; ready to restart into");
                UpdateReady?.Invoke(version);
            }
        }
        catch (Exception error)
        {
            AppLog.Write($"updates: check failed ({error.GetType().Name}: {error.Message})");
            if (showsProgress) windows?.CloseProgress();
            if (applyNow && prompts is not null)
            {
                await prompts.TellAsync("Update failed", "OpenClicky couldn't check for or download the update. Check your connection and try again.").ConfigureAwait(false);
            }
        }
        finally
        {
            isChecking = false;
        }
    }

    public void Dispose() => dailyTimer?.Dispose();
}
