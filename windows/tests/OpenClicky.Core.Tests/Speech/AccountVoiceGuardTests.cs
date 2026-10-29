// Ports AccountVoiceGuardTests.swift (#23): a late or failing free voice never makes the person wait: the piece
// and the rest of the answer go to the local voice, a stop is never mistaken for trouble, and two troubled answers
// in a row rest the free voice for ten minutes in Automatic only.

using System.Diagnostics;
using System.Text;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class AnswerVoiceSessionTests
{
    private sealed class VoiceRefused : Exception;

    private static byte[] Audio(string voice, string text) => Encoding.UTF8.GetBytes($"{voice}:{text}");
    private static string Spoken(byte[] audio) => Encoding.UTF8.GetString(audio);

    /// <summary>Records what the session reports, and makes the session with short deadlines.</summary>
    private sealed class Harness
    {
        public List<AnswerVoiceSession.AccountTrouble> Troubles { get; } = [];
        public int AnswersOnTime;
        public int TroubledAnswers;
        public int FirstSoundCount;

        public AnswerVoiceSession Session(Func<string, double>? accountDelaySeconds = null, Func<string, bool>? accountFails = null,
                                          bool speaksWithAccountVoice = true) => new(
            speaksWithAccountVoice,
            async (text, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(accountDelaySeconds?.Invoke(text) ?? 0), cancellationToken);
                if (accountFails?.Invoke(text) == true) throw new VoiceRefused();
                return Audio("account", text);
            },
            (text, _) => Task.FromResult(Audio("local", text)),
            deadline: pieceNumber => TimeSpan.FromSeconds(pieceNumber == 0 ? 0.5 : 1.5),
            onFirstAccountAudio: _ => Interlocked.Increment(ref FirstSoundCount),
            onAccountTrouble: trouble => { lock (Troubles) Troubles.Add(trouble); },
            onAnswerOnTime: () => Interlocked.Increment(ref AnswersOnTime),
            onTroubledAnswer: () => Interlocked.Increment(ref TroubledAnswers));
    }

    [Fact]
    public async Task AnAccountVoiceInTimeSpeaksEveryPiece()
    {
        var harness = new Harness();
        var session = harness.Session();
        Assert.Equal("account:one", Spoken(await session.AudioAsync("one")));
        Assert.Equal("account:two", Spoken(await session.AudioAsync("two")));
        Assert.Equal(1, harness.AnswersOnTime);
        Assert.Equal(1, harness.FirstSoundCount);
        Assert.Equal(0, harness.TroubledAnswers);
    }

    /// <summary>The first piece is the silence after "thinking…": past its deadline the local voice speaks it.</summary>
    [Fact]
    public async Task ALateFirstPieceIsSpokenByTheLocalVoiceAndSoIsTheRest()
    {
        var harness = new Harness();
        var session = harness.Session(accountDelaySeconds: text => text == "one" ? 5 : 0);
        var stopwatch = Stopwatch.StartNew();
        Assert.Equal("local:one", Spoken(await session.AudioAsync("one")));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
        // The account would answer the second piece at once; the answer stays in one voice.
        Assert.Equal("local:two", Spoken(await session.AudioAsync("two")));
        Assert.Equal(1, harness.TroubledAnswers);
        Assert.Equal(0, harness.AnswersOnTime);
        var tooSlow = Assert.IsType<AnswerVoiceSession.AccountTrouble.TooSlow>(Assert.Single(harness.Troubles));
        Assert.Equal(0, tooSlow.PieceNumber);
    }

    /// <summary>Later pieces are fetched while earlier ones play, so they get the longer deadline.</summary>
    [Fact]
    public async Task ALaterPieceHasMoreRoomThanTheFirst()
    {
        var harness = new Harness();
        var session = harness.Session(accountDelaySeconds: text => text == "two" ? 0.8 : 0);
        await session.AudioAsync("one");
        Assert.Equal("account:two", Spoken(await session.AudioAsync("two")));
        Assert.Empty(harness.Troubles);
    }

    [Fact]
    public async Task AFailedPieceIsSpokenByTheLocalVoice()
    {
        var harness = new Harness();
        var session = harness.Session(accountFails: text => text == "two");
        Assert.Equal("account:one", Spoken(await session.AudioAsync("one")));
        Assert.Equal("local:two", Spoken(await session.AudioAsync("two")));
        Assert.Equal("local:three", Spoken(await session.AudioAsync("three")));
        Assert.Equal(1, harness.TroubledAnswers);
        var failed = Assert.IsType<AnswerVoiceSession.AccountTrouble.Failed>(Assert.Single(harness.Troubles));
        Assert.Equal(1, failed.PieceNumber);
    }

    /// <summary>Pieces are fetched ahead: one still in flight on the account voice when an earlier one fell back is said by
    /// the local voice too, without waiting out its own deadline.</summary>
    [Fact]
    public async Task APieceInFlightWhenAnotherFallsBackFollowsIt()
    {
        var harness = new Harness();
        var session = harness.Session(accountDelaySeconds: text => text == "one" ? 5 : 1.0);
        var first = session.AudioAsync("one");
        var second = session.AudioAsync("two");
        Assert.Equal("local:one", Spoken(await first));
        Assert.Equal("local:two", Spoken(await second));
        Assert.Single(harness.Troubles);
        Assert.Equal(1, harness.TroubledAnswers);
    }

    /// <summary>Esc or a new question cancels the fetch: that is an interruption, not a voice to give up on.</summary>
    [Fact]
    public async Task AStoppedAnswerIsNotTrouble()
    {
        var harness = new Harness();
        var session = harness.Session(accountDelaySeconds: _ => 5);
        using var stop = new CancellationTokenSource();
        var piece = session.AudioAsync("one", stop.Token);
        await Task.Delay(20);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => piece);
        Assert.Empty(harness.Troubles);
        Assert.Equal(0, harness.TroubledAnswers);
    }

    [Fact]
    public async Task AnotherVoiceGoesStraightToItsOwnFetch()
    {
        var harness = new Harness();
        var session = harness.Session(accountDelaySeconds: _ => 5, speaksWithAccountVoice: false);
        Assert.Equal("local:one", Spoken(await session.AudioAsync("one")));
        Assert.Empty(harness.Troubles);
        Assert.Equal(0, harness.AnswersOnTime);
    }
}

