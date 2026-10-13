// Ports BackendSignInTests.swift and SignInExpiryTests.swift (the parts in Core): a 401 from the
// backend refreshes once, sends once more, and a second 401 signs out; the expiry sentence stays
// reachable on the account form. The session side is injected, so nothing touches shell.json.

using System.Net;
using System.Text;
using OpenClicky.Core.Providers;
using static OpenClicky.Core.Providers.OpenClickyAuthSession;

namespace OpenClicky.Core.Tests.Providers;

public sealed class BackendSignInTests
{
    private static readonly byte[] BackendRefusal = Encoding.UTF8.GetBytes("""{"error":"invalid session token: expired"}""");
    private static readonly byte[] OwnKeyRefusal = Encoding.UTF8.GetBytes("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");

    /// <summary>Records what the injected session side was asked to do.</summary>
    private sealed class SignInRecorder
    {
        public List<string?> RejectionsHandled { get; } = [];
        public int SignOutCount { get; set; }
        public List<string?> SentAuthorizations { get; } = [];

        public void NoteSent(HttpRequestMessage request) => SentAuthorizations.Add(request.HeaderValue("Authorization"));
    }

    private static HttpResponseMessage Answer(int status, byte[]? body = null) =>
        new((HttpStatusCode)status) { Content = new ByteArrayContent(body ?? Encoding.UTF8.GetBytes("{}")) };

