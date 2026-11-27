// Updating OpenClicky where it can be seen. The updater (Velopack, against the project's GitHub Releases) downloads a new
// version in the background; then:
//
//   1. a card says it is ready (UpdateReadyCard): Restart now, or Later (it is not asked about again for a day, and it
//      goes in by itself the next time OpenClicky starts);
//   2. Restart now opens the update window, which walks Download, Install and Open (UpdateStep) with a bar, and OpenClicky
//      restarts into the new version with RestartedArgument, so the new version carries the window through Open until
//      it is ready;
//   3. the first start of a new version, however it was installed, says so once (UpdatedNotice).
//
// The words and the decisions are here; the windows are the Windows side's (Companion/UpdateWindows).

namespace OpenClicky.Core.Updates;

/// <summary>The update window's steps, in order.</summary>
public enum UpdateStep
{
    Download,
    Install,
    Open,
    /// <summary>The new version is running.</summary>
    Done,
}

/// <summary>What the update window shows: the step, how far through it (0 to 1, null when it can't tell), and its words.</summary>
public sealed record UpdateProgress(UpdateStep Step, double? Fraction, string Title, string Detail)
{
    public static UpdateProgress Downloading(string version, double fraction) =>
        new(UpdateStep.Download, Math.Clamp(fraction, 0, 1), $"Updating OpenClicky to {version}", $"Downloading… {(int)Math.Round(Math.Clamp(fraction, 0, 1) * 100)}%");

    public static UpdateProgress Installing(string version) =>
        new(UpdateStep.Install, null, $"Updating OpenClicky to {version}", "Installing. OpenClicky closes and opens again by itself.");

    public static UpdateProgress Opening(string version) =>
        new(UpdateStep.Open, null, "Opening OpenClicky", $"Starting {version}…");

    public static UpdateProgress Finished(string version) =>
        new(UpdateStep.Done, 1, $"OpenClicky {version} is ready", "All done.");

    /// <summary>Whether <paramref name="step"/> is behind this progress (its dot shows a tick).</summary>
    public bool HasDone(UpdateStep step) => step < Step || Step == UpdateStep.Done;
}

public static class UpdateFlow
{
    /// <summary>Passed to the new version when Restart now restarts into it: it opens with the update window on Open.</summary>
    public const string RestartedArgument = "--openclicky-after-update";

    /// <summary>The version Later was pressed for, and when (UpdateReadyCard).</summary>
    public const string LaterVersionKey = "openclicky.updates.laterVersion";
    public const string LaterAtKey = "openclicky.updates.laterAt";

    /// <summary>The version that ran last (UpdatedNotice).</summary>
    public const string LastRunVersionKey = "openclicky.updates.lastRunVersion";

    /// <summary>How long Later keeps the card away for the same version.</summary>
    public static readonly TimeSpan LaterLasts = TimeSpan.FromDays(1);

    /// <summary>The steps the window lists, with their names.</summary>
    public static IReadOnlyList<(UpdateStep Step, string Name)> Steps { get; } =
        [(UpdateStep.Download, "Download"), (UpdateStep.Install, "Install"), (UpdateStep.Open, "Open")];

    /// <summary>The project's page for a version's release notes.</summary>
    public static string ReleaseNotesUrl(string repositoryUrl, string version) => $"{repositoryUrl.TrimEnd('/')}/releases/tag/v{version}";
}

/// <summary>The card that says a downloaded update is ready.</summary>
public static class UpdateReadyCard
{
    public static string Title(string version) => $"OpenClicky {version} is ready";

    public const string Detail = "Restart to update. It takes a few seconds, and Clicky opens again by itself.";

    public const string RestartTitle = "Restart now";
    public const string LaterTitle = "Later";

    /// <summary>Shown unless Later was pressed for this same version less than a day ago.</summary>
    public static bool ShouldShow(string version, string? laterVersion, DateTimeOffset? laterAt, DateTimeOffset now) =>
        !(laterVersion == version && laterAt is { } at && now - at < UpdateFlow.LaterLasts && now >= at);
}

/// <summary>Said once on the first start of a new version.</summary>
public static class UpdatedNotice
{
    /// <summary>"OpenClicky was updated to 0.12.3" when <paramref name="current"/> differs from the version that ran last; null
    /// on the very first run (nothing ran before) and when nothing changed.</summary>
    public static string? Message(string? lastRunVersion, string? current)
    {
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(lastRunVersion)) return null;
        return string.Equals(lastRunVersion.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase) ? null : $"OpenClicky was updated to {current.Trim()}";
    }
}
