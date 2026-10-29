//
//  IslandComponents.cs
//  OpenClicky (Windows port of IslandComponents.swift)
//
//  The island's slot: what the open panel shows under its band. A component declares when it is
//  available; the selector picks one for the moment (a live take, a working agent) or by the user's
//  choice (a default, an order, and a flip in the capsule). Pure: the WPF views live in
//  OpenClicky.Windows/Island (the built-ins) and OpenClicky.Windows/Plugins (the plugins), looked up
//  by the component's id.
//
//  Ported so far: the built-ins (shortcuts, transcript, agent) and the System plugin. The Mac's
//  Shelf, Coding agents, Now playing and Teacher plugins are not yet; the context already carries
//  what they will read.
//

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins;

namespace OpenClicky.Core.Companion;

/// <summary>What the island knows about the moment.</summary>
public sealed record IslandContext
{
    public bool IsTakeLive { get; init; }
    /// <summary>When the last take landed (the live → idle flip).</summary>
    public DateTimeOffset? TakeLandedAt { get; init; }
    /// <summary>The take's words so far (the orb's live transcript).</summary>
    public string LiveTranscript { get; init; } = "";
    /// <summary>The last take's words as they were pasted, for the moments after it lands.</summary>
    public string LandedTranscript { get; init; } = "";
    public string? AgentActivityText { get; init; }
    public bool IsResultCardUp { get; init; }
    /// <summary>A drag from another app is over the island.</summary>
    public bool IsDragHovering { get; init; }
    /// <summary>Music is playing (the Now playing plugin is on and found a playing track).</summary>
    public bool IsMediaPlaying { get; init; }
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UnixEpoch;

    public static IslandContext Idle { get; } = new();

    /// <summary>The landing time to carry: <paramref name="now"/> at the live → idle flip, the previous one while
    /// the take stays landed, nothing while a take is live.</summary>
    public static DateTimeOffset? LandingTime(bool wasLive, DateTimeOffset? previousLandedAt, bool isLive, DateTimeOffset now)
    {
        if (isLive) return null;
        if (wasLive) return now;
        return previousLandedAt;
    }

    /// <summary>How long a landed take keeps its transcript in front.</summary>
    public static readonly TimeSpan LandedTranscriptWindow = TimeSpan.FromSeconds(8);

    /// <summary>The component the moment asks for: the shelf while a drag hovers (an explicit act that
    /// outranks everything), the transcript while a take is live or just landed, the agent while one
    /// works or its result card is up, the track while music plays, else nothing.</summary>
    public string? ContextualKey
    {
        get
        {
            if (IsDragHovering) return IslandPluginRegistry.ShelfId;
            if (IsTakeLive) return IslandComponentRegistry.TranscriptId;
            if (TakeLandedAt is { } landedAt && Now - landedAt < LandedTranscriptWindow) return IslandComponentRegistry.TranscriptId;
            if (AgentActivityText is not null || IsResultCardUp) return IslandComponentRegistry.AgentId;
            if (IsMediaPlaying) return IslandPluginRegistry.MediaId;
            return null;
        }
    }
}

/// <summary>One thing the island's slot can show.</summary>
/// <param name="Id">Stable; stored in the preferences.</param>
/// <param name="Title">For Settings and the capsule's tooltip.</param>
/// <param name="Glyph">The capsule's icon: a Segoe Fluent Icons glyph.</param>
/// <param name="PreferredHeight">The slot's height for this component at the top of the screen.</param>
/// <param name="IsPlugin">An optional extra the user turns on in Settings → Plugins; off, it is never available.</param>
/// <param name="SettingsDetail">The line under the plugin's switch in Settings (what it reads).</param>
public sealed record IslandComponent(string Id, string Title, string Glyph, double PreferredHeight, bool IsPlugin = false, string? SettingsDetail = null)
{
    /// <summary>When the component can show; always, unless it says otherwise.</summary>
    public Func<IslandContext, bool> IsAvailable { get; init; } = _ => true;
}

/// <summary>The selector's answer: the active component and every one the capsule may flip to.</summary>
public sealed record IslandComponentSelection(string Active, IReadOnlyList<string> Available)
{
    public bool Equals(IslandComponentSelection? other) =>
        other is not null && Active == other.Active && Available.SequenceEqual(other.Available);

    public override int GetHashCode() => HashCode.Combine(Active, Available.Count);
}

