// The rules and sentences the Settings rows read, kept out of the views so they can be tested:
// the clean-up switch (DictationCleanUpRow), the Your words page's engine sentence
// (WordsPageView.engineSentenceInSentenceCase, sentenceCased), the provider keys of Keys & Plans
// (KeyProvider), the plan rows (PlanSubscriptionRow.detail), the account rows
// (BackendEngineUsageRow, PlanSummary, the sidebar foot), "Answers come from"'s "Now:" line, the
// account form's rules (EmailAccountForm.canSubmitEmail / canSubmitCode, AccountWorkSlot), and the
// window's appearance (followAppearanceSetting).

using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Settings;

public static class DictationCleanUp
{
    /// <summary>
    /// The switch is on exactly when takes are polished: an upgrader whose two old settings disagree
    /// sees what the takes do, not one half of it.
    /// </summary>
    public static bool SwitchShowsOn(DictationSettings settings) => DictationEngineResolver.WantsModelPolish(settings);

    /// <summary>One switch sets both settings underneath (online takes, and takes heard on this PC).</summary>
    public static void SetSwitch(DictationSettings settings, bool isOn)
    {
        settings.PolishWithModel = isOn;
        settings.ChoosePolishOfflineTakes(isOn);
    }

    /// <summary>The row's copy. There is no on-device model on Windows, so the words are always tidied online.</summary>
    public static string Detail(bool hasModel)
    {
        var needsModelNote = hasModel ? "" : " Needs an AI key (Settings → Keys & Plans) or a free OpenClicky account.";
        return "Adds punctuation and fixes numbers. Your words are sent online to be tidied, so they appear when you let go. Off: words type straight in as you speak." + needsModelNote;
    }
}

public static class WordsPageSentences
{
    /// <summary>Where a take's voice and words go, as the Your words page shows it.</summary>
    public static string EngineSentenceInSentenceCase(DictationSettings settings, bool hasPolisher)
    {
        var history = settings.Incognito ? "Incognito: nothing is saved." : "Your history stays on this PC.";
        if (!settings.Engine.IsOnDevice()) return settings.Engine.AudioDestinationSentence() + " " + history;
        if (DictationEngineResolver.WantsModelPolish(settings) && hasPolisher)
            return "Your voice stays on this PC; the words go to a model for polish. " + history;
        return settings.Incognito ? "Your voice stays on this PC, and nothing is saved." : "Your voice and your history stay on this PC.";
    }

    /// <summary>The same, lower case (the rule the Mac's tests pin down).</summary>
    public static string EngineSentence(DictationSettings settings, bool hasPolisher) =>
        EngineSentenceInSentenceCase(settings, hasPolisher).ToLower(CultureInfo.InvariantCulture);

    private static readonly (string Lowercase, string Proper)[] ProperNames =
    [
        ("openclicky", "OpenClicky"), ("openai", "OpenAI"), ("assemblyai", "AssemblyAI"), ("sarvam", "Sarvam"), ("pc", "PC"), ("windows", "Windows"),
    ];

    /// <summary>
    /// The resolver's reasons are written in lower case ("needs your sarvam key") for the orb; this
    /// page writes sentences, so the first letter and the product names get their capitals.
    /// </summary>
    public static string SentenceCased(string lowercaseSentence)
    {
        var sentence = lowercaseSentence;
        foreach (var (lowercase, proper) in ProperNames)
        {
            sentence = Regex.Replace(sentence, $@"\b{lowercase}\b", proper);
        }
        return sentence.Length == 0 ? sentence : char.ToUpper(sentence[0], CultureInfo.CurrentCulture) + sentence[1..];
    }

    /// <summary>"1 word" / "12 words": what the try-it box counts.</summary>
    public static string WordCount(string text)
    {
        var count = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return count == 1 ? "1 word" : $"{count} words";
    }

