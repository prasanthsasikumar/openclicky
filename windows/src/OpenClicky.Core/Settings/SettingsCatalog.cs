// Ports Dictation/UI/SettingsCatalog.swift and the `*Items()` halves of every Settings*Page.swift:
// every setting as a SettingsItem, in page order, then the search-only ways to your own pages.
// The rows themselves are drawn by the Windows window for each item id. Words that named a Mac
// thing say the Windows thing ("this PC", "the tray", "right Ctrl"); the fn rows, the Emoji guard,
// haptics and the three Mac-only permissions are gone (windows/PORTING.md).

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Plugins;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Shortcuts;

namespace OpenClicky.Core.Settings;

/// <summary>Every setting on every page, built against the live settings so relevance follows them.</summary>
public sealed class SettingsCatalog
{
    private readonly DictationSettings settings;
    private readonly Func<OpenClickyShellSettings> shellSettings;
    private readonly Func<AccountCapabilities> capabilities;

    public SettingsCatalog(DictationSettings settings, Func<OpenClickyShellSettings>? shellSettings = null, Func<AccountCapabilities>? capabilities = null)
    {
        this.settings = settings;
        this.shellSettings = shellSettings ?? (() => OpenClickyConfiguration.Settings);
        this.capabilities = capabilities ?? (() => AccountCapabilities.Current());
    }

    /// <summary>Every row, placed and ordered by <see cref="SettingsLayout"/>, then the search-only ways to your own pages.</summary>
    public IReadOnlyList<SettingsItem> AllItems =>
        [.. SettingsLayout.Arrange(UnplacedItems()), .. YourPagesSearchItems()];

    /// <summary>Every row as its builder makes it, before the layout gives it a page and a place.</summary>
    private IReadOnlyList<SettingsItem> UnplacedItems() =>
    [
        .. DictationItems(), .. KeyboardItems(), .. OrbItems(), .. ClickyItems(), .. VoiceItems(), .. PluginsItems(), .. IslandItems(),
        .. AccountItems(), .. KeysItems(), .. PrivacyItems(), .. GeneralItems(), .. AboutItems(), .. NavigationItems(),
    ];

    /// <summary>The items that apply now.</summary>
    public IReadOnlyList<SettingsItem> RelevantItems => AllItems.Where(item => item.IsRelevant()).ToList();

    /// <summary>What a settings page shows: its relevant items, without the ones only search finds.</summary>
    public IReadOnlyList<SettingsItem> PageItems(DictationSettingsPage page) =>
        AllItems.Where(item => item.Page == page && item.ShowsOnPage && item.SearchOnlyGroup is null && item.IsRelevant()).ToList();

    /// <summary>The relevant items a (trimmed) query matches; none for an empty query.</summary>
    public IReadOnlyList<SettingsItem> SearchMatches(string query)
    {
        var trimmed = query.Trim();
        return trimmed.Length == 0 ? [] : RelevantItems.Where(item => item.Matches(trimmed)).ToList();
    }

    /// <summary>
    /// The rows that open a page of their own: Your words, How it writes and The orb from Dictation,
    /// Calendars from Island.
    /// </summary>
    public IReadOnlyList<SettingsItem> NavigationItems()
    {
        SettingsItem Item(string id, string title, string detail, string[] keywords, DictationDestination destination) => new()
        {
            Id = id, Title = title, Detail = detail, Keywords = keywords, OpensDestination = destination,
        };
        return
        [
            Item("dictation.openWords", "Your words", "Every take, with search. Try dictation here.",
                ["history", "takes", "try"], DictationDestination.Of(DictationSection.Words)),
            Item("dictation.openWriting", "How it writes", "Dictionary, snippets and per-app styles.",
                ["dictionary", "snippets", "styles"], DictationDestination.Of(DictationSection.Writing)),
            Item("dictation.openOrb", "The orb", "Style, theme, size and when it appears.",
                ["orb", "pill", "look", "theme", "size"], DictationDestination.Of(DictationSettingsPage.Orb)),
            Item("island.openCalendars", "Calendars", "Which calendars the island shows.",
                ["calendar", "calendars", "hide", "show", "account"], DictationDestination.Of(DictationSettingsPage.Calendars)),
        ];
    }

