//
//  Typewriter.cs
//  OpenClicky (Windows)
//
//  The captions type out one character every 30 ms, as the Mac's Timer does. Windows timers tick
//  every 15.6 ms, so a 30 ms delay often lasts 31 or 47 ms and a long caption would type a third
//  slower; the count of characters shown therefore follows the clock (at least one per tick).
//

using System.Diagnostics;
using System.Globalization;

namespace OpenClicky.Core.Companion;

public static class Typewriter
{
    public static readonly TimeSpan CharacterInterval = TimeSpan.FromMilliseconds(30);

    /// <summary>Whole text elements of <paramref name="message"/>, so an emoji or an accented letter never shows half typed.</summary>
    public static IReadOnlyList<string> TextElements(string message)
    {
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(message);
        while (enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
        return elements;
    }

    private static readonly Stopwatch SharedClock = Stopwatch.StartNew();

    /// <summary>Seconds on a monotonic clock (the default for <see cref="TypeAsync"/>).</summary>
    public static double MonotonicSeconds() => SharedClock.Elapsed.TotalSeconds;

    /// <summary>Calls <paramref name="show"/> with ever longer beginnings of <paramref name="message"/> until all of it shows.
    /// <paramref name="secondsClock"/> is the clock the delays run on (tests pass their own).</summary>
    public static async Task TypeAsync(string message, Action<string> show, Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken, Func<double>? secondsClock = null)
    {
        var now = secondsClock ?? MonotonicSeconds;
        var elements = TextElements(message);
        var startSeconds = now();
        var shownCount = 0;
        while (shownCount < elements.Count)
        {
            await delay(CharacterInterval, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var dueCount = (int)((now() - startSeconds) / CharacterInterval.TotalSeconds + 1e-9);
            shownCount = Math.Min(elements.Count, Math.Max(shownCount + 1, dueCount));
            show(string.Concat(elements.Take(shownCount)));
        }
    }
}
