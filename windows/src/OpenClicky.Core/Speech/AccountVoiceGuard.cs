//
//  AccountVoiceGuard.cs
//  OpenClicky (Windows port of AccountVoiceGuard.swift, #23)
//
//  Keeps a slow or failing account voice (the backend's /tts, ElevenLabs) from making anyone wait.
//  Each answer gets an AnswerVoiceSession: a piece whose account audio is late or fails is spoken by
//  the fastest local voice (Kokoro if it is downloaded, else this PC), and so is the rest of that
//  answer, so the voice changes at most once. AccountVoiceRest remembers troubled answers: two in a
//  row rest the account voice for ten minutes, during which Automatic resolves past it (a voice
//  picked by hand only ever falls back for one answer). The deadlines, the rest and the session are
//  unit-tested with injected fetches.
//
//  SpeechQueue fetches pieces ahead from more than one thread (the Mac's session runs on the main
//  actor), so the session's state is behind a lock.
//

using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Speech;

/// <summary>How long an account piece may take before the local voice speaks instead.</summary>
public static class AccountVoiceDeadline
{
    /// <summary>The first piece is what the person waits for in silence after "thinking…".</summary>
    public static readonly TimeSpan FirstPiece = TimeSpan.FromSeconds(2.5);
    /// <summary>Later pieces are fetched while earlier ones play, so they have room before anyone hears a gap.</summary>
    public static readonly TimeSpan LaterPiece = TimeSpan.FromSeconds(6);

    public static TimeSpan For(int pieceNumber) => pieceNumber == 0 ? FirstPiece : LaterPiece;
}

/// <summary>Troubled answers in a row, and the rest they earn the account voice.</summary>
public sealed class AccountVoiceRestRule
{
    public const int TroubledAnswersBeforeResting = 2;
    public static readonly TimeSpan Resting = TimeSpan.FromMinutes(10);

    public int ConsecutiveTroubledAnswers { get; private set; }
    public DateTimeOffset? RestingUntil { get; private set; }

    public bool IsResting(DateTimeOffset now) => RestingUntil is { } until && now < until;

    /// <summary>An answer whose first account piece arrived in time.</summary>
    public void RecordAnswerOnTime() => ConsecutiveTroubledAnswers = 0;

    /// <summary>An answer that had to fall back. True when this one starts a rest.</summary>
    public bool RecordTroubledAnswer(DateTimeOffset now)
    {
        ConsecutiveTroubledAnswers++;
        if (ConsecutiveTroubledAnswers < TroubledAnswersBeforeResting) return false;
        ConsecutiveTroubledAnswers = 0;
        RestingUntil = now + Resting;
        return true;
    }
}

/// <summary>The app's one memory of how the account voice has been doing; SpeechVoiceChoices.Current reads it so
/// Automatic skips the account voice while it rests.</summary>
public static class AccountVoiceRest
{
    private static readonly object RuleLock = new();
    private static readonly AccountVoiceRestRule Rule = new();

    public static bool IsResting
    {
        get { lock (RuleLock) return Rule.IsResting(DateTimeOffset.Now); }
    }

    public static void RecordAnswerOnTime()
    {
        lock (RuleLock) Rule.RecordAnswerOnTime();
    }

    public static void RecordTroubledAnswer()
    {
        bool startedARest;
        lock (RuleLock) startedARest = Rule.RecordTroubledAnswer(DateTimeOffset.Now);
        if (startedARest)
        {
            AppLog.Write($"voice: account resting {(int)AccountVoiceRestRule.Resting.TotalMinutes} min after {AccountVoiceRestRule.TroubledAnswersBeforeResting} troubled answers");
        }
    }
}

/// <summary>One answer's voice. <see cref="AudioAsync"/> is SpeechQueue's fetch step; pieces are asked for in text order.</summary>
public sealed class AnswerVoiceSession
{
    /// <summary>Why the account voice was let go for this answer.</summary>
    public abstract record AccountTrouble(int PieceNumber)
    {
        public sealed record TooSlow(int PieceNumber, TimeSpan Waited) : AccountTrouble(PieceNumber);
        public sealed record Failed(int PieceNumber, Exception Error) : AccountTrouble(PieceNumber);
    }

