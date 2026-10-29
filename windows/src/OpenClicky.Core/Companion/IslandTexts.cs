//
//  IslandTexts.cs
//  OpenClicky (Windows port of the words in NotchHUD.swift and NotchHUDPanels.swift)
//
//  What the island says, as pure functions so the views and the sizing agree and the tests can
//  pin them: the busy strip's status and caption line, the band's pointer status, the dock
//  button, the shortcut grid (Windows keys, PORTING.md), the engine warning, the explanation (i).
//

using OpenClicky.Core.Pointing;
using OpenClicky.Core.Shortcuts;

namespace OpenClicky.Core.Companion;

/// <summary>One cell of the panel's 2 × 2 shortcut grid.</summary>
public sealed record IslandShortcut(string Title, IReadOnlyList<string> Keys, string Gesture, bool IsLive)
{
    public bool Equals(IslandShortcut? other) =>
        other is not null && Title == other.Title && Gesture == other.Gesture && IsLive == other.IsLive && Keys.SequenceEqual(other.Keys);

    public override int GetHashCode() => HashCode.Combine(Title, Gesture, IsLive);
}

/// <summary>Shown in place of the shortcut grid when the dictation engine can't run (NotchEngineWarning).</summary>
public sealed record IslandEngineWarning(string Heading, string Message, string ButtonTitle)
{
    /// <summary>Null when the engine can run. Otherwise the resolver's own reason ("needs …" is said of the engine, any
    /// other reason is a whole sentence) and a button named for the page it opens.</summary>
    public static IslandEngineWarning? Make(string engineDisplayName, string? unavailableReason, string buttonTitle)
    {
        if (unavailableReason is null) return null;
        var heading = $"{engineDisplayName.ToUpperInvariant()} CAN'T HEAR YOU YET";
        var message = unavailableReason.StartsWith("needs", StringComparison.Ordinal)
            ? $"{engineDisplayName.ToLowerInvariant()} {unavailableReason}. dictation stays off until then."
            : $"{unavailableReason}. dictation stays off until then.";
        return new IslandEngineWarning(heading, message, buttonTitle);
    }
}

public static class IslandTexts
{
    /// <summary>The busy strip's status (NotchCompactStatusView.statusText).</summary>
    public static string CompactStatus(CompanionVoiceState voiceState, bool isDictatingToFrontApp, bool isAlwaysListening,
        string? agentActivityText, string? answeringSourceName)
    {
        switch (voiceState)
        {
            case CompanionVoiceState.Idle:
                if (agentActivityText is not null) return "an agent is working";
                return isAlwaysListening ? "hands-free · just talk" : "openclicky";
            case CompanionVoiceState.Listening:
                if (isDictatingToFrontApp) return "dictating";
                return isAlwaysListening ? "listening · hands-free" : "listening";
            case CompanionVoiceState.Processing:
                if (isDictatingToFrontApp) return "typing it in";
                return agentActivityText is null ? "looking at your screen" : "working";
            default:
                return answeringSourceName is null ? "speaking" : $"speaking · {answeringSourceName}";
        }
    }

    /// <summary>The strip's caption line, or null for a strip with only its status (NotchCompactStatusView.detailLine).</summary>
    public static string? DetailLine(CompanionVoiceState voiceState, string? agentActivityText, string? lastTranscript)
    {
        if (voiceState == CompanionVoiceState.Idle) return null;
        if (!string.IsNullOrEmpty(agentActivityText)) return agentActivityText;
        if (voiceState == CompanionVoiceState.Responding && !string.IsNullOrEmpty(lastTranscript)) return $"“{lastTranscript}”";
        return null;
    }

