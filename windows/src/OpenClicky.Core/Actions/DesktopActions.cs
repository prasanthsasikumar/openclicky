// Port of macos/OpenClicky/OpenClicky/MacActions.swift.
//
// The fast lane: the local actions OpenClicky performs itself instead of handing them to the
// Codex agent. Opening an app or making a folder is one round-trip on the Realtime session
// (about two seconds) rather than two Codex model turns (about twelve).
//
// Every action takes typed arguments — never a path, never a command. A voice model mishears; the
// worst a mishearing can do here is create a wrongly *named* folder in a location the user asked
// for. Anything outside this set still goes to `send_to_agent`, which has the sandbox, the routing
// instructions, and the approval gate in front of it.
//
// Windows differences: `reveal_in_finder` is `reveal_in_explorer` (the old name is still accepted
// when parsing, so a plan or a cached prompt that says it keeps working); folder names follow the
// Windows rules (reserved device names, `<>:"/\|?*`, a trailing dot or space); the four known
// folders come from the shell (redirected folders included) through IDesktopActionExecutor. The
// log prefix stays `mac action:` so macos/OpenClicky/scripts/measure-actions.sh reads Windows
// logs unchanged.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Actions;

/// <summary>The folders the fast lane will touch. A closed set, so no spoken phrase can widen it.</summary>
public enum DesktopActionLocation
{
    Desktop,
    Downloads,
    Documents,
    Workspace,
    Home,
}

public static class DesktopActionLocations
{
    /// <summary>In the order the tool schema lists them.</summary>
    public static readonly IReadOnlyList<DesktopActionLocation> All =
    [
        DesktopActionLocation.Desktop, DesktopActionLocation.Downloads, DesktopActionLocation.Documents,
        DesktopActionLocation.Workspace, DesktopActionLocation.Home,
    ];

    /// <summary>The schema value: "desktop", "downloads", …</summary>
    public static string RawValue(this DesktopActionLocation location) => location switch
    {
        DesktopActionLocation.Desktop => "desktop",
        DesktopActionLocation.Downloads => "downloads",
        DesktopActionLocation.Documents => "documents",
        DesktopActionLocation.Workspace => "workspace",
        _ => "home",
    };

    /// <summary>
    /// Parses the model's argument. Unknown values return null so the caller can say so out loud
    /// rather than writing somewhere the user did not ask for.
    /// </summary>
    public static DesktopActionLocation? Named(string raw)
    {
        var wanted = raw.Trim().ToLowerInvariant();
        foreach (var location in All)
        {
            if (location.RawValue() == wanted) return location;
        }
        return null;
    }

    /// <summary>
    /// The folder under the home directory (the Mac's rule). The runner prefers the shell's known
    /// folder path, which follows a redirected (OneDrive) Desktop or Documents.
    /// </summary>
    public static string DirectoryPath(this DesktopActionLocation location, string homeDirectory, string workspaceDirectory) => location switch
    {
        DesktopActionLocation.Desktop => Path.Combine(homeDirectory, "Desktop"),
        DesktopActionLocation.Downloads => Path.Combine(homeDirectory, "Downloads"),
        DesktopActionLocation.Documents => Path.Combine(homeDirectory, "Documents"),
        DesktopActionLocation.Workspace => workspaceDirectory,
        _ => homeDirectory,
    };

    /// <summary>How the assistant refers to the folder when it speaks.</summary>
    public static string SpokenName(this DesktopActionLocation location) => location switch
    {
        DesktopActionLocation.Desktop => "your Desktop",
        DesktopActionLocation.Downloads => "your Downloads folder",
        DesktopActionLocation.Documents => "your Documents folder",
        DesktopActionLocation.Workspace => "your OpenClicky folder",
        _ => "your home folder",
    };
}

/// <summary>
/// What happened, and the single sentence the assistant says about it. The Realtime model speaks
/// this string back verbatim, so it is product copy: no jargon, no paths read out character by
/// character, no apologies.
/// </summary>
public abstract record DesktopActionOutcome
{
    private DesktopActionOutcome() { }

    public sealed record OpenedApp(string ApplicationName) : DesktopActionOutcome;
    public sealed record AppNotFound(string ApplicationName) : DesktopActionOutcome;
    public sealed record CreatedFolder(string Name, DesktopActionLocation Location) : DesktopActionOutcome;
    public sealed record FolderAlreadyExists(string Name, DesktopActionLocation Location) : DesktopActionOutcome;
    public sealed record Revealed(string Name, DesktopActionLocation Location) : DesktopActionOutcome;
    public sealed record NothingToReveal(string Name, DesktopActionLocation Location) : DesktopActionOutcome;
    public sealed record OpenedUrl(string Url) : DesktopActionOutcome;
    public sealed record VolumeSet(int Level) : DesktopActionOutcome;
    public sealed record MediaControlled(string Action) : DesktopActionOutcome;
    /// <summary>Pause or play went to the player itself ("pause" / "play").</summary>
    public sealed record PlayerControlled(string Command) : DesktopActionOutcome;
    public sealed record Searched(string Query, WebSearchSite Site) : DesktopActionOutcome;
    public sealed record InvalidQuery : DesktopActionOutcome;
    public sealed record MinimizedWindows(int Count, string? KeptApp) : DesktopActionOutcome;
    public sealed record RestoredWindows(int Count, string? App) : DesktopActionOutcome;
    public sealed record WindowAppNotRunning(string App) : DesktopActionOutcome;
    public sealed record OpenedInbox(EmailApp App) : DesktopActionOutcome;
    public sealed record DraftedEmail(EmailApp App, string To, int BodyLength) : DesktopActionOutcome;
    public sealed record InvalidRecipient : DesktopActionOutcome;
    public sealed record PlayingTrack(string Name, string Artist) : DesktopActionOutcome;
    /// <summary>Spotify opened the track but Windows could not confirm it started (no media session answered).</summary>
    public sealed record OpenedTrack(string Name, string Artist) : DesktopActionOutcome;
    public sealed record OpenedSpotifySearch(string Query) : DesktopActionOutcome;
    public sealed record SpotifyStarting : DesktopActionOutcome;
    public sealed record SpotifyNotInstalled : DesktopActionOutcome;
    public sealed record PlayFailed : DesktopActionOutcome;
    public sealed record InvalidName : DesktopActionOutcome;
    public sealed record InvalidAppName : DesktopActionOutcome;
    public sealed record UnknownLocation(string Raw) : DesktopActionOutcome;
    public sealed record InvalidUrl : DesktopActionOutcome;
    public sealed record PermissionDenied(DesktopActionLocation Location) : DesktopActionOutcome;
    public sealed record Failed(string Reason) : DesktopActionOutcome;

