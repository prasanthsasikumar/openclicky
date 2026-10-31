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
//  Ported: System, Shelf, Coding agents, Now playing, Teacher, Timer, Notes and Clipboard, as on
//  the Mac. The Teacher also changes what the voice lanes can do, so they call its one facade,
//  TeacherPlugin, and nothing else of it. The tools (Shelf, Clipboard, Notes, Timer) get a round
//  button each in the open island's band instead of a place in the capsule. Home's calendar and
//  weather are not plugins but switches of their own (Plugins/Calendar, Plugins/Weather).
//

using OpenClicky.Core.Companion;
using OpenClicky.Core.Plugins.ClipboardList;
using OpenClicky.Core.Plugins.CodingAgents;
using OpenClicky.Core.Plugins.FocusTimer;
using OpenClicky.Core.Plugins.Media;
using OpenClicky.Core.Plugins.Notes;
using OpenClicky.Core.Plugins.Shelf;
using OpenClicky.Core.Plugins.SystemStats;
using OpenClicky.Core.Plugins.Teacher;

namespace OpenClicky.Core.Plugins;

public static class IslandPluginRegistry
{
    public const string SystemId = "system";
    public const string ShelfId = "shelf";
    public const string AgentsId = "agents";
    public const string MediaId = "media";
    public const string TeacherId = "teacher";
    public const string TimerId = "timer";
    public const string NotesId = "notes";
    public const string ClipboardId = "clipboard";

    /// <summary>The tools: each gets a round button in the band (in this order) instead of a place in the capsule.</summary>
    public static IReadOnlyList<string> ToolIds { get; } = [ShelfId, ClipboardId, NotesId, TimerId];

    /// <summary>In the order the capsule and Settings show them.</summary>
    public static IReadOnlyList<IslandComponent> All { get; } =
        [SystemStatsPlugin.Component, ShelfPlugin.Component, CodingAgentsPlugin.Component, MediaPlugin.Component, TeacherPlugin.Component,
         TimerPlugin.Component, NotesPlugin.Component, ClipboardPlugin.Component];
}
