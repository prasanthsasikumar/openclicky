// Ports AccountAuthTests.swift, AccountCapabilitiesTests.swift, AccountProfileTests.swift (the
// parts in Core) and ClickyAnalyticsTests.swift, plus the email flow's pure rules.

using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;
using static OpenClicky.Core.Providers.OpenClickyAuthSession;

namespace OpenClicky.Core.Tests.Providers;

public sealed class AccountAuthTests
{
    [Fact]
    public void OldBackendsWithoutTheFieldDecode()
    {
        var decoded = JsonSerializer.Deserialize<AuthConfig>("""{"supabaseUrl":"u","publishableKey":"k"}""")!;
        Assert.Equal("u", decoded.SupabaseUrl);
        Assert.Null(decoded.AccountsOpen);
        Assert.Null(decoded.ConfirmRedirectUrl);
    }

    [Fact]
    public void AConfigWithoutTheKeyDoesNotDecode() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AuthConfig>("""{"supabaseUrl":"u"}"""));

    [Fact]
    public void ARefreshKeepsTheStoredEmailWhenTheReplyHasNone()
    {
        var reply = JsonSerializer.Deserialize<TokenResponse>("""{"access_token":"a","refresh_token":"r","expires_in":3600,"user":{"email":""}}""")!;
        Assert.Equal("me@x.co", RefreshedEmail(reply, "me@x.co"));
        var withEmail = reply with { User = new TokenResponse.TokenUser(" new@x.co ") };
        Assert.Equal("new@x.co", RefreshedEmail(withEmail, "me@x.co"));
    }

    [Fact]
    public void StartAndCodeRepliesBecomeOutcomes()
    {
        var session = Encoding.UTF8.GetBytes("""{"session":{"access_token":"a","refresh_token":"r","expires_in":3600},"confirmed":false}""");
        var signedIn = Assert.IsType<StartOutcome.SignedIn>(Outcome(200, session, "a@b.co"));
        Assert.Equal("a", signedIn.Session.AccessToken);
        Assert.False(signedIn.Confirmed);
        Assert.IsType<StartOutcome.CodeSent>(Outcome(200, Encoding.UTF8.GetBytes("""{"status":"code_sent"}"""), "a@b.co"));
        Assert.Equal(new StartOutcome.Failed(UnreachableMessage), Outcome(200, Encoding.UTF8.GetBytes("{}"), "a@b.co"));
        Assert.Equal(new StartOutcome.Failed(BadCodeMessage), Outcome(400, Encoding.UTF8.GetBytes("""{"error":"bad_code"}"""), "a@b.co"));
        Assert.Equal(new StartOutcome.Failed("too many tries, wait about 3 minutes and try again."),
            Outcome(429, Encoding.UTF8.GetBytes("not json"), "a@b.co", retryAfterSeconds: 150));
        Assert.Equal(new StartOutcome.Failed(AccountsUnavailableMessage), Outcome(503, Encoding.UTF8.GetBytes("""{"error":"accounts_unavailable"}"""), "a@b.co"));
    }

    [Fact]
    public void ConfirmRepliesBecomeOutcomes()
    {
        Assert.IsType<ConfirmOutcome.AlreadyConfirmed>(ConfirmOutcomeFor(200, Encoding.UTF8.GetBytes("""{"status":"already_confirmed"}""")));
        Assert.IsType<ConfirmOutcome.NotSupported>(ConfirmOutcomeFor(404, Encoding.UTF8.GetBytes("")));
        Assert.Equal(new ConfirmOutcome.Failed(BadCodeMessage), ConfirmOutcomeFor(401, Encoding.UTF8.GetBytes("""{"error":"bad_code"}""")));
    }

    [Fact]
    public void SlowDownSaysHowLong()
    {
        Assert.Equal("too many tries, wait a few minutes and try again.", SlowDownMessage(null));
        Assert.Equal("too many tries, wait about 1 minute and try again.", SlowDownMessage(30));
        Assert.Equal("too many tries, wait about 2 minutes and try again.", SlowDownMessage(61));
    }

    [Fact]
    public void TheFormStateRules()
    {
        Assert.False(CanStart(EmailFlowState.Sending));
        Assert.False(CanStart(EmailFlowState.CheckingCode("a@b.co")));
        Assert.True(CanStart(EmailFlowState.NeedsCode("a@b.co")));
        Assert.Equal(EmailFlowState.Idle, StateAfterCancellation(EmailFlowState.Sending));
        Assert.Equal(EmailFlowState.NeedsCode("a@b.co"), StateAfterCancellation(EmailFlowState.CheckingCode("a@b.co")));
        Assert.True(ShouldApply(EmailFlowState.CheckingCode("a@b.co"), EmailFlowState.CheckingCode("a@b.co")));
        Assert.False(ShouldApply(EmailFlowState.Idle, EmailFlowState.Sending));
    }

    [Fact]
    public void TheAuthRequestsGoToTheBackend()
    {
        var start = StartRequest("https://backend.test", "a@b.co", "dev")!;
        Assert.Equal("https://backend.test/auth/start", start.RequestUri!.AbsoluteUri);
        Assert.Equal("""{"email":"a@b.co","device":"dev"}""", start.Content!.BodyText());
        var confirm = ConfirmRequest("https://backend.test", "tok", "123456")!;
        Assert.Equal("Bearer tok", confirm.HeaderValue("Authorization"));
        Assert.Null(StartRequest("not a url", "a@b.co", "dev"));
    }
}

