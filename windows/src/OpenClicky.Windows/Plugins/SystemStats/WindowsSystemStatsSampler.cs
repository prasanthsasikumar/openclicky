//
//  WindowsSystemStatsSampler.cs
//  OpenClicky (Windows port of Plugins/System/SystemStatsSampler.swift)
//
//  The live sampler behind the System plugin: CPU time from GetSystemTimes, memory from
//  GlobalMemoryStatusEx, the Windows drive's capacity, the battery from GetSystemPowerStatus.
//

using System.IO;
using System.Runtime.InteropServices;
using OpenClicky.Core.Plugins.SystemStats;

namespace OpenClicky.Windows.Plugins.SystemStats;

internal sealed class WindowsSystemStatsSampler : ISystemStatsSampler
{
    /// <summary>The last idle and total CPU times, so the next sample can take a delta.</summary>
    private (ulong Idle, ulong Total)? previousTimes;

    /// <summary>Busy time over all time between two samples; 0 on the first sample or without a delta.</summary>
    public static double CpuFraction((ulong Idle, ulong Total)? previous, (ulong Idle, ulong Total) current)
    {
        if (previous is not { } before || current.Total <= before.Total) return 0;
        var total = (double)(current.Total - before.Total);
        var idle = (double)(current.Idle - Math.Min(current.Idle, before.Idle));
        return Math.Clamp((total - idle) / total, 0, 1);
    }

    public SystemStatsSnapshot Sample()
    {
        var times = CpuTimes();
        var cpu = times is { } current ? CpuFraction(previousTimes, current) : 0;
        if (times is not null) previousTimes = times;
        var (memoryUsed, memoryTotal) = Memory();
        var (diskFree, diskTotal) = Disk();
        return new SystemStatsSnapshot(cpu, memoryUsed, memoryTotal, diskFree, diskTotal, Battery());
    }

    // MARK: CPU

    /// <summary>Idle and kernel + user time over every CPU (kernel time includes the idle time).</summary>
    private static (ulong Idle, ulong Total)? CpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        return (idle.Value, kernel.Value + user.Value);
    }

    // MARK: Memory

    private static (long Used, long Total) Memory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
        var total = (long)status.ullTotalPhys;
        var used = total - (long)Math.Min(status.ullAvailPhys, status.ullTotalPhys);
        return (used, total);
    }

    // MARK: Disk

    /// <summary>The drive Windows is on.</summary>
    private static (long Free, long Total) Disk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(root)) return (0, 0);
            var drive = new DriveInfo(root);
            return (Math.Min(drive.AvailableFreeSpace, drive.TotalSize), drive.TotalSize);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (0, 0);
        }
    }

    // MARK: Battery

    private const byte BatteryFlagCharging = 8;
    private const byte BatteryFlagNoBattery = 128;
    private const byte BatteryFlagUnknown = 255;
    private const byte BatteryPercentUnknown = 255;

    /// <summary>Null on a PC without a battery, or when Windows cannot tell.</summary>
    private static SystemStatsSnapshot.BatteryState? Battery()
    {
        if (!GetSystemPowerStatus(out var status)) return null;
        if (status.BatteryFlag is BatteryFlagUnknown || (status.BatteryFlag & BatteryFlagNoBattery) != 0) return null;
        if (status.BatteryLifePercent == BatteryPercentUnknown) return null;
        var isPluggedIn = status.ACLineStatus == 1;
        var isCharging = (status.BatteryFlag & BatteryFlagCharging) != 0;
        return new SystemStatsSnapshot.BatteryState(Math.Clamp(status.BatteryLifePercent / 100.0, 0, 1), isCharging, isPluggedIn);
    }

    // MARK: Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
