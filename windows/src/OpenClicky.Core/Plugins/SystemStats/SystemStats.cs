//
//  SystemStats.cs
//  OpenClicky (Windows port of Plugins/System/SystemStats.swift and SystemIslandView.swift's tiles)
//
//  The System plugin's figures: one snapshot of CPU, memory, disk and battery, how each reads on a
//  tile, when a ring warns, and a store that samples only while a tile row is on screen. The live
//  sampler (Windows' own counters) is OpenClicky.Windows/Plugins/SystemStats.
//

using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Plugins.SystemStats;

/// <summary>The plugin's component: a glance at the PC.</summary>
public static class SystemStatsPlugin
{
    /// <summary>A ring, its name and the long figure under it.</summary>
    public static IslandComponent Component { get; } = new(
        IslandPluginRegistry.SystemId, "System", "", 84, IsPlugin: true,
        SettingsDetail: "CPU, memory, disk and battery at a glance, sampled only while the island shows them.");
}

/// <summary>One reading of the PC. Bytes are bytes; fractions run 0…1.</summary>
public sealed record SystemStatsSnapshot(double Cpu, long MemoryUsed, long MemoryTotal, long DiskFree, long DiskTotal, SystemStatsSnapshot.BatteryState? Battery)
{
    public sealed record BatteryState(double Level, bool IsCharging, bool IsPluggedIn);

    public static SystemStatsSnapshot Empty { get; } = new(0, 0, 0, 0, 0, null);
}

/// <summary>Where a snapshot comes from; the live one asks Windows, tests hand back fixed values.</summary>
public interface ISystemStatsSampler
{
    SystemStatsSnapshot Sample();
}

/// <summary>How the figures read on a tile: short inside the ring, long in the line under it.</summary>
public static class SystemStatsFormatter
{
    private const double BytesPerGigabyte = 1_073_741_824;

    public static string Percent(double fraction) => $"{(int)Math.Round(Math.Clamp(fraction, 0, 1) * 100, MidpointRounding.AwayFromZero)}%";

    /// <summary>Whole gigabytes (1 GB = 2^30 bytes), rounded.</summary>
    public static string Gigabytes(long bytes) => $"{WholeGigabytes(bytes)} GB";

    public static string Memory(long used, long total) => $"{WholeGigabytes(used)} / {Gigabytes(total)}";

    public static string Disk(long free) => $"{Gigabytes(free)} free";

    public static string Battery(double level, bool isCharging) => isCharging ? $"{Percent(level)} ⚡" : Percent(level);

    private static long WholeGigabytes(long bytes) => (long)Math.Round(bytes / BytesPerGigabyte, MidpointRounding.AwayFromZero);
}

/// <summary>When a ring turns amber: a load above 85 %, a battery at or under 20 %.</summary>
public static class SystemStatsTone
{
    public static bool IsWarning(double fraction) => fraction > 0.85;
    public static bool IsBatteryWarning(double level) => level <= 0.2;
}

/// <summary>One ring's worth: the fraction it fills, the figure inside it, the long figure under it, and whether it warns.</summary>
public sealed record SystemTile(string Id, string Caption, double Fraction, string Short, string Long, bool IsWarning)
{
    /// <summary>CPU, memory and disk always; the battery when the PC has one.</summary>
    public static IReadOnlyList<SystemTile> For(SystemStatsSnapshot snapshot)
    {
        var memoryFraction = snapshot.MemoryTotal > 0 ? (double)snapshot.MemoryUsed / snapshot.MemoryTotal : 0;
        var diskFraction = snapshot.DiskTotal > 0 ? 1 - (double)snapshot.DiskFree / snapshot.DiskTotal : 0;
        var tiles = new List<SystemTile>
        {
            new("cpu", "cpu", snapshot.Cpu, SystemStatsFormatter.Percent(snapshot.Cpu), $"{SystemStatsFormatter.Percent(snapshot.Cpu)} busy", SystemStatsTone.IsWarning(snapshot.Cpu)),
            new("memory", "memory", memoryFraction, SystemStatsFormatter.Percent(memoryFraction), SystemStatsFormatter.Memory(snapshot.MemoryUsed, snapshot.MemoryTotal), SystemStatsTone.IsWarning(memoryFraction)),
            new("disk", "disk", diskFraction, SystemStatsFormatter.Percent(diskFraction), SystemStatsFormatter.Disk(snapshot.DiskFree), SystemStatsTone.IsWarning(diskFraction)),
        };
        if (snapshot.Battery is { } battery)
        {
            tiles.Add(new("battery", "battery", battery.Level, SystemStatsFormatter.Percent(battery.Level),
                SystemStatsFormatter.Battery(battery.Level, battery.IsCharging), SystemStatsTone.IsBatteryWarning(battery.Level)));
        }
        return tiles;
    }
}

/// <summary>
/// One per app: samples every <see cref="Interval"/> while at least one tile row watches, and not at
/// all otherwise, so a closed island costs nothing. Start and stop it on the UI thread: the samples
/// come back on the thread that started it.
/// </summary>
public sealed class SystemStatsStore(ISystemStatsSampler sampler, TimeSpan? interval = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;
    private int watchers;
    private CancellationTokenSource? sampling;

    public TimeSpan Interval { get; } = interval ?? TimeSpan.FromSeconds(2);

    public SystemStatsSnapshot Snapshot { get; private set; } = SystemStatsSnapshot.Empty;

    /// <summary>A new snapshot differs from the last one.</summary>
    public event Action? Changed;

    public bool IsSampling => sampling is not null;

    /// <summary>A tile row appeared: the first one samples now and starts the clock.</summary>
    public void BeginWatching()
    {
        watchers++;
        if (sampling is not null) return;
        var cancellation = new CancellationTokenSource();
        sampling = cancellation;
        SampleNow();
        _ = SampleLoopAsync(cancellation.Token);
    }

    /// <summary>A tile row went away: the last one stops the clock.</summary>
    public void EndWatching()
    {
        watchers = Math.Max(0, watchers - 1);
        if (watchers > 0 || sampling is null) return;
        sampling.Cancel();
        sampling = null;
    }

    private async Task SampleLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await delay(Interval, cancellationToken); }
            catch (OperationCanceledException) { return; }
            if (cancellationToken.IsCancellationRequested) return;
            SampleNow();
        }
    }

    private void SampleNow()
    {
        SystemStatsSnapshot next;
        try { next = sampler.Sample(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"system stats: sample failed ({error.GetType().Name})");
            return;
        }
        if (next == Snapshot) return;
        Snapshot = next;
        Changed?.Invoke();
    }
}