public sealed class AccountCapabilitiesTests
{
    private static BillingSummary Summary(double spent, bool exhausted = false) => new()
    {
        Byok = false, SpentMonthUsd = spent, MonthlyLimitUsd = 10, SpentTodayUsd = 0, DailyLimitUsd = 2,
        TtsCharsMonth = 0, TtsCharsLimit = 20000, MonthEnd = "", DayEnd = "", BudgetExhausted = exhausted, Blocked = false,
    };

    [Fact]
    public void DecodesTheNewBillingSummary()
    {
        var summary = BillingSummary.Decode("""{"byok":false,"spentMonthUsd":4.5,"monthlyLimitUsd":10,"spentTodayUsd":0.2,"dailyLimitUsd":2,"ttsCharsMonth":300,"ttsCharsLimit":20000,"monthEnd":"2026-11-01T00:00:00.000Z","dayEnd":"2026-10-09T00:00:00.000Z","budgetExhausted":false,"blocked":false}""");
        Assert.Equal(0.45, summary.FractionUsed);
        Assert.Equal(UsageLevel.Plenty, summary.Level);
        Assert.Equal("nov 1", summary.MonthResetDay(CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("$4.50 of $10 this month · $0.20 of $2 today · 300 spoken characters of 20,000", summary.UsageDetails(CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("plenty left this month · resets on nov 1", summary.AllowanceSentence(summary.MonthResetDay(CultureInfo.GetCultureInfo("en-US"))));
    }

    [Fact]
    public void ASummaryMissingAFieldDoesNotDecode() =>
        Assert.Throws<JsonException>(() => BillingSummary.Decode("""{"byok":false}"""));

    [Fact]
    public void RunningLowAtEightyPercentAndUsedUpAtTheLimit()
    {
        Assert.Equal(UsageLevel.RunningLow, Summary(8).Level);
        Assert.Equal(UsageLevel.UsedUp, Summary(10).Level);
        Assert.Equal(UsageLevel.UsedUp, Summary(1, exhausted: true).Level);
    }

    [Fact]
    public void EachStandingHasItsOwnSentence()
    {
        Assert.Equal("running low this month · resets on the 1st", Summary(8).AllowanceSentence(null));
        Assert.Equal("this month's free allowance is used, it comes back on the 1st", Summary(10).AllowanceSentence(null));
        Assert.Equal("the free allowance is used up for now", Summary(1, exhausted: true).AllowanceSentence(null));
        Assert.Equal("today's free allowance is used, it comes back tomorrow", (Summary(1) with { SpentTodayUsd = 2 }).AllowanceSentence(null));
        Assert.Equal("this account is paused, dictation still works", (Summary(1) with { Blocked = true }).AllowanceSentence(null));
        Assert.Equal("confirm your email to unlock the full free allowance · 25% of the starter used",
            (Summary(1) with { Confirmed = false, GuestSpentUsd = 0.25, GuestLimitUsd = 1 }).AllowanceSentence(null));
        Assert.Equal("the starter allowance is used, confirm your email to keep going",
            (Summary(1) with { Confirmed = false, GuestSpentUsd = 1, GuestLimitUsd = 1 }).AllowanceSentence(null));
        Assert.Equal("$1 of $10 this month · 0 spoken characters of 20,000", (Summary(1) with { DailyLimitUsd = 0 }).UsageDetails(CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void AccountProfileRoutesEveryLane()
    {
        var account = new AccountCapabilities(AccountKind.Account);
        Assert.False(account.UsesRealtime);
        Assert.False(account.UsesAgent);
        Assert.True(account.HearsOnDevice);
        Assert.Equal("/v1/polish", account.PolishPath);
        Assert.Equal(2, account.MaxAskScreens);
        var own = new AccountCapabilities(AccountKind.OwnKeys);
        Assert.True(own.UsesRealtime);
        Assert.Equal("/v1/chat/completions", own.PolishPath);
        Assert.Null(own.MaxAskScreens);
    }

    [Fact]
    public void LimitErrorsBecomePlainSentences()
    {
        var body = Encoding.UTF8.GetBytes("""{"error":"daily_limit","resets_at":"2026-10-09T00:00:00Z"}""");
        var error = AccountLimitErrors.From(402, body);
        Assert.Equal(AccountLimitError.DailyLimit, error);
        Assert.Equal("you've used today's free allowance, it comes back tomorrow. dictation still works.", error!.Value.Message());
        Assert.Null(AccountLimitErrors.From(500, body));
        Assert.True(AccountLimitErrors.IsServiceTrouble(503, Encoding.UTF8.GetBytes("""{"error":"unavailable"}""")));
        Assert.False(AccountLimitErrors.IsServiceTrouble(503, Encoding.UTF8.GetBytes("upstream down")));
    }

    [Fact]
    public void OnlyAnAccountIsToldTheAgentNeedsItsOwnKey()
    {
        Assert.Equal("agent tasks need your own key for now.", new AccountCapabilities(AccountKind.Account).AgentUnavailableHint);
        Assert.Null(new AccountCapabilities(AccountKind.OwnKeys).AgentUnavailableHint);
        Assert.Null(new AccountCapabilities(AccountKind.SignedOut).AgentUnavailableHint);
    }
}

/// <summary>The account profile comes from the backend's /billing/me answer, never from a guess.</summary>
public sealed class AccountProfileTests
{
    private static BillingSummary Summary(string? plan, bool? onPlan = true, double spent = 0, bool blocked = false, bool byok = false) => new()
    {
        Byok = byok, SpentMonthUsd = spent, MonthlyLimitUsd = 10, SpentTodayUsd = 0, DailyLimitUsd = 2, TtsCharsMonth = 0, TtsCharsLimit = 20000,
        MonthEnd = "2026-11-01T00:00:00.000Z", DayEnd = "", BudgetExhausted = false, Blocked = blocked, Plan = plan, OnPlan = onPlan,
    };

    [Fact]
    public void EachPlanMapsToAProfile()
    {
        Assert.Equal(AccountKind.Account, AccountCapabilities.KindFor(false, true, "grant"));
        Assert.Equal(AccountKind.OwnKeys, AccountCapabilities.KindFor(false, true, "byok"));
        Assert.Equal(AccountKind.OwnKeys, AccountCapabilities.KindFor(false, true, "unmetered"));
    }

    [Fact]
    public void BeforeAnyAnswerNothingIsTakenAway()
    {
        Assert.Equal(AccountKind.OwnKeys, AccountCapabilities.KindFor(false, true, null));
        Assert.Equal(AccountKind.OwnKeys, AccountCapabilities.KindFor(true, true, "grant"));
    }

    [Fact]
    public void NoTokenIsSignedOut()
    {
        Assert.Equal(AccountKind.SignedOut, AccountCapabilities.KindFor(false, false, "grant"));
        Assert.Equal(AccountKind.SignedOut, AccountCapabilities.KindFor(false, false, null));
    }

    [Fact]
    public void CurrentReadsThePersistedPlan()
    {
        var preferences = Preferences.InMemory();
        var settings = new OpenClickyShellSettings { Token = "jwt" };
        Assert.Equal(AccountKind.OwnKeys, AccountCapabilities.Current(settings, preferences).Kind);
        preferences.Set(AccountProfileStore.PlanKey, "grant");
        Assert.Equal(AccountKind.Account, AccountCapabilities.Current(settings, preferences).Kind);
        settings.Token = "";
        Assert.Equal(AccountKind.SignedOut, AccountCapabilities.Current(settings, preferences).Kind);
        settings.Token = "jwt";
        settings.OpenaiApiKey = "sk-own";
        Assert.Equal(AccountKind.OwnKeys, AccountCapabilities.Current(settings, preferences).Kind);
    }

    [Fact]
    public void TheStorePersistsTheBackendsAnswer()
    {
        var preferences = Preferences.InMemory();
        var store = new AccountProfileStore(preferences);
        store.Absorb(Summary("grant", onPlan: false));
        Assert.Equal("grant", preferences.GetString(AccountProfileStore.PlanKey));
        Assert.False(store.OnPlan);
        store.Absorb(Summary("unmetered"));
        Assert.Equal("unmetered", preferences.GetString(AccountProfileStore.PlanKey));
    }

    [Fact]
    public void SummariesDecodeWithAndWithoutThePlan()
    {
        var decodedOld = BillingSummary.Decode("""{"byok":false,"spentMonthUsd":0,"monthlyLimitUsd":10,"spentTodayUsd":0,"dailyLimitUsd":2,"ttsCharsMonth":0,"ttsCharsLimit":0,"monthEnd":"","dayEnd":"","budgetExhausted":false,"blocked":false}""");
        Assert.True(decodedOld.Plan is null && decodedOld.OnPlan is null && decodedOld.IsMetered);
        var decodedNew = BillingSummary.Decode("""{"plan":"unmetered","onPlan":true,"byok":false,"spentMonthUsd":0,"monthlyLimitUsd":0,"spentTodayUsd":0,"dailyLimitUsd":0,"ttsCharsMonth":0,"ttsCharsLimit":0,"monthEnd":"","dayEnd":"","budgetExhausted":false,"blocked":false}""");
        Assert.True(decodedNew.Plan == "unmetered" && decodedNew.OnPlan == true && !decodedNew.IsMetered);
    }

    [Fact]
    public void AGrantLoginWithoutAnAccountRowIsTold()
    {
        Assert.True(Summary("grant", onPlan: false).IsSwitchedOff);
        Assert.False(Summary("grant", onPlan: true).IsSwitchedOff);
        Assert.False(Summary("grant", onPlan: false, blocked: true).IsSwitchedOff);
        Assert.False(Summary("unmetered", onPlan: true).IsSwitchedOff);
        Assert.Equal("this login isn't switched on for openclicky yet. write to hello@flowsxr.com, or use your own key.", BillingSummary.SwitchedOffMessage);
    }

    [Fact]
    public void TheRunningLowNoticeIsDueOnceAMonth()
    {
        var low = Summary("grant", spent: 8.5);
        Assert.Equal("2026-11-01T00:00:00.000Z", low.RunningLowNoticeMonth(null));
        Assert.Null(low.RunningLowNoticeMonth("2026-11-01T00:00:00.000Z"));
        Assert.NotNull(low.RunningLowNoticeMonth("2026-10-01T00:00:00.000Z"));
        Assert.Null(Summary("grant", spent: 5).RunningLowNoticeMonth(null));
        Assert.Null(Summary("grant", spent: 10).RunningLowNoticeMonth(null));
        Assert.Null(Summary("unmetered", spent: 9).RunningLowNoticeMonth(null));
    }

    [Fact]
    public void TheStoreShowsTheNoticeOnlyOnceAMonth()
    {
        var store = new AccountProfileStore(Preferences.InMemory());
        var shown = new List<string>();
        store.ShowNotice = shown.Add;
        store.Absorb(Summary("grant", spent: 8.5));
        store.Absorb(Summary("grant", spent: 9));
        Assert.Equal([BillingSummary.RunningLowNotice], shown);
    }

    [Fact]
    public void BillingErrorsSayWhatHappened()
    {
        Assert.Equal(AccountLimitErrors.ServiceTroubleMessage, BillingStatusModel.ErrorSentence(new BillingException(503, "x")));
        Assert.Equal("your sign-in ran out, sign in again.", BillingStatusModel.ErrorSentence(new BillingException(401, "x")));
        Assert.Equal("couldn't load your allowance right now.", BillingStatusModel.ErrorSentence(new InvalidOperationException()));
    }

    [Fact]
    public void ClosedSignUpOnlyAddsANoteUnderTheForm()
    {
        Assert.Null(SignUpClosedNote(true));
        Assert.Null(SignUpClosedNote(null));
        Assert.Equal(AccountLimitError.AccountsFull.Message(), SignUpClosedNote(false));
    }

    [Fact]
    public void ComingBackChecksOnlyAnUnconfirmedEmailOnce()
    {
        Assert.True(ShouldCheckOnActivate(false, false));
        Assert.False(ShouldCheckOnActivate(false, true));
        Assert.False(ShouldCheckOnActivate(true, false));
        Assert.False(ShouldCheckOnActivate(null, false));
    }
}

public sealed class ClickyAnalyticsTests
{
    [Fact]
    public void UserMessageSentPropertiesCarryOnlyTheCharacterCount()
    {
        var properties = ClickyAnalytics.UserMessageSentProperties(42);
        Assert.Single(properties);
        Assert.Equal(42, properties["character_count"]);
        Assert.False(properties.ContainsKey("transcript"));
    }

    [Fact]
    public void AIResponseReceivedPropertiesCarryOnlyTheCharacterCount()
    {
        var properties = ClickyAnalytics.AIResponseReceivedProperties(7);
        Assert.Single(properties);
        Assert.Equal(7, properties["character_count"]);
        Assert.False(properties.ContainsKey("response"));
    }

    [Fact]
    public void WithNoKeyNothingIsCaptured()
    {
        Assert.False(ClickyAnalytics.IsEnabled);
        Assert.Null(ClickyAnalytics.CaptureBody("app_opened", null));
    }
}