    /// <summary>True when the action did what was asked (the tour counts it as a task handed over).</summary>
    public bool DidSomething => this is OpenedApp or CreatedFolder or FolderAlreadyExists or Revealed or OpenedUrl or VolumeSet or MediaControlled
        or PlayerControlled or Searched or MinimizedWindows or RestoredWindows or OpenedInbox or DraftedEmail or PlayingTrack or OpenedTrack or OpenedSpotifySearch;

    /// <summary>
    /// What the Realtime model is told in the tool's output. The same as the sentence unless an outcome
    /// has more to say to the model than to the person (a screenshot to look at, a rule to keep).
    /// </summary>
    public string ToolOutput => this switch
    {
        Searched searched => $"The results for {searched.Query} are on screen in the latest screenshot. Say what the first few are in one sentence.",
        OpenedInbox inbox => $"{inbox.App.Title()}'s inbox is open and attached as the latest screenshot. Unread rows are in bold. Read out sender and subject of the unread ones briefly, newest first, at most five, then stop.",
        DraftedEmail => SpokenSentence + " If they ask you to send it, say you never send email and that it is open in front of them.",
        WindowAppNotRunning => SpokenSentence + " Call open_app if they want it opened.",
        _ => SpokenSentence,
    };

    /// <summary>What goes in app.log. The same as the sentence unless it would carry something private (an email's recipient).</summary>
    public string LogDescription => this switch
    {
        DraftedEmail drafted => $"draft_email: {drafted.App.RawValue()}, {drafted.BodyLength}-character body, {(drafted.To.Contains('@') ? "address" : "name")} given",
        _ => SpokenSentence,
    };

    /// <summary>
    /// Whether the model should see a fresh screenshot before it speaks: true for an action that
    /// changed what is on screen in a way worth describing (a page opened), false for the rest.
    /// </summary>
    public bool ThenLooksAtScreen => this is OpenedUrl or Searched or OpenedInbox or DraftedEmail;

    public string SpokenSentence => this switch
    {
        OpenedApp opened => $"Opened {opened.ApplicationName}.",
        AppNotFound missing => $"I couldn't find an app called {missing.ApplicationName}.",
        CreatedFolder created => $"Created {created.Name} on {created.Location.SpokenName()}.",
        FolderAlreadyExists existing => $"{existing.Name} is already on {existing.Location.SpokenName()}.",
        Revealed revealed => $"Showing {revealed.Name} on {revealed.Location.SpokenName()}.",
        NothingToReveal nothing => $"I couldn't find {nothing.Name} on {nothing.Location.SpokenName()}.",
        OpenedUrl => "Opened it in your browser.",
        VolumeSet volume => $"Volume set to {volume.Level} percent.",
        MediaControlled => "Done.",
        PlayerControlled player => player.Command == "pause" ? "Paused." : "Playing.",
        Searched searched => $"Searching for {searched.Query}.",
        InvalidQuery => "I didn't catch what to search for.",
        MinimizedWindows { KeptApp: null } minimized => minimized.Count == 0
            ? "Nothing to minimize."
            : $"Minimized {minimized.Count} window{(minimized.Count == 1 ? "" : "s")}.",
        MinimizedWindows minimized => minimized.Count == 0
            ? $"Nothing else to minimize; {minimized.KeptApp} stays up."
            : $"Minimized {minimized.Count} window{(minimized.Count == 1 ? "" : "s")}; {minimized.KeptApp} stays up.",
        RestoredWindows { App: null } restored => restored.Count == 0
            ? "I hadn't minimized anything."
            : $"Brought {restored.Count} window{(restored.Count == 1 ? "" : "s")} back.",
        // Zero is not a failure: the app had nothing minimized and is now in front.
        RestoredWindows restored => restored.Count == 0 ? $"{restored.App} is in front now." : $"Brought {restored.App} back.",
        WindowAppNotRunning notRunning => $"{notRunning.App} isn't open.",
        OpenedInbox => "Here's your inbox.",
        DraftedEmail drafted => $"Drafted an email to {drafted.To}; it's open for you to check. I don't send email — you do.",
        InvalidRecipient => "I didn't catch who it's for.",
        PlayingTrack playing => playing.Artist.Length == 0 ? $"Playing {playing.Name}." : $"Playing {playing.Name} by {playing.Artist}.",
        OpenedTrack opened => (opened.Artist.Length == 0 ? $"Opened {opened.Name}" : $"Opened {opened.Name} by {opened.Artist}") + " in Spotify; press play if it hasn't started.",
        OpenedSpotifySearch search => $"I couldn't look that up, so I opened Spotify's search for {search.Query}; pick one.",
        SpotifyStarting => "Spotify is starting; give it a moment and ask again.",
        SpotifyNotInstalled => "Spotify isn't installed on this PC.",
        PlayFailed => "Spotify didn't play it.",
        InvalidName => "That name has characters a folder can't have.",
        InvalidAppName => "That app name has characters an app name can't have.",
        UnknownLocation unknown => $"I don't know where {unknown.Raw} is.",
        InvalidUrl => "That doesn't look like a web address I can open.",
        // The Mac's TCC prompt has no Windows twin; Controlled folder access (Windows Security) is
        // what refuses a write to Documents or the Desktop, and it is allowed from there.
        PermissionDenied denied => $"Windows hasn't allowed access to {denied.Location.SpokenName()}. Windows Security can allow it.",
        Failed failed => $"That didn't work: {failed.Reason}.",
        _ => "Done.",
    };
}