    /// <summary>What lives on Your words and How it writes, findable by search: each a row that opens its page.</summary>
    public IReadOnlyList<SettingsItem> YourPagesSearchItems()
    {
        SettingsItem Item(string id, DictationSection section, string title, string detail, string[] keywords) => new()
        {
            Id = id, Page = DictationSettingsPage.Dictation, Section = section.SidebarTitle(), Title = title, Detail = detail,
            Keywords = keywords, SearchOnlyGroup = section.SidebarTitle(), OpensSection = section, ShowsOnPage = false,
        };
        return
        [
            Item("words.takes", DictationSection.Words, "Your takes", "Every take you dictated, with search and filters.",
                ["history", "takes", "transcript", "past", "record", "recording", "retry", "dictated"]),
            Item("words.tryIt", DictationSection.Words, "Try it here", "A box to dictate into and see what OpenClicky writes.",
                ["test", "practice", "try", "scratch"]),
            Item("writing.dictionary", DictationSection.Writing, "Dictionary", "Names and terms written exactly as you expect.",
                ["dictionary", "terms", "names", "spelling", "vocabulary", "words", "misheard"]),
            Item("writing.snippets", DictationSection.Writing, "Snippets", "Say a short phrase, get a longer text.",
                ["snippet", "shortcut", "text expansion", "template", "signature", "address", "email"]),
            Item("writing.styles", DictationSection.Writing, "Styles", "How you sound, app by app: the rules the AI clean-up reads.",
                ["style", "tone", "formal", "casual", "per app", "rules", "apps"]),
        ];
    }

    // MARK: - General (SettingsGeneralPage.swift)

    public IReadOnlyList<SettingsItem> GeneralItems()
    {
        var dictationKeyName = settings.DictationKey.KeycapLabel();
        SettingsItem Item(string id, string title, string detail, string[] keywords) => new()
        {
            Id = id, Title = title, Detail = detail, Keywords = keywords,
        };
        return
        [
            Item("general.appearance", "Appearance", "This window, the orb and its transcript box follow this. The island stays black.",
                ["light", "dark", "auto", "match windows", "theme", "mode", "colour", "color"]),
            Item("general.hudPlacement", "Where it lives", "The top of the screen, a drawer on the left or right edge, or hidden until you use a shortcut.",
                ["top", "notch", "island", "hud", "side", "edge", "drawer", "hide", "hidden", "position", "placement", "shortcuts only"]),
            Item("general.trayIcon", "Show in the tray", "Off is fine: the island is OpenClicky’s home.",
                ["tray", "notification area", "system tray", "icon", "taskbar", "menu bar"]),
            Item("general.appIcon", "App icon", "Its colour on this window, the taskbar and the tray. The Start menu keeps the red one.",
                ["icon", "colour", "color", "taskbar", "tray", "app icon", "signal", "ultraviolet", "sunset", "paper", "aqua", "purple", "blue", "pink", "red"]),
            Item("general.openAtLogin", "Open at sign-in", $"So {dictationKeyName} always works.",
                ["startup", "launch", "restart", "login", "log in", "sign in", "boot"]),
            Item("general.walkthrough", "Welcome tour", "Clicky shows you dictating, asking and handing over a task, from the island.",
                ["onboarding", "setup", "replay", "tour", "first run"]),
            Item("general.settingsTour", "Settings tour", "A one-minute look around this window: Clicky points at what matters most.",
                ["tour", "crash course", "show me around", "help", "guide", "replay", "walkthrough"]),
            Item("general.reduceMotion", "Reduce motion", "The orb and the island appear and leave without animating.",
                ["animation", "motion", "accessibility", "calm"]),
            Item("about.autoUpdate", "Check for updates daily", "Updates come from OpenClicky’s GitHub releases.",
                ["update", "updates", "automatic", "velopack", "version"]),
        ];
    }

    // MARK: - Dictation (SettingsDictationPage.swift, the key rows of SettingsKeyboardPage.swift)

    /// <summary>
    /// "Hold to talk anywhere…" plus what a right-hand key changes: the left twin keeps the
    /// companion's jobs, and a key typed while it is held makes the hold a shortcut, not a take.
    /// </summary>
    public static string DictationKeyDetail(DictationHotkey key)
    {
        var rightHandKeyNote = key switch
        {
            DictationHotkey.RightControl => " Edits, hands-free and Ctrl twice use the left Ctrl.",
            DictationHotkey.LeftControl => " Edits, hands-free and Ctrl twice use the right Ctrl.",
            DictationHotkey.RightAlt => " A key typed with right Alt held (é, €) cancels the take.",
            DictationHotkey.RightShift => " A key typed with right Shift held (a capital) cancels the take.",
            _ => "",
        };
        return "Hold to talk anywhere." + rightHandKeyNote;
    }

