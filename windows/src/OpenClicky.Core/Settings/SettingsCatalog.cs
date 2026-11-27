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
using OpenClicky.Core.Localization;

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
        .. DictationItems(), .. KeyboardItems(), .. OrbItems(), .. ClickyItems(), .. ModelsItems(), .. VoiceItems(), .. PluginsItems(), .. IslandItems(),
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
            Item("dictation.openWords", L.T("Your words"), L.T("Every take, with search. Try dictation here."),
                ["history", "takes", "try"], DictationDestination.Of(DictationSection.Words)),
            Item("dictation.openWriting", L.T("How it writes"), L.T("Dictionary, snippets and per-app styles."),
                ["dictionary", "snippets", "styles"], DictationDestination.Of(DictationSection.Writing)),
            Item("dictation.openOrb", L.T("The orb"), L.T("Style, theme, size and when it appears."),
                ["orb", "pill", "look", "theme", "size"], DictationDestination.Of(DictationSettingsPage.Orb)),
            Item("island.openCalendars", L.T("Calendars"), L.T("Which calendars the island shows."),
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
            Item("words.takes", DictationSection.Words, L.T("Your takes"), L.T("Every take you dictated, with search and filters."),
                ["history", "takes", "transcript", "past", "record", "recording", "retry", "dictated"]),
            Item("words.tryIt", DictationSection.Words, L.T("Try it here"), L.T("A box to dictate into and see what OpenClicky writes."),
                ["test", "practice", "try", "scratch"]),
            Item("writing.dictionary", DictationSection.Writing, L.T("Dictionary"), L.T("Names and terms written exactly as you expect."),
                ["dictionary", "terms", "names", "spelling", "vocabulary", "words", "misheard"]),
            Item("writing.snippets", DictationSection.Writing, L.T("Snippets"), L.T("Say a short phrase, get a longer text."),
                ["snippet", "shortcut", "text expansion", "template", "signature", "address", "email"]),
            Item("writing.styles", DictationSection.Writing, L.T("Styles"), L.T("How you sound, app by app: the rules the AI clean-up reads."),
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
            Item("general.appearance", L.T("Appearance"), L.T("This window, the orb and its transcript box follow this. The island stays black."),
                ["light", "dark", "auto", "match windows", "theme", "mode", "colour", "color"]),
            Item("general.hudPlacement", L.T("Where it lives"), L.T("The top of the screen, a drawer on the left or right edge, or hidden until you use a shortcut."),
                ["top", "notch", "island", "hud", "side", "edge", "drawer", "hide", "hidden", "position", "placement", "shortcuts only"]),
            Item("general.trayIcon", L.T("Show in the tray"), L.T("Off is fine: the island is OpenClicky’s home."),
                ["tray", "notification area", "system tray", "icon", "taskbar", "menu bar"]),
            Item("general.appIcon", L.T("App icon"), L.T("Its colour on this window, the taskbar and the tray. The Start menu keeps the red one."),
                ["icon", "colour", "color", "taskbar", "tray", "app icon", "signal", "ultraviolet", "sunset", "paper", "aqua", "purple", "blue", "pink", "red"]),
            Item("general.openAtLogin", L.T("Open at sign-in"), L.F("So {0} always works.", dictationKeyName),
                ["startup", "launch", "restart", "login", "log in", "sign in", "boot"]),
            Item("general.walkthrough", L.T("Welcome tour"), L.T("Clicky shows you dictating, asking and handing over a task, from the island."),
                ["onboarding", "setup", "replay", "tour", "first run"]),
            Item("general.settingsTour", L.T("Settings tour"), L.T("A one-minute look around this window: Clicky points at what matters most."),
                ["tour", "crash course", "show me around", "help", "guide", "replay", "walkthrough"]),
            Item("general.reduceMotion", L.T("Reduce motion"), L.T("The orb and the island appear and leave without animating."),
                ["animation", "motion", "accessibility", "calm"]),
            Item("about.autoUpdate", L.T("Check for updates daily"), L.T("Updates come from OpenClicky’s GitHub releases."),
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
            DictationHotkey.RightControl => L.T(" Edits, hands-free and Ctrl twice use the left Ctrl."),
            DictationHotkey.LeftControl => L.T(" Edits, hands-free and Ctrl twice use the right Ctrl."),
            DictationHotkey.RightAlt => L.T(" A key typed with right Alt held (é, €) cancels the take."),
            DictationHotkey.RightShift => L.T(" A key typed with right Shift held (a capital) cancels the take."),
            _ => "",
        };
        return L.T("Hold to talk anywhere.") + rightHandKeyNote;
    }

    public IReadOnlyList<SettingsItem> DictationKeyItems() =>
    [
        new()
        {
            Id = "keyboard.dictationKey",
            Title = L.T("Dictation key"), Detail = DictationKeyDetail(settings.DictationKey),
            Keywords = ["hotkey", "push to talk", "right control", "right ctrl", "right alt", "altgr", "right shift", "caps lock", "dictate", "shortcut"],
        },
        new()
        {
            Id = "keyboard.tapKeepsListening",
            Title = L.T("Tap to keep listening"),
            Detail = L.T("Off: hold the key while you talk, as on the Mac. On: a quick tap starts a take and the next tap finishes it."),
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
            Item("dictation.language", L.T("Language"), L.T("Pins every engine to one language, or lets it detect."),
                ["language", "english", "hindi", "tamil", "spanish", "french", "german", "pin", "auto-detect", "detect"]),
            // Only while the words are pinned to an Indian language (or, in India, could be one).
            Item("dictation.script", L.T("Script for Indian languages"), L.T("Same words, written two ways."),
                ["script", "roman", "native", "devanagari", "hinglish", "transliteration"],
                () => settings.Language.IsIndian || (settings.Region.OffersIndianOptions() && settings.Language == DictationLanguage.Auto)),
            Item("dictation.cleanUp", L.T("Clean up my words with AI"), L.T("Adds punctuation and fixes numbers."),
                ["polish", "clean up", "cleanup", "clean-up", "ai", "punctuation", "grammar", "model", "offline takes"]),
            Item("dictation.input", L.T("Input"), L.T("Automatic follows Windows’ default microphone. Clicky listens through it too."),
                ["microphone", "mic", "device", "input", "headset", "headphones"]),
            Item("dictation.micTest", L.T("Test your microphone"), L.T("Hear whether the chosen input picks you up."),
                ["microphone", "mic", "level", "test"]),
            Item("dictation.inactivityTimeout", L.T("End a quiet take after"), L.T("A tapped take finishes on its own after this much silence."),
                ["inactivity", "timeout", "silence", "minutes", "quiet"]),
            Item("dictation.pasteLast", L.T("Paste last take"), L.T("Put your last take at the cursor again."),
                ["paste", "again", "re-insert", "last"]),
            Item("dictation.sounds", L.T("Sounds"), L.T("Tones when a take starts, stops or finishes."),
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
            Item("clicky.assistantName", L.T("Its name"), L.T("What your assistant is called and answers to, like Dyna or Professor Pip."),
                ["name", "rename", "call it", "assistant", "persona", "dyna", "nickname"]),
            Item("clicky.answerSimpler", "Simpler words", "Everyday words a young child knows, and hard words explained. Clicky also turns this on when you ask it to.",
                ["simple", "simpler", "easy", "easier", "words", "kids", "child", "children", "level", "explain", "style", "remember"]),
            Item("clicky.answerShorter", "Shorter answers", "One or two sentences unless you ask for more. Clicky also turns this on when you ask it to.",
                ["short", "shorter", "brief", "long", "length", "less", "talk less", "style", "remember"]),
            Item("clicky.answerYesNoFirst", "Yes or no first", "A yes-or-no question gets yes or no first, then why. Clicky also turns this on when you ask it to.",
                ["yes", "no", "first", "straight", "direct", "answer", "style", "remember"]),
            Item("clicky.answerSource", L.T("Answers come from"), L.T("Automatic uses your own keys first, then plans you switched on, then the free credits."),
                ["provider", "model", "openai", "anthropic", "claude", "openrouter", "gemini", "groq", "mistral", "grok", "codex", "subscription", "plan", "answers"]),
            Item("clicky.model", L.T("Claude model"), L.T("When Claude answers: Sonnet is quicker, Opus thinks harder."),
                ["model", "claude", "sonnet", "opus", "smarter", "quicker"]),
            Item("clicky.pointerColor", L.T("Pointer colour"), L.T("The colour it flies and glows in."),
                ["pointer", "cursor", "colour", "color", "red", "blue", "green", "theme"]),
            Item("clicky.pointerStyle", L.T("Pointer shape"), L.T("A triangle, an arrow, a dot or a ring."),
                ["pointer", "cursor", "shape", "style", "arrow", "dot", "ring", "triangle", "design"]),
            Item("clicky.pointer", L.T("Show the pointer"), L.T("It flies to what Clicky means."),
                ["pointer", "cursor", "buddy", "orange", "visible", "hide", "always", "while the mouse moves", "after a shake", "shake", "while i talk"]),
            Item("clicky.agentMode", L.T("Agent mode"), L.T("Clicky can hand work to Codex."),
                ["agent", "codex", "tasks", "automation"]),
            Item("clicky.workspace", L.T("Workspace"), L.T("Where agents keep their files."),
                ["workspace", "folder", "codex", "files", "directory"]),
        ];
    }

    /// <summary>"Always listening": instant on fast voice, else after the person stops talking.</summary>
    public static string HandsFreeDetail(bool usesRealtime) => usesRealtime
        ? L.T("Hands-free: just talk, no keys. Instant with fast voice on.")
        : L.T("Hands-free: just talk, no keys. Clicky answers once you stop talking.");

    public const string CaptionsDetail =
        "What Clicky says, written beside the pointer as it speaks. Automatically when you typed the question, or the sound is off or low.";

    public static string FastVoiceNeedsKeyDetail => L.T("Instant replies need your own OpenAI or Gemini key. Without one, Clicky answers in the standard voice.");

    /// <summary>Models of the person's own (Ollama, LM Studio, any OpenAI-compatible server) and who writes drawings.</summary>
    public IReadOnlyList<SettingsItem> ModelsItems()
    {
        SettingsItem Item(string id, string title, string detail, string[] keywords) => new()
        {
            Id = id, Title = title, Detail = detail, Keywords = keywords,
        };
        return
        [
            Item("models.ollama", "Ollama", L.T("Models running on this PC with Ollama. Free, private and offline; pick one that sees images (gemma3, llava, qwen2.5-vl)."),
                ["ollama", "local", "offline", "private", "llama", "gemma", "qwen", "model", "free"]),
            Item("models.lmStudio", "LM Studio", L.T("Models running on this PC in LM Studio (start its server on port 1234)."),
                ["lm studio", "lmstudio", "local", "offline", "private", "model", "free"]),
            Item("models.custom", L.T("Your own server"), L.T("Any OpenAI-compatible endpoint: vLLM, llama.cpp, LocalAI, DeepSeek, Together, and others, with a key if it needs one."),
                ["openai compatible", "custom", "server", "endpoint", "vllm", "llama.cpp", "localai", "deepseek", "together", "self-hosted", "api"]),
            Item("models.speech", "Speech server", SpeechServerDetail,
                ["speech", "voice", "hearing", "transcription", "speech to text", "text to speech", "tts", "stt", "whisper", "kokoro", "piper",
                 "phonon", "chatterbox", "gemma", "speaches", "localai", "ollama", "local", "offline", "private", "self-hosted", "server", "free"]),
            Item("clicky.drawingSource", L.T("Drawings come from"), L.T("Who writes the answers drawn on your screen. A stronger model draws better; answers can stay quick."),
                ["drawing", "draw", "diagram", "teacher", "3d", "model", "explain", "whiteboard"]),
            Item("clicky.drawingDetail", L.T("How detailed"), L.T("More parts and finer detail take longer to draw. Very detailed suits a strong model."),
                ["drawing", "detail", "detailed", "parts", "realistic", "real", "quality", "3d", "simple", "complex"]),
            Item("clicky.pieceMaker", L.T("3D pieces come from"), L.T("A complex thing (a plane, a car, an animal) is built from pieces made one at a time, each one clickable."),
                ["3d", "generator", "pieces", "model", "mesh", "hunyuan", "llama-mesh", "ollama", "lm studio", "gpu", "graphics card", "realistic"]),
            Item("clicky.drawingInstructions", L.T("Your drawing instructions"), L.T("Added to every drawing's instructions: a style, a level, what to show more of."),
                ["drawing", "prompt", "instructions", "style", "custom", "realistic", "kids", "colours", "language", "3d", "2d"]),
            Item("clicky.drawingScriptFont", L.T("Urdu in drawings"), L.T("The font Urdu words are drawn in. Noto Nastaliq Urdu comes with OpenClicky; a Nastaliq font you installed (IndoPak Nastaleeq, Jameel Noori Nastaleeq) can be used by its name."),
                ["urdu", "font", "nastaliq", "nastaleeq", "arabic", "script", "indopak", "jameel", "noori", "drawing", "language"]),
            Item("clicky.markerShortcut", L.T("Mark the screen with"), L.T("Keys that let you circle part of the screen to ask about it, while drawn answers are on. Word uses Ctrl+Alt+M for comments."),
                ["marker", "mark", "circle", "highlight", "shortcut", "hotkey", "keys", "ctrl+alt+m", "screen", "ask about"]),
        ];
    }

    /// <summary>The speech server row's line before it is turned on.</summary>
    public const string SpeechServerDetail = "Hear questions and speak answers with your own models, on this PC or your network: Ollama, Speaches (Whisper, Kokoro), LocalAI. Free and private.";

    public IReadOnlyList<SettingsItem> VoiceItems()
    {
        var usesRealtime = capabilities().UsesRealtime;
        SettingsItem Item(string id, string section, string title, string detail, string[] keywords) => new()
        {
            Id = id, Section = section, Title = title, Detail = detail, Keywords = keywords,
        };
        List<SettingsItem> items =
        [
            Item("clicky.screenLanguage", L.T("Language"), L.T("Screen language"),
                L.T("The language of the island, the tour and what Clicky says when something goes wrong. Changes the next time OpenClicky starts."),
                ["screen language", "app language", "interface", "ui", "urdu", "اردو", "english", "right to left", "rtl", "translate"]),
            Item("clicky.language", L.T("Language"), L.T("Language"), L.T("Clicky speaks and listens in this."),
                ["language", "english", "hindi", "tamil", "spanish", "french", "speak", "reply", "listen"]),
            Item("clicky.nextQuestions", "Voice", "Next questions",
                "After an answer, a few questions you could ask next, to tap. Made by your own key or model, never the free allowance.",
                ["next questions", "follow up", "follow-up", "suggestions", "suggested questions", "what to ask", "ideas", "curious", "buttons", "chips"]),
            Item("clicky.speechVoice", L.T("Voice"), L.T("Voice"), L.T("Who speaks the answers: the cloud voice, or this PC’s own voice, free and offline."),
                ["voice", "speech", "tts", "text to speech", "elevenlabs", "eleven labs", "kokoro", "offline", "on device",
                 "pc voice", "windows voice", "narrator", "credits", "allowance", "left"]),
            Item("clicky.captions", "Voice", "Captions", CaptionsDetail,
                ["captions", "subtitles", "read along", "read the answer", "deaf", "hard of hearing", "hearing", "muted", "mute",
                 "sound off", "quiet", "noisy", "classroom", "written", "text"]),
            Item("clicky.captionColor", "Voice", "Caption colour", "The colour of the words beside the pointer: the red bubble, or bright words on a dark card.",
                ["caption", "captions", "subtitles", "colour", "color", "text colour", "font colour", "orange", "yellow", "white", "dark"]),
            Item("clicky.captionGlow", "Voice", "Caption glow", "A soft halo around the words, in their own colour.",
                ["caption", "captions", "subtitles", "glow", "glowing", "neon", "halo", "shine", "bright"]),
        ];
        // Fast voice runs on OpenAI Realtime or Gemini Live, which need an own OpenAI or Gemini key; without one
        // its switch would do nothing, so the row shows the way to a key instead.
        if (usesRealtime)
        {
            items.Add(Item("clicky.fastVoice", L.T("Fast voice"), L.T("Fast voice"), L.T("Talks back instantly, over OpenAI Realtime or Gemini Live."),
                ["realtime", "voice", "fast", "openai", "gemini", "live"]));
        }
        // Hands-free works on every lane that can answer: instantly with fast voice, otherwise the standard lane
        // hears each turn on this PC once the person stops talking.
        if (usesRealtime || OpenClickyConfiguration.IsConfiguredFor(shellSettings()))
        {
            items.Add(Item("clicky.alwaysListening", L.T("Fast voice"), L.T("Always listening"), HandsFreeDetail(usesRealtime),
                ["hands-free", "hands free", "always on", "listening"]));
        }
        if (usesRealtime)
        {
            // Only worth a choice when both can run here.
            var credentials = ProviderCredentials.FromSettings(shellSettings());
            if (credentials.GeminiKey is not null && (credentials.OpenAIKey is not null || credentials.AccountToken is not null))
            {
                items.Add(Item("clicky.realtimeProvider", L.T("Fast voice"), L.T("Fast voice with"), RealtimeProviderDetail,
                    ["realtime", "gemini", "openai", "live", "provider", "fast voice", "hands-free"]));
            }
        }
        else
        {
            items.Add(Item("clicky.fastVoiceNeedsKey", L.T("Fast voice"), L.T("Fast voice"), FastVoiceNeedsKeyDetail,
                ["realtime", "voice", "fast", "openai", "gemini", "key"]));
        }
        // "Check my email" and "draft an email": which inbox they go to.
        items.Add(Item("clicky.emailApp", L.T("Doing things"), L.T("Email"), EmailAppDetail,
            ["email", "mail", "gmail", "outlook", "inbox", "draft"]));
        items.Add(new SettingsItem
        {
            Id = "clicky.emailNeverSent", Section = L.T("Doing things"), Chrome = SettingsItemChrome.Footer,
            Title = EmailNeverSentText, Detail = EmailNeverSentText, Keywords = ["send", "email"],
        });
        return items;
    }

    public static string EmailAppDetail => L.T("Which inbox “check my email” and “draft an email” open. Automatic is Gmail.");
    public static string EmailNeverSentText => L.T("Nothing is ever sent by voice.");

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
                Id = "plugins.teacher", Section = "",
                Title = Plugins.Teacher.TeacherPlugin.Component.Title, Detail = Plugins.Teacher.TeacherPlugin.Component.SettingsDetail ?? "",
                Keywords = ["island", "teacher", "teach", "lesson", "draw", "drawing", "explain", "3d", "whiteboard"],
            },
            new SettingsItem
            {
                Id = "plugins.islandDefault", Section = L.T("On the island"),
                Title = L.T("Default component"), Detail = L.T("What the open island shows when nothing else is going on."),
                Keywords = ["island", "component", "panel", "shortcuts", "default", "slot"],
            },
            new SettingsItem
            {
                Id = "plugins.islandOrder", Section = L.T("On the island"),
                Title = L.T("Component order"), Detail = L.T("The order of the island's buttons when there is more than one to flip to."),
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
            Item("appearance.peekDirection", L.T("When you hover it"), L.T("Sideways keeps a thin strip along the top edge; downwards drops it open a little."),
                ["hover", "peek", "sideways", "downwards", "grow", "direction", "island"]),
            Item("appearance.style", L.T("Style"), L.T("Liquid glass, blend or black."),
                ["style", "glass", "liquid", "blend", "black", "look", "island", "transparent"]),
            Item("calendar.enabled", L.T("Calendar"), L.T("Today and the week on the island's home."),
                ["calendar", "events", "meetings", "agenda", "outlook", "week", "today"]),
            Item("weather.enabled", L.T("Weather"), L.T("Now and the next hours, from Open-Meteo."),
                ["weather", "temperature", "forecast", "rain", "open-meteo"]),
            Item("weather.unit", L.T("Temperature"), L.T("Celsius or Fahrenheit."), ["celsius", "fahrenheit", "unit", "temperature", "weather"]),
            Item("weather.location", L.T("Weather location"), L.T("Where Windows says you are, or a place you type."),
                ["location", "city", "place", "town", "weather", "gps"]),
            Item("timer.focusMinutes", L.T("Focus"), L.T("How long a focus round lasts."), ["timer", "focus", "pomodoro", "minutes"]),
            Item("timer.breakMinutes", L.T("Break"), L.T("The short break between rounds."), ["timer", "break", "pomodoro", "minutes"]),
            Item("timer.longBreakMinutes", L.T("Long break"), L.T("After every fourth round."), ["timer", "long break", "pomodoro", "minutes"]),
            Item("calendar.access", L.T("Calendar"), L.T("Lets the island read your calendars. Nothing is changed or sent."),
                ["calendar", "permission", "privacy", "access", "allow"]),
            Item("calendar.calendars", L.T("Calendars"), L.T("Switch off a calendar to keep it off the island."),
                ["calendar", "calendars", "hide", "show", "account", "outlook"]),
        ];
    }

    public static string RealtimeProviderDetail => L.T("Who talks back in fast voice and hands-free. Automatic is OpenAI, whose pointing is tuned best.");

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
            Item("orb.visible", "", L.T("Show the orb"), L.F("Off: {0} still works everywhere, just without narration.", dictationKeyName),
                ["orb", "pill", "hide", "visible"]),
            Item("orb.look", L.T("Look"), L.T("Style"), L.T("Pill, classic or pixel."), ["pill", "classic", "pixel", "style", "shape", "look"]),
            Item("orb.theme", L.T("Look"), L.T("Theme"), L.T("Black, coral or mist."), ["colour", "color", "black", "coral", "mist"]),
            Item("orb.size", L.T("Look"), L.T("Size"), L.T("Full or mini."), ["full", "mini", "small", "big"]),
            Item("orb.hidesWhenIdle", L.T("When it appears"), L.T("Hide when idle"), L.T("Appears when a take starts, leaves when it’s done."),
                ["hide", "idle", "not in use"]),
            Item("orb.restsExpanded", L.T("When it appears"), L.T("Rest with the box open"), L.T("Keeps the transcript box showing between takes."),
                ["box", "transcript", "expanded"]),
            Item("orb.opensBoxWhenPasteUnverified", L.T("When it appears"), L.T("Open the box if a paste can’t be confirmed"),
                L.T("So words never vanish when an app ignores the paste."), ["paste", "visibility", "box", "unverified", "confirm"]),
            Item("orb.isDraggable", L.T("When it appears"), L.T("Free to drag"), L.T("Off pins the orb at the bottom centre."),
                ["drag", "move", "pin", "position", "center", "centre", "bottom"]),
            Item("orb.tooltips", L.T("When it appears"), L.T("Tooltips"), L.T("A short hint under the orb."), ["hint", "help"]),
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
            "keyboard.dictate" => new([key], L.T("hold")),
            "keyboard.edit" => new([key, "Ctrl"], L.T("hold")),
            "keyboard.cancel" => new(["Esc"], L.F("or {0} twice", key)),
            "keyboard.talk" => new(dictationKey.TalkKeycapLabels(), L.T("hold")),
            "keyboard.type" => new(["Ctrl"], L.T("tap twice")),
            "keyboard.handsFree" => new([key, "Ctrl"], L.T("tap twice")),
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
            Item("keyboard.edit", L.T("Dictation"), L.T("Edit selected text"), L.T("Hold and say the change, like “make it formal”."),
                ["hey clicky", "edit", "rewrite", "control", "ctrl", "selection"]),
            Item("keyboard.cancel", L.T("Dictation"), L.T("Cancel a take"), L.T("Nothing is pasted."),
                ["esc", "escape", "discard", "stop", "double-tap"]),
            Item("keyboard.talk", L.T("Clicky"), L.T("Talk to Clicky"), L.T("Ask about anything on screen. It answers out loud and points."),
                ["ctrl", "win", "windows key", "control windows", "voice", "ask", "companion"]),
            Item("keyboard.mouseTalk", L.T("Clicky"), L.T("Talk with a mouse button"), L.T("Hold it to ask, let go to send, like the keys. For a wireless mouse away from the keyboard."),
                ["mouse", "button", "side button", "back button", "forward button", "middle", "wireless", "remote", "trigger"]),
            Item("keyboard.type", L.T("Clicky"), L.T("Type to Clicky"), L.T("A one-line composer opens in the island."),
                ["ctrl", "composer", "island", "text", "control twice"]),
            // Hands-free runs on fast voice or, without a realtime key, on the standard lane; with nothing that
            // can answer the keys would do nothing, so it isn't listed.
            Item("keyboard.handsFree", L.T("Clicky"), L.T("Hands-free"), L.T("Always-on listening. Same keys to stop."),
                ["always on", "realtime", "listening", "hands free"], () => usesRealtime || OpenClickyConfiguration.IsConfiguredFor(shellSettings())),
        ];
    }

    // MARK: - Keys & Account, Account (SettingsAccountPage.swift)

    public IReadOnlyList<SettingsItem> KeysItems() =>
    [
        new()
        {
            Id = "account.keys", Section = L.T("Your keys"),
            Title = L.T("Your keys"), Detail = L.T("Keys from AI providers (they answer, clean up and speak) and voice services."),
            Keywords = ["api key", "byok", "own key", "provider", "add key", "openai", "openaiApiKey", "anthropic", "claude",
                        "anthropicApiKey", "openrouter", "llama", "deepseek", "gemini", "google", "ai studio", "groq",
                        "mistral", "xai", "grok", "sarvam", "saaras", "elevenlabs", "polly", "google cloud", "voice", "test"],
        },
        new()
        {
            Id = "account.keysNote", Section = L.T("Your keys"), Chrome = SettingsItemChrome.Footer,
            Title = "Where keys go", Detail = KeysNoteText, Keywords = ["shell.json", "byok"],
        },
        new()
        {
            Id = "account.claudeCodePlan", Section = L.T("Subscriptions"),
            Title = L.T("Claude Code (your Claude plan)"), Detail = L.T("Answer questions through Claude Code, signed in to your Claude plan."),
            Keywords = ["claude code", "claude plan", "pro", "max", "subscription", "anthropic"],
        },
        new()
        {
            Id = "account.codexPlan", Section = L.T("Subscriptions"),
            Title = L.T("Codex (your ChatGPT plan)"), Detail = L.T("Answer questions through Codex, signed in with ChatGPT."),
            Keywords = ["codex", "chatgpt", "plus", "pro", "subscription", "openai"],
        },
        new()
        {
            Id = "account.plansNote", Section = L.T("Subscriptions"), Chrome = SettingsItemChrome.Footer,
            Title = "What a plan can do", Detail = PlansNoteText, Keywords = ["subscription", "plan"],
        },
    ];

    public static string KeysNoteText => L.F("Your keys go straight to that provider, never through OpenClicky, and are used instead of the free credits. Stored in {0}.", @"%USERPROFILE%\.openclicky\shell.json");

    public static string PlansNoteText => L.T("A plan answers questions, points, and does quick actions like opening apps or making folders. Bigger tasks need the agent. Codex answers in one go, a few seconds after you ask.");

    public IReadOnlyList<SettingsItem> AccountItems()
    {
        SettingsItem Item(string id, string section, string title, string detail, string[] keywords, Func<bool>? isRelevant = null) => new()
        {
            Id = id, Section = section, Title = title, Detail = detail, Keywords = keywords,
            IsRelevant = isRelevant ?? (() => true),
        };
        return
        [
            Item("account.profile", "", L.T("Account"), L.T("Sign in or out. A free account adds AI polish and spoken answers; dictation on this PC works without one."),
                ["account", "sign in", "sign up", "sign out", "log in", "log out", "create", "email", "code", "free"]),
            Item("account.plan", L.T("Plan & usage"), L.T("Free allowance"), L.T("How much of this month’s free allowance is used."),
                ["billing", "allowance", "usage", "plan", "cost", "price", "limit"]),
            Item("account.usage.sarvam", L.T("Plan & usage"), "Sarvam", L.T("Billed by Sarvam to your key."), ["sarvam", "billing", "engine", "dashboard"],
                () => !string.IsNullOrWhiteSpace(shellSettings().SarvamKey)),
            Item("account.usage.backend", L.T("Plan & usage"), L.T("OpenAI and AssemblyAI"), L.T("Through OpenClicky."),
                ["openai", "assemblyai", "allowance", "metered", "engine"]),
            Item("account.backendHost", L.T("Backend"), L.T("Host"), L.T("Where OpenClicky’s model calls go. Change it in shell.json."),
                ["backend", "server", "url", "host", "shell.json"]),
            Item("account.backendToken", L.T("Backend"), L.T("Session token"), L.T("Written when you sign in."), ["token", "bearer", "shell.json", "session"]),
        ];
    }

    // MARK: - Privacy & Security (SettingsPrivacyPage.swift)

    public static string PrivacySummaryText => L.T("With This PC as the engine, what you dictate stays on this PC unless AI clean-up is on (edits need it too). The other engines send audio to the provider you chose. Questions to Clicky always go online with a picture of your screen, and fast voice and hands-free send your voice to OpenAI.");

    public static string MicrophonePermissionDetail => L.T("Listens only during a take. With This PC as the engine, audio never leaves it.");

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
            Item("privacy.microphone", L.T("Permissions"), L.T("Microphone"), MicrophonePermissionDetail, ["permission", "mic", "audio", "grant", "allow"]),
            Item("privacy.readNearbyText", L.T("Permissions"), L.T("Read nearby text"), L.T("Helps spell names and terms on screen right."),
                ["context", "screen", "spelling", "names", "ui automation", "accessibility"]),
            Item("privacy.pauseBeforeSendingScreen", L.T("Permissions"), L.T("Pause before sending my screen"), L.T("A moment to press esc before Clicky sends your screen."),
                ["screenshot", "screen", "cancel", "esc", "send", "wait", "consent"]),
            Item("privacy.memoryOnThisMac", L.T("What it keeps"), L.T("Keep my memory on this PC only"), L.T("History, dictionary and styles stay here. Questions to Clicky still go online."),
                ["local", "memory", "cloud", "sync", "data"]),
            Item("privacy.incognito", L.T("What it keeps"), L.T("Incognito"), L.T("Takes still paste, but nothing is saved to history."), ["private", "history", "don't save"]),
            Item("privacy.clipboardHistory", L.T("What it keeps"), L.T("Clipboard history"), L.T("Capture what you copy, so it shows in history too."),
                ["clipboard", "copy", "pasteboard"]),
            Item("privacy.retryFailedTakes", L.T("What it keeps"), L.T("Keep failed takes to retry"), L.T("Up to 100 takes or 1 GB. Off removes them now."),
                ["retry", "audio", "recording", "failed", "network"]),
            Item("privacy.deleteTakes", L.T("What it keeps"), L.T("Delete all takes"), L.T("Your dictionary and snippets stay."), ["delete", "wipe", "history", "erase", "data"]),
            Item("privacy.whereHistory", L.T("Where things live"), L.T("History"), DisplayPath(OpenClickyPaths.DictationDatabaseFile),
                ["sqlite", "database", "file", "folder", "takes"]),
            Item("privacy.whereSpace", L.T("Where things live"), L.T("Styles, dictionary, snippets"), DisplayPath(OpenClickyPaths.DictationDirectory),
                ["json", "file", "folder", "shortcuts"]),
            Item("privacy.whereKeys", L.T("Where things live"), L.T("Keys and account"), DisplayPath(OpenClickyPaths.ShellSettingsFile),
                ["shell.json", "token", "file", "key"]),
            new()
            {
                Id = "privacy.summary", Section = L.T("Where things live"), Chrome = SettingsItemChrome.Footer,
                Title = "Privacy summary", Detail = PrivacySummaryText, Keywords = ["policy", "privacy", "what leaves", "readme"],
            },
        ];
    }

    // MARK: - About (SettingsAboutPage.swift)

    /// <summary>The version line, "Version 0.9.0 (312) · Open source, MIT".</summary>
    public static string VersionLine(string shortVersion, string buildNumber) =>
        string.IsNullOrEmpty(buildNumber) ? L.F("Version {0} · Open source, MIT", shortVersion) : L.F("Version {0} ({1}) · Open source, MIT", shortVersion, buildNumber);

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
            Item("about.source", L.T("Help"), L.T("Source code"), L.T("Issues and pull requests welcome."), ["github", "source", "license", "mit", "code"]),
            Item("about.reportBug", L.T("Help"), L.T("Report a bug"), L.T("Attach the log. It lists what each take did, not what you said."),
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
