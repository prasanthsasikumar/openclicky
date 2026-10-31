// Port of NotchHUDPanels.swift's NotchVoiceDestination: what the island says about the dictation
// engine (the voice pill's call to action) and where its button leads.

using OpenClicky.Core.Dictation;
using OpenClicky.Core.Settings;

namespace OpenClicky.Core.Companion;

public static class IslandVoiceDestination
{
    /// <summary>Null when the engine can run; otherwise the pill's call to action, naming what is missing.</summary>
    public static string? BlockedPillLabel(DictationEngineChoice engine, string? unavailableReason)
    {
        if (unavailableReason is null) return null;
        return engine switch
        {
            DictationEngineChoice.Sarvam => "add sarvam key ›",
            // Downloaded but pinned to a language it doesn't hear: downloading again would not help.
            DictationEngineChoice.Parakeet => unavailableReason == DictationEngineResolver.ParakeetLanguageNotHeardReason
                ? "pick a language parakeet hears ›"
                : "download parakeet ›",
            DictationEngineChoice.Moonshine => unavailableReason == DictationEngineResolver.MoonshineLanguageNotHeardReason
                ? "pick english or auto-detect ›"
                : "pick another engine ›",
            _ => "set up backend ›",
        };
    }

    public enum SetupDestination { AccountSettings, KeySettings, DictationSettings, LanguageSettings }

    /// <summary>Where the engine is fixed, given why it can't run (null: it can, and the pill opens the engines).</summary>
    public static SetupDestination Destination(DictationEngineChoice engine, string? unavailableReason)
    {
        if (unavailableReason is null) return SetupDestination.DictationSettings;
        if (unavailableReason is DictationEngineResolver.ParakeetLanguageNotHeardReason or DictationEngineResolver.MoonshineLanguageNotHeardReason)
            return SetupDestination.LanguageSettings;
        if (engine.IsOnDevice()) return SetupDestination.DictationSettings;
        // A missing key is added on Keys & Account; a missing sign-in on Account.
        if (engine == DictationEngineChoice.Sarvam || unavailableReason == "needs your own key") return SetupDestination.KeySettings;
        return SetupDestination.AccountSettings;
    }

    /// <summary>The warning's button, named for the page it really opens.</summary>
    public static string ButtonTitle(DictationEngineChoice engine, string? unavailableReason) => Destination(engine, unavailableReason) switch
    {
        SetupDestination.AccountSettings => "open account settings",
        SetupDestination.KeySettings => "add a key",
        SetupDestination.DictationSettings => "open dictation settings",
        _ => "open language settings",
    };

    public static DictationSettingsPage SetupPage(DictationEngineChoice engine, string? unavailableReason) => Destination(engine, unavailableReason) switch
    {
        SetupDestination.AccountSettings => DictationSettingsPage.Account,
        SetupDestination.KeySettings => DictationSettingsPage.Account,
        _ => DictationSettingsPage.Dictation,
    };
}