    public IReadOnlyList<SettingsItem> DictationKeyItems() =>
    [
        new()
        {
            Id = "keyboard.dictationKey",
            Title = "Dictation key", Detail = DictationKeyDetail(settings.DictationKey),
            Keywords = ["hotkey", "push to talk", "right control", "right ctrl", "right alt", "altgr", "right shift", "caps lock", "dictate", "shortcut"],
        },
        new()
        {
            Id = "keyboard.tapKeepsListening",
            Title = "Tap to keep listening",
            Detail = "Off: hold the key while you talk, as on the Mac. On: a quick tap starts a take and the next tap finishes it.",
            Keywords = ["tap", "toggle", "hold", "push to talk", "latch", "keep listening"],
        },
    ];

    public IReadOnlyList<SettingsItem> DictationItems()
    {
        var engineItems = DictationEngineChoices.AllCases.Select(choice => new SettingsItem
        {
            Id = $"dictation.engine.{choice.Id()}",
            Title = choice.SettingsTitle(), Detail = choice.SettingsDetail(), Keywords = choice.SearchKeywords(),
            // Sarvam hears Indian languages: offered only where it is already chosen (AppRegion).
            IsRelevant = () => choice != DictationEngineChoice.Sarvam || settings.Region.OffersIndianOptions() || settings.Engine == DictationEngineChoice.Sarvam,
        });
        SettingsItem Item(string id, string title, string detail, string[] keywords, Func<bool>? isRelevant = null) => new()
        {
            Id = id, Title = title, Detail = detail, Keywords = keywords,
            IsRelevant = isRelevant ?? (() => true),
        };
        return
        [
            .. DictationKeyItems(),
            .. engineItems,
            Item("dictation.language", "Language", "Pins every engine to one language, or lets it detect.",
                ["language", "english", "hindi", "tamil", "spanish", "french", "german", "pin", "auto-detect", "detect"]),
            // Only while the words are pinned to an Indian language (or, in India, could be one).
            Item("dictation.script", "Script for Indian languages", "Same words, written two ways.",
                ["script", "roman", "native", "devanagari", "hinglish", "transliteration"],
                () => settings.Language.IsIndian || (settings.Region.OffersIndianOptions() && settings.Language == DictationLanguage.Auto)),
            Item("dictation.cleanUp", "Clean up my words with AI", "Adds punctuation and fixes numbers.",
                ["polish", "clean up", "cleanup", "clean-up", "ai", "punctuation", "grammar", "model", "offline takes"]),
            Item("dictation.input", "Input", "Automatic follows Windows’ default microphone. Clicky listens through it too.",
                ["microphone", "mic", "device", "input", "headset", "headphones"]),
            Item("dictation.micTest", "Test your microphone", "Hear whether the chosen input picks you up.",
                ["microphone", "mic", "level", "test"]),
            Item("dictation.inactivityTimeout", "End a quiet take after", "A tapped take finishes on its own after this much silence.",
                ["inactivity", "timeout", "silence", "minutes", "quiet"]),
            Item("dictation.pasteLast", "Paste last take", "Put your last take at the cursor again.",
                ["paste", "again", "re-insert", "last"]),
            Item("dictation.sounds", "Sounds", "Tones when a take starts, stops or finishes.",
                ["sound", "tone", "earcon", "audio", "beep", "mute", "quiet"]),
        ];
    }

    // MARK: - Clicky and Voice (SettingsClickyPage.swift)

    public IReadOnlyList<SettingsItem> ClickyItems()
    {
        SettingsItem Item(string id, string title, string detail, string[] keywords) => new()
        {
            Id = id, Title = title, Detail = detail, Keywords = keywords,
        };
        return
        [
            Item("clicky.answerSource", "Answers come from", "Automatic uses your own keys first, then plans you switched on, then the free credits.",
                ["provider", "model", "openai", "anthropic", "claude", "openrouter", "gemini", "groq", "mistral", "grok", "codex", "subscription", "plan", "answers"]),
            Item("clicky.model", "Claude model", "When Claude answers: Sonnet is quicker, Opus thinks harder.",
                ["model", "claude", "sonnet", "opus", "smarter", "quicker"]),
            Item("clicky.pointer", "Show the pointer", "It flies to what Clicky means.",
                ["pointer", "cursor", "buddy", "orange", "visible", "hide", "always", "while the mouse moves", "after a shake", "shake", "while i talk"]),
            Item("clicky.agentMode", "Agent mode", "Clicky can hand work to Codex.",
                ["agent", "codex", "tasks", "automation"]),
            Item("clicky.workspace", "Workspace", "Where agents keep their files.",
                ["workspace", "folder", "codex", "files", "directory"]),
        ];
    }

