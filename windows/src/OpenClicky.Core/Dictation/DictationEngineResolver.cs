// Ports `DictationEngineResolver` and `HeyClickyEditor` from Dictation/DictationTakeController.swift
// (the controller itself waits for the Platform layer): which engine can run and why not, the
// provider the chosen engine is built as, which model polishes takes and answers Hey Clicky, and
// whether a take's words may go to a model at all.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Dictation;

/// <summary>
/// Builds each engine's transcription provider. The providers live in the Speech port, so the
/// resolver asks through this seam; `TProvider` is the Speech port's provider interface
/// (the Mac's BuddyTranscriptionProvider).
/// </summary>
public interface IDictationProviderFactory<out TProvider>
{
    /// <summary>Windows speech recognition, pinned to `preferredLocale` ("hi-IN") or the PC's language when null.</summary>
    TProvider MakeOffline(string? preferredLocale);
    TProvider MakeParakeet();
    /// <summary>Sarvam, reading the dictation language when a take starts.</summary>
    TProvider MakeSarvam(Func<DictationLanguage> language);
    /// <summary>Upload transcription (OpenAI, or the backend's /agent/transcribe); the bare language read when a take starts, null = detect.</summary>
    TProvider MakeOpenClicky(Func<string?> bareLanguageCode);
    /// <summary>AssemblyAI streaming; the bare language read when a take starts, null = detect.</summary>
    TProvider MakeAssemblyAI(Func<string?> bareLanguageCode);
}

/// <summary>Builds the transcription provider the settings ask for, and says why one cannot be used.</summary>
public static class DictationEngineResolver
{
    public static TProvider MakeProvider<TProvider>(DictationEngineChoice choice, DictationSettings settings, IDictationProviderFactory<TProvider> factory) =>
        choice switch
        {
            DictationEngineChoice.Offline => factory.MakeOffline(settings.Language.BareCode is null ? null : settings.Language.Code),
            DictationEngineChoice.Parakeet => factory.MakeParakeet(),
            DictationEngineChoice.Sarvam => factory.MakeSarvam(() => settings.Language),
            // The styles page's language, read when a take starts; auto sends none so the model detects it.
            DictationEngineChoice.OpenClicky => factory.MakeOpenClicky(() => settings.Language.BareCode),
            _ => factory.MakeAssemblyAI(() => settings.Language.BareCode),
        };

    /// <summary>Null when the engine can be used; otherwise one line saying what is missing.</summary>
    public static string? UnavailableReason(DictationEngineChoice choice) =>
        UnavailableReason(choice, AccountCapabilities.Current().Kind, OpenClickyConfiguration.IsConfigured,
            OpenClickyConfiguration.HasAccountToken, OpenClickyConfiguration.Settings.SarvamKey);

    /// <summary>
    /// The backend engines hear through OpenAI (/agent/transcribe, or OpenAI itself on an own key) and
    /// AssemblyAI (/transcribe-token, minted on OpenClicky's key: account only), neither of which a
    /// free account's grant covers.
    /// </summary>
    public static string? UnavailableReason(DictationEngineChoice choice, AccountKind kind, bool isConfigured, bool signedIn, string? sarvamKey,
                                            bool? parakeetInstalled = null, Func<string?>? bareLanguageCode = null)
    {
        switch (choice)
        {
            case DictationEngineChoice.Offline:
                return null;
            case DictationEngineChoice.Parakeet:
                return ParakeetUnavailableReason(parakeetInstalled ?? ParakeetFacts.IsInstalled,
                    bareLanguageCode is null ? DictationSettings.StoredLanguage().BareCode : bareLanguageCode());
            case DictationEngineChoice.Sarvam:
                return SarvamUnavailableReason(sarvamKey);
            default:
                if (!isConfigured) return "needs an openclicky account or your openai key";
                if (kind == AccountKind.Account) return "needs your own key";
                if (choice == DictationEngineChoice.AssemblyAI && !signedIn) return "needs an openclicky account: assemblyai runs on openclicky's key";
                return null;
        }
    }

    /// <summary>
    /// Parakeet's half of `UnavailableReason`. Its provider reads the same function, so the provider
    /// and the resolver can never disagree about whether Parakeet can run.
    /// </summary>
    public static string? ParakeetUnavailableReason(bool isInstalled, string? bareLanguageCode)
    {
        if (!ParakeetFacts.Hears(bareLanguageCode)) return ParakeetLanguageNotHeardReason;
        return isInstalled ? null : $"needs its {ParakeetFacts.DownloadSizeText} download";
    }

