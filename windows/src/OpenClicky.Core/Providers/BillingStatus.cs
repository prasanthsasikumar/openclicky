// Ports BillingStatus.swift: what the account has used (GET /billing/me) and the sentences that say
// it: the free allowance this month and today, the shared budget, the spoken characters. The
// island's account rows read these sentences.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Providers;

public sealed partial record BillingSummary
{
    [JsonPropertyName("byok"), JsonRequired] public bool Byok { get; init; }
    [JsonPropertyName("spentMonthUsd"), JsonRequired] public double SpentMonthUsd { get; init; }
    [JsonPropertyName("monthlyLimitUsd"), JsonRequired] public double MonthlyLimitUsd { get; init; }
    [JsonPropertyName("spentTodayUsd"), JsonRequired] public double SpentTodayUsd { get; init; }
    [JsonPropertyName("dailyLimitUsd"), JsonRequired] public double DailyLimitUsd { get; init; }
    [JsonPropertyName("ttsCharsMonth"), JsonRequired] public int TtsCharsMonth { get; init; }
    [JsonPropertyName("ttsCharsLimit"), JsonRequired] public int TtsCharsLimit { get; init; }
    [JsonPropertyName("monthEnd"), JsonRequired] public string MonthEnd { get; init; } = "";
    [JsonPropertyName("dayEnd"), JsonRequired] public string DayEnd { get; init; } = "";
    [JsonPropertyName("budgetExhausted"), JsonRequired] public bool BudgetExhausted { get; init; }
    [JsonPropertyName("blocked"), JsonRequired] public bool Blocked { get; init; }
    /// <summary>"grant", "byok" or "unmetered"; older backends send none of these.</summary>
    [JsonPropertyName("plan")] public string? Plan { get; init; }
    /// <summary>For a grant login: whether it has an OpenClicky account (and is not paused).</summary>
    [JsonPropertyName("onPlan")] public bool? OnPlan { get; init; }
    /// <summary>Email-first accounts: false until the confirmation link is clicked; older backends omit it.</summary>
    [JsonPropertyName("confirmed")] public bool? Confirmed { get; init; }
    /// <summary>The PC's starter allowance (spend before confirming) and its cap.</summary>
    [JsonPropertyName("guestSpentUsd")] public double? GuestSpentUsd { get; init; }
    [JsonPropertyName("guestLimitUsd")] public double? GuestLimitUsd { get; init; }
    /// <summary>Masked, e.g. "p•••@flowsxr.com".</summary>
    [JsonPropertyName("email")] public string? Email { get; init; }

    public bool IsUnconfirmed => Confirmed == false;

    public static BillingSummary Decode(byte[] json) =>
        JsonSerializer.Deserialize<BillingSummary>(json) ?? throw new JsonException("empty billing summary");

    public static BillingSummary Decode(string json) =>
        JsonSerializer.Deserialize<BillingSummary>(json) ?? throw new JsonException("empty billing summary");

    public AllowanceStanding AllowanceStanding
    {
        get
        {
            if (Blocked) return AllowanceStanding.Paused;
            if (Confirmed == false) return AllowanceStanding.Unconfirmed;
            if (FractionUsed >= 1) return AllowanceStanding.UsedUpThisMonth;
            if (BudgetExhausted) return AllowanceStanding.SharedBudgetUsedUp;
            if (DailyLimitUsd > 0 && SpentTodayUsd >= DailyLimitUsd) return AllowanceStanding.UsedUpToday;
            return Level == UsageLevel.RunningLow ? AllowanceStanding.RunningLow : AllowanceStanding.Plenty;
        }
    }

    /// <summary>
    /// One calm line under the usage bar and in the island ("plenty left this month · resets on nov
    /// 1"). `resetDay` is `MonthResetDay()`; null when the date could not be read.
    /// </summary>
    public string AllowanceSentence(string? resetDay)
    {
        var onResetDay = resetDay is null ? "on the 1st" : $"on {resetDay}";
        switch (AllowanceStanding)
        {
            case AllowanceStanding.Plenty: return $"plenty left this month · resets {onResetDay}";
            case AllowanceStanding.RunningLow: return $"running low this month · resets {onResetDay}";
            case AllowanceStanding.UsedUpThisMonth: return $"this month's free allowance is used, it comes back {onResetDay}";
            case AllowanceStanding.UsedUpToday: return "today's free allowance is used, it comes back tomorrow";
            case AllowanceStanding.SharedBudgetUsedUp: return "the free allowance is used up for now";
            case AllowanceStanding.Paused: return "this account is paused, dictation still works";
            default:
                double limit = GuestLimitUsd ?? 0, spent = GuestSpentUsd ?? 0;
                if (limit > 0 && spent >= limit) return "the starter allowance is used, confirm your email to keep going";
                var percent = limit > 0 ? (int)Math.Round(spent / limit * 100, MidpointRounding.AwayFromZero) : 0;
                return $"confirm your email to unlock the full free allowance · {percent}% of the starter used";
        }
    }

