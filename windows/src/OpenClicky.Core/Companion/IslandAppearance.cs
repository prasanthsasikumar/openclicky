//
//  IslandAppearance.cs
//  OpenClicky (Windows port of IslandAppearance.swift)
//
//  The island's style and which way it grows when hovered, in the Mac's keys. Blend is black
//  melting into a translucent bottom; liquid glass is the whole open island translucent under a
//  tint; black is solid like the closed island. Windows draws the island in a layered window,
//  which cannot blur what is behind it, so the glass styles are translucent tints without the
//  Mac's frost (Island/IslandWindow.cs). Only the open panel takes the style: the closed island,
//  the peek, the busy strip and the cards stay solid black.
//

using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Companion;

public enum IslandLook
{
    Blend,
    Glass,
    Black,
}

/// <summary>Which way the hovered island grows: sideways, a wide thin strip along the top edge so the tabs and toolbars
/// just under it stay uncovered; or down, the narrower line under the band.</summary>
public enum IslandPeekDirection
{
    Sideways,
    Down,
}

public static class IslandLooks
{
    public const string DefaultsKey = "openclicky.island.style";
    public const IslandLook DefaultLook = IslandLook.Glass;
    /// <summary>The old glass slider (0 all black, above 0 a melt), read once for someone who set it by hand.</summary>
    public const string LegacyGlassLevelKey = "openclicky.island.glassLevel";

    public static string RawValue(this IslandLook look) => look switch
    {
        IslandLook.Blend => "blend",
        IslandLook.Black => "black",
        _ => "glass",
    };

    public static IslandLook? FromRawValue(string? raw) => raw switch
    {
        "blend" => IslandLook.Blend,
        "glass" => IslandLook.Glass,
        "black" => IslandLook.Black,
        _ => null,
    };

    /// <summary>The picker's names.</summary>
    public static string Title(this IslandLook look) => look switch
    {
        IslandLook.Blend => "Blend",
        IslandLook.Black => "Black",
        _ => "Liquid Glass",
    };

    /// <summary>The line under the picker for the chosen style.</summary>
    public static string Footnote(this IslandLook look) => look switch
    {
        IslandLook.Blend => "Black at the top, fading into see-through towards the bottom.",
        IslandLook.Black => "Solid black all over, just like the closed island.",
        _ => "See-through all over, with a dark tint so the text stays readable.",
    };

    /// <summary>True when the open island is see-through at all.</summary>
    public static bool HasGlass(this IslandLook look) => look != IslandLook.Black;

    /// <summary>The stored style; else the old glass slider's meaning (a melt when it was above 0, black at 0); else the
    /// default.</summary>
    public static IslandLook Current(Preferences preferences)
    {
        if (FromRawValue(preferences.GetString(DefaultsKey)) is { } look) return look;
        if (preferences.GetOptionalDouble(LegacyGlassLevelKey) is { } level) return level > 0 ? IslandLook.Blend : IslandLook.Black;
        return DefaultLook;
    }
}

public static class IslandPeekDirections
{
    public const string DefaultsKey = "openclicky.island.peekDirection";
    public const IslandPeekDirection DefaultDirection = IslandPeekDirection.Sideways;

    public static string RawValue(this IslandPeekDirection direction) => direction == IslandPeekDirection.Down ? "down" : "sideways";

    public static string Title(this IslandPeekDirection direction) => direction == IslandPeekDirection.Down ? "Downwards" : "Sideways";

    public static IslandPeekDirection Current(Preferences preferences) => preferences.GetString(DefaultsKey) switch
    {
        "down" => IslandPeekDirection.Down,
        _ => DefaultDirection,
    };
}

/// <summary>The island's look for Settings (Island › More on the Mac): the style and the hover's direction, read and
/// written in the preferences; <see cref="Observe"/> follows either changing.</summary>
public sealed class IslandAppearanceSettings(Preferences preferences)
{
    public static IReadOnlyList<string> Keys { get; } = [IslandLooks.DefaultsKey, IslandLooks.LegacyGlassLevelKey, IslandPeekDirections.DefaultsKey];

    /// <summary>"Style": blend, liquid glass (the default) or black.</summary>
    public IslandLook Look
    {
        get => IslandLooks.Current(preferences);
        set => preferences.Set(IslandLooks.DefaultsKey, value.RawValue());
    }

    /// <summary>"When you hover the island": sideways (the default) or downwards.</summary>
    public IslandPeekDirection PeekDirection
    {
        get => IslandPeekDirections.Current(preferences);
        set => preferences.Set(IslandPeekDirections.DefaultsKey, value.RawValue());
    }

    /// <summary>Calls <paramref name="changed"/> whenever the style or the direction changes; dispose to stop.</summary>
    public IDisposable Observe(Action changed) => PreferenceObservations.ObserveAll(preferences, Keys, changed);
}

/// <summary>Several keys observed as one.</summary>
public static class PreferenceObservations
{
    public static IDisposable ObserveAll(Preferences preferences, IEnumerable<string> keys, Action changed)
    {
        var observations = keys.Select(key => preferences.Observe(key, changed)).ToList();
        return new Group(observations);
    }

    private sealed class Group(List<IDisposable> observations) : IDisposable
    {
        public void Dispose()
        {
            foreach (var observation in observations) observation.Dispose();
            observations.Clear();
        }
    }
}