/// <summary>Argument checks. Both return null for anything the fast lane will not act on.</summary>
public static class DesktopActionValidation
{
    private static readonly char[] InvalidNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A folder name the model spoke, or null when it could reach outside the location it was given
    /// or Windows would not create it as named. `\` and `/` are the separators Windows honours and
    /// `:` names a drive or a stream; `&lt;&gt;"|?*` and control characters are refused by NTFS; a
    /// reserved device name (CON, NUL, COM1…, also with an extension) opens a device instead of a
    /// folder; Windows silently drops a trailing dot or space; a leading dot would create something
    /// the user cannot easily see (and covers `..`); 255 characters is the filesystem's own limit.
    /// Also used to validate `open_app`'s name, so it cannot resolve to a path outside the app
    /// lookup (`..\Windows\System32\calc`, say).
    /// </summary>
    public static string? FolderName(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 255) return null;
        if (trimmed.IndexOfAny(InvalidNameCharacters) >= 0) return null;
        if (trimmed.Any(character => character < ' ')) return null;
        if (trimmed.StartsWith('.')) return null;
        if (trimmed.EndsWith('.') || trimmed.EndsWith(' ')) return null;
        var firstDot = trimmed.IndexOf('.');
        var baseName = (firstDot >= 0 ? trimmed[..firstDot] : trimmed).TrimEnd(' ');
        if (ReservedDeviceNames.Contains(baseName)) return null;
        return trimmed;
    }

    /// <summary>
    /// Something to search for (or a song to look up), or null: trimmed, at most 200 characters, and
    /// nothing a control character (a newline, a NUL) could do to the URL or the page.
    /// </summary>
    public static string? SearchQuery(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 200) return null;
        if (trimmed.Any(char.IsControl)) return null;
        return trimmed;
    }

    /// <summary>
    /// Who an email is for: an address or a name as said, at most 80 characters, never an angle
    /// bracket (so it can only ever land in a To field). Null when there is nothing usable.
    /// </summary>
    public static string? Recipient(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 80 || trimmed.Contains('<') || trimmed.Contains('>')) return null;
        return trimmed;
    }

    public static string EmailSubject(string raw)
    {
        var trimmed = raw.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }

    /// <summary>A compose URL carries about this much; both inboxes take the same cap so they behave alike.</summary>
    public static string EmailBody(string raw)
    {
        var trimmed = raw.Trim();
        return trimmed.Length <= 2_000 ? trimmed : trimmed[..2_000];
    }

    /// <summary>
    /// An http(s) URL, or null. Other schemes are refused: `file:` reads the disk and a custom scheme
    /// launches whichever app registered it, neither of which is "open this page for me".
    /// </summary>
    public static Uri? WebUrl(string raw)
    {
        var trimmed = raw.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var url)) return null;
        var scheme = url.Scheme.ToLowerInvariant();
        if (scheme != "http" && scheme != "https") return null;
        if (string.IsNullOrEmpty(url.Host)) return null;
        return url;
    }
}

/// <summary>One action the Realtime model asked for, already parsed and typed.</summary>
public abstract record DesktopAction
{
    private DesktopAction() { }

    public sealed record OpenApp(string Name) : DesktopAction;
    public sealed record OpenUrl(string Raw) : DesktopAction;
    public sealed record CreateFolder(string Name, DesktopActionLocation Location) : DesktopAction;
    /// <summary>The Mac's revealInFinder.</summary>
    public sealed record RevealInExplorer(string Name, DesktopActionLocation Location) : DesktopAction;
    public sealed record SetVolume(int Level) : DesktopAction;
    public sealed record MediaControl(string Action) : DesktopAction;
    public sealed record WebSearch(string Query, WebSearchSite Site) : DesktopAction;
    /// <summary>Every window minimized, except the named app's when there is one.</summary>
    public sealed record MinimizeAll(string? KeepApp) : DesktopAction;
    /// <summary>What MinimizeAll minimized, or only the named app's windows when there is one.</summary>
    public sealed record RestoreWindows(string? App) : DesktopAction;
    public sealed record CheckEmail(string? App) : DesktopAction;
    public sealed record DraftEmail(string To, string Subject, string Body, string? App) : DesktopAction;
    public sealed record PlayMusic(string Query) : DesktopAction;

