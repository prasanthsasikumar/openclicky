//
//  IslandHome.cs
//  OpenClicky (Windows port of IslandHome.swift's IslandHomeLayout and IslandDashboardStyle.swift's sizes)
//
//  The open island's home: what it shows when nothing else is going on. The columns that are on
//  sit side by side under the band (now playing, the calendar, then the weather and System tiles),
//  each a hairline apart, and the island grows to fit them. With none on, home is the shortcuts
//  row. Pure, so the sizes are tested; the view is OpenClicky.Windows/Island/IslandHomeView.cs.
//

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins;

namespace OpenClicky.Core.Companion;

/// <summary>The open island's dashboard sizes, shared by every column and tile on it (the Mac's IslandDash).</summary>
public static class IslandDash
{
    /// <summary>Every row of the open island is inset this far from its sides.</summary>
    public const double SidePadding = 20;
    /// <summary>Columns sit this far apart, a hairline divider between them.</summary>
    public const double ColumnSpacing = 16;
    /// <summary>The dashboard row's height under the band (the calendar column's).</summary>
    public const double ContentHeight = 130;

    /// <summary>The now-playing column.</summary>
    public const double MediaWidth = 300;
    public const double ArtworkSize = 84;
    public const double ArtworkRadius = 12;

    /// <summary>The calendar column.</summary>
    public const double CalendarWidth = 268;

    /// <summary>A widget tile: a square with soft corners, a faint fill and a hairline border.</summary>
    public const double TileSize = 118;
    public const double TileSpacing = 8;
    public const double TileRadius = 20;
}

/// <summary>One column of the home row.</summary>
public enum IslandHomeColumn
{
    Media,
    Calendar,
    Weather,
    System,
}

/// <summary>The home row's columns and sizes, from the switches.</summary>
public sealed class IslandHomeLayout : IEquatable<IslandHomeLayout>
{
    /// <summary>Settings → Island: the calendar column (on unless turned off).</summary>
    public const string CalendarEnabledKey = "openclicky.calendar.enabled";
    /// <summary>Settings → Island: the weather tile (off until turned on, as it asks the network).</summary>
    public const string WeatherEnabledKey = "openclicky.weather.enabled";

    /// <summary>The shortcuts row's slot height, which home falls back to.</summary>
    public const double ShortcutsHeight = 45;
    /// <summary>The tile height, a little air above and more below.</summary>
    public const double RowTopPadding = 2;
    public const double RowBottomPadding = 14;

    /// <summary>The preferences home's columns follow: the plugins (Now playing, System) and the two switches.</summary>
    public static IReadOnlyList<string> Keys { get; } = [IslandComponentSettings.PluginsKey, CalendarEnabledKey, WeatherEnabledKey];

    public IReadOnlyList<IslandHomeColumn> Columns { get; }

    public IslandHomeLayout(bool mediaOn, bool calendarOn, bool weatherOn, bool systemOn)
    {
        var columns = new List<IslandHomeColumn>();
        if (mediaOn) columns.Add(IslandHomeColumn.Media);
        if (calendarOn) columns.Add(IslandHomeColumn.Calendar);
        if (weatherOn) columns.Add(IslandHomeColumn.Weather);
        if (systemOn) columns.Add(IslandHomeColumn.System);
        Columns = columns;
    }

    /// <summary>From the plugin switches (Now playing, System) and the calendar and weather switches.</summary>
    public static IslandHomeLayout Current(Preferences preferences)
    {
        var plugins = new IslandComponentSettings(preferences).EnabledPlugins;
        return new IslandHomeLayout(
            mediaOn: plugins.Contains(IslandPluginRegistry.MediaId),
            calendarOn: preferences.GetBool(CalendarEnabledKey, true),
            weatherOn: preferences.GetBool(WeatherEnabledKey, false),
            systemOn: plugins.Contains(IslandPluginRegistry.SystemId));
    }

    public static bool IsTile(IslandHomeColumn column) => column is IslandHomeColumn.Weather or IslandHomeColumn.System;

    public bool IsEmpty => Columns.Count == 0;

    /// <summary>The groups drawn with a divider between them: each column on its own, the tiles together.</summary>
    public IReadOnlyList<IReadOnlyList<IslandHomeColumn>> Groups
    {
        get
        {
            var groups = new List<List<IslandHomeColumn>>();
            foreach (var column in Columns)
            {
                if (IsTile(column) && groups.Count > 0 && IsTile(groups[^1][0])) groups[^1].Add(column);
                else groups.Add([column]);
            }
            return groups;
        }
    }

    public static double WidthOf(IReadOnlyList<IslandHomeColumn> group)
    {
        if (group.Count == 0) return 0;
        if (IsTile(group[0])) return group.Count * IslandDash.TileSize + (group.Count - 1) * IslandDash.TileSpacing;
        return group[0] switch
        {
            IslandHomeColumn.Media => IslandDash.MediaWidth,
            IslandHomeColumn.Calendar => IslandDash.CalendarWidth,
            _ => 0,
        };
    }

    /// <summary>The island's width for this row: the groups, a divider and two gaps between each, and the side padding.
    /// Null when nothing is on (home falls back to the shortcuts).</summary>
    public double? Width
    {
        get
        {
            if (IsEmpty) return null;
            var groups = Groups;
            var content = groups.Sum(WidthOf);
            var gutters = (groups.Count - 1) * (IslandDash.ColumnSpacing * 2 + 1);
            return content + gutters + IslandDash.SidePadding * 2;
        }
    }

    public double Height => IsEmpty ? ShortcutsHeight : IslandDash.TileSize + RowTopPadding + RowBottomPadding;

    public bool Equals(IslandHomeLayout? other) => other is not null && Columns.SequenceEqual(other.Columns);
    public override bool Equals(object? obj) => Equals(obj as IslandHomeLayout);
    public override int GetHashCode() => Columns.Aggregate(17, (hash, column) => hash * 31 + (int)column);
    public override string ToString() => string.Join(",", Columns);
}
