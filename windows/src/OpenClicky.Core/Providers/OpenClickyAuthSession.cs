// Ports OpenClickyAuthSession.swift. Email-first accounts. The one question is the email (POST
// /auth/start): a new email is signed in at once, unconfirmed, with a link mailed to it; an email
// that already has an account is mailed a 6-digit code (POST /auth/code). No passwords. The
// session is kept alive (Supabase Auth, whose details the backend publishes at GET /auth/config):
// the access token goes into shell.json as `token` together with the refresh token, and it is
// refreshed before it expires (a 5-minute timer). Nobody has to touch shell.json by hand.
//
// The Mac's "app became active" observer is `CheckConfirmationOnActivate()`, which the Windows
// side calls when the app is activated.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Providers;

public sealed partial class OpenClickyAuthSession : ObservableObject
{
    public static OpenClickyAuthSession Shared { get; } = new();

    [ObservableProperty] private bool isRefreshing;
    [ObservableProperty] private string? lastErrorText;
    [ObservableProperty] private EmailFlowState emailFlow = EmailFlowState.Idle;
    /// <summary>Whether the backend takes new accounts (GET /auth/config); null until it has answered.</summary>
    [ObservableProperty] private bool? accountsOpen;
    /// <summary>False once the backend answered 404 to /auth/confirm: the link stays the way to confirm.</summary>
    [ObservableProperty] private bool confirmByCodeSupported = true;

    /// <summary>The account form's steps. Records, so two states compare by value.</summary>
    public abstract record EmailFlowState
    {
        public static EmailFlowState Idle { get; } = new IdleState();
        public static EmailFlowState Sending { get; } = new SendingState();
        public static EmailFlowState CheckingCode(string email) => new CheckingCodeState(email);
        public static EmailFlowState NeedsCode(string email) => new NeedsCodeState(email);
        public static EmailFlowState SignedIn(bool confirmed) => new SignedInState(confirmed);
        public static EmailFlowState Failed(string message) => new FailedState(message);

        public sealed record IdleState : EmailFlowState;
        public sealed record SendingState : EmailFlowState;
        public sealed record CheckingCodeState(string Email) : EmailFlowState;
        public sealed record NeedsCodeState(string Email) : EmailFlowState;
        public sealed record SignedInState(bool Confirmed) : EmailFlowState;
        public sealed record FailedState(string Message) : EmailFlowState;
    }

    private Timer? refreshTimer;
    private SynchronizationContext? uiContext;
    private bool isCheckingOnActivate;
    /// <summary>Refresh this long before the access token expires (Supabase tokens last an hour).</summary>
    private static readonly TimeSpan RefreshLead = TimeSpan.FromMinutes(10);
    /// <summary>How often the refresh timer looks at the token.</summary>
    public static readonly TimeSpan RefreshTimerInterval = TimeSpan.FromMinutes(5);

    public sealed record AuthConfig(
        [property: JsonPropertyName("supabaseUrl"), JsonRequired] string SupabaseUrl,
        [property: JsonPropertyName("publishableKey"), JsonRequired] string PublishableKey,
        // Older backends omit these.
        [property: JsonPropertyName("accountsOpen")] bool? AccountsOpen = null,
        [property: JsonPropertyName("confirmRedirectUrl")] string? ConfirmRedirectUrl = null);

    public sealed record TokenResponse(
        [property: JsonPropertyName("access_token"), JsonRequired] string AccessToken,
        [property: JsonPropertyName("refresh_token"), JsonRequired] string RefreshToken,
        [property: JsonPropertyName("expires_in"), JsonRequired] double ExpiresIn,
        [property: JsonPropertyName("user")] TokenResponse.TokenUser? User = null)
    {
        public sealed record TokenUser([property: JsonPropertyName("email")] string? Email);
    }

    /// <summary>
    /// The email to keep after a refresh. A guest whose address is still unconfirmed comes back with
    /// `"email": ""`, so an empty reply keeps the stored one instead of looking signed out.
    /// </summary>
    public static string RefreshedEmail(TokenResponse reply, string? stored)
    {
        var replied = reply.User?.Email?.Trim() ?? "";
        return replied.Length == 0 ? stored ?? "" : replied;
    }

    /// <summary>Email of the signed-in account, when the session came from a sign-in (not a pasted token).</summary>
    public string? AccountEmail => NonEmpty(OpenClickyConfiguration.Settings.AccountEmail);
    public bool CanRefresh => NonEmpty(OpenClickyConfiguration.Settings.RefreshToken) is not null;