    public const string OpenAppToolName = "open_app";
    public const string OpenUrlToolName = "open_url";
    public const string CreateFolderToolName = "create_folder";
    public const string RevealInExplorerToolName = "reveal_in_explorer";
    /// <summary>The Mac tool name, still accepted by <see cref="Parse(string, JsonObject?)"/>.</summary>
    public const string RevealInFinderToolName = "reveal_in_finder";
    public const string SetVolumeToolName = "set_volume";
    public const string MediaControlToolName = "media_control";
    public const string WebSearchToolName = "web_search";
    public const string MinimizeAllToolName = "minimize_all";
    public const string CheckEmailToolName = "check_email";
    public const string DraftEmailToolName = "draft_email";
    public const string PlayMusicToolName = "play_music";
    public const string RestoreWindowsToolName = "restore_windows";

    public const string VolumeArgumentMessage = "I need a volume between 0 and 100";

    /// <summary>
    /// The local actions OpenClicky performs itself, as Realtime function tools. Typed arguments
    /// only: `location` is a closed set and there is no path or command anywhere in the schema, so
    /// a mishearing cannot widen what the fast lane is able to do. `Parse` below reads the calls
    /// these produce.
    /// </summary>
    public static IReadOnlyList<JsonObject> RealtimeToolDefinitions()
    {
        JsonObject LocationProperty() => new()
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(DesktopActionLocations.All.Select(location => (JsonNode)location.RawValue()).ToArray()),
            ["description"] = "Which folder. Leave it out for OpenClicky's own workspace folder.",
        };
        JsonObject Function(string name, string description, JsonObject properties, params string[] required) => new()
        {
            ["type"] = "function",
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(key => (JsonNode)key).ToArray()),
            },
        };
        return
        [
            Function(OpenAppToolName,
                     "Open or switch to a Windows app by its name, e.g. 'Spotify'. Use this instead of the agent for simply opening an app.",
                     new JsonObject { ["name"] = new JsonObject { ["type"] = "string", ["description"] = "The app's name as the user said it." } },
                     "name"),
            Function(OpenUrlToolName,
                     "Open a web page in the user's browser. http and https only.",
                     new JsonObject { ["url"] = new JsonObject { ["type"] = "string", ["description"] = "The full https URL." } },
                     "url"),
            Function(CreateFolderToolName,
                     "Make a new folder. Use this instead of the agent for a single folder.",
                     new JsonObject
                     {
                         ["name"] = new JsonObject { ["type"] = "string", ["description"] = "The folder's name, without any slashes. The request was spoken, so write numbers said as words in digits ('twenty three' → '23') unless the person spelled them out." },
                         ["location"] = LocationProperty(),
                     },
                     "name"),
            Function(RevealInExplorerToolName,
                     "Show an existing file or folder in File Explorer.",
                     new JsonObject
                     {
                         ["name"] = new JsonObject { ["type"] = "string", ["description"] = "The file or folder's name." },
                         ["location"] = LocationProperty(),
                     },
                     "name"),
            Function(SetVolumeToolName,
                     "Set the PC's output volume.",
                     new JsonObject { ["level"] = new JsonObject { ["type"] = "integer", ["description"] = "0 to 100." } },
                     "level"),
            Function(MediaControlToolName,
                     "Pause, resume, or skip whatever is playing. Use pause for 'stop', 'pause', 'be quiet'; play to resume; playpause only when they say play/pause without saying which.",
                     new JsonObject { ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("playpause", "next", "previous", "pause", "play") } },
                     "action"),
            Function(WebSearchToolName,
                     "Search the web (or YouTube, Wikipedia, Google Maps, Amazon) for what the user said and show the results in their browser. Use for 'search for…', 'look up…', 'google…'.",
                     new JsonObject
                     {
                         ["query"] = new JsonObject { ["type"] = "string", ["description"] = "What to search for, as the user said it." },
                         ["site"] = new JsonObject
                         {
                             ["type"] = "string",
                             ["enum"] = new JsonArray(WebSearchSites.All.Select(site => (JsonNode)site.RawValue()).ToArray()),
                             ["description"] = "Where to search. Leave it out for the web.",
                         },
                     },
                     "query"),
            Function(MinimizeAllToolName,
                     "Minimize every open window so the desktop is clear. For 'minimize everything', 'clear my screen', 'hide all windows'; with keep for 'everything except Terminal', 'leave just Chrome'.",
                     new JsonObject { ["keep"] = new JsonObject { ["type"] = "string", ["description"] = "Only when they named an app to leave on screen: its name, e.g. 'Terminal'." } }),
            Function(CheckEmailToolName,
                     "Show the user's unread email. Use when they say check my email, any new mail, what's in my inbox.",
                     new JsonObject
                     {
                         ["app"] = new JsonObject
                         {
                             ["type"] = "string",
                             ["enum"] = new JsonArray("gmail", "outlook"),
                             ["description"] = "Only when they named one: 'gmail' for Gmail, 'outlook' for Outlook.",
                         },
                     }),
            Function(DraftEmailToolName,
                     "Start a new email with the recipient and a short body written from what the user said, and leave it open for them to read and send themselves. Never sends.",
                     new JsonObject
                     {
                         ["to"] = new JsonObject { ["type"] = "string", ["description"] = "The recipient's email address, or their name exactly as the user said it." },
                         ["subject"] = new JsonObject { ["type"] = "string", ["description"] = "A short subject. Leave it out if nothing fits." },
                         ["body"] = new JsonObject { ["type"] = "string", ["description"] = "The email's text, short and in the user's voice, saying what they asked you to say. No invented facts." },
                         ["app"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("gmail", "outlook"), ["description"] = "Only when they named one." },
                     },
                     "to", "body"),
            Function(PlayMusicToolName,
                     "Play a specific song, album or artist in Spotify. Pass what they said, e.g. 'Blinding Lights by The Weeknd'. For pause, resume or skip use media_control.",
                     new JsonObject { ["query"] = new JsonObject { ["type"] = "string", ["description"] = "The song (and artist, if said), as the user said it." } },
                     "query"),
            Function(RestoreWindowsToolName,
                     "Bring minimized windows back. Without app: every window minimize_all minimized ('bring them back', 'restore my windows'). With app: that app's windows only, nothing else ('show just the terminal', 'bring back Chrome').",
                     new JsonObject { ["app"] = new JsonObject { ["type"] = "string", ["description"] = "Only when they named one app: its name, e.g. 'Terminal'. Never leave it out when they named one." } }),
        ];
    }

    /// <summary>
    /// The same quick actions as Anthropic tool definitions, for the ask lane wherever the agent
    /// lane does not run (and, through PlanToolProtocol, the plans). This is what
    /// AskLanePrompts.AskTools takes as its quickActionToolDefinitions.
    /// </summary>
    public static IReadOnlyList<JsonObject> AnthropicToolDefinitions() =>
        RealtimeToolDefinitions().Select(definition => new JsonObject
        {
            ["name"] = definition["name"]?.GetValue<string>() ?? "",
            ["description"] = definition["description"]?.GetValue<string>() ?? "",
            ["input_schema"] = definition["parameters"]?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        }).ToList();

    /// <summary>Every tool name <see cref="Parse(string, JsonObject?)"/> reads as a fast action (the alias included).</summary>
    public static readonly IReadOnlySet<string> ToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        OpenAppToolName, OpenUrlToolName, CreateFolderToolName, RevealInExplorerToolName, RevealInFinderToolName,
        SetVolumeToolName, MediaControlToolName, WebSearchToolName, MinimizeAllToolName, CheckEmailToolName, DraftEmailToolName,
        PlayMusicToolName, RestoreWindowsToolName,
    };

    /// <summary>
    /// Turns the Realtime tool call into a typed action. The model controls these values: nothing
    /// here trusts them beyond the enum and the validation in <see cref="DesktopActionValidation"/>.
    /// </summary>
    public static DesktopActionParseResult Parse(string toolName, JsonObject? arguments)
    {
        string StringArgument(string key) =>
            arguments?[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>().Trim() : "";
        // Locations are optional: the workspace is the only one that never needs a permission, so
        // an unspecified location goes there rather than to the Desktop.
        DesktopActionLocation? Location()
        {
            var raw = StringArgument("location");
            if (raw.Length == 0) return DesktopActionLocation.Workspace;
            return DesktopActionLocations.Named(raw);
        }

        switch (toolName)
        {
            case OpenAppToolName:
                return DesktopActionValidation.FolderName(StringArgument("name")) is { } appName
                    ? new DesktopActionParseResult.Action(new OpenApp(appName))
                    : new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.InvalidAppName());

            case OpenUrlToolName:
                return new DesktopActionParseResult.Action(new OpenUrl(StringArgument("url")));

            case CreateFolderToolName:
                return Location() is { } createLocation
                    ? new DesktopActionParseResult.Action(new CreateFolder(StringArgument("name"), createLocation))
                    : new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.UnknownLocation(StringArgument("location")));

            case RevealInExplorerToolName:
            case RevealInFinderToolName:
                return Location() is { } revealLocation
                    ? new DesktopActionParseResult.Action(new RevealInExplorer(StringArgument("name"), revealLocation))
                    : new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.UnknownLocation(StringArgument("location")));

            case SetVolumeToolName:
                if (VolumeLevel(arguments?["level"]) is { } level) return new DesktopActionParseResult.Action(new SetVolume(level));
                return new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.Failed(VolumeArgumentMessage));

            case MediaControlToolName:
                return new DesktopActionParseResult.Action(new MediaControl(StringArgument("action").ToLowerInvariant()));

            case WebSearchToolName:
            {
                if (DesktopActionValidation.SearchQuery(StringArgument("query")) is not { } query)
                {
                    return new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.InvalidQuery());
                }
                var rawSite = StringArgument("site");
                return (rawSite.Length == 0 ? WebSearchSite.Web : WebSearchSites.Named(rawSite)) is { } site
                    ? new DesktopActionParseResult.Action(new WebSearch(query, site))
                    : new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.InvalidQuery());
            }

            case MinimizeAllToolName:
            case RestoreWindowsToolName:
            {
                // An app name is only matched against running apps, never resolved to a path, but it
                // gets open_app's checks all the same.
                var rawAppName = StringArgument(toolName == MinimizeAllToolName ? "keep" : "app");
                string? windowAppName = null;
                if (rawAppName.Length > 0)
                {
                    if (DesktopActionValidation.FolderName(rawAppName) is not { } validName)
                    {
                        return new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.InvalidAppName());
                    }
                    windowAppName = validName;
                }
                return new DesktopActionParseResult.Action(toolName == MinimizeAllToolName ? new MinimizeAll(windowAppName) : new RestoreWindows(windowAppName));
            }

            case PlayMusicToolName:
                return DesktopActionValidation.SearchQuery(StringArgument("query")) is { } song
                    ? new DesktopActionParseResult.Action(new PlayMusic(song))
                    : new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.InvalidQuery());

            case CheckEmailToolName:
            {
                var app = StringArgument("app");
                return new DesktopActionParseResult.Action(new CheckEmail(app.Length == 0 ? null : app));
            }

            case DraftEmailToolName:
            {
                if (DesktopActionValidation.Recipient(StringArgument("to")) is not { } recipient)
                {
                    return new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.InvalidRecipient());
                }
                var body = DesktopActionValidation.EmailBody(StringArgument("body"));
                if (body.Length == 0) return new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.Failed("I need something to write"));
                var app = StringArgument("app");
                return new DesktopActionParseResult.Action(new DraftEmail(recipient, StringArgument("subject"), body, app.Length == 0 ? null : app));
            }

            default:
                return new DesktopActionParseResult.NotAFastAction();
        }
    }

    /// <summary>The same for a call whose arguments are all strings (a ClaudeToolCall, a plan's [ACTION: …] line).</summary>
    public static DesktopActionParseResult Parse(string toolName, IReadOnlyDictionary<string, string> arguments)
    {
        var json = new JsonObject();
        foreach (var (key, value) in arguments) json[key] = value;
        return Parse(toolName, json);
    }

    /// <summary>The same for the Realtime event's `arguments` text; unreadable JSON counts as no arguments.</summary>
    public static DesktopActionParseResult ParseJson(string toolName, string? argumentsJson)
    {
        JsonObject? arguments = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try { arguments = JsonNode.Parse(argumentsJson) as JsonObject; }
            catch (JsonException) { arguments = null; }
        }
        return Parse(toolName, arguments);
    }

    /// <summary>The Realtime model sends integers, but occasionally quotes them.</summary>
    private static int? VolumeLevel(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        switch (value.GetValueKind())
        {
            case JsonValueKind.Number:
                if (value.TryGetValue<int>(out var whole)) return whole;
                if (value.TryGetValue<double>(out var fractional) && !double.IsNaN(fractional))
                {
                    return (int)Math.Clamp(Math.Truncate(fractional), int.MinValue, int.MaxValue);
                }
                return null;
            case JsonValueKind.String:
                return int.TryParse(value.GetValue<string>().Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
            default:
                return null;
        }
    }
}