    /// <summary>"Always listening": instant on fast voice, else after the person stops talking.</summary>
    public static string HandsFreeDetail(bool usesRealtime) => usesRealtime
        ? "Hands-free: just talk, no keys. Instant with fast voice on."
        : "Hands-free: just talk, no keys. Clicky answers once you stop talking.";

    public const string FastVoiceNeedsKeyDetail = "Instant replies need your own OpenAI or Gemini key. Without one, Clicky answers in the standard voice.";

    public IReadOnlyList<SettingsItem> VoiceItems()
    {
        var usesRealtime = capabilities().UsesRealtime;
        SettingsItem Item(string id, string section, string title, string detail, string[] keywords) => new()
        {
            Id = id, Section = section, Title = title, Detail = detail, Keywords = keywords,
        };
        List<SettingsItem> items =
        [
            Item("clicky.language", "Language", "Language", "Clicky speaks and listens in this.",
                ["language", "english", "hindi", "tamil", "spanish", "french", "speak", "reply", "listen"]),
            Item("clicky.speechVoice", "Voice", "Voice", "Who speaks the answers: the cloud voice, or this PC’s own voice, free and offline.",
                ["voice", "speech", "tts", "text to speech", "elevenlabs", "eleven labs", "kokoro", "offline", "on device",
                 "pc voice", "windows voice", "narrator", "credits", "allowance", "left"]),
        ];
        // Fast voice runs on OpenAI Realtime or Gemini Live, which need an own OpenAI or Gemini key; without one
        // its switch would do nothing, so the row shows the way to a key instead.
        if (usesRealtime)
        {
            items.Add(Item("clicky.fastVoice", "Fast voice", "Fast voice", "Talks back instantly, over OpenAI Realtime or Gemini Live.",
                ["realtime", "voice", "fast", "openai", "gemini", "live"]));
        }
        // Hands-free works on every lane that can answer: instantly with fast voice, otherwise the standard lane
        // hears each turn on this PC once the person stops talking.
        if (usesRealtime || OpenClickyConfiguration.IsConfiguredFor(shellSettings()))
        {
            items.Add(Item("clicky.alwaysListening", "Fast voice", "Always listening", HandsFreeDetail(usesRealtime),
                ["hands-free", "hands free", "always on", "listening"]));
        }
        if (usesRealtime)
        {
            // Only worth a choice when both can run here.
            var credentials = ProviderCredentials.FromSettings(shellSettings());
            if (credentials.GeminiKey is not null && (credentials.OpenAIKey is not null || credentials.AccountToken is not null))
            {
                items.Add(Item("clicky.realtimeProvider", "Fast voice", "Fast voice with", RealtimeProviderDetail,
                    ["realtime", "gemini", "openai", "live", "provider", "fast voice", "hands-free"]));
            }
        }
        else
        {
            items.Add(Item("clicky.fastVoiceNeedsKey", "Fast voice", "Fast voice", FastVoiceNeedsKeyDetail,
                ["realtime", "voice", "fast", "openai", "gemini", "key"]));
        }
        // "Check my email" and "draft an email": which inbox they go to.
        items.Add(Item("clicky.emailApp", "Doing things", "Email", EmailAppDetail,
            ["email", "mail", "gmail", "outlook", "inbox", "draft"]));
        items.Add(new SettingsItem
        {
            Id = "clicky.emailNeverSent", Section = "Doing things", Chrome = SettingsItemChrome.Footer,
            Title = EmailNeverSentText, Detail = EmailNeverSentText, Keywords = ["send", "email"],
        });
        return items;
    }

    public const string EmailAppDetail = "Which inbox “check my email” and “draft an email” open. Automatic is Gmail.";
    public const string EmailNeverSentText = "Nothing is ever sent by voice.";

    // MARK: - Plugins (SettingsPluginsPage.swift)