    /// <summary>
    /// Refreshes a stale session at launch and keeps refreshing on a timer. Property changes are
    /// raised on `context` (default: the caller's SynchronizationContext).
    /// </summary>
    public void Start(SynchronizationContext? context = null)
    {
        uiContext = context ?? SynchronizationContext.Current;
        refreshTimer?.Dispose();
        refreshTimer = new Timer(_ => OnUiThread(() => _ = RefreshIfNeededAsync()), null, RefreshTimerInterval, RefreshTimerInterval);
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        await RefreshIfNeededAsync();
        BillingSummary summary;
        try { summary = await BillingStatusModel.FetchSummaryAsync(); }
        catch { return; }
        AccountProfileStore.Shared.Absorb(summary);
        if (summary.Confirmed == false) WatchConfirmation();
    }

    private void OnUiThread(Action action)
    {
        var context = uiContext;
        if (context is null) action();
        else context.Post(_ => action(), null);
    }

    public const string UnreachableMessage = "couldn't reach openclicky right now, try again in a minute.";

    public sealed record Session(
        [property: JsonPropertyName("access_token"), JsonRequired] string AccessToken,
        [property: JsonPropertyName("refresh_token"), JsonRequired] string RefreshToken,
        [property: JsonPropertyName("expires_in"), JsonRequired] double ExpiresIn);

    public abstract record StartOutcome
    {
        public sealed record SignedIn(Session Session, bool Confirmed) : StartOutcome;
        public sealed record CodeSent : StartOutcome;
        public sealed record Failed(string Message) : StartOutcome;
    }

