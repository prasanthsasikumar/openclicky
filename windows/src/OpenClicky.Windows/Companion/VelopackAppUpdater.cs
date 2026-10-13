// AppUpdater.swift's job (Sparkle on the Mac) through Velopack, against the GitHub Releases of the
// OpenClicky repository. A development build (not installed by Setup.exe) has no feed: IsConfigured
// is false and the row says so. Automatic checks run at launch and once a day: an update found then
// downloads in the background and is applied when OpenClicky quits. "Check now" downloads and
// restarts into the new version at once.

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Settings;
using Velopack;
using Velopack.Sources;

namespace OpenClicky.Windows.Companion;

public sealed class VelopackAppUpdater : ObservableObject, IAppUpdater, IDisposable
{
    public const string RepositoryUrl = "https://github.com/weijietan97/openclicky";
    public const string AutomaticallyChecksKey = "openclicky.updates.automaticallyChecks";
    public const string LastCheckKey = "openclicky.updates.lastCheck";

    private readonly Preferences preferences;
    private readonly UpdateManager? updateManager;
    private Timer? dailyTimer;
    private bool isChecking;

    public VelopackAppUpdater(Preferences preferences)
    {
        this.preferences = preferences;
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
        try
        {
            var update = await updateManager.CheckForUpdatesAsync().ConfigureAwait(false);
            preferences.Set(LastCheckKey, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            OnPropertyChanged(nameof(LastCheckDescription));
            if (update is null)
            {
                AppLog.Write("updates: up to date");
                return;
            }
            AppLog.Write($"updates: {update.TargetFullRelease.Version} available; downloading");
            await updateManager.DownloadUpdatesAsync(update).ConfigureAwait(false);
            if (applyNow)
            {
                updateManager.ApplyUpdatesAndRestart(update.TargetFullRelease);
            }
            else
            {
                // Installed when OpenClicky quits, without a window in the way.
                updateManager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
            }
        }
        catch (Exception error)
        {
            AppLog.Write($"updates: check failed ({error.GetType().Name}: {error.Message})");
        }
        finally
        {
            isChecking = false;
        }
    }

    public void Dispose() => dailyTimer?.Dispose();
}
