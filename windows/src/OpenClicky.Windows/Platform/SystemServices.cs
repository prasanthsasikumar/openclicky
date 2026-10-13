using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenClicky.Core.Platform;
using Windows.Networking.Connectivity;

namespace OpenClicky.Windows.Platform;

/// <summary>"Open at login" through HKCU\Software\Microsoft\Windows\CurrentVersion\Run, value "OpenClicky".</summary>
public sealed class RunKeyLoginItem : ILoginItem
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenClicky";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string { Length: > 0 };
        }
    }

    public string LaunchCommand => $"\"{LaunchExecutablePath()}\"";

    public bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled) key.SetValue(ValueName, LaunchCommand, RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"login item: {exception.Message}");
        }
        return IsEnabled;
    }

    /// <summary>
    /// Velopack installs to %LOCALAPPDATA%\OpenClicky\current\OpenClicky.exe with a stub launcher at
    /// %LOCALAPPDATA%\OpenClicky\OpenClicky.exe that survives updates (current\ is replaced): when the
    /// running exe sits in "current" and the stub exists, the stub is what starts at login.
    /// </summary>
    internal static string LaunchExecutablePath()
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var directory = Path.GetDirectoryName(processPath);
        if (directory is not null && string.Equals(Path.GetFileName(directory), "current", StringComparison.OrdinalIgnoreCase))
        {
            var root = Path.GetDirectoryName(directory);
            var stub = root is null ? null : Path.Combine(root, Path.GetFileName(processPath));
            if (stub is not null && File.Exists(stub)) return stub;
        }
        return processPath;
    }
}

/// <summary>
/// Network reachability from the WinRT connection profile (internet access level + cost), refreshed
/// on NetworkInformation.NetworkStatusChanged. "Online" until the first answer.
/// </summary>
public sealed class NetworkReachability : INetworkReachability
{
    private readonly object stateLock = new();
    private bool isOnline = true;
    private bool isMetered;
    private bool isConstrained;

    public NetworkReachability()
    {
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        Task.Run(Refresh);
    }

    public event Action? Changed;

    public bool IsOnline { get { lock (stateLock) return isOnline; } }
    public bool IsMetered { get { lock (stateLock) return isMetered; } }
    public bool IsConstrained { get { lock (stateLock) return isConstrained; } }

    private void OnNetworkStatusChanged(object sender) => Refresh();

    private void Refresh()
    {
        bool online, metered = false, constrained = false;
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            online = profile is not null && profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            if (profile is not null)
            {
                var cost = profile.GetConnectionCost();
                metered = cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable;
                constrained = cost.Roaming || cost.ApproachingDataLimit || cost.OverDataLimit;
            }
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            online = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        }
        bool changed;
        lock (stateLock)
        {
            changed = online != isOnline || metered != isMetered || constrained != isConstrained;
            isOnline = online;
            isMetered = metered;
            isConstrained = constrained;
        }
        if (changed) Changed?.Invoke();
    }

    public void Dispose() => NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
}

/// <summary>The WPF Dispatcher as Core's main thread.</summary>
public sealed class DispatcherMainThread(Dispatcher dispatcher) : IMainThread
{
    public bool IsMainThread => dispatcher.CheckAccess();

    public void Post(Action action) => dispatcher.BeginInvoke(action);

    public Task InvokeAsync(Action action) =>
        IsMainThread ? RunInline(action) : dispatcher.InvokeAsync(action).Task;

    public Task<T> InvokeAsync<T>(Func<T> function) =>
        IsMainThread ? Task.FromResult(function()) : dispatcher.InvokeAsync(function).Task;

    public Task InvokeAsync(Func<Task> function) =>
        IsMainThread ? function() : dispatcher.InvokeAsync(function).Task.Unwrap();

    private static Task RunInline(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