    public static string Plural(int count, string unit) => count == 1 ? $"1 {unit}" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} {unit}s";

    /// <summary>"Couldn't finish: no key", or "Couldn't finish".</summary>
    public static string FailureLine(TakeRecord take)
    {
        var reason = take.FailureReason?.Trim();
        return string.IsNullOrEmpty(reason) ? "Couldn't finish" : $"Couldn't finish: {reason}";
    }

    /// <summary>The take's detail line: "13 Oct 2026, 3:04 PM · 4 s · sarvam · Pasted".</summary>
    public static string TakeDetailLine(TakeRecord take, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var parts = new List<string> { take.CreatedAt.LocalDateTime.ToString("d MMM yyyy, t", culture) };
        if (take.DurationSeconds > 0) parts.Add($"{Math.Round(take.DurationSeconds).ToString(CultureInfo.InvariantCulture)} s");
        if (!string.IsNullOrEmpty(take.Engine)) parts.Add(take.Engine);
        switch (take.PasteOutcome)
        {
            case TakePasteOutcome.Verified: parts.Add("Pasted"); break;
            case TakePasteOutcome.Posted: parts.Add("Pasted (unconfirmed)"); break;
            case TakePasteOutcome.LeftInOrb: parts.Add("Left in the orb"); break;
            case TakePasteOutcome.LeftOnPasteboard: parts.Add("Copied to the clipboard"); break;
        }
        return string.Join(" · ", parts);
    }

    public sealed record DayGroup(string Title, DateTime Day, IReadOnlyList<TakeRecord> Takes);

    /// <summary>Takes a section per day, newest day first: "Today", "Yesterday", else "Monday, 12 Oct".</summary>
    public static IReadOnlyList<DayGroup> GroupedByDay(IEnumerable<TakeRecord> takes, DateTime? today = null, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var todayDate = (today ?? DateTime.Now).Date;
        return takes
            .GroupBy(take => take.CreatedAt.LocalDateTime.Date)
            .OrderByDescending(group => group.Key)
            .Select(group =>
            {
                var title = group.Key == todayDate ? "Today"
                    : group.Key == todayDate.AddDays(-1) ? "Yesterday"
                    : group.Key.ToString("dddd, d MMM", culture);
                return new DayGroup(title, group.Key, group.ToList());
            })
            .ToList();
    }

    /// <summary>When and where an answer's source take was said: "3:04 PM · Outlook", with the date before yesterday.</summary>
    public static string SourceLabel(TakeRecord take, string? appName, DateTime? today = null, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var todayDate = (today ?? DateTime.Now).Date;
        var created = take.CreatedAt.LocalDateTime;
        var timeText = created.ToString("t", culture);
        var when = created.Date >= todayDate.AddDays(-1) ? timeText : created.ToString("d MMM", culture) + " " + timeText;
        return appName is null ? when : $"{when} · {appName}";
    }
}

/// <summary>A provider a key can be saved for (Keys & Plans): where it lives in shell.json and how its row reads.</summary>
public enum ProviderKey
{
    OpenAI, Anthropic, OpenRouter, Gemini, Groq, Mistral, Xai, Sarvam, ElevenLabs, GoogleCloud, AmazonPolly, Spotify,
}

public static class ProviderKeys
{
    public static IReadOnlyList<ProviderKey> AllCases { get; } = Enum.GetValues<ProviderKey>();

    public static string Title(this ProviderKey provider) => provider switch
    {
        ProviderKey.OpenAI => "OpenAI",
        ProviderKey.Anthropic => "Anthropic (Claude)",
        ProviderKey.OpenRouter => "OpenRouter",
        ProviderKey.Gemini => "Google Gemini",
        ProviderKey.Groq => "Groq",
        ProviderKey.Mistral => "Mistral",
        ProviderKey.Xai => "xAI (Grok)",
        ProviderKey.Sarvam => "Sarvam",
        ProviderKey.ElevenLabs => "ElevenLabs",
        ProviderKey.GoogleCloud => "Google Cloud voice",
        ProviderKey.AmazonPolly => "Amazon Polly",
        _ => "Spotify",
    };

    public static string Detail(this ProviderKey provider) => provider switch
    {
        ProviderKey.OpenAI => "Answers, fast voice and hands-free.",
        ProviderKey.Anthropic => "Claude answers your questions about the screen.",
        ProviderKey.OpenRouter => "One key for Claude, GPT, Gemini, Llama and more.",
        ProviderKey.Gemini => "Answers and a voice. From Google AI Studio.",
        ProviderKey.Groq => "Very fast answers on open models.",
        ProviderKey.Mistral => "Mistral’s own models.",
        ProviderKey.Xai => "Grok, from xAI.",
        ProviderKey.Sarvam => "Speaks answers in a natural voice (Bulbul), and the Sarvam dictation engine for Indian languages.",
        ProviderKey.ElevenLabs => "Speaks answers in ElevenLabs’ voice, on your own ElevenLabs account.",
        ProviderKey.GoogleCloud => "Speaks answers. A Google Cloud API key with the Text-to-Speech API switched on; an AI Studio key won’t do.",
        ProviderKey.AmazonPolly => "Speaks answers in Amazon’s neural voices. Paste access key ID:secret key, then :region if it isn’t us-east-1.",
        _ => "Finds the song you asked for. Paste client id:client secret from developer.spotify.com; without it OpenClicky’s own lookup is used.",
    };