public class AccountVoiceRestRuleTests
{
    [Fact]
    public void TwoTroubledAnswersInARowRestTheVoiceForTenMinutes()
    {
        var rule = new AccountVoiceRestRule();
        var now = DateTimeOffset.Now;
        Assert.False(rule.RecordTroubledAnswer(now));
        Assert.False(rule.IsResting(now));
        Assert.True(rule.RecordTroubledAnswer(now));
        Assert.True(rule.IsResting(now.AddMinutes(9)));
        Assert.False(rule.IsResting(now.AddMinutes(10).AddSeconds(1)));
    }

    [Fact]
    public void AnAnswerOnTimeInBetweenStartsTheCountAgain()
    {
        var rule = new AccountVoiceRestRule();
        var now = DateTimeOffset.Now;
        rule.RecordTroubledAnswer(now);
        rule.RecordAnswerOnTime();
        Assert.False(rule.RecordTroubledAnswer(now));
        Assert.False(rule.IsResting(now));
    }

    /// <summary>After a rest the next answer tries the free voice again, and it takes two more to rest again.</summary>
    [Fact]
    public void AfterARestTheCountStartsFromNothing()
    {
        var rule = new AccountVoiceRestRule();
        var now = DateTimeOffset.Now;
        rule.RecordTroubledAnswer(now);
        rule.RecordTroubledAnswer(now);
        var later = now.AddMinutes(11);
        Assert.False(rule.RecordTroubledAnswer(later));
        Assert.False(rule.IsResting(later));
    }
}

public class AccountVoiceRestResolveTests
{
    private static SpeechVoiceChoice Resolve(string? picked, bool resting, bool kokoro = false) =>
        SpeechVoiceChoices.Resolve(new OpenClickyShellSettings { SpeechVoice = picked }, hasAccount: true, kokoroInstalled: kokoro,
            languageCode: "en", accountVoiceIsResting: resting);

    /// <summary>The free voice comes before Kokoro now (#23); while it rests, Automatic passes it by.</summary>
    [Fact]
    public void AutomaticPrefersTheFreeVoiceAndPassesItByWhileItRests()
    {
        Assert.Equal(SpeechVoiceChoice.Account, Resolve(picked: null, resting: false, kokoro: true));
        Assert.Equal(SpeechVoiceChoice.Kokoro, Resolve(picked: null, resting: true, kokoro: true));
        Assert.Equal(SpeechVoiceChoice.Mac, Resolve(picked: null, resting: true));
    }

    /// <summary>Someone who picked the free voice by hand keeps it; only one answer at a time falls back.</summary>
    [Fact]
    public void AFreeVoicePickedByHandIsNeverPassedBy()
    {
        Assert.Equal(SpeechVoiceChoice.Account, Resolve(picked: "account", resting: true));
    }

    [Fact]
    public void TheLocalVoiceIsKokoroWhenItIsDownloadedElseThisPC()
    {
        Assert.Equal(SpeechVoiceChoice.Kokoro, SpeechVoiceChoices.FastestLocalVoice(kokoroInstalled: true, languageCode: "en"));
        Assert.Equal(SpeechVoiceChoice.Mac, SpeechVoiceChoices.FastestLocalVoice(kokoroInstalled: false, languageCode: "en"));
    }
}