    /// <summary>Installed but pinned to a language Parakeet does not hear: the island says this, not "download".</summary>
    public const string ParakeetLanguageNotHeardReason = "parakeet doesn't hear this language; pick auto-detect or a european language";

    /// <summary>Sarvam's half of `UnavailableReason`; its provider reads the same function.</summary>
    public static string? SarvamUnavailableReason(string? sarvamKey) =>
        string.IsNullOrWhiteSpace(sarvamKey) ? "needs your sarvam key" : null;

    /// <summary>The ways to get a model, for a Hey Clicky edit or a history question asked with none set up.</summary>
    public const string HowToGetAModelSentence = "one of your own keys (settings → account), or an openclicky account";

    /// <summary>
    /// The model that polishes takes and answers Hey Clicky: Sarvam with a key, else the person's
    /// own OpenAI, Anthropic, Groq, Gemini, Mistral, OpenRouter or xAI key, else the backend with an
    /// account, else none. (The Mac asks Apple's on-device model first; Windows has none.)
    /// </summary>
    public static ITakePolisher? MakePolisher() =>
        MakePolisher(OpenClickyConfiguration.Settings.SarvamKey, ProviderRouter.CurrentCredentials(), AccountCapabilities.Current().Kind);

    public static ITakePolisher? MakePolisher(string? sarvamKey, ProviderCredentials credentials, AccountKind kind)
    {
        var trimmedSarvamKey = sarvamKey?.Trim() ?? "";
        if (trimmedSarvamKey.Length > 0) return new SarvamTakePolisher(new SarvamSpeechClient(trimmedSarvamKey));
        // Own keys first: an OpenAI key polishes on OpenAI (BackendTakePolisher routes it there),
        // an Anthropic key alone polishes on Claude Haiku at Anthropic, any other answer key on its
        // own vendor. No account needed for any of them.
        if (credentials.OpenAIKey is null)
        {
            if (credentials.AnthropicKey is { } anthropicKey) return new AnthropicTakePolisher(anthropicKey);
            if (AnswerKeyTakePolisher.FirstAvailable(credentials) is { } answerKeyPolisher) return answerKeyPolisher;
        }
        return kind switch
        {
            AccountKind.Account => new AccountTakePolisher(),
            AccountKind.OwnKeys => new BackendTakePolisher(),
            _ => null,
        };
    }

    /// <summary>
    /// Whether a take's words may go to a model: the switch, and — for the engines whose promise is
    /// that nothing leaves the PC — only when that was explicitly allowed. Every reader of "clean up
    /// my words with ai" asks here (the styles switch, the record page, the take, live typing, the
    /// smoke run), so the switch never shows one thing while takes do another.
    /// </summary>
    public static bool WantsModelPolish(DictationSettings settings) => WantsModelPolish(settings, settings.Engine);

    /// <summary>The same, for an engine other than the chosen one (the smoke run names its own).</summary>
    public static bool WantsModelPolish(DictationSettings settings, DictationEngineChoice engine)
    {
        if (!settings.PolishWithModel) return false;
        if (engine.IsOnDevice()) return settings.PolishOfflineTakes;
        return true;
    }
}

/// <summary>Hey Clicky: an instruction applied to a piece of text by the configured model.</summary>
public static class HeyClickyEditor
{
    public static string SystemPrompt(TakeFormattingContext context) =>
        "You edit text for someone who dictated an instruction. Apply the instruction to the text and return only the edited text: no preamble, no quotes, no explanation, no markdown fences. Keep the language and script of the text unless the instruction changes them. "
        + $"Style for this app ({context.Style.Name}): {context.Style.Rules}";

    public static string UserPrompt(string selection, string instruction) => $"Instruction: {instruction}\n\nText:\n{selection}";

    public static async Task<string> EditAsync(string selection, string instruction, TakeFormattingContext context, ITakePolisher polisher,
                                               CancellationToken cancellationToken = default)
    {
        var answer = await polisher.PolishAsync(SystemPrompt(context), UserPrompt(selection, instruction), cancellationToken).ConfigureAwait(false);
        var cleaned = TakeFormatter.UnwrapModelAnswer(answer);
        // Any model can make the edit, so the error names the one that did, not Sarvam's.
        if (cleaned.Length == 0) throw TakePolishException.EmptyAnswer(polisher.DisplayName);
        return cleaned;
    }
}
