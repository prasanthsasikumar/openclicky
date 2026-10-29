//
//  IslandPlugins.cs
//  OpenClicky (Windows port of Plugins/IslandPlugins.swift)
//
//  The island's plugins: optional extras, each off until the user turns it on in Settings → Plugins.
//  Everything a plugin needs lives in its own folder under Plugins/ (its figures and store here in
//  Core, its view and live sources under OpenClicky.Windows/Plugins), so the rest of the app never
//  depends on it. A plugin is an IslandComponent whose IsPlugin is true; the registry below is the
//  only place that lists them.
//
//  Ported so far: System, Shelf and Now playing. The Mac's Coding agents and Teacher keep their ids
//  here so the context and the preferences agree with the Mac's.
//

using OpenClicky.Core.Companion;
using OpenClicky.Core.Plugins.Media;
using OpenClicky.Core.Plugins.Shelf;
using OpenClicky.Core.Plugins.SystemStats;

namespace OpenClicky.Core.Plugins;

public static class IslandPluginRegistry
{
    public const string SystemId = "system";
    public const string ShelfId = "shelf";
    public const string AgentsId = "agents";
    public const string MediaId = "media";
    public const string TeacherId = "teacher";

    /// <summary>In the order the capsule and Settings show them.</summary>
    public static IReadOnlyList<IslandComponent> All { get; } = [SystemStatsPlugin.Component, ShelfPlugin.Component, MediaPlugin.Component];
}
