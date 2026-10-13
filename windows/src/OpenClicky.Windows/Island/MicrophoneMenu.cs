//
//  MicrophoneMenu.cs
//  OpenClicky (Windows port of NotchHUDPanels.swift's MicrophoneMenu)
//
//  The microphone menu the island and the first-run tour open ("listening through … · change"):
//  Automatic (the default device's name), then every input, the chosen one ticked. It pops up at
//  the mouse; choosing calls back with the device id (empty = automatic).
//

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenClicky.Core.Platform;

namespace OpenClicky.Windows.Island;

public static class MicrophoneMenu
{
    /// <summary>The chosen device's name, else "automatic · &lt;default&gt;" (MicrophoneMenu.currentName).</summary>
    public static string CurrentName(IMicrophoneDevices devices, string? preferredId)
    {
        if (preferredId is not null && devices.Find(preferredId) is { } device) return device.Name;
        return $"automatic · {devices.EffectiveInputName(null) ?? "none found"}";
    }

    /// <summary>Just the device's name, for a line with no room for more (MicrophoneMenu.deviceName).</summary>
    public static string DeviceName(IMicrophoneDevices devices, string? preferredId)
    {
        if (preferredId is not null && devices.Find(preferredId) is { } device) return device.Name;
        return devices.EffectiveInputName(null) ?? "no microphone";
    }

    /// <summary>Pops the menu up at the mouse; <paramref name="onChoose"/> gets the device id, or null for automatic.</summary>
    public static void Present(IMicrophoneDevices devices, string? preferredId, Action<string?> onChoose)
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, StaysOpen = false };
        var automatic = new MenuItem
        {
            Header = $"Automatic ({devices.EffectiveInputName(null) ?? "none"})",
            IsCheckable = true,
            IsChecked = preferredId is null,
        };
        automatic.Click += (_, _) => onChoose(null);
        menu.Items.Add(automatic);
        menu.Items.Add(new Separator());
        foreach (var device in devices.Available())
        {
            var item = new MenuItem { Header = device.Name, IsCheckable = true, IsChecked = device.Id == preferredId };
            var deviceId = device.Id;
            item.Click += (_, _) => onChoose(deviceId);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