/// <summary>What the tool call turned out to be.</summary>
public abstract record DesktopActionParseResult
{
    private DesktopActionParseResult() { }

    public sealed record Action(DesktopAction Value) : DesktopActionParseResult;

    /// <summary>Not one of the fast-lane tools: the caller's existing handling applies.</summary>
    public sealed record NotAFastAction : DesktopActionParseResult;

    /// <summary>A fast-lane tool with arguments that cannot be acted on; the outcome carries what to say.</summary>
    public sealed record BadArguments(DesktopActionOutcome Outcome) : DesktopActionParseResult;
}

/// <summary>
/// Performs the fast-lane actions. The OS calls go through <see cref="IDesktopActionExecutor"/>, so
/// the whole runner can be tested against a temporary directory without launching apps or moving
/// the user's volume.
/// </summary>
public sealed class DesktopActionRunner
{
    /// <summary>"play" is here for when no player session took it: the media key is the next best thing.</summary>
    private static readonly string[] MediaActions = ["playpause", "next", "previous", "play"];

    private readonly IDesktopActionExecutor executor;
    private readonly string homeDirectory;
    private readonly Func<string> workspaceDirectory;
    private readonly bool usesKnownFolders;
    private readonly DesktopActionExtras extras;

    /// <param name="usesKnownFolders">
    /// True (the app) asks the executor for the shell's Desktop / Downloads / Documents paths, which
    /// follow a OneDrive redirection; false (tests) keeps them under <paramref name="homeDirectory"/>.
    /// </param>
    public DesktopActionRunner(IDesktopActionExecutor executor, string homeDirectory, string workspaceDirectory, bool usesKnownFolders = false,
                               DesktopActionExtras? extras = null)
        : this(executor, homeDirectory, () => workspaceDirectory, usesKnownFolders, extras)
    {
    }