    public static string Placeholder(this ProviderKey provider) => provider switch
    {
        ProviderKey.OpenAI => "sk-…",
        ProviderKey.Anthropic => "sk-ant-…",
        ProviderKey.OpenRouter => "sk-or-…",
        ProviderKey.Gemini => "AIza…",
        ProviderKey.Groq => "gsk_…",
        ProviderKey.Mistral => "Mistral key",
        ProviderKey.Xai => "xai-…",
        ProviderKey.Sarvam => "Sarvam key",
        ProviderKey.ElevenLabs => "sk_…",
        ProviderKey.GoogleCloud => "AIza…",
        ProviderKey.AmazonPolly => "AKIA…:secret",
        _ => "id:secret",
    };

    public static string? StoredKey(this ProviderKey provider, OpenClickyShellSettings shell) => provider switch
    {
        ProviderKey.OpenAI => shell.OpenaiApiKey,
        ProviderKey.Anthropic => shell.AnthropicApiKey,
        ProviderKey.OpenRouter => shell.OpenRouterApiKey,
        ProviderKey.Gemini => shell.GeminiApiKey,
        ProviderKey.Groq => shell.GroqApiKey,
        ProviderKey.Mistral => shell.MistralApiKey,
        ProviderKey.Xai => shell.XaiApiKey,
        ProviderKey.Sarvam => shell.SarvamKey,
        ProviderKey.ElevenLabs => shell.ElevenLabsApiKey,
        ProviderKey.GoogleCloud => shell.GoogleCloudApiKey,
        ProviderKey.AmazonPolly => shell.AmazonPollyCredentials,
        _ => shell.SpotifyCredentials,
    };

    public static void Store(this ProviderKey provider, OpenClickyShellSettings shell, string? key)
    {
        switch (provider)
        {
            case ProviderKey.OpenAI: shell.OpenaiApiKey = key; break;
            case ProviderKey.Anthropic: shell.AnthropicApiKey = key; break;
            case ProviderKey.OpenRouter: shell.OpenRouterApiKey = key; break;
            case ProviderKey.Gemini: shell.GeminiApiKey = key; break;
            case ProviderKey.Groq: shell.GroqApiKey = key; break;
            case ProviderKey.Mistral: shell.MistralApiKey = key; break;
            case ProviderKey.Xai: shell.XaiApiKey = key; break;
            case ProviderKey.Sarvam: shell.SarvamKey = key; break;
            case ProviderKey.ElevenLabs: shell.ElevenLabsApiKey = key; break;
            case ProviderKey.GoogleCloud: shell.GoogleCloudApiKey = key; break;
            case ProviderKey.AmazonPolly: shell.AmazonPollyCredentials = key; break;
            default: shell.SpotifyCredentials = key; break;
        }
    }

    /// <summary>The saved key, trimmed; null when none.</summary>
    public static string? SavedKey(this ProviderKey provider, OpenClickyShellSettings shell)
    {
        var key = provider.StoredKey(shell)?.Trim();
        return string.IsNullOrEmpty(key) ? null : key;
    }

    /// <summary>"••••abcd": the last four characters only.</summary>
    public static string Masked(string key) => "••••" + (key.Length <= 4 ? key : key[^4..]);

    /// <summary>The source this key answers questions as; the voice-only keys answer nothing.</summary>
    public static AnswerSource? AnswerSource(this ProviderKey provider) => provider switch
    {
        ProviderKey.OpenAI => Providers.AnswerSource.OpenAI,
        ProviderKey.Anthropic => Providers.AnswerSource.Anthropic,
        ProviderKey.OpenRouter => Providers.AnswerSource.OpenRouter,
        ProviderKey.Gemini => Providers.AnswerSource.Gemini,
        ProviderKey.Groq => Providers.AnswerSource.Groq,
        ProviderKey.Mistral => Providers.AnswerSource.Mistral,
        ProviderKey.Xai => Providers.AnswerSource.Xai,
        _ => null,
    };

