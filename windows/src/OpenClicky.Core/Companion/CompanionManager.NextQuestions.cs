//
//  CompanionManager.NextQuestions.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  Next questions (Answers/NextQuestions): every finished exchange (RememberExchange: the ask lane, the fast voice, a
//  drawing, an agent's reply) asks the answering model for a few follow-up questions while the voice is still speaking,
//  and offers them (NextQuestionsOffered) for the windows to show as buttons. Tapping one asks it like a typed question.
//  A new question, esc, or the person speaking takes them away (NextQuestionsWithdrawn).
//

using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Companion;

public sealed partial class CompanionManager
{
    /// <summary>A few questions to ask next, for the last answer: shown as buttons until withdrawn.</summary>
    public event Action<IReadOnlyList<string>>? NextQuestionsOffered;

    /// <summary>The questions offered are out of date (a new question, esc): they go.</summary>
    public event Action? NextQuestionsWithdrawn;

    private CancellationTokenSource? nextQuestionsCancellation;
    private bool nextQuestionsShown;

    /// <summary>After an exchange: asks the answering model for questions to ask next, when they are on, the exchange has
    /// somewhere to go, and the person's own key or model answers.</summary>
    private void OfferNextQuestions(string question, string answer)
    {
        WithdrawNextQuestions();
        if (!NextQuestions.IsOn(preferences) || !NextQuestions.WorthOffering(question, answer)) return;
        var route = ProviderRouter.Route(ProviderCapability.Claude, ProviderRouter.CurrentCredentials());
        if (!NextQuestions.RouteAllows(route.Kind))
        {
            AppLog.Write($"next questions: not asked on {route.Kind} (only an own key or model asks them)");
            return;
        }
        var cancellation = new CancellationTokenSource();
        nextQuestionsCancellation = cancellation;
        _ = MakeNextQuestionsAsync(question, answer, cancellation);
    }

    private async Task MakeNextQuestionsAsync(string question, string answer, CancellationTokenSource cancellation)
    {
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var (reply, _, _) = await claudeAPI.AnalyzeImageStreamingAsync([], NextQuestions.SystemPrompt, null,
                NextQuestions.UserPrompt(question, answer), null, _ => { }, cancellation.Token, NextQuestions.OutputTokens).ConfigureAwait(false);
            var questions = NextQuestions.Parse(reply);
            AppLog.Write($"next questions: {questions.Count} in {(DateTimeOffset.UtcNow - startedAt).TotalSeconds:0.0} s");
            if (questions.Count == 0 || cancellation.IsCancellationRequested) return;
            services.MainThread.Post(() =>
            {
                if (!ReferenceEquals(nextQuestionsCancellation, cancellation) || cancellation.IsCancellationRequested) return;
                nextQuestionsShown = true;
                NextQuestionsOffered?.Invoke(questions);
            });
        }
        catch (Exception error) when (error is OperationCanceledException || cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            // Never in the way of the answer: nothing is offered.
            AppLog.Write($"next questions: failed ({error.GetType().Name})");
        }
    }

    /// <summary>Takes the questions away, and stops any still being made.</summary>
    public void WithdrawNextQuestions()
    {
        if (nextQuestionsCancellation is { } making)
        {
            nextQuestionsCancellation = null;
            try { making.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        if (!nextQuestionsShown) return;
        nextQuestionsShown = false;
        NextQuestionsWithdrawn?.Invoke();
    }

    /// <summary>A question button was tapped: it is asked like a typed question.</summary>
    public void AskNextQuestion(string question)
    {
        AppLog.Write($"next questions: tapped ({question.Length} chars)");
        SubmitTypedRequest(question);
    }
}