    public DesktopActionRunner(IDesktopActionExecutor executor, string homeDirectory, Func<string> workspaceDirectory, bool usesKnownFolders = false,
                               DesktopActionExtras? extras = null)
    {
        this.executor = executor;
        this.homeDirectory = homeDirectory;
        this.workspaceDirectory = workspaceDirectory;
        this.usesKnownFolders = usesKnownFolders;
        this.extras = extras ?? DesktopActionExtras.Inert;
    }

    /// <summary>The music lookup's route is logged when it changes (own credentials added, say), not per call.</summary>
    private static string? lastLoggedMusicRoute;

    /// <summary>The runner the app uses: the real home folder, the configured workspace, the shell's known folders.</summary>
    public static DesktopActionRunner Live(IDesktopActionExecutor executor) =>
        new(executor, OpenClickyPaths.HomeDirectory, () => OpenClickyConfiguration.WorkspacePath, usesKnownFolders: true, new DesktopActionExtras
        {
            LookUpTrack = query =>
            {
                var route = MusicLookup.CurrentRoute();
                if (route.LogName != lastLoggedMusicRoute)
                {
                    lastLoggedMusicRoute = route.LogName;
                    AppLog.Write($"route: music lookup → {route.LogName}");
                }
                return new MusicLookup(route).SearchAsync(query);
            },
            ChooseEmailApp = requested => EmailApps.Choose(requested, OpenClickyConfiguration.Settings.EmailApp),
        });