    /// <summary>The voice this key speaks answers in (SpeechVoiceChoice raw value), for its Test button.</summary>
    public static string? SpeechVoiceId(this ProviderKey provider) => provider switch
    {
        ProviderKey.Sarvam => "sarvam",
        ProviderKey.ElevenLabs => "elevenlabs",
        ProviderKey.GoogleCloud => "google",
        ProviderKey.AmazonPolly => "polly",
        _ => null,
    };

    /// <summary>The providers the "Add a key" menu offers: those without a key; Sarvam only in India.</summary>
    public static IReadOnlyList<ProviderKey> Unsaved(OpenClickyShellSettings shell, AppRegion region) =>
        AllCases.Where(provider => provider.SavedKey(shell) is null && (provider != ProviderKey.Sarvam || region.OffersIndianOptions())).ToList();

    public static IReadOnlyList<ProviderKey> Saved(OpenClickyShellSettings shell) =>
        AllCases.Where(provider => provider.SavedKey(shell) is not null).ToList();
}

public static class PlanSubscription
{
    public static string GetItUrl(AnswerSource plan) =>
        plan == AnswerSource.ClaudeCodePlan ? "https://claude.com/product/claude-code" : "https://developers.openai.com/codex/cli";

    /// <summary>The plan row's copy (PlanSubscriptionRow.detail); null status is still checking.</summary>
    public static string Detail(AnswerSource plan, PlanCliStatus? status, bool isOn)
    {
        var command = plan == AnswerSource.ClaudeCodePlan ? "claude" : "codex login";
        return status switch
        {
            null => "Checking…",
            PlanCliStatus.NotInstalled => plan == AnswerSource.ClaudeCodePlan ? "Claude Code isn’t installed on this PC." : "Codex isn’t installed on this PC.",
            PlanCliStatus.InstalledSignedOut => $"Installed, not signed in. Run “{command}” in a terminal and sign in.",
            _ => isOn ? "Signed in. Answering with your plan when it’s the one picked (Settings → Clicky)." : "Signed in. Switch on to answer with your plan.",
        };
    }

    public static bool IsOn(AnswerSource plan, OpenClickyShellSettings shell) =>
        plan == AnswerSource.ClaudeCodePlan ? shell.UseClaudeCodePlan == true : shell.UseCodexPlan == true;
}

public static class AccountRows
{
    /// <summary>"Now: …" under "Answers come from": who answers a Claude-lane question with these credentials.</summary>
    public static string NowAnswering(ProviderCredentials credentials)
    {
        var route = ProviderRouter.Route(ProviderCapability.Claude, credentials);
        return route.Kind switch
        {
            ProviderRouteKind.AnthropicDirect => AnswerSource.Anthropic.DisplayName(),
            ProviderRouteKind.Backend => AnswerSource.OpenClickyAccount.DisplayName(),
            ProviderRouteKind.AnswerProvider => route.Source!.Value.DisplayName(),
            ProviderRouteKind.OpenAIDirect => AnswerSource.OpenAI.DisplayName(),
            _ => "nothing set up yet",
        };
    }

    /// <summary>The "OpenAI and AssemblyAI" row's copy and badge.</summary>
    public static (string Detail, string Badge) BackendEngineUsage(bool usesOwnKeys, bool hasAccountToken)
    {
        if (usesOwnKeys)
        {
            return (hasAccountToken
                ? "OpenAI runs straight on your own key, not metered. AssemblyAI is on your account."
                : "OpenAI runs straight on your own key, not metered. AssemblyAI needs an account.", "Your key");
        }
        return hasAccountToken
            ? ("Through OpenClicky, counted against your free allowance.", "Allowance")
            : ("Through OpenClicky. Needs an account or your own OpenAI key.", "Needs account");
    }

    /// <summary>What the "Free allowance" row shows.</summary>
    public sealed record PlanSummaryState(string Trailing, string? Caption, double? FractionUsed, string? Details);