public static class IslandComponentSelector
{
    /// <summary>
    /// Disabled components are dropped first: never available, never the default, never pulled in by
    /// the moment. Then the override wins while it is available; else the moment's component; else the
    /// user's default (the shortcuts when the stored one is disabled); else the first available in the
    /// user's order. <c>Available</c> is the registry filtered by availability, in the user's order
    /// (unknown ids ignored, missing ids appended in registration order).
    /// </summary>
    public static IslandComponentSelection Select(IReadOnlyList<IslandComponent> registry, IReadOnlyList<string> order, string defaultId,
                                                  string? overrideId, IslandContext context, IReadOnlySet<string>? enabled = null)
    {
        if (registry.Count == 0) throw new ArgumentException("the island needs at least one component", nameof(registry));
        var availableIds = registry.Where(component => (enabled?.Contains(component.Id) ?? true) && component.IsAvailable(context))
            .Select(component => component.Id).ToList();
        var ordered = order.Where(availableIds.Contains).Distinct().Concat(availableIds.Where(id => !order.Contains(id))).ToList();
        var fallbackDefault = enabled is null || enabled.Contains(defaultId) ? defaultId : IslandComponentRegistry.ShortcutsId;
        var candidates = new[] { overrideId, context.ContextualKey, fallbackDefault };
        var active = candidates.FirstOrDefault(id => id is not null && ordered.Contains(id)) ?? ordered.FirstOrDefault() ?? registry[0].Id;
        return new IslandComponentSelection(active, ordered);
    }
}

/// <summary>The user's default component, order and switched-on plugins, in the preferences (the Mac's keys).</summary>
public sealed class IslandComponentSettings(Preferences preferences)
{
    public const string DefaultKey = "openclicky.island.defaultComponent";
    public const string OrderKey = "openclicky.island.componentOrder";
    public const string PluginsKey = "openclicky.island.plugins";

    public static IReadOnlyList<string> Keys { get; } = [DefaultKey, OrderKey, PluginsKey];

    public string DefaultId
    {
        get => preferences.GetString(DefaultKey) ?? IslandComponentRegistry.ShortcutsId;
        set => preferences.Set(DefaultKey, value);
    }

    public IReadOnlyList<string> Order
    {
        get => preferences.GetStringArray(OrderKey) ?? [];
        set => preferences.Set(OrderKey, value);
    }

    /// <summary>The plugins the user turned on; unset means none.</summary>
    public IReadOnlyList<string> EnabledPlugins
    {
        get => preferences.GetStringArray(PluginsKey) ?? [];
        set => preferences.Set(PluginsKey, value);
    }

    /// <summary>Turns one plugin on or off, keeping the others as they are.</summary>
    public void SetPluginEnabled(string id, bool isOn)
    {
        var enabled = EnabledPlugins.Where(enabledId => enabledId != id).ToList();
        if (isOn) enabled.Add(id);
        EnabledPlugins = enabled;
    }

    /// <summary>A built-in is always on; a plugin only when the user turned it on. Unknown ids are off.</summary>
    public bool IsEnabled(string id, IReadOnlyList<IslandComponent> registry)
    {
        var component = registry.FirstOrDefault(candidate => candidate.Id == id);
        if (component is null) return false;
        return !component.IsPlugin || EnabledPlugins.Contains(id);
    }

    /// <summary>The stored default when it is on; the shortcuts when it has been switched off since.</summary>
    public string ResolvedDefaultId(IReadOnlyList<IslandComponent> registry) =>
        IsEnabled(DefaultId, registry) ? DefaultId : IslandComponentRegistry.ShortcutsId;

    /// <summary>The stored order with unknown ids dropped and missing ones appended in registration order.</summary>
    public IReadOnlyList<string> ResolvedOrder(IReadOnlyList<IslandComponent> registry)
    {
        var known = registry.Select(component => component.Id).ToList();
        var stored = Order.Where(known.Contains).Distinct().ToList();
        return [.. stored, .. known.Where(id => !stored.Contains(id))];
    }

    /// <summary>The ids the selector may use, from the registry and the user's switches.</summary>
    public IReadOnlySet<string> EnabledIds(IReadOnlyList<IslandComponent> registry) =>
        registry.Select(component => component.Id).Where(id => IsEnabled(id, registry)).ToHashSet();
}

/// <summary>The built-in components, in registration order, then the plugins (<see cref="IslandPluginRegistry"/>).</summary>
public static class IslandComponentRegistry
{
    public const string ShortcutsId = "shortcuts";
    public const string TranscriptId = "transcript";
    public const string AgentId = "agent";

    /// <summary>Today's body row: dock or release the pointer, (i), and the shortcuts (or the engine warning).
    /// 45 under the band; a side drawer stacks the dock button over the grid (IslandLayout.FullHeight).</summary>
    public static IslandComponent Shortcuts { get; } = new(ShortcutsId, "Shortcuts", "", 45);

    /// <summary>The take's words so far, while a take is live and for a moment after it lands.</summary>
    public static IslandComponent Transcript { get; } = new(TranscriptId, "Transcript", "", 52)
    {
        IsAvailable = context => context.ContextualKey == TranscriptId,
    };

    /// <summary>The working agent's latest line, and its result card's follow-up.</summary>
    public static IslandComponent Agent { get; } = new(AgentId, "Agent", "", 52)
    {
        IsAvailable = context => context.AgentActivityText is not null || context.IsResultCardUp,
    };

    public static IReadOnlyList<IslandComponent> BuiltIns { get; } = [Shortcuts, Transcript, Agent];

    /// <summary>The built-ins first, then the plugins (off until turned on in Settings).</summary>
    public static IReadOnlyList<IslandComponent> All { get; } = [.. BuiltIns, .. IslandPluginRegistry.All];
}