    /// <summary>Where a location is on this PC.</summary>
    public string DirectoryPath(DesktopActionLocation location)
    {
        if (usesKnownFolders && location is DesktopActionLocation.Desktop or DesktopActionLocation.Downloads or DesktopActionLocation.Documents
            && executor.KnownFolderPath(location.RawValue()) is { Length: > 0 } knownFolder)
        {
            return knownFolder;
        }
        return location.DirectoryPath(homeDirectory, workspaceDirectory());
    }

    public Task<DesktopActionOutcome> PerformAsync(DesktopAction action) => action switch
    {
        DesktopAction.PlayMusic playMusic => PlayMusicAsync(playMusic.Query),
        DesktopAction.MediaControl { Action: "pause" or "play" } mediaControl => ControlPlayerAsync(mediaControl.Action),
        _ => Task.Run(() => Perform(action)),
    };

    /// <summary>
    /// "Stop that song" goes to the player itself: the play/pause key toggles, so with nothing playing
    /// it would start something. Play with no player session falls back to the key.
    /// </summary>
    private async Task<DesktopActionOutcome> ControlPlayerAsync(string command)
    {
        var playerTookIt = await executor.ControlPlayerAsync(command == "pause" ? PlayerCommand.Pause : PlayerCommand.Play).ConfigureAwait(false);
        if (playerTookIt) return new DesktopActionOutcome.PlayerControlled(command);
        if (command == "pause") return new DesktopActionOutcome.Failed("nothing is playing");
        executor.SendMediaKey("playpause");
        return new DesktopActionOutcome.MediaControlled(command);
    }

    private async Task<DesktopActionOutcome> PlayMusicAsync(string query)
    {
        if (!executor.SpotifyIsInstalled()) return new DesktopActionOutcome.SpotifyNotInstalled();
        if (!executor.SpotifyIsRunning() && !await executor.LaunchSpotifyAsync().ConfigureAwait(false)) return new DesktopActionOutcome.SpotifyStarting();
        var tracks = await extras.LookUpTrack(query).ConfigureAwait(false);
        // No lookup answered, or it answered with something that is not a track (the backend is not
        // trusted with what Spotify is told to open): Spotify's own search leaves the person one click away.
        if (MusicTrackPicker.Pick(tracks, query) is not { } track || !track.Uri.StartsWith("spotify:track:", StringComparison.Ordinal))
        {
            executor.OpenSpotifyUri(MusicLookup.SpotifySearchUri(query));
            return new DesktopActionOutcome.OpenedSpotifySearch(query);
        }
        var artist = track.Artists.FirstOrDefault() ?? "";
        return await executor.PlaySpotifyTrackAsync(track.Uri, track.Name).ConfigureAwait(false) switch
        {
            SpotifyPlayResult.Playing => new DesktopActionOutcome.PlayingTrack(track.Name, artist),
            SpotifyPlayResult.Opened => new DesktopActionOutcome.OpenedTrack(track.Name, artist),
            _ => new DesktopActionOutcome.PlayFailed(),
        };
    }