    /// <summary>
    /// The day the month's allowance comes back, as "nov 1". Months are UTC calendar months, so the
    /// date is read in UTC: midnight on the 1st is still the 31st in the Americas otherwise.
    /// </summary>
    public string? MonthResetDay(CultureInfo? culture = null)
    {
        if (!DateTimeOffset.TryParse(MonthEnd, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            return null;
        if (!MonthEnd.Contains('T')) return null; // ISO 8601 date-time only, as the Mac's formatter
        culture ??= CultureInfo.CurrentCulture;
        return date.UtcDateTime.ToString("MMM d", culture).Replace(".", "").ToLower(culture);
    }

    /// <summary>
    /// The "details" line: "$4.50 of $10 this month · 300 spoken characters of 20,000" (with
    /// "· $0.20 of $2 today" when a daily limit is set).
    /// </summary>
    public string UsageDetails(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        static string Dollars(double amount) =>
            amount == Math.Round(amount) ? $"${(long)amount}" : "$" + amount.ToString("0.00", CultureInfo.InvariantCulture);
        string Count(int number) => number.ToString("N0", culture);
        // No daily limit (0) is the norm since 2026-10-12: only the month is shown then.
        var todayPart = DailyLimitUsd > 0 ? $" · {Dollars(SpentTodayUsd)} of {Dollars(DailyLimitUsd)} today" : "";
        return $"{Dollars(SpentMonthUsd)} of {Dollars(MonthlyLimitUsd)} this month{todayPart} · {Count(TtsCharsMonth)} spoken characters of {Count(TtsCharsLimit)}";
    }
}

/// <summary>
/// Where the free allowance stands, most pressing first: each case has its own sentence because
/// "used up this month" is wrong when only today's share or the shared budget ran out.
/// </summary>
public enum AllowanceStanding { Plenty, RunningLow, UsedUpThisMonth, UsedUpToday, SharedBudgetUsedUp, Paused, Unconfirmed }

/// <summary>GET /billing/me failed; `Code` is the HTTP status (503: the free service's ledger is down).</summary>
public sealed class BillingException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

public sealed partial class BillingStatusModel : ObservableObject
{
    [ObservableProperty] private BillingSummary? summary;
    [ObservableProperty] private string? errorText;

    public void Refresh()
    {
        if (!OpenClickyConfiguration.HasAccountToken)
        {
            Summary = null;
            return;
        }
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var fetched = await FetchSummaryAsync();
            Summary = fetched;
            ErrorText = null;
            AccountProfileStore.Shared.Absorb(fetched);
        }
        catch (Exception error)
        {
            ErrorText = ErrorSentence(error);
            AppLog.Write($"billing: /billing/me failed: {error.Message}");
        }
    }

    /// <summary>GET /billing/me. Errors carry the HTTP status as their code.</summary>
    public static async Task<BillingSummary> FetchSummaryAsync(CancellationToken cancellationToken = default)
    {
        // The backend URL is user-configurable, so a malformed value must throw instead of crashing.
        if (!Uri.TryCreate($"{OpenClickyConfiguration.BackendBaseUrl}/billing/me", UriKind.Absolute, out var billingUrl))
            throw new BillingException(-1, "backend URL is invalid");
        var request = new HttpRequestMessage(HttpMethod.Get, billingUrl);
        OpenClickyConfiguration.Authorize(request);
        using var response = await BackendSignIn.SendAsync(request, sent => HttpClients.Shared.SendAsync(sent, cancellationToken));
        var statusCode = (int)response.StatusCode;
        if (statusCode != 200)
            throw new BillingException(statusCode, statusCode == 401 ? "session expired, sign in again" : $"backend answered {statusCode}");
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return BillingSummary.Decode(data);
    }

    public static string ErrorSentence(Exception error)
    {
        // A refused sign-in has already been refreshed once, or signed out; its sentence says which.
        if (error is BackendSignInException signInError) return signInError.Message;
        return (error as BillingException)?.Code switch
        {
            401 => "your sign-in ran out, sign in again.",
            503 => AccountLimitErrors.ServiceTroubleMessage,
            _ => "couldn't load your allowance right now.",
        };
    }
}