    /// <summary>A row with a switch per plugin, then how the island picks between its components.</summary>
    public IReadOnlyList<SettingsItem> PluginsItems()
    {
        var pluginRows = IslandPluginRegistry.All.Select(plugin => new SettingsItem
        {
            Id = $"plugins.{plugin.Id}", Section = "",
            Title = plugin.Title, Detail = plugin.SettingsDetail ?? "",
            Keywords = ["plugin", "plugins", "island", "extra", plugin.Title.ToLowerInvariant(), plugin.Id],
        });
        return
        [
            .. pluginRows,
            new SettingsItem
            {
                Id = "plugins.islandDefault", Section = "On the island",
                Title = "Default component", Detail = "What the open island shows when nothing else is going on.",
                Keywords = ["island", "component", "panel", "shortcuts", "default", "slot"],
            },
            new SettingsItem
            {
                Id = "plugins.islandOrder", Section = "On the island",
                Title = "Component order", Detail = "The order of the island's buttons when there is more than one to flip to.",
                Keywords = ["island", "component", "order", "capsule", "arrange", "reorder"],
            },
        ];
    }

    // MARK: - Island (SettingsIslandPages.swift and the plugin folders' settings)

    /// <summary>The island's own rows: how it looks and opens, its home tiles, the timer's lengths, and its calendars.</summary>
    public IReadOnlyList<SettingsItem> IslandItems()
    {
        SettingsItem Item(string id, string title, string detail, string[] keywords) => new()
        {
            Id = id, Title = title, Detail = detail, Keywords = keywords,
        };
        return
        [
            Item("appearance.peekDirection", "When you hover it", "Sideways keeps a thin strip along the top edge; downwards drops it open a little.",
                ["hover", "peek", "sideways", "downwards", "grow", "direction", "island"]),
            Item("appearance.style", "Style", "Liquid glass, blend or black.",
                ["style", "glass", "liquid", "blend", "black", "look", "island", "transparent"]),
            Item("calendar.enabled", "Calendar", "Today and the week on the island's home.",
                ["calendar", "events", "meetings", "agenda", "outlook", "week", "today"]),
            Item("weather.enabled", "Weather", "Now and the next hours, from Open-Meteo.",
                ["weather", "temperature", "forecast", "rain", "open-meteo"]),
            Item("weather.unit", "Temperature", "Celsius or Fahrenheit.", ["celsius", "fahrenheit", "unit", "temperature", "weather"]),
            Item("weather.location", "Weather location", "Where Windows says you are, or a place you type.",
                ["location", "city", "place", "town", "weather", "gps"]),
            Item("timer.focusMinutes", "Focus", "How long a focus round lasts.", ["timer", "focus", "pomodoro", "minutes"]),
            Item("timer.breakMinutes", "Break", "The short break between rounds.", ["timer", "break", "pomodoro", "minutes"]),
            Item("timer.longBreakMinutes", "Long break", "After every fourth round.", ["timer", "long break", "pomodoro", "minutes"]),
            Item("calendar.access", "Calendar", "Lets the island read your calendars. Nothing is changed or sent.",
                ["calendar", "permission", "privacy", "access", "allow"]),
            Item("calendar.calendars", "Calendars", "Switch off a calendar to keep it off the island.",
                ["calendar", "calendars", "hide", "show", "account", "outlook"]),
        ];
    }

    public const string RealtimeProviderDetail = "Who talks back in fast voice and hands-free. Automatic is OpenAI, whose pointing is tuned best.";

    /// <summary>The fast voice picker's choices (RealtimeProviderRow on the Mac): automatic, then each vendor.</summary>
    public static IReadOnlyList<(string Id, string Title)> RealtimeProviderChoices { get; } =
        [("auto", "Automatic"), ("openai", "OpenAI Realtime"), ("gemini", "Gemini Live")];

    /// <summary>The picker's choice for a saved `realtimeProvider`; anything unknown shows as automatic.</summary>
    public static string RealtimeProviderChoice(string? setting) =>
        RawValues.FromRawValue<Speech.RealtimeVoiceProvider>(setting) is { } provider ? provider.RawValue() : "auto";

    // MARK: - The Orb (SettingsOrbPage.swift)