    private static HttpRequestMessage Request(string token = "old-token")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://backend.test/x") { Content = new StringContent("{\"q\":1}") };
        request.SetHeader("Authorization", $"Bearer {token}");
        return request;
    }

    private static BackendSignIn.SignInRejectionHandling SignIn(SignInRecorder recorder, RejectionOutcome outcome, string? renewedToken = "new-token") =>
        new(sentToken => { recorder.RejectionsHandled.Add(sentToken); return Task.FromResult(outcome); },
            () => Task.FromResult(renewedToken),
            () => { recorder.SignOutCount++; return Task.CompletedTask; });

    // MARK: the decision for one 401

    [Fact]
    public void ATokenWithNothingToRefreshItSignsOut() =>
        Assert.Equal(RejectionDecision.SignOut, DecisionAfterRejection("pasted", null, hasRefreshToken: false, tokenFromLastForcedRefresh: null));

    [Fact]
    public void TheSecond401AfterARefreshSignsOut() =>
        Assert.Equal(RejectionDecision.SignOut, DecisionAfterRejection("renewed", null, hasRefreshToken: true, tokenFromLastForcedRefresh: "renewed"));

    [Fact]
    public void AFirst401Refreshes() =>
        Assert.Equal(RejectionDecision.Refresh, DecisionAfterRejection("current", "current", hasRefreshToken: true, tokenFromLastForcedRefresh: "earlier"));

    [Fact]
    public void ARequestSentWithAnOlderTokenIsJustSentAgain() =>
        Assert.Equal(RejectionDecision.AlreadyRenewed, DecisionAfterRejection("renewed", "old", hasRefreshToken: true, tokenFromLastForcedRefresh: "renewed"));

    [Fact]
    public void NoTokenHasNothingToSignOutOf() =>
        Assert.Equal(RejectionDecision.NothingToSignOutOf, DecisionAfterRejection(null, null, hasRefreshToken: false, tokenFromLastForcedRefresh: null));

    [Fact]
    public void TheAskLaneSaysWhatHappened()
    {
        Assert.Equal("please ask that again.", SpokenSentence(RejectionOutcome.TryAgain));
        Assert.Equal(SpokenSignInExpiredSentence, SpokenSentence(RejectionOutcome.SignedOut));
        Assert.Equal(UnreachableMessage, SpokenSentence(RejectionOutcome.CouldNotCheck));
    }

    // MARK: F-26: the sentence is reachable

    [Fact]
    public void TheExpirySentenceSurvivesOpeningTheForm()
    {
        Assert.True(FlowSurvivesFreshForm(EmailFlowState.Failed(SessionExpiredMessage)));
        Assert.False(FlowSurvivesFreshForm(EmailFlowState.Failed("that code didn't work. check it, or ask for a new one.")));
        Assert.False(FlowSurvivesFreshForm(EmailFlowState.Idle));
        Assert.False(FlowSurvivesFreshForm(EmailFlowState.SignedIn(true)));
        Assert.True(FlowSurvivesFreshForm(EmailFlowState.NeedsCode("a@b.co")));
    }

    [Fact]
    public void TheSignInErrorReadsAsTheFormsSentence()
    {
        var expired = new BackendSignInException(BackendSignInError.Expired);
        Assert.Equal(SessionExpiredMessage, expired.Message);
        Assert.Equal(SessionExpiredMessage, BillingStatusModel.ErrorSentence(expired));
    }

    // MARK: F-20: one path for every caller

    [Fact]
    public async Task AnAcceptedRequestNeverTouchesTheSession()
    {
        var recorder = new SignInRecorder();
        var result = await BackendSignIn.SendAsync(Request(), _ => Task.FromResult(Answer(200)), SignIn(recorder, RejectionOutcome.SignedOut));
        Assert.Equal(200, (int)result.StatusCode);
        Assert.Empty(recorder.RejectionsHandled);
    }

    [Fact]
    public async Task A401IsRefreshedOnceAndSentAgainWithTheNewToken()
    {
        var recorder = new SignInRecorder();
        var sentBodies = new List<string>();
        var result = await BackendSignIn.SendAsync(Request(), async sent =>
        {
            recorder.NoteSent(sent);
            sentBodies.Add(await sent.Content!.ReadAsStringAsync());
            return recorder.SentAuthorizations.Count == 1 ? Answer(401, BackendRefusal) : Answer(200);
        }, SignIn(recorder, RejectionOutcome.TryAgain));
        Assert.Equal(200, (int)result.StatusCode);
        Assert.Equal(["old-token"], recorder.RejectionsHandled);
        Assert.Equal(["Bearer old-token", "Bearer new-token"], recorder.SentAuthorizations);
        Assert.Equal(["{\"q\":1}", "{\"q\":1}"], sentBodies); // the retry carries the same body
        Assert.Equal(0, recorder.SignOutCount);
    }

    [Fact]
    public async Task ASecond401SignsOutAndThrowsExpired()
    {
        var recorder = new SignInRecorder();
        var error = await Assert.ThrowsAsync<BackendSignInException>(() => BackendSignIn.SendAsync(Request(), sent =>
        {
            recorder.NoteSent(sent);
            return Task.FromResult(Answer(401, BackendRefusal));
        }, SignIn(recorder, RejectionOutcome.TryAgain)));
        Assert.Equal(BackendSignInError.Expired, error.Error);
        Assert.Equal(2, recorder.SentAuthorizations.Count);
        Assert.Equal(1, recorder.SignOutCount);
    }

    [Fact]
    public async Task ASignInNothingCanRenewIsNotSentAgain()
    {
        var recorder = new SignInRecorder();
        var error = await Assert.ThrowsAsync<BackendSignInException>(() => BackendSignIn.SendAsync(Request(), sent =>
        {
            recorder.NoteSent(sent);
            return Task.FromResult(Answer(401, BackendRefusal));
        }, SignIn(recorder, RejectionOutcome.SignedOut)));
        Assert.Equal(BackendSignInError.Expired, error.Error);
        Assert.Single(recorder.SentAuthorizations);
    }

    [Fact]
    public async Task ARefreshThatCouldNotRunKeepsTheSession()
    {
        var recorder = new SignInRecorder();
        var error = await Assert.ThrowsAsync<BackendSignInException>(() => BackendSignIn.SendAsync(Request(),
            _ => Task.FromResult(Answer(401, BackendRefusal)), SignIn(recorder, RejectionOutcome.CouldNotCheck)));
        Assert.Equal(BackendSignInError.CouldNotRenew, error.Error);
        Assert.Equal(UnreachableMessage, error.Message);
        Assert.Equal(0, recorder.SignOutCount);
    }

    [Fact]
    public async Task ARelayedOwnKey401IsNotAboutTheSignIn()
    {
        var recorder = new SignInRecorder();
        var result = await BackendSignIn.SendAsync(Request(), _ => Task.FromResult(Answer(401, OwnKeyRefusal)), SignIn(recorder, RejectionOutcome.SignedOut));
        Assert.Equal(401, (int)result.StatusCode);
        // The caller can still read the body that was read ahead.
        Assert.Equal(OwnKeyRefusal, await result.Content.ReadAsByteArrayAsync());
        Assert.Empty(recorder.RejectionsHandled);
        Assert.True(BackendSignIn.IsOwnKeyRefusal(OwnKeyRefusal));
        Assert.False(BackendSignIn.IsOwnKeyRefusal(BackendRefusal));
    }

    // The ask lane's streamed answer goes through the same rule: its body is read only when refused.
    private abstract record StreamedAnswer
    {
        public sealed record Streaming(int Status) : StreamedAnswer;
        public sealed record Refused(int Status, string Body) : StreamedAnswer;

        public (int, byte[]) StatusAndBody => this switch
        {
            Streaming streaming => (streaming.Status, Array.Empty<byte>()),
            Refused refused => (refused.Status, Encoding.UTF8.GetBytes(refused.Body)),
            _ => (0, Array.Empty<byte>()),
        };
    }

    [Fact]
    public async Task AStreamedQuestionIsAskedAgainAfterARefresh()
    {
        var recorder = new SignInRecorder();
        var result = await BackendSignIn.SendAsync<StreamedAnswer>(Request(), sent =>
        {
            recorder.NoteSent(sent);
            StreamedAnswer answer = recorder.SentAuthorizations.Count == 1
                ? new StreamedAnswer.Refused(401, Encoding.UTF8.GetString(BackendRefusal))
                : new StreamedAnswer.Streaming(200);
            return Task.FromResult(answer);
        }, answer => Task.FromResult(answer.StatusAndBody), SignIn(recorder, RejectionOutcome.TryAgain));
        Assert.Equal(new StreamedAnswer.Streaming(200), result);
        Assert.Equal(["Bearer old-token", "Bearer new-token"], recorder.SentAuthorizations);
    }

    [Fact]
    public async Task AStreamedQuestionsOwnKeyRefusalComesBackAsItCame()
    {
        var recorder = new SignInRecorder();
        var refused = new StreamedAnswer.Refused(401, Encoding.UTF8.GetString(OwnKeyRefusal));
        var result = await BackendSignIn.SendAsync<StreamedAnswer>(Request(), _ => Task.FromResult<StreamedAnswer>(refused),
            answer => Task.FromResult(answer.StatusAndBody), SignIn(recorder, RejectionOutcome.SignedOut));
        Assert.Equal(refused, result);
        Assert.Empty(recorder.RejectionsHandled);
    }

    [Fact]
    public void ARefusalKeepsItsBodyAndReadsAsBefore()
    {
        var body = Encoding.UTF8.GetBytes("""{"error":"daily_limit"}""");
        var question = new BackendHttpException(BackendHttpException.ServiceKind.Answers, 402, body);
        Assert.Equal("""API Error (402): {"error":"daily_limit"}""", question.Message);
        Assert.Equal(402, question.Code);
        Assert.Equal("""TTS API error (402): {"error":"daily_limit"}""", new BackendHttpException(BackendHttpException.ServiceKind.Voice, 402, body).Message);
    }

    // MARK: SignInExpiryTests

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    public void ARefusedRefreshTokenEndsTheSession(int status) => Assert.True(RefreshRejectionEndsSession(status));

    [Theory]
    [InlineData(0)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void APassingFailureKeepsTheSession(int status) => Assert.False(RefreshRejectionEndsSession(status));

    [Fact]
    public void ASignInOneRenewalCouldNotSaveIsSaidAsBefore()
    {
        Assert.Equal(SpokenSentence(RejectionOutcome.SignedOut), SpokenSentence(BackendSignInError.Expired));
        Assert.Equal("your openclicky sign-in has expired. sign in again in settings, account.", SpokenSentence(BackendSignInError.Expired));
        Assert.Equal(UnreachableMessage, SpokenSentence(BackendSignInError.CouldNotRenew));
    }
}