    /// <summary>One line saying what the pointer is doing (NotchPointerStatus).</summary>
    public static string PointerStatus(bool isClickyCursorEnabled, PointerPresence presence, bool isPointerAwake)
    {
        if (!isClickyCursorEnabled) return "pointer shows while you talk";
        return presence switch
        {
            PointerPresence.Always => "pointer follows your mouse",
            PointerPresence.WhileMoving => "pointer follows while moving",
            _ => isPointerAwake ? "pointer follows your mouse" : "shake the mouse for the pointer",
        };
    }

    /// <summary>The peek line's words: a live coding session, then the playing track (each when its plugin is on), outrank
    /// the pointer's status (NotchPeekLine.peekText).</summary>
    public static string PeekText(string status, string? agentLine, string? mediaLine = null) => agentLine ?? mediaLine ?? status;

    /// <summary>The pointer's status in fewer words, so it fits beside "click to open" on the peek's one line.</summary>
    public static string PeekStatus(bool isClickyCursorEnabled, PointerPresence presence, bool isPointerAwake)
    {
        if (!isClickyCursorEnabled) return "pointer while you talk";
        return presence switch
        {
            PointerPresence.Always => "pointer follows you",
            PointerPresence.WhileMoving => "pointer while moving",
            _ => isPointerAwake ? "pointer follows you" : "shake for the pointer",
        };
    }

    /// <summary>The four shortcuts CompanionShortcutRecognizer knows, with the Windows keys (PORTING.md).</summary>
    public static IReadOnlyList<IslandShortcut> ShortcutGrid(DictationHotkey dictationKey, bool isAlwaysListening)
    {
        var dictationKeyLabel = ShortKeycapLabel(dictationKey);
        return new[]
        {
            new IslandShortcut("talk", dictationKey.TalkKeycapLabels(), "hold", false),
            new IslandShortcut("type", new[] { "Ctrl" }, "tap ×2", false),
            new IslandShortcut("dictate", new[] { dictationKeyLabel }, "hold", false),
            new IslandShortcut("hands-free", new[] { dictationKeyLabel, "Ctrl" }, "tap ×2", isAlwaysListening),
        };
    }

    /// <summary>The dictation key on a keycap, where the Mac drew one glyph (fn) and the full name ("right Ctrl")
    /// would not fit: the panel's grid and the tour's keycaps.</summary>
    public static string ShortKeycapLabel(DictationHotkey dictationKey) => dictationKey switch
    {
        DictationHotkey.RightControl => "R Ctrl",
        DictationHotkey.LeftControl => "L Ctrl",
        DictationHotkey.RightAlt => "R Alt",
        DictationHotkey.RightShift => "R Shift",
        DictationHotkey.CapsLock => "Caps",
        _ => "R Ctrl",
    };

    /// <summary>The (i): what OpenClicky does, typed out beside the pointer (CompanionManager.explainWhatOpenClickyDoes,
    /// with the Windows keys).</summary>
    public static string ExplainMessage(DictationHotkey dictationKey) =>
        $"hi, i'm openclicky. hold {dictationKey.TalkKeysText()} and ask about anything on your screen. "
        + "i'll answer out loud and point at what you need. tap control twice to type instead, "
        + $"hold {dictationKey.DisplayName()} to dictate into any app, and real work goes to the agent.";

    public const string ComposerPlaceholder = "ask clicky anything about your screen…";
    public const string ComposerSendHint = "↵ send";
    public const string ComposerEscapeHint = "esc";
    public const string StopSpeakingToolTip = "click to stop speaking";
    /// <summary>The strip's hover text while a question is on its way (it stops it, as esc does).</summary>
    public const string StopAskToolTip = "click to stop";

    public const string InfoToolTip = "what can you do?";
    public const string SettingsToolTip = "settings";
    public const string QuitToolTip = "quit openclicky";
    public const string PermissionLaterToolTip = "hide these cards until openclicky next opens";

    /// <summary>The voice pill's hover text while the engine can run.</summary>
    public static string VoicePillToolTip(string engineDisplayName) =>
        $"dictation is heard by {engineDisplayName.ToLowerInvariant()}. click to change.";
}