    public IReadOnlyList<SettingsItem> OrbItems()
    {
        var dictationKeyName = settings.DictationKey.KeycapLabel();
        SettingsItem Item(string id, string section, string title, string detail, string[] keywords) => new()
        {
            Id = id, Section = section, Title = title, Detail = detail, Keywords = keywords,
        };
        return
        [
            Item("orb.visible", "", "Show the orb", $"Off: {dictationKeyName} still works everywhere, just without narration.",
                ["orb", "pill", "hide", "visible"]),
            Item("orb.look", "Look", "Style", "Pill, classic or pixel.", ["pill", "classic", "pixel", "style", "shape", "look"]),
            Item("orb.theme", "Look", "Theme", "Black, coral or mist.", ["colour", "color", "black", "coral", "mist"]),
            Item("orb.size", "Look", "Size", "Full or mini.", ["full", "mini", "small", "big"]),
            Item("orb.hidesWhenIdle", "When it appears", "Hide when idle", "Appears when a take starts, leaves when it’s done.",
                ["hide", "idle", "not in use"]),
            Item("orb.restsExpanded", "When it appears", "Rest with the box open", "Keeps the transcript box showing between takes.",
                ["box", "transcript", "expanded"]),
            Item("orb.opensBoxWhenPasteUnverified", "When it appears", "Open the box if a paste can’t be confirmed",
                "So words never vanish when an app ignores the paste.", ["paste", "visibility", "box", "unverified", "confirm"]),
            Item("orb.isDraggable", "When it appears", "Free to drag", "Off pins the orb at the bottom centre.",
                ["drag", "move", "pin", "position", "center", "centre", "bottom"]),
            Item("orb.tooltips", "When it appears", "Tooltips", "A short hint under the orb.", ["hint", "help"]),
        ];
    }

    // MARK: - Shortcuts (SettingsKeyboardPage.swift)

    /// <summary>One keycap per key and the gesture beside them, for a shortcut row.</summary>
    public sealed record ShortcutKeys(IReadOnlyList<string> Keys, string? Gesture);

    /// <summary>The keycaps each Shortcuts row shows (the Windows keys from windows/PORTING.md).</summary>
    public static ShortcutKeys KeysFor(string itemId, DictationHotkey dictationKey)
    {
        var key = dictationKey.KeycapLabel();
        return itemId switch
        {
            "keyboard.dictate" => new([key], "hold"),
            "keyboard.edit" => new([key, "Ctrl"], "hold"),
            "keyboard.cancel" => new(["Esc"], $"or {key} twice"),
            "keyboard.talk" => new(dictationKey.TalkKeycapLabels(), "hold"),
            "keyboard.type" => new(["Ctrl"], "tap twice"),
            "keyboard.handsFree" => new([key, "Ctrl"], "tap twice"),
            _ => new([], null),
        };
    }

    public IReadOnlyList<SettingsItem> KeyboardItems()
    {
        var usesRealtime = capabilities().UsesRealtime;
        SettingsItem Item(string id, string section, string title, string detail, string[] keywords, Func<bool>? isRelevant = null) => new()
        {
            Id = id, Section = section, Title = title, Detail = detail, Keywords = keywords,
            IsRelevant = isRelevant ?? (() => true),
        };
        return
        [
            Item("keyboard.edit", "Dictation", "Edit selected text", "Hold and say the change, like “make it formal”.",
                ["hey clicky", "edit", "rewrite", "control", "ctrl", "selection"]),
            Item("keyboard.cancel", "Dictation", "Cancel a take", "Nothing is pasted.",
                ["esc", "escape", "discard", "stop", "double-tap"]),
            Item("keyboard.talk", "Clicky", "Talk to Clicky", "Ask about anything on screen. It answers out loud and points.",
                ["ctrl", "win", "windows key", "control windows", "voice", "ask", "companion"]),
            Item("keyboard.type", "Clicky", "Type to Clicky", "A one-line composer opens in the island.",
                ["ctrl", "composer", "island", "text", "control twice"]),
            // Hands-free runs on fast voice or, without a realtime key, on the standard lane; with nothing that
            // can answer the keys would do nothing, so it isn't listed.
            Item("keyboard.handsFree", "Clicky", "Hands-free", "Always-on listening. Same keys to stop.",
                ["always on", "realtime", "listening", "hands free"], () => usesRealtime || OpenClickyConfiguration.IsConfiguredFor(shellSettings())),
        ];
    }

    // MARK: - Keys & Account, Account (SettingsAccountPage.swift)

