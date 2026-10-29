// Ports SystemStatsTests.swift: how the figures read, when a ring warns, which tiles show, and a store
// that samples only while a row watches.

using OpenClicky.Core.Plugins.SystemStats;
using OpenClicky.Core.Tests.Speech;

namespace OpenClicky.Core.Tests.Plugins;

public class SystemStatsTests
{
    [Fact]
    public void FiguresReadLikeAGlance()
    {
        Assert.Equal("23%", SystemStatsFormatter.Percent(0.234));
        Assert.Equal("100%", SystemStatsFormatter.Percent(1.2));
        Assert.Equal("16 GB", SystemStatsFormatter.Gigabytes(17_179_869_184));
        Assert.Equal("1024 GB", SystemStatsFormatter.Gigabytes(1_099_511_627_776));
        Assert.Equal("7 / 16 GB", SystemStatsFormatter.Memory(7_516_192_768, 17_179_869_184));
        Assert.Equal("210 GB free", SystemStatsFormatter.Disk(225_485_783_040));
        Assert.Equal("86% ⚡", SystemStatsFormatter.Battery(0.86, isCharging: true));
        Assert.Equal("17%", SystemStatsFormatter.Battery(0.17, isCharging: false));
    }

    [Fact]
    public void RingsTurnAmberAboveEightyFivePercentAndBatteriesAtTwenty()
    {
        Assert.False(SystemStatsTone.IsWarning(0.85));
        Assert.True(SystemStatsTone.IsWarning(0.851));
        Assert.True(SystemStatsTone.IsBatteryWarning(0.2));
        Assert.False(SystemStatsTone.IsBatteryWarning(0.21));
    }

    [Fact]
    public void TilesFollowTheSnapshotAndSkipTheBatteryWhenThereIsNone()
    {
        var desktop = new SystemStatsSnapshot(0.5, 8, 16, 25, 100, null);
        var tiles = SystemTile.For(desktop);
        Assert.Equal(["cpu", "memory", "disk"], tiles.Select(tile => tile.Id));
        Assert.Equal(0.5, tiles[1].Fraction);
        Assert.Equal(0.75, tiles[2].Fraction);
        var laptop = desktop with { Battery = new SystemStatsSnapshot.BatteryState(0.15, IsCharging: false, IsPluggedIn: false) };
        var battery = SystemTile.For(laptop).Last();
        Assert.Equal("battery", battery.Id);
        Assert.True(battery.IsWarning);
    }

    private sealed class CountingSampler : ISystemStatsSampler
    {
        public int Count;
        public SystemStatsSnapshot Sample() => new(++Count / 100.0, 0, 0, 0, 0, null);
    }

    [Fact]
    public async Task TheStoreSamplesWhileAnyRowWatchesAndStopsWithTheLast()
    {
        var sampler = new CountingSampler();
        var ticks = new List<TaskCompletionSource>();
        var store = new SystemStatsStore(sampler, delay: (_, cancellationToken) =>
        {
            var tick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tick.TrySetCanceled());
            lock (ticks) ticks.Add(tick);
            return tick.Task;
        });
        var changes = 0;
        store.Changed += () => Interlocked.Increment(ref changes);
        Assert.False(store.IsSampling);

        store.BeginWatching();
        store.BeginWatching();
        Assert.True(store.IsSampling);
        Assert.Equal(1, sampler.Count);
        Assert.Equal(0.01, store.Snapshot.Cpu);

        lock (ticks) ticks[^1].SetResult();
        Assert.True(await Eventually.TrueAsync(() => sampler.Count == 2));

        store.EndWatching();
        Assert.True(store.IsSampling);
        store.EndWatching();
        Assert.False(store.IsSampling);
        await Task.Delay(50);
        Assert.Equal(2, sampler.Count);
        Assert.Equal(2, changes);
    }
}