    private static HttpRequestMessage? Post(string backendBaseUrl, string path, Dictionary<string, string>? body, string? token = null)
    {
        if (!Uri.TryCreate($"{backendBaseUrl}{path}", UriKind.Absolute, out var url)) return null;
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (token is not null) request.SetHeader("Authorization", $"Bearer {token}");
        request.Content = DirectProviderRequests.JsonContent(body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body));
        return request;
    }

    public static HttpRequestMessage? StartRequest(string backendBaseUrl, string email, string device) =>
        Post(backendBaseUrl, "/auth/start", new() { ["email"] = email, ["device"] = device });

    /// <summary>`device` is the same hash /auth/start sends, so the backend can tie the code sign-in to this PC.</summary>
    public static HttpRequestMessage? CodeRequest(string backendBaseUrl, string email, string code, string? device)
    {
        var body = new Dictionary<string, string> { ["email"] = email, ["code"] = code };
        if (device is not null) body["device"] = device;
        return Post(backendBaseUrl, "/auth/code", body);
    }

    public static HttpRequestMessage? ResendRequest(string backendBaseUrl, string token) =>
        Post(backendBaseUrl, "/auth/resend", null, token);

    /// <summary>A guest types the 6-digit code from the confirmation email; the guest's own token says who.</summary>
    public static HttpRequestMessage? ConfirmRequest(string backendBaseUrl, string token, string code) =>
        Post(backendBaseUrl, "/auth/confirm", new() { ["code"] = code }, token);

    public const string BadCodeMessage = "that code didn't work. check it, or ask for a new one.";
    public const string AccountsUnavailableMessage = "openclicky's accounts are having trouble right now, try again in a few minutes.";

    /// <summary>"wait about 3 minutes" when the backend said how long (Retry-After, in seconds).</summary>
    public static string SlowDownMessage(int? retryAfterSeconds)
    {
        if (retryAfterSeconds is not { } seconds || seconds <= 0) return "too many tries, wait a few minutes and try again.";
        var minutes = Math.Max(1, (int)Math.Ceiling(seconds / 60.0));
        return $"too many tries, wait about {minutes} minute{(minutes == 1 ? "" : "s")} and try again.";
    }

    /// <summary>Retry-After as whole seconds (the HTTP-date form is not used by the backend).</summary>
    public static int? RetryAfterSeconds(HttpResponseMessage? response)
    {
        if (response is null || !response.Headers.TryGetValues("Retry-After", out var values)) return null;
        return int.TryParse(values.FirstOrDefault()?.Trim(), out var seconds) ? seconds : null;
    }

    /// <summary>The sentence for a refused /auth/start, /auth/code or /auth/confirm.</summary>
    public static string FailureMessage(int status, string? error, int? retryAfterSeconds) => error switch
    {
        "accounts_full" => AccountLimitError.AccountsFull.Message(),
        "device_limit" => AccountLimitError.DeviceLimit.Message(),
        "bad_email" => "that doesn't look like an email address.",
        "bad_code" => BadCodeMessage,
        "slow_down" => SlowDownMessage(retryAfterSeconds),
        // The backend was reached; its account store was not. "can't reach" would send people to their wifi.
        "accounts_unavailable" => AccountsUnavailableMessage,
        _ => status == 429 ? SlowDownMessage(retryAfterSeconds) : UnreachableMessage,
    };

    private sealed record Reply(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("session")] Session? Session,
        [property: JsonPropertyName("confirmed")] bool? Confirmed,
        [property: JsonPropertyName("error")] string? Error);

    private static Reply? DecodeReply(byte[] body)
    {
        try { return JsonSerializer.Deserialize<Reply>(body); }
        catch (JsonException) { return null; }
    }

    /// <summary>The backend's answer to /auth/start or /auth/code, as one thing the form can show.</summary>
    public static StartOutcome Outcome(int status, byte[] body, string email, int? retryAfterSeconds = null)
    {
        var reply = DecodeReply(body);
        if (status is >= 200 and < 300)
        {
            if (reply?.Status == "code_sent") return new StartOutcome.CodeSent();
            if (reply?.Session is { } session) return new StartOutcome.SignedIn(session, reply.Confirmed ?? false);
            return new StartOutcome.Failed(UnreachableMessage);
        }
        return new StartOutcome.Failed(FailureMessage(status, reply?.Error, retryAfterSeconds));
    }

    public abstract record ConfirmOutcome
    {
        /// <summary>Confirmed: the backend hands back a fresh session for the confirmed account.</summary>
        public sealed record SignedIn(Session Session) : ConfirmOutcome;
        public sealed record AlreadyConfirmed : ConfirmOutcome;
        /// <summary>An older backend without /auth/confirm: the link in the email is the way.</summary>
        public sealed record NotSupported : ConfirmOutcome;
        public sealed record Failed(string Message) : ConfirmOutcome;
    }

    public static ConfirmOutcome ConfirmOutcomeFor(int status, byte[] body, int? retryAfterSeconds = null)
    {
        var reply = DecodeReply(body);
        if (status is >= 200 and < 300)
        {
            if (reply?.Status == "already_confirmed") return new ConfirmOutcome.AlreadyConfirmed();
            if (reply?.Session is { } session) return new ConfirmOutcome.SignedIn(session);
            return new ConfirmOutcome.Failed(UnreachableMessage);
        }
        if (status == 404) return new ConfirmOutcome.NotSupported();
        return new ConfirmOutcome.Failed(FailureMessage(status, reply?.Error, retryAfterSeconds));
    }

    public static bool CanStart(EmailFlowState state) =>
        state is not (EmailFlowState.SendingState or EmailFlowState.CheckingCodeState);

    /// <summary>A closed form cancels its task: a send in flight goes back to the form, anything else stays.</summary>
    public static EmailFlowState StateAfterCancellation(EmailFlowState state) => state switch
    {
        EmailFlowState.SendingState => EmailFlowState.Idle,
        EmailFlowState.CheckingCodeState checking => EmailFlowState.NeedsCode(checking.Email),
        _ => state,
    };

    /// <summary>
    /// A reply (or cancellation) may change the form only while it still shows the state this
    /// request put it in; a reset, sign-out or newer request has moved on and must not be undone.
    /// </summary>
    public static bool ShouldApply(EmailFlowState current, EmailFlowState inFlight) => current == inFlight;

    public static bool IsCancellation(Exception error) => error is OperationCanceledException;

    /// <summary>When sign-up is known closed, this note goes under the form. An unknown answer shows nothing.</summary>
    public static string? SignUpClosedNote(bool? accountsOpen) =>
        accountsOpen == false ? AccountLimitError.AccountsFull.Message() : null;

    /// <summary>Asks the backend whether it takes new accounts; a failure leaves the last answer.</summary>
    public async Task RefreshAccountsOpenAsync()
    {
        try { AccountsOpen = (await AuthConfigAsync()).AccountsOpen; }
        catch { }
    }

    /// <summary>
    /// Setup's one question: the email. A new email is signed in at once (unconfirmed); an email
    /// that already has an account gets a 6-digit code (NeedsCode).
    /// </summary>
    public async Task StartAsync(string email, CancellationToken cancellationToken = default)
    {
        if (!CanStart(EmailFlow)) return;
        var trimmed = email.Trim().ToLowerInvariant();
        var device = DeviceIdentity.Current;
        var request = device is null ? null : StartRequest(OpenClickyConfiguration.BackendBaseUrl, trimmed, device);
        if (request is null)
        {
            LastErrorText = UnreachableMessage;
            EmailFlow = EmailFlowState.Failed(UnreachableMessage);
            return;
        }
        // "send a new code" asks again from the code step; a failure or cancellation goes back to it.
        var resendingCode = EmailFlow == EmailFlowState.NeedsCode(trimmed);
        await RunAsync(request, trimmed, EmailFlowState.Sending, resendingCode ? EmailFlow : null, cancellationToken);
    }

    /// <summary>
    /// Try it with no email: a guest made from this PC's device hash alone, on OpenClicky's credits.
    /// False when the backend wants an email after all or cannot be reached.
    /// </summary>
    public async Task<bool> StartGuestWithoutEmailAsync()
    {
        if (OpenClickyConfiguration.IsConfigured) return false;
        var device = DeviceIdentity.Current;
        if (device is null) return false;
        var request = Post(OpenClickyConfiguration.BackendBaseUrl, "/auth/guest", new() { ["device"] = device });
        if (request is null) return false;
        try
        {
            using var response = await HttpClients.Shared.SendAsync(request);
            var data = await response.Content.ReadAsByteArrayAsync();
            var status = (int)response.StatusCode;
            if (Outcome(status, data, "") is not StartOutcome.SignedIn signedIn)
            {
                AppLog.Write($"sign-in: no guest without an email (status {status})");
                return false;
            }
            Store(signedIn.Session, "");
            AccountProfileStore.Shared.Refresh();
            AppLog.Write("sign-in: started a guest with no email");
            return true;
        }
        catch (Exception error)
        {
            AppLog.Write($"sign-in: guest request failed: {error.Message}");
            return false;
        }
    }

    /// <summary>Signed in as a guest that has not given an email yet.</summary>
    public bool IsGuestWithoutEmail => OpenClickyConfiguration.HasAccountToken && AccountEmail is null;

    public async Task SubmitCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        if (EmailFlow is not EmailFlowState.NeedsCodeState needsCode) return;
        var digits = new string(code.Where(char.IsDigit).ToArray());
        var request = CodeRequest(OpenClickyConfiguration.BackendBaseUrl, needsCode.Email, digits, DeviceIdentity.Current);
        if (request is null) return;
        await RunAsync(request, needsCode.Email, EmailFlowState.CheckingCode(needsCode.Email), null, cancellationToken);
    }

    /// <summary>
    /// `returnTo`, when set, is where a failed or cancelled request goes back to (instead of the
    /// email step), with the sentence under the code field.
    /// </summary>
    private async Task RunAsync(HttpRequestMessage request, string email, EmailFlowState inFlight, EmailFlowState? returnTo,
                                CancellationToken cancellationToken)
    {
        LastErrorText = null;
        EmailFlow = inFlight;
        try
        {
            using var response = await HttpClients.Shared.SendAsync(request, cancellationToken);
            var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!ShouldApply(EmailFlow, inFlight)) return;
            switch (Outcome((int)response.StatusCode, data, email, RetryAfterSeconds(response)))
            {
                case StartOutcome.SignedIn signedIn:
                    Store(signedIn.Session, email);
                    EmailFlow = EmailFlowState.SignedIn(signedIn.Confirmed);
                    AccountProfileStore.Shared.Refresh();
                    if (!signedIn.Confirmed) WatchConfirmation();
                    break;
                case StartOutcome.CodeSent:
                    EmailFlow = EmailFlowState.NeedsCode(email);
                    break;
                case StartOutcome.Failed failed:
                    // A wrong code leaves the code field up, with the sentence under it.
                    LastErrorText = failed.Message;
                    EmailFlow = returnTo ?? (inFlight == EmailFlowState.Sending ? EmailFlowState.Failed(failed.Message) : EmailFlowState.NeedsCode(email));
                    break;
            }
        }
        catch (Exception error)
        {
            if (!ShouldApply(EmailFlow, inFlight)) return;
            if (IsCancellation(error) || cancellationToken.IsCancellationRequested)
            {
                // Back to whatever the form showed before this send (the email step, or the code step).
                EmailFlow = returnTo ?? StateAfterCancellation(inFlight);
            }
            else
            {
                AppLog.Write($"sign-in: account request failed: {error.Message}");
                if (returnTo is not null) { LastErrorText = UnreachableMessage; EmailFlow = returnTo; }
                else EmailFlow = EmailFlowState.Failed(UnreachableMessage);
            }
        }
    }

    /// <summary>Mails the confirmation link again.</summary>
    public async Task<bool> ResendLinkAsync()
    {
        await RefreshIfNeededAsync();
        var token = OpenClickyConfiguration.Settings.Token;
        if (string.IsNullOrEmpty(token)) return false;
        var request = ResendRequest(OpenClickyConfiguration.BackendBaseUrl, token);
        if (request is null) return false;
        int status;
        try
        {
            using var response = await BackendSignIn.SendAsync(request);
            status = (int)response.StatusCode;
        }
        catch
        {
            status = 0;
        }
        if (status is >= 200 and < 300) { WatchConfirmation(); return true; }
        return false;
    }

    /// <summary>
    /// A guest confirms with the 6-digit code from the email instead of the link. Returns the
    /// sentence to show under the field, null when confirmed.
    /// </summary>
    public async Task<string?> ConfirmEmailAsync(string code)
    {
        await RefreshIfNeededAsync();
        var token = OpenClickyConfiguration.Settings.Token;
        var digits = new string(code.Where(char.IsDigit).ToArray());
        var request = string.IsNullOrEmpty(token) ? null : ConfirmRequest(OpenClickyConfiguration.BackendBaseUrl, token, digits);
        if (request is null) return UnreachableMessage;
        // Not through BackendSignIn: this route's 401 is a wrong code, not a dead sign-in.
        int status;
        byte[] body;
        int? retryAfter;
        try
        {
            using var response = await HttpClients.Shared.SendAsync(request);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsByteArrayAsync();
            retryAfter = RetryAfterSeconds(response);
        }
        catch (Exception error)
        {
            AppLog.Write($"sign-in: confirm by code failed: {error.Message}");
            return IsCancellation(error) ? null : UnreachableMessage;
        }
        switch (ConfirmOutcomeFor(status, body, retryAfter))
        {
            case ConfirmOutcome.SignedIn signedIn:
                // The confirmed account's own session replaces the guest's.
                Store(signedIn.Session, AccountEmail ?? "");
                CancelConfirmationWatch();
                EmailFlow = EmailFlowState.SignedIn(true);
                AccountProfileStore.Shared.Refresh();
                AppLog.Write("sign-in: email confirmed by code");
                return null;
            case ConfirmOutcome.AlreadyConfirmed:
                AccountProfileStore.Shared.Refresh();
                OnPropertyChanged(string.Empty);
                return null;
            case ConfirmOutcome.NotSupported:
                ConfirmByCodeSupported = false;
                return null;
            case ConfirmOutcome.Failed failed:
                return failed.Message;
            default:
                return UnreachableMessage;
        }
    }

    /// <summary>"Use a different email": drops even a pending code step (`ForgetSettledFlow` keeps that one).</summary>
    public void ResetFlow()
    {
        EmailFlow = EmailFlowState.Idle;
        LastErrorText = null;
    }

    /// <summary>
    /// A fresh form starts empty: a finished or failed attempt is forgotten, one in flight is kept.
    /// So is "your sign-in expired": the form is where the person goes to find out why.
    /// </summary>
    public void ForgetSettledFlow()
    {
        if (FlowSurvivesFreshForm(EmailFlow)) return;
        EmailFlow = EmailFlowState.Idle;
        LastErrorText = null;
    }

    public static bool FlowSurvivesFreshForm(EmailFlowState state) => state switch
    {
        EmailFlowState.SendingState or EmailFlowState.CheckingCodeState or EmailFlowState.NeedsCodeState => true,
        EmailFlowState.FailedState failed => failed.Message == SessionExpiredMessage,
        _ => false,
    };

    /// <summary>The expiry sentence while the sign-out it explains is the last thing that happened.</summary>
    public string? SessionExpiredNotice =>
        EmailFlow == EmailFlowState.Failed(SessionExpiredMessage) ? SessionExpiredMessage : null;

    private CancellationTokenSource? confirmationWatch;

    /// <summary>
    /// While the email is unconfirmed, asks /billing/me once a minute for 15 minutes, so the "check
    /// your inbox" note disappears soon after the link is clicked.
    /// </summary>
    public void WatchConfirmation()
    {
        confirmationWatch?.Cancel();
        var watch = new CancellationTokenSource();
        confirmationWatch = watch;
        _ = WatchConfirmationAsync(watch);
    }

    private async Task WatchConfirmationAsync(CancellationTokenSource watch)
    {
        try
        {
            for (var attempt = 0; attempt < 15; attempt++)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(1), watch.Token); }
                catch (OperationCanceledException) { return; }
                BillingSummary summary;
                try { summary = await BillingStatusModel.FetchSummaryAsync(); }
                catch { continue; }
                if (watch.IsCancellationRequested) return;
                AccountProfileStore.Shared.Absorb(summary);
                if (summary.Confirmed != false) { OnPropertyChanged(string.Empty); return; }
            }
        }
        finally
        {
            if (confirmationWatch == watch) confirmationWatch = null;
        }
    }

    private void CancelConfirmationWatch()
    {
        confirmationWatch?.Cancel();
        confirmationWatch = null;
    }

    /// <summary>
    /// Coming back to the app (often from the browser where the link was clicked) asks /billing/me
    /// once, unless the email is already known confirmed or a check is running.
    /// </summary>
    public static bool ShouldCheckOnActivate(bool? lastConfirmed, bool checkRunning) => lastConfirmed == false && !checkRunning;

    /// <summary>Call when the app is activated (the Mac's didBecomeActive).</summary>
    public void CheckConfirmationOnActivate()
    {
        if (!ShouldCheckOnActivate(AccountProfileStore.Shared.LastConfirmed, confirmationWatch is not null || isCheckingOnActivate)) return;
        isCheckingOnActivate = true;
        _ = CheckOnActivateAsync();
    }

    private async Task CheckOnActivateAsync()
    {
        try
        {
            await RefreshIfNeededAsync();
            var summary = await BillingStatusModel.FetchSummaryAsync();
            AccountProfileStore.Shared.Absorb(summary);
            if (summary.Confirmed != false) OnPropertyChanged(string.Empty);
        }
        catch { }
        finally
        {
            isCheckingOnActivate = false;
        }
    }

    public void SignOut() => SignOut(becauseSignInExpired: false);

    /// <summary>
    /// `becauseSignInExpired`: the backend or the sign-in server refused this sign-in for good. The
    /// person did not ask for it, so the account form, the log and a quiet caption all say why.
    /// </summary>
    public void SignOut(bool becauseSignInExpired)
    {
        var hadSession = OpenClickyConfiguration.HasAccountToken;
        OpenClickyConfiguration.Update(settings =>
        {
            settings.Token = "";
            settings.RefreshToken = null;
            settings.TokenExpiresAt = null;
            settings.AccountEmail = null;
        });
        CancelConfirmationWatch();
        tokenFromLastForcedRefresh = null;
        EmailFlow = EmailFlowState.Idle;
        LastErrorText = null;
        AppLog.Write(becauseSignInExpired ? "sign-in: expired, signed out" : "sign-in: signed out");
        if (becauseSignInExpired && hadSession) AnnounceSignInExpired();
        AccountProfileStore.Shared.Refresh();
    }

    /// <summary>Raised when a dead sign-in signed this PC out; the argument is the sentence.</summary>
    public static event Action<string>? SignInExpired;

    private void AnnounceSignInExpired()
    {
        EmailFlow = EmailFlowState.Failed(SessionExpiredMessage);
        LastErrorText = SessionExpiredMessage;
        SignInExpired?.Invoke(SessionExpiredMessage);
        AccountProfileStore.Shared.ShowNotice?.Invoke(SessionExpiredMessage);
    }

    public enum RefreshOutcome { Refreshed, NotNeeded, Failed, SignedOut }

    /// <summary>The refresh being run, so a second caller waits for it instead of reporting "not needed".</summary>
    private Task<RefreshOutcome>? refreshInFlight;
    private readonly object refreshLock = new();

    /// <summary>Refreshes when the stored access token is within ten minutes of expiring (or always, forced).</summary>
    public async Task<RefreshOutcome> RefreshIfNeededAsync(bool force = false)
    {
        Task<RefreshOutcome> refresh;
        lock (refreshLock)
        {
            if (refreshInFlight is not null)
            {
                refresh = refreshInFlight;
            }
            else
            {
                var settings = OpenClickyConfiguration.Settings;
                var refreshToken = NonEmpty(settings.RefreshToken);
                if (refreshToken is null) return RefreshOutcome.NotNeeded;
                var expiresAt = settings.TokenExpiresAt ?? 0;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                if (!force && !(now > expiresAt - RefreshLead.TotalSeconds)) return RefreshOutcome.NotNeeded;
                // Its own task: a caller cancelled mid-refresh must not cut it short.
                refresh = RefreshAsync(refreshToken, settings.AccountEmail);
                refreshInFlight = refresh;
                IsRefreshing = true;
            }
        }
        var outcome = await refresh;
        lock (refreshLock)
        {
            if (refreshInFlight == refresh)
            {
                refreshInFlight = null;
                IsRefreshing = false;
            }
        }
        return outcome;
    }

    private async Task<RefreshOutcome> RefreshAsync(string refreshToken, string? storedEmail)
    {
        await Task.Yield();
        try
        {
            var config = await AuthConfigAsync();
            var session = await RequestTokenAsync(config, "refresh_token", new() { ["refresh_token"] = refreshToken });
            Store(new Session(session.AccessToken, session.RefreshToken, session.ExpiresIn), RefreshedEmail(session, storedEmail));
            AppLog.Write("sign-in: session refreshed");
            return RefreshOutcome.Refreshed;
        }
        catch (Exception error)
        {
            var status = (error as AuthRequestException)?.HttpStatus ?? 0;
            if (RefreshRejectionEndsSession(status))
            {
                // The sign-in server refused this refresh token outright (expired, revoked, or from
                // another project after a move): it will never work again.
                AppLog.Write($"sign-in: refresh refused ({status}); signing out");
                SignOut(becauseSignInExpired: true);
                return RefreshOutcome.SignedOut;
            }
            LastErrorText = error.Message;
            AppLog.Write($"sign-in: refresh failed, session kept: {LastErrorText}");
            return RefreshOutcome.Failed;
        }
    }

    // MARK: - The backend refused the token (401)

    /// <summary>What one 401 from the backend calls for.</summary>
    public enum RejectionDecision
    {
        /// <summary>No token at all: there is no sign-in to end.</summary>
        NothingToSignOutOf,
        /// <summary>The request went out with an older token than the one now stored; send it again.</summary>
        AlreadyRenewed,
        /// <summary>Nothing can renew this token (pasted, invite, or one a refresh just produced): sign out.</summary>
        SignOut,
        /// <summary>Refresh once and send it again.</summary>
        Refresh,
    }

    public static RejectionDecision DecisionAfterRejection(string? currentToken, string? sentToken, bool hasRefreshToken,
                                                           string? tokenFromLastForcedRefresh)
    {
        if (string.IsNullOrEmpty(currentToken)) return RejectionDecision.NothingToSignOutOf;
        if (!string.IsNullOrEmpty(sentToken) && sentToken != currentToken) return RejectionDecision.AlreadyRenewed;
        // A pasted or invite token has nothing to refresh with; repeating "ask again" would loop forever.
        if (!hasRefreshToken) return RejectionDecision.SignOut;
        // The token this refresh produced was refused too: the second 401 in a row ends it.
        if (currentToken == tokenFromLastForcedRefresh) return RejectionDecision.SignOut;
        return RejectionDecision.Refresh;
    }

    public enum RejectionOutcome
    {
        /// <summary>A fresh token is stored; the request (or the question) can go again.</summary>
        TryAgain,
        /// <summary>Signed out; the person has to sign in again.</summary>
        SignedOut,
        /// <summary>The refresh could not be tried (offline, sign-in server down); the session is kept.</summary>
        CouldNotCheck,
    }

    /// <summary>The token the last 401-driven refresh produced: refused once more, it signs out.</summary>
    private string? tokenFromLastForcedRefresh;

    /// <summary>
    /// Every backend caller's one answer to a 401: refresh once, and sign out when nothing can renew
    /// the token or the renewed token is refused as well.
    /// </summary>
    public async Task<RejectionOutcome> HandleBackendRejectionAsync(string? sentToken = null)
    {
        var decision = DecisionAfterRejection(OpenClickyConfiguration.Token, sentToken, CanRefresh, tokenFromLastForcedRefresh);
        AppLog.Write($"sign-in: backend refused the token (401): {decision.RawValue()}");
        switch (decision)
        {
            case RejectionDecision.NothingToSignOutOf:
                return RejectionOutcome.SignedOut;
            case RejectionDecision.AlreadyRenewed:
                return RejectionOutcome.TryAgain;
            case RejectionDecision.SignOut:
                SignOut(becauseSignInExpired: true);
                return RejectionOutcome.SignedOut;
            default:
                switch (await RefreshIfNeededAsync(force: true))
                {
                    case RefreshOutcome.Refreshed:
                        tokenFromLastForcedRefresh = OpenClickyConfiguration.Token;
                        return RejectionOutcome.TryAgain;
                    case RefreshOutcome.SignedOut:
                        return RejectionOutcome.SignedOut;
                    default:
                        return RejectionOutcome.CouldNotCheck;
                }
        }
    }

    public const string AskAgainSentence = "please ask that again.";
    public const string SpokenSignInExpiredSentence = "your openclicky sign-in has expired. sign in again in settings, account.";
    public const string OwnKeyRefusedSentence = "your own api key was refused. check it in settings, keys & plans.";

    public static string SpokenSentence(RejectionOutcome outcome) => outcome switch
    {
        RejectionOutcome.TryAgain => AskAgainSentence,
        RejectionOutcome.SignedOut => SpokenSignInExpiredSentence,
        _ => UnreachableMessage,
    };

    /// <summary>
    /// What the ask lane says when BackendSignIn has already renewed a refused token once and asked
    /// again: the same sentences as for the outcome it ended on.
    /// </summary>
    public static string SpokenSentence(BackendSignInError signInError) => signInError switch
    {
        BackendSignInError.Expired => SpokenSentence(RejectionOutcome.SignedOut),
        _ => SpokenSentence(RejectionOutcome.CouldNotCheck),
    };

    /// <summary>
    /// For a 401 the ask lane got back as it came: handles it and returns the sentence to speak. A
    /// 401 about the person's own provider key is not about the sign-in.
    /// </summary>
    public async Task<string> SentenceAfterBackendRejectionAsync(byte[] responseBody)
    {
        if (BackendSignIn.IsOwnKeyRefusal(responseBody))
        {
            AppLog.Write("sign-in: 401 came from the person's own provider key, sign-in kept");
            return OwnKeyRefusedSentence;
        }
        // No account on this PC, only own keys: there is no sign-in to expire, so never "sign in again".
        if (!OpenClickyConfiguration.HasAccountToken && OpenClickyConfiguration.IsConfigured)
        {
            AppLog.Write("sign-in: 401 on a pc with own keys and no account; nothing to sign out of");
            return OwnKeyRefusedSentence;
        }
        return SpokenSentence(await HandleBackendRejectionAsync());
    }

    public const string SessionExpiredMessage = "your sign-in expired, sign in again with your email.";

    /// <summary>
    /// A 4xx from the token endpoint means this refresh token is dead; a 429, a 5xx or no answer
    /// (status 0) may pass, so the session is kept and tried again later.
    /// </summary>
    public static bool RefreshRejectionEndsSession(int status) => status is >= 400 and < 500 && status != 429;

    // MARK: - Supabase calls

    /// <summary>A refused auth call, with the HTTP status (0 when there was none).</summary>
    public sealed class AuthRequestException(string message, int httpStatus = 0) : Exception(message)
    {
        public int HttpStatus { get; } = httpStatus;
    }

    private static async Task<AuthConfig> AuthConfigAsync()
    {
        // The backend URL is user-configurable, so a malformed value must throw, not crash.
        if (!Uri.TryCreate($"{OpenClickyConfiguration.BackendBaseUrl}/auth/config", UriKind.Absolute, out var url))
            throw new AuthRequestException("backend URL is invalid");
        using var response = await HttpClients.Shared.GetAsync(url);
        if ((int)response.StatusCode != 200) throw new AuthRequestException("this backend has no sign-in configured");
        var data = await response.Content.ReadAsByteArrayAsync();
        return JsonSerializer.Deserialize<AuthConfig>(data) ?? throw new AuthRequestException("this backend has no sign-in configured");
    }

    private static async Task<TokenResponse> RequestTokenAsync(AuthConfig config, string grant, Dictionary<string, string> body)
    {
        // `supabaseUrl` comes straight from the backend's answer — a malformed value must throw.
        if (!Uri.TryCreate($"{config.SupabaseUrl}/auth/v1/token?grant_type={grant}", UriKind.Absolute, out var tokenUrl))
            throw new AuthRequestException("sign-in backend returned an invalid Supabase URL");
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
        request.SetHeader("apikey", config.PublishableKey);
        request.Content = DirectProviderRequests.JsonContent(JsonSerializer.SerializeToUtf8Bytes(body));
        using var response = await HttpClients.Shared.SendAsync(request);
        var data = await response.Content.ReadAsByteArrayAsync();
        var status = (int)response.StatusCode;
        if (status != 200)
        {
            JsonObject? json = null;
            try { json = JsonNode.Parse(data) as JsonObject; } catch (JsonException) { }
            var message = StringValue(json?["error_description"]) ?? StringValue(json?["msg"]) ?? StringValue(json?["error"]) ?? "sign-in rejected";
            throw new AuthRequestException(message, status);
        }
        return JsonSerializer.Deserialize<TokenResponse>(data) ?? throw new AuthRequestException("sign-in rejected", status);
    }

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static void Store(Session session, string email)
    {
        OpenClickyConfiguration.Update(settings =>
        {
            settings.Token = session.AccessToken;
            settings.RefreshToken = session.RefreshToken;
            settings.TokenExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + session.ExpiresIn;
            settings.AccountEmail = email;
        });
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