    public IReadOnlyList<SettingsItem> KeysItems() =>
    [
        new()
        {
            Id = "account.keys", Section = "Your keys",
            Title = "Your keys", Detail = "Keys from AI providers (they answer, clean up and speak) and voice services.",
            Keywords = ["api key", "byok", "own key", "provider", "add key", "openai", "openaiApiKey", "anthropic", "claude",
                        "anthropicApiKey", "openrouter", "llama", "deepseek", "gemini", "google", "ai studio", "groq",
                        "mistral", "xai", "grok", "sarvam", "saaras", "elevenlabs", "polly", "google cloud", "voice", "test"],
        },
        new()
        {
            Id = "account.keysNote", Section = "Your keys", Chrome = SettingsItemChrome.Footer,
            Title = "Where keys go", Detail = KeysNoteText, Keywords = ["shell.json", "byok"],
        },
        new()
        {
            Id = "account.claudeCodePlan", Section = "Subscriptions",
            Title = "Claude Code (your Claude plan)", Detail = "Answer questions through Claude Code, signed in to your Claude plan.",
            Keywords = ["claude code", "claude plan", "pro", "max", "subscription", "anthropic"],
        },
        new()
        {
            Id = "account.codexPlan", Section = "Subscriptions",
            Title = "Codex (your ChatGPT plan)", Detail = "Answer questions through Codex, signed in with ChatGPT.",
            Keywords = ["codex", "chatgpt", "plus", "pro", "subscription", "openai"],
        },
        new()
        {
            Id = "account.plansNote", Section = "Subscriptions", Chrome = SettingsItemChrome.Footer,
            Title = "What a plan can do", Detail = PlansNoteText, Keywords = ["subscription", "plan"],
        },
    ];

    public const string KeysNoteText = "Your keys go straight to that provider, never through OpenClicky, and are used instead of the free credits. Stored in %USERPROFILE%\\.openclicky\\shell.json.";

    public const string PlansNoteText = "A plan answers questions, points, and does quick actions like opening apps or making folders. Bigger tasks need the agent. Codex answers in one go, a few seconds after you ask.";

    public IReadOnlyList<SettingsItem> AccountItems()
    {
        SettingsItem Item(string id, string section, string title, string detail, string[] keywords, Func<bool>? isRelevant = null) => new()
        {
            Id = id, Section = section, Title = title, Detail = detail, Keywords = keywords,
            IsRelevant = isRelevant ?? (() => true),
        };
        return
        [
            Item("account.profile", "", "Account", "Sign in or out. A free account adds AI polish and spoken answers; dictation on this PC works without one.",
                ["account", "sign in", "sign up", "sign out", "log in", "log out", "create", "email", "code", "free"]),
            Item("account.plan", "Plan & usage", "Free allowance", "How much of this month’s free allowance is used.",
                ["billing", "allowance", "usage", "plan", "cost", "price", "limit"]),
            Item("account.usage.sarvam", "Plan & usage", "Sarvam", "Billed by Sarvam to your key.", ["sarvam", "billing", "engine", "dashboard"],
                () => !string.IsNullOrWhiteSpace(shellSettings().SarvamKey)),
            Item("account.usage.backend", "Plan & usage", "OpenAI and AssemblyAI", "Through OpenClicky.",
                ["openai", "assemblyai", "allowance", "metered", "engine"]),
            Item("account.backendHost", "Backend", "Host", "Where OpenClicky’s model calls go. Change it in shell.json.",
                ["backend", "server", "url", "host", "shell.json"]),
            Item("account.backendToken", "Backend", "Session token", "Written when you sign in.", ["token", "bearer", "shell.json", "session"]),
        ];
    }

    // MARK: - Privacy & Security (SettingsPrivacyPage.swift)

    public const string PrivacySummaryText = "With This PC as the engine, what you dictate stays on this PC unless AI clean-up is on (edits need it too). The other engines send audio to the provider you chose. Questions to Clicky always go online with a picture of your screen, and fast voice and hands-free send your voice to OpenAI.";

    public const string MicrophonePermissionDetail = "Listens only during a take. With This PC as the engine, audio never leaves it.";

    /// <summary>A path as the page shows it: the profile and local app data folders as their %VARIABLES%.</summary>
    public static string DisplayPath(string path)
    {
        string Replace(string text, string folder, string variable) =>
            folder.Length > 0 && text.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? variable + text[folder.Length..] : text;
        // Local app data sits inside the profile, so it is replaced first.
        var shown = Replace(path, OpenClickyPaths.LocalAppDataDirectory, "%LOCALAPPDATA%");
        return Replace(shown, OpenClickyPaths.HomeDirectory, "%USERPROFILE%");
    }