    private readonly bool speaksWithAccountVoice;
    private readonly Func<string, CancellationToken, Task<byte[]>> fetchAccountAudio;
    private readonly Func<string, CancellationToken, Task<byte[]>> fetchFallbackAudio;
    private readonly Func<int, TimeSpan> deadline;
    private readonly Action<TimeSpan> onFirstAccountAudio;
    private readonly Action<AccountTrouble> onAccountTrouble;
    private readonly Action onAnswerOnTime;
    private readonly Action onTroubledAnswer;

    private readonly object stateLock = new();
    private int nextPieceNumber;
    /// <summary>Completed once a piece fell back: every later piece, including ones already in flight on the account
    /// voice, is spoken by the fallback so the voice changes only once.</summary>
    private readonly TaskCompletionSource fellBack = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="fetchAccountAudio">Only used when <paramref name="speaksWithAccountVoice"/>; otherwise every piece goes to
    /// <paramref name="fetchFallbackAudio"/>, which is then simply the voice that speaks.</param>
    public AnswerVoiceSession(bool speaksWithAccountVoice,
                              Func<string, CancellationToken, Task<byte[]>> fetchAccountAudio,
                              Func<string, CancellationToken, Task<byte[]>> fetchFallbackAudio,
                              Func<int, TimeSpan>? deadline = null,
                              Action<TimeSpan>? onFirstAccountAudio = null,
                              Action<AccountTrouble>? onAccountTrouble = null,
                              Action? onAnswerOnTime = null,
                              Action? onTroubledAnswer = null)
    {
        this.speaksWithAccountVoice = speaksWithAccountVoice;
        this.fetchAccountAudio = fetchAccountAudio;
        this.fetchFallbackAudio = fetchFallbackAudio;
        this.deadline = deadline ?? AccountVoiceDeadline.For;
        this.onFirstAccountAudio = onFirstAccountAudio ?? (_ => { });
        this.onAccountTrouble = onAccountTrouble ?? (_ => { });
        this.onAnswerOnTime = onAnswerOnTime ?? (() => { });
        this.onTroubledAnswer = onTroubledAnswer ?? (() => { });
    }

    public bool HasFallenBack => fellBack.Task.IsCompleted;

    public async Task<byte[]> AudioAsync(string pieceText, CancellationToken cancellationToken = default)
    {
        int pieceNumber;
        lock (stateLock) pieceNumber = nextPieceNumber++;
        if (!speaksWithAccountVoice || HasFallenBack) return await fetchFallbackAudio(pieceText, cancellationToken).ConfigureAwait(false);

        var waitFor = deadline(pieceNumber);
        var startedAt = DateTimeOffset.Now;
        // The account fetch races its deadline and another piece falling back; the losers are cancelled.
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var accountTask = fetchAccountAudio(pieceText, race.Token);
        var deadlineTask = Task.Delay(waitFor, race.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        var first = await Task.WhenAny(accountTask, deadlineTask, fellBack.Task).ConfigureAwait(false);
        race.Cancel();
        // A stopped answer (Esc, a new question) is an interruption, never a reason to fall back.
        cancellationToken.ThrowIfCancellationRequested();

        if (first == accountTask)
        {
            if (accountTask.Status == TaskStatus.RanToCompletion)
            {
                // Another piece fell back while this one was in flight: keep the answer in one voice.
                if (HasFallenBack) return await fetchFallbackAudio(pieceText, cancellationToken).ConfigureAwait(false);
                if (pieceNumber == 0)
                {
                    onFirstAccountAudio(DateTimeOffset.Now - startedAt);
                    onAnswerOnTime();
                }
                return accountTask.Result;
            }
            var error = accountTask.Exception?.InnerException ?? new OperationCanceledException("the account voice request was cancelled");
            FallBack(new AccountTrouble.Failed(pieceNumber, error));
        }
        else if (first == deadlineTask)
        {
            FallBack(new AccountTrouble.TooSlow(pieceNumber, waitFor));
        }
        // else: another piece fell back while this one waited, so there is no need to wait out its own deadline.
        ObserveLater(accountTask);
        return await fetchFallbackAudio(pieceText, cancellationToken).ConfigureAwait(false);
    }

    private void FallBack(AccountTrouble trouble)
    {
        if (!fellBack.TrySetResult()) return;
        onAccountTrouble(trouble);
        onTroubledAnswer();
    }

    /// <summary>A cancelled or failed account fetch nobody waits for any more: its exception is seen, not left unobserved.</summary>
    private static void ObserveLater(Task task) =>
        task.ContinueWith(finished => _ = finished.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
