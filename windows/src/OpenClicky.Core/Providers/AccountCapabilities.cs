// Ports AccountCapabilities.swift: one answer to "what can this person use?", so every lane asks
// the same question: a free account (signed in, on the grant: no realtime, no agent), own keys
// (everything, unmetered), or signed out (offline dictation only).
//
// The backend decides which: GET /billing/me answers `plan` ("grant", "byok" or "unmetered"),
// AccountProfileStore keeps the last answer (in Preferences, under the Mac's key), and nothing is
// taken away on a guess — until the backend has said "grant", a signed-in PC keeps everything.

using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Providers;

public enum AccountKind { OwnKeys, Account, SignedOut }

public sealed record AccountCapabilities(AccountKind Kind)
{
    public static AccountCapabilities Current(OpenClickyShellSettings? settings = null, Preferences? preferences = null)
    {
        settings ??= OpenClickyConfiguration.Settings;
        preferences ??= Preferences.Shared;
        var signedIn = settings.Token.Trim().Length > 0;
        return new AccountCapabilities(KindFor(
            OpenClickyConfiguration.UsesOwnKeysFor(settings), signedIn, preferences.GetString(AccountProfileStore.PlanKey)));
    }

    /// <summary>
    /// An own OpenAI key always means own keys; no token means signed out; otherwise the backend's
    /// last answer: only "grant" is a free account. "byok", "unmetered" and no answer yet all keep
    /// full capabilities.
    /// </summary>
    public static AccountKind KindFor(bool ownOpenAIKey, bool signedIn, string? plan)
    {
        if (ownOpenAIKey) return AccountKind.OwnKeys;
        if (!signedIn) return AccountKind.SignedOut;
        return plan == "grant" ? AccountKind.Account : AccountKind.OwnKeys;
    }

    public bool UsesRealtime => Kind == AccountKind.OwnKeys;
    public bool UsesAgent => Kind == AccountKind.OwnKeys;
    public bool HearsOnDevice => Kind != AccountKind.OwnKeys;
    public string PolishPath => Kind == AccountKind.OwnKeys ? "/v1/chat/completions" : "/v1/polish";
    /// <summary>The Agents tab's line under a disabled composer; signed out has nothing to explain.</summary>
    public string? AgentUnavailableHint => Kind == AccountKind.Account ? "agent tasks need your own key for now." : null;
    /// <summary>"teach a skill" drafts the skill on OpenAI (/skills/create), which the grant does not cover.</summary>
    public string? SkillTeachingUnavailableHint => Kind == AccountKind.Account ? "teaching skills needs your own key for now." : null;
    /// <summary>At most two screenshots go with an account's question (the backend refuses more).</summary>
    public int? MaxAskScreens => Kind == AccountKind.Account ? 2 : null;
}

public enum AccountLimitError
{
    [RawValue("personal_limit")] PersonalLimit,
    [RawValue("daily_limit")] DailyLimit,
    [RawValue("monthly_budget")] MonthlyBudget,
    [RawValue("not_on_plan")] NotOnPlan,
    [RawValue("accounts_full")] AccountsFull,
    [RawValue("blocked")] Blocked,
    [RawValue("tts_budget")] TtsBudget,
    [RawValue("confirm_email")] ConfirmEmail,
    [RawValue("device_limit")] DeviceLimit,
}

