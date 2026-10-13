using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using OpenClicky.Core.Platform;

namespace OpenClicky.Windows.Platform;

/// <summary>Capture endpoints through the MMDevice API (CoreAudio's device list on the Mac).</summary>
public sealed class MicrophoneDevices : IMicrophoneDevices
{
    private readonly object notificationLock = new();
    private MMDeviceEnumerator? notificationEnumerator;
    private NotificationClient? notificationClient;
    private Action? devicesChanged;

    public event Action? DevicesChanged
    {
        add
        {
            lock (notificationLock)
            {
                devicesChanged += value;
                if (notificationEnumerator is null)
                {
                    notificationEnumerator = new MMDeviceEnumerator();
                    notificationClient = new NotificationClient(() => devicesChanged?.Invoke());
                    notificationEnumerator.RegisterEndpointNotificationCallback(notificationClient);
                }
            }
        }
        remove
        {
            lock (notificationLock) devicesChanged -= value;
        }
    }

    public IReadOnlyList<MicrophoneDevice> Available()
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = DefaultId(enumerator, Role.Console);
        var communicationsId = DefaultId(enumerator, Role.Communications);
        var devices = new List<MicrophoneDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                devices.Add(new MicrophoneDevice(device.ID, SafeName(device), device.ID == defaultId, device.ID == communicationsId));
            }
        }
        return devices
            .OrderBy(device => device.IsDefault ? 0 : 1)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public MicrophoneDevice? Default() => Available().FirstOrDefault(device => device.IsDefault);

    public MicrophoneDevice? Find(string id) => Available().FirstOrDefault(device => device.Id == id);

    public (MicrophoneDevice? Device, bool ChosenIdIsMissing) DeviceToSelect(string? preferredId)
    {
        var devices = Available();
        var defaultDevice = devices.FirstOrDefault(device => device.IsDefault);
        if (string.IsNullOrEmpty(preferredId)) return (defaultDevice, false);
        var chosen = devices.FirstOrDefault(device => device.Id == preferredId);
        return chosen is not null ? (chosen, false) : (defaultDevice, true);
    }

    public string? EffectiveInputName(string? preferredId) => DeviceToSelect(preferredId).Device?.Name;

    public bool ChoiceDiffersFromDefault(string? preferredId)
    {
        var (device, chosenIdIsMissing) = DeviceToSelect(preferredId);
        return device is not null && !chosenIdIsMissing && !device.IsDefault;
    }

    private static string? DefaultId(MMDeviceEnumerator enumerator, Role role)
    {
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, role)) return null;
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
        return device.ID;
    }

    internal static string SafeName(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return device.ID;
        }
    }

    public void Dispose()
    {
        lock (notificationLock)
        {
            if (notificationEnumerator is not null && notificationClient is not null)
            {
                notificationEnumerator.UnregisterEndpointNotificationCallback(notificationClient);
            }
            notificationEnumerator?.Dispose();
            notificationEnumerator = null;
            notificationClient = null;
        }
    }

    private sealed class NotificationClient(Action onChange) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => onChange();
        public void OnDeviceAdded(string pwstrDeviceId) => onChange();
        public void OnDeviceRemoved(string deviceId) => onChange();

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Capture) onChange();
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
        }
    }
}