    public DesktopActionOutcome Perform(DesktopAction action)
    {
        switch (action)
        {
            case DesktopAction.OpenApp openApp:
            {
                if (executor.FindApplication(openApp.Name) is not { } application) return new DesktopActionOutcome.AppNotFound(openApp.Name);
                if (!executor.LaunchApplication(application)) return new DesktopActionOutcome.Failed($"{openApp.Name} didn't start");
                return new DesktopActionOutcome.OpenedApp(openApp.Name);
            }

            case DesktopAction.OpenUrl openUrl:
            {
                if (DesktopActionValidation.WebUrl(openUrl.Raw) is not { } url) return new DesktopActionOutcome.InvalidUrl();
                if (!executor.OpenUrl(url)) return new DesktopActionOutcome.Failed("the browser didn't open");
                return new DesktopActionOutcome.OpenedUrl(openUrl.Raw.Trim());
            }

            case DesktopAction.CreateFolder createFolder:
            {
                if (DesktopActionValidation.FolderName(createFolder.Name) is not { } name) return new DesktopActionOutcome.InvalidName();
                var target = Path.Combine(DirectoryPath(createFolder.Location), name);
                if (Directory.Exists(target) || File.Exists(target))
                {
                    return new DesktopActionOutcome.FolderAlreadyExists(name, createFolder.Location);
                }
                try
                {
                    Directory.CreateDirectory(target);
                    return new DesktopActionOutcome.CreatedFolder(name, createFolder.Location);
                }
                catch (UnauthorizedAccessException)
                {
                    // Controlled folder access (and an ACL) answers with access denied.
                    return new DesktopActionOutcome.PermissionDenied(createFolder.Location);
                }
                catch (IOException exception)
                {
                    return new DesktopActionOutcome.Failed(exception.Message.TrimEnd('.', ' '));
                }
            }

            case DesktopAction.RevealInExplorer reveal:
            {
                if (DesktopActionValidation.FolderName(reveal.Name) is not { } name) return new DesktopActionOutcome.InvalidName();
                var target = Path.Combine(DirectoryPath(reveal.Location), name);
                if (!Directory.Exists(target) && !File.Exists(target))
                {
                    return new DesktopActionOutcome.NothingToReveal(name, reveal.Location);
                }
                if (!executor.RevealInExplorer(target)) return new DesktopActionOutcome.Failed("File Explorer didn't open");
                return new DesktopActionOutcome.Revealed(name, reveal.Location);
            }

            case DesktopAction.SetVolume setVolume:
            {
                var level = Math.Min(100, Math.Max(0, setVolume.Level));
                executor.SetVolume(level);
                return new DesktopActionOutcome.VolumeSet(level);
            }

            case DesktopAction.MediaControl mediaControl:
            {
                if (!MediaActions.Contains(mediaControl.Action))
                {
                    return new DesktopActionOutcome.Failed($"I don't know how to {mediaControl.Action}");
                }
                // Pause and play normally go to the player (PerformAsync); here only as the key.
                if (mediaControl.Action == "pause") return new DesktopActionOutcome.Failed("nothing is playing");
                executor.SendMediaKey(mediaControl.Action == "play" ? "playpause" : mediaControl.Action);
                return new DesktopActionOutcome.MediaControlled(mediaControl.Action);
            }

            case DesktopAction.WebSearch webSearch:
            {
                if (!executor.OpenUrl(webSearch.Site.Url(webSearch.Query))) return new DesktopActionOutcome.Failed("the browser didn't open");
                return new DesktopActionOutcome.Searched(webSearch.Query, webSearch.Site);
            }

            case DesktopAction.MinimizeAll minimizeAll:
                return executor.MinimizeAllWindows(minimizeAll.KeepApp) is { } minimized
                    ? new DesktopActionOutcome.MinimizedWindows(minimized, minimizeAll.KeepApp)
                    : new DesktopActionOutcome.WindowAppNotRunning(minimizeAll.KeepApp ?? "");

            case DesktopAction.RestoreWindows restoreWindows:
                return executor.RestoreMinimizedWindows(restoreWindows.App) is { } restored
                    ? new DesktopActionOutcome.RestoredWindows(restored, restoreWindows.App)
                    : new DesktopActionOutcome.WindowAppNotRunning(restoreWindows.App ?? "");

            case DesktopAction.CheckEmail checkEmail:
            {
                var app = extras.ChooseEmailApp(checkEmail.App);
                if (!executor.OpenUrl(app.InboxUrl())) return new DesktopActionOutcome.Failed("the browser didn't open");
                return new DesktopActionOutcome.OpenedInbox(app);
            }

            case DesktopAction.DraftEmail draftEmail:
            {
                if (DesktopActionValidation.Recipient(draftEmail.To) is not { } recipient) return new DesktopActionOutcome.InvalidRecipient();
                var subject = DesktopActionValidation.EmailSubject(draftEmail.Subject);
                var body = DesktopActionValidation.EmailBody(draftEmail.Body);
                if (body.Length == 0) return new DesktopActionOutcome.Failed("I need something to write");
                var app = extras.ChooseEmailApp(draftEmail.App);
                if (!executor.OpenUrl(app.ComposeUrl(recipient, subject, body))) return new DesktopActionOutcome.Failed("the browser didn't open");
                return new DesktopActionOutcome.DraftedEmail(app, recipient, body.Length);
            }

            default:
                return new DesktopActionOutcome.Failed("I don't know that action");
        }
    }

    /// <summary>
    /// Parses one tool call and performs it, logging it the Mac way. Null when the call is not a fast
    /// action (the caller's own handling applies); otherwise the outcome whose sentence goes back
    /// to the model.
    /// </summary>
    public async Task<DesktopActionOutcome?> PerformToolCallAsync(string toolName, JsonObject? arguments)
    {
        switch (DesktopAction.Parse(toolName, arguments))
        {
            case DesktopActionParseResult.Action parsed:
            {
                var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                var outcome = await PerformAsync(parsed.Value).ConfigureAwait(false);
                AppLog.Write(DesktopActionLog.Performed(toolName, outcome, System.Diagnostics.Stopwatch.GetElapsedTime(startedAt)));
                return outcome;
            }
            case DesktopActionParseResult.BadArguments bad:
                AppLog.Write(DesktopActionLog.Rejected(toolName, bad.Outcome));
                return bad.Outcome;
            default:
                return null;
        }
    }
}

/// <summary>
/// What the newer actions need besides the OS (the Mac's MacActionRunner.Extras): the song lookup and
/// the inbox choice, both read from the settings at the time of the call. Defaults do nothing, so a
/// test passes only what it drives.
/// </summary>
public sealed record DesktopActionExtras
{
    /// <summary>The catalogue search for play_music; empty when nothing answered.</summary>
    public Func<string, Task<IReadOnlyList<MusicTrack>>> LookUpTrack { get; init; } = _ => Task.FromResult<IReadOnlyList<MusicTrack>>([]);

    /// <summary>Which inbox a check or draft goes to, given the one the person named (or null).</summary>
    public Func<string?, EmailApp> ChooseEmailApp { get; init; } = requested => EmailApps.Choose(requested, setting: null);

    public static DesktopActionExtras Inert { get; } = new();
}

/// <summary>
/// The log lines for a fast action. The prefix stays `mac action:` on Windows on purpose: the
/// measure script (macos/OpenClicky/scripts/measure-actions.sh) greps for
/// `mac action: &lt;tool&gt; … in &lt;seconds&gt; s`.
/// </summary>
public static class DesktopActionLog
{
    public const string Prefix = "mac action:";

    /// <summary>"mac action: open_app Opened Spotify. in 0.42 s"</summary>
    public static string Performed(string toolName, DesktopActionOutcome outcome, TimeSpan elapsed) =>
        $"{Prefix} {toolName} {outcome.LogDescription} in {elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s";

    /// <summary>"mac action: create_folder rejected, I don't know where icloud is."</summary>
    public static string Rejected(string toolName, DesktopActionOutcome outcome) =>
        $"{Prefix} {toolName} rejected, {outcome.SpokenSentence}";
}