    public static PlanSummaryState PlanSummary(BillingSummary? summary, string? errorText, bool usesOwnKeys, bool hasAccountToken)
    {
        if (summary is not null)
        {
            if (summary.Byok) return new("Your keys", "OpenClicky meters nothing: every request runs on your own key.", null, null);
            if (!summary.IsMetered) return new("Not metered", "This backend meters nothing: every request runs without an allowance.", null, null);
            if (summary.IsSwitchedOff) return new("Paused", BillingSummary.SwitchedOffMessage, null, null);
            var fraction = Math.Clamp(summary.FractionUsed, 0, 1);
            return new($"{(int)Math.Round(summary.FractionUsed * 100, MidpointRounding.AwayFromZero)}% used", null, fraction, summary.UsageDetails());
        }
        if (usesOwnKeys) return new("Your keys", "No account needed: your keys in shell.json go straight to OpenAI and Anthropic, and nothing is metered.", null, null);
        // No sign-in, so there is no allowance to fetch (waiting for a summary said Loading… forever).
        if (!hasAccountToken) return new("Sign in to see usage", null, 0, null);
        return new(errorText ?? "Loading…", null, null, null);
    }

    /// <summary>"prasanth" for a signed-in address, else the first name and the last initial ("Prasanth S."), else "You".</summary>
    public static string AccountName(string? email, string? fullUserName)
    {
        if (email is not null) return email.Split('@')[0];
        var nameParts = (fullUserName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (nameParts.Length == 0) return "You";
        if (nameParts.Length > 1) return $"{nameParts[0]} {char.ToUpper(nameParts[^1][0], CultureInfo.CurrentCulture)}.";
        return nameParts[0];
    }

    public static string AccountInitial(string accountName) =>
        accountName.Length == 0 ? "?" : accountName[..1].ToUpper(CultureInfo.CurrentCulture);

    /// <summary>The sidebar foot's second line.</summary>
    public static string AccountStanding(bool isSignedInWithEmail, bool isGuestWithoutEmail) =>
        isSignedInWithEmail ? "Signed in" : isGuestWithoutEmail ? "Trying it free" : "Personal · no account";

    /// <summary>"••••abcd", or "None" with no token.</summary>
    public static string MaskedToken(string? token) => token is null ? "None" : ProviderKeys.Masked(token);
}

public static class EmailAccountFormRules
{
    /// <summary>Something before an @ and a dot after it.</summary>
    public static bool CanSubmitEmail(string raw)
    {
        var email = raw.Trim();
        var at = email.IndexOf('@');
        if (at <= 0) return false;
        return email[(at + 1)..].Contains('.');
    }

    /// <summary>Exactly six digits, spaces ignored.</summary>
    public static bool CanSubmitCode(string raw)
    {
        var digits = new string(raw.Where(character => !char.IsWhiteSpace(character)).ToArray());
        return digits.Length == 6 && digits.All(char.IsAsciiDigit);
    }
}

/// <summary>
/// Holds the one sign-up or sign-in a form runs at a time. Start refuses while one is running and
/// says so synchronously, before the work has had a chance to run a single line.
/// </summary>
public sealed class AccountWorkSlot
{
    private CancellationTokenSource? cancellation;

    public bool IsRunning { get; private set; }

    public bool Start(Func<CancellationToken, Task> work)
    {
        if (IsRunning) return false;
        IsRunning = true;
        var source = new CancellationTokenSource();
        cancellation = source;
        _ = RunAsync(work, source);
        return true;
    }

    private async Task RunAsync(Func<CancellationToken, Task> work, CancellationTokenSource source)
    {
        try { await work(source.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Write($"account: work failed: {error.Message}"); }
        finally
        {
            IsRunning = false;
            if (cancellation == source) cancellation = null;
            source.Dispose();
        }
    }

    public void Cancel()
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}

/// <summary>Light or dark for a window, as the dictation Appearance setting says (System follows Windows).</summary>
public static class WindowAppearance
{
    public static bool IsDark(DictationAppearance appearance, bool systemIsDark) => appearance switch
    {
        DictationAppearance.Dark => true,
        DictationAppearance.Light => false,
        _ => systemIsDark,
    };

    /// <summary>
    /// Keeps a window light, dark or matching the system as the setting says: applied now, and again
    /// on every change of the setting (followAppearanceSetting). Dispose to stop following.
    /// </summary>
    public static IDisposable Follow(DictationSettings settings, Action<DictationAppearance> apply)
    {
        apply(settings.Appearance);
        void OnChanged(object? sender, PropertyChangedEventArgs arguments)
        {
            if (arguments.PropertyName == nameof(DictationSettings.Appearance)) apply(settings.Appearance);
        }
        settings.PropertyChanged += OnChanged;
        return new Subscription(() => settings.PropertyChanged -= OnChanged);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? disposeAction = dispose;

        public void Dispose() => Interlocked.Exchange(ref disposeAction, null)?.Invoke();
    }
}