public static class AccountLimitErrors
{
    public static AccountLimitError? From(int status, byte[] body)
    {
        if (status != 402) return null;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject json || json["error"] is not JsonValue errorValue
                || !errorValue.TryGetValue<string>(out var raw)) return null;
            return RawValues.FromRawValue<AccountLimitError>(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Message(this AccountLimitError error) => error switch
    {
        AccountLimitError.PersonalLimit => "you've used this month's free allowance, it comes back on the 1st. dictation still works.",
        AccountLimitError.DailyLimit => "you've used today's free allowance, it comes back tomorrow. dictation still works.",
        AccountLimitError.MonthlyBudget => "openclicky's free allowance is used up for now, you can keep going with your own key.",
        AccountLimitError.NotOnPlan => "that needs your own key for now.",
        AccountLimitError.AccountsFull => "openclicky's free accounts are full right now, you can use your own key instead.",
        AccountLimitError.Blocked => "this account is paused. dictation still works.",
        AccountLimitError.TtsBudget => "",
        AccountLimitError.ConfirmEmail => "confirm your email to keep going: type the code from the email in settings, account.",
        AccountLimitError.DeviceLimit => "this pc already has two openclicky accounts, sign in with one of them.",
        _ => "",
    };

    /// <summary>A grant route answered 503 `unavailable`: the ledger itself could not be reached.</summary>
    public const string ServiceTroubleMessage = "openclicky's free service is having trouble, try again in a minute.";

    /// <summary>True for the backend's 503 `{"error":"unavailable"}` on a grant route.</summary>
    public static bool IsServiceTrouble(int status, byte[] body)
    {
        if (status != 503) return false;
        try
        {
            return JsonNode.Parse(body) is JsonObject json && json["error"] is JsonValue value
                && value.TryGetValue<string>(out var error) && error == "unavailable";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public enum UsageLevel { Plenty, RunningLow, UsedUp }

public sealed partial record BillingSummary
{
    /// <summary>Requests are metered on the grant. Older backends send no plan: byok was their only signal.</summary>
    public bool IsMetered => Plan is not null ? Plan == "grant" : !Byok;
    /// <summary>A grant login with no OpenClicky account row: the backend refuses it until someone adds it.</summary>
    public bool IsSwitchedOff => Plan == "grant" && OnPlan == false && !Blocked;
    public const string SwitchedOffMessage = "this login isn't switched on for openclicky yet. write to hello@flowsxr.com, or use your own key.";
    public const string RunningLowNotice = "your free allowance is running low this month, dictation still works offline.";

    /// <summary>
    /// The month to remember when the "running low" notice is due: a metered account past 80% (and
    /// not yet used up) whose notice has not been shown this month. Null when nothing is due.
    /// </summary>
    public string? RunningLowNoticeMonth(string? lastNoticeMonth)
    {
        if (!IsMetered || Level != UsageLevel.RunningLow || MonthEnd.Length == 0 || MonthEnd == lastNoticeMonth) return null;
        return MonthEnd;
    }

    public double FractionUsed
    {
        get
        {
            if (MonthlyLimitUsd <= 0) return 0;
            return Math.Min(1, Math.Round(SpentMonthUsd / MonthlyLimitUsd * 100, MidpointRounding.AwayFromZero) / 100);
        }
    }

    public UsageLevel Level
    {
        get
        {
            if (BudgetExhausted || Blocked || FractionUsed >= 1 || (DailyLimitUsd > 0 && SpentTodayUsd >= DailyLimitUsd)) return UsageLevel.UsedUp;
            return FractionUsed >= 0.8 ? UsageLevel.RunningLow : UsageLevel.Plenty;
        }
    }
}

/// <summary>
/// The backend's last word on this login (GET /billing/me), fetched at launch, when credentials
/// change and after sign-in, and kept in Preferences so the next launch starts from it.
/// </summary>
public sealed partial class AccountProfileStore : ObservableObject
{
    private static readonly Lazy<AccountProfileStore> SharedInstance = new(() => new AccountProfileStore(Preferences.Shared));
    public static AccountProfileStore Shared => SharedInstance.Value;

    public const string PlanKey = "openclicky.account.plan";
    public const string RunningLowNoticeKey = "openclicky.account.runningLowNoticeMonth";

    /// <summary>Raised when the backend's answer changed what this login can use.</summary>
    public static event Action? ProfileChanged;

    /// <summary>Whether a grant login is switched on (null until the backend has said).</summary>
    [ObservableProperty] private bool? onPlan;

    /// <summary>Whether the account's email is confirmed, from the last /billing/me answer (null until then).</summary>
    public bool? LastConfirmed { get; private set; }

    /// <summary>Shows the once-a-month "running low" notice (the companion's quiet caption).</summary>
    public Action<string>? ShowNotice { get; set; }

    private readonly Preferences preferences;
    private AccountKind lastKind;
    private bool started;

    public AccountProfileStore(Preferences preferences)
    {
        this.preferences = preferences;
        lastKind = AccountCapabilities.Current(preferences: preferences).Kind;
    }

    public void Start()
    {
        if (started) return;
        started = true;
        OpenClickyConfiguration.CredentialsChanged += Refresh;
        // A shell.json that stopped reading is said in the same quiet caption.
        OpenClickyConfiguration.SettingsFileUnreadable += sentence => ShowNotice?.Invoke(sentence);
        Refresh();
    }

    public void Refresh()
    {
        if (!OpenClickyConfiguration.HasAccountToken)
        {
            OnPlan = null;
            LastConfirmed = null;
            NoteKind();
            return;
        }
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try { Absorb(await BillingStatusModel.FetchSummaryAsync()); }
        catch (Exception error) { AppLog.Write($"account profile: /billing/me failed: {error.Message}"); }
    }

    /// <summary>Every /billing/me answer lands here, whoever asked for it.</summary>
    public void Absorb(BillingSummary summary)
    {
        if (summary.Plan is not null) preferences.Set(PlanKey, summary.Plan);
        OnPlan = summary.OnPlan;
        LastConfirmed = summary.Confirmed;
        NoteKind();
        if (summary.RunningLowNoticeMonth(preferences.GetString(RunningLowNoticeKey)) is { } month)
        {
            preferences.Set(RunningLowNoticeKey, month);
            ShowNotice?.Invoke(BillingSummary.RunningLowNotice);
        }
    }

    private void NoteKind()
    {
        var kind = AccountCapabilities.Current(preferences: preferences).Kind;
        if (kind == lastKind) return;
        lastKind = kind;
        ProfileChanged?.Invoke();
    }
}
