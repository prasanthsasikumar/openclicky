namespace OpenClicky.Core.Tests.Companion;

/// <summary>A clock for the models' timers: delays finish only when the test advances time.</summary>
internal sealed class ManualDelays
{
    private sealed record Pending(TimeSpan Due, TaskCompletionSource Completion);

    private readonly List<Pending> pending = new();

    public TimeSpan Now { get; private set; }

    public Task Delay(TimeSpan span, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        var completion = new TaskCompletionSource();
        var entry = new Pending(Now + span, completion);
        pending.Add(entry);
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                pending.Remove(entry);
                completion.TrySetCanceled(cancellationToken);
            });
        }
        return completion.Task;
    }

    /// <summary>Moves time on, finishing every delay that falls due, earliest first (continuations run inline).</summary>
    public void Advance(TimeSpan by)
    {
        var end = Now + by;
        while (true)
        {
            var next = pending.Where(entry => entry.Due <= end).OrderBy(entry => entry.Due).FirstOrDefault();
            if (next is null) break;
            pending.Remove(next);
            Now = next.Due;
            next.Completion.TrySetResult();
        }
        Now = end;
    }

    public void Advance(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
}