    public IReadOnlyList<SettingsItem> PrivacyItems()
    {
        SettingsItem Item(string id, string section, string title, string detail, string[] keywords) => new()
        {
            Id = id, Section = section, Title = title, Detail = detail, Keywords = keywords,
        };
        return
        [
            Item("privacy.microphone", "Permissions", "Microphone", MicrophonePermissionDetail, ["permission", "mic", "audio", "grant", "allow"]),
            Item("privacy.readNearbyText", "Permissions", "Read nearby text", "Helps spell names and terms on screen right.",
                ["context", "screen", "spelling", "names", "ui automation", "accessibility"]),
            Item("privacy.pauseBeforeSendingScreen", "Permissions", "Pause before sending my screen", "A moment to press esc before Clicky sends your screen.",
                ["screenshot", "screen", "cancel", "esc", "send", "wait", "consent"]),
            Item("privacy.memoryOnThisMac", "What it keeps", "Keep my memory on this PC only", "History, dictionary and styles stay here. Questions to Clicky still go online.",
                ["local", "memory", "cloud", "sync", "data"]),
            Item("privacy.incognito", "What it keeps", "Incognito", "Takes still paste, but nothing is saved to history.", ["private", "history", "don't save"]),
            Item("privacy.clipboardHistory", "What it keeps", "Clipboard history", "Capture what you copy, so it shows in history too.",
                ["clipboard", "copy", "pasteboard"]),
            Item("privacy.retryFailedTakes", "What it keeps", "Keep failed takes to retry", "Up to 100 takes or 1 GB. Off removes them now.",
                ["retry", "audio", "recording", "failed", "network"]),
            Item("privacy.deleteTakes", "What it keeps", "Delete all takes", "Your dictionary and snippets stay.", ["delete", "wipe", "history", "erase", "data"]),
            Item("privacy.whereHistory", "Where things live", "History", DisplayPath(OpenClickyPaths.DictationDatabaseFile),
                ["sqlite", "database", "file", "folder", "takes"]),
            Item("privacy.whereSpace", "Where things live", "Styles, dictionary, snippets", DisplayPath(OpenClickyPaths.DictationDirectory),
                ["json", "file", "folder", "shortcuts"]),
            Item("privacy.whereKeys", "Where things live", "Keys and account", DisplayPath(OpenClickyPaths.ShellSettingsFile),
                ["shell.json", "token", "file", "key"]),
            new()
            {
                Id = "privacy.summary", Section = "Where things live", Chrome = SettingsItemChrome.Footer,
                Title = "Privacy summary", Detail = PrivacySummaryText, Keywords = ["policy", "privacy", "what leaves", "readme"],
            },
        ];
    }

    // MARK: - About (SettingsAboutPage.swift)

    /// <summary>The version line, "Version 0.9.0 (312) · Open source, MIT".</summary>
    public static string VersionLine(string shortVersion, string buildNumber) =>
        string.IsNullOrEmpty(buildNumber) ? $"Version {shortVersion} · Open source, MIT" : $"Version {shortVersion} ({buildNumber}) · Open source, MIT";

    public IReadOnlyList<SettingsItem> AboutItems()
    {
        SettingsItem Item(string id, string section, string title, string detail, string[] keywords) => new()
        {
            Id = id, Section = section, Title = title, Detail = detail, Keywords = keywords,
        };
        return
        [
            Item("about.app", "", "OpenClicky", VersionLine(AppVersion.ShortVersion, AppVersion.BuildNumber),
                ["version", "about", "build", "mit", "license", "update", "updates", "velopack", "check now"]),
            Item("about.source", "Help", "Source code", "Issues and pull requests welcome.", ["github", "source", "license", "mit", "code"]),
            Item("about.reportBug", "Help", "Report a bug", "Attach the log. It lists what each take did, not what you said.",
                ["bug", "log", "issue", "problem", "feedback", "app.log"]),
        ];
    }
}

/// <summary>The app's version ("0.9.0") and build number, from the entry assembly.</summary>
public static class AppVersion
{
    public static string ShortVersion
    {
        get
        {
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    public static string BuildNumber
    {
        get
        {
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            return version is null || version.Revision <= 0 ? "" : version.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public const string SourceUrl = "https://github.com/prasanthsasikumar/openclicky";
    public const string IssuesUrl = "https://github.com/prasanthsasikumar/openclicky/issues";
    public const string ReadmeUrl = "https://github.com/prasanthsasikumar/openclicky#readme";
}
