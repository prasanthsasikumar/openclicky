using System.IO;
using System.Diagnostics;
using Microsoft.Win32;
using OpenClicky.Core.Platform;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// Microphone privacy on Windows: the ConsentStore registry values Settings → Privacy &amp; security →
/// Microphone writes, plus a short test capture (a denied desktop app gets silence or E_ACCESSDENIED).
/// </summary>
public sealed class MicrophonePermission(Func<IAudioCapture> makeCapture) : IMicrophonePermission
{
    private const string ConsentStoreKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    public MicrophoneConsent ReadConsent()
    {
        try
        {
            if (IsDenied(Registry.LocalMachine, ConsentStoreKey)) return MicrophoneConsent.DeniedForDevice;
            if (IsDenied(Registry.CurrentUser, ConsentStoreKey)) return MicrophoneConsent.DeniedForAllApps;
            if (IsDenied(Registry.CurrentUser, ConsentStoreKey + @"\NonPackaged")) return MicrophoneConsent.DeniedForDesktopApps;
            var userValue = ReadValue(Registry.CurrentUser, ConsentStoreKey);
            return userValue is null ? MicrophoneConsent.Unknown : MicrophoneConsent.Allowed;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return MicrophoneConsent.Unknown;
        }
    }

    public async Task<MicrophoneTestResult> TestCaptureAsync(string? deviceId = null, CancellationToken cancellationToken = default)
    {
        using var capture = makeCapture();
        var peak = 0f;
        var frames = 0;
        capture.FramesAvailable += frame =>
        {
            Interlocked.Increment(ref frames);
            foreach (var sample in frame.Samples)
            {
                var magnitude = Math.Abs(sample);
                if (magnitude > peak) peak = magnitude;
            }
        };
        try
        {
            await capture.StartAsync(new AudioCaptureOptions { DeviceId = deviceId, SampleRate = 16_000, Format = AudioSampleFormat.Float32 }, cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(0.4), cancellationToken).ConfigureAwait(false);
        }
        catch (AudioCaptureException exception)
        {
            return exception.Failure switch
            {
                AudioCaptureFailure.AccessDenied => MicrophoneTestResult.AccessDenied,
                AudioCaptureFailure.NoDevice => MicrophoneTestResult.NoDevice,
                _ => MicrophoneTestResult.Failed,
            };
        }
        finally
        {
            capture.Stop();
        }
        if (Volatile.Read(ref frames) == 0) return MicrophoneTestResult.Failed;
        // Real microphones are never digitally silent; Windows feeds exact zeros to a denied app.
        return peak > 0 ? MicrophoneTestResult.Works : MicrophoneTestResult.Silent;
    }

    public void OpenPrivacySettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static bool IsDenied(RegistryKey hive, string path) =>
        string.Equals(ReadValue(hive, path), "Deny", StringComparison.OrdinalIgnoreCase);

    private static string? ReadValue(RegistryKey hive, string path)
    {
        using var key = hive.OpenSubKey(path);
        return key?.GetValue("Value") as string;
    }
}
