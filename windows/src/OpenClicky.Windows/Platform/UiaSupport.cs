using System.Diagnostics;
using System.Windows.Automation;
using OpenClicky.Core.Platform;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// Shared plumbing for the UI Automation readers.
/// <para>
/// Choice of API: the managed client in System.Windows.Automation (UIAutomationClient.dll, part of
/// the WPF runtime the exe already loads). It wraps the same UIA COM client, supports CacheRequest
/// (one cross-process round trip per node in tree walks), runtime ids and Automation.Compare, and
/// needs no hand-written COM vtables — a wrong slot in a hand-declared IUIAutomationElement would
/// crash rather than fail. What it lacks is IUIAutomation2's connection/transaction timeouts, so
/// every query runs on its own fresh MTA thread and the caller waits with a timeout instead: a
/// hung target app can strand that one thread until UIA's own (long) timeout, never the caller.
/// </para>
/// </summary>
internal static class UiaSupport
{
    /// <summary>Runs <paramref name="work"/> on a new MTA background thread; returns <paramref name="fallback"/> when it takes longer than <paramref name="timeout"/> or throws.</summary>
    public static async Task<T> RunAsync<T>(Func<T> work, TimeSpan timeout, T fallback, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(work()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        })
        {
            IsBackground = true,
            Name = "OpenClicky UI Automation query",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        try
        {
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Debug.WriteLine("ui automation: query timed out");
            return fallback;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsUiaFailure(exception))
        {
            return fallback;
        }
    }

    public static bool IsUiaFailure(Exception exception) =>
        exception is ElementNotAvailableException or ElementNotEnabledException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or ArgumentException or UnauthorizedAccessException
            or NotSupportedException;

    /// <summary>"Edit" for ControlType.Edit.</summary>
    public static string ControlTypeName(ControlType? controlType)
    {
        if (controlType is null) return "Unknown";
        var name = controlType.ProgrammaticName;
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    public static string RuntimeIdString(AutomationElement element)
    {
        try
        {
            return string.Join('.', element.GetRuntimeId());
        }
        catch (Exception exception) when (IsUiaFailure(exception))
        {
            return string.Empty;
        }
    }

    public static PixelRect ToPixelRect(System.Windows.Rect rect) =>
        rect.IsEmpty || double.IsInfinity(rect.Width)
            ? default
            : new PixelRect((int)Math.Round(rect.X), (int)Math.Round(rect.Y), (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));

    /// <summary>A property read that answers null instead of throwing when the element went away.</summary>
    public static T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (IsUiaFailure(exception))
        {
            return default;
        }
    }

    public static ValuePattern? ValuePatternOf(AutomationElement element) =>
        Safe(() => element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? pattern as ValuePattern : null);

    public static TextPattern? TextPatternOf(AutomationElement element) =>
        Safe(() => element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) ? pattern as TextPattern : null);

    /// <summary>A cache request for the properties tree walks read on every node.</summary>
    public static CacheRequest WalkCacheRequest()
    {
        var request = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.HelpTextProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(AutomationElement.IsOffscreenProperty);
        request.Add(AutomationElement.AutomationIdProperty);
        request.Add(AutomationElement.ClassNameProperty);
        request.Add(AutomationElement.IsValuePatternAvailableProperty);
        request.Add(ValuePattern.ValueProperty);
        request.Add(SelectionItemPattern.IsSelectedProperty);
        request.Add(AutomationElement.IsSelectionItemPatternAvailableProperty);
        return request;
    }

    public static T? Cached<T>(AutomationElement element, AutomationProperty property)
    {
        try
        {
            var value = element.GetCachedPropertyValue(property, ignoreDefaultValue: true);
            return value is T typed ? typed : default;
        }
        catch (Exception exception) when (IsUiaFailure(exception))
        {
            return default;
        }
    }

    /// <summary>The children of a node with the walk's properties cached (control view).</summary>
    public static IEnumerable<AutomationElement> CachedChildren(AutomationElement parent, CacheRequest request, int limit = int.MaxValue)
    {
        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? child = Safe(() => walker.GetFirstChild(parent, request));
        var count = 0;
        while (child is not null && count < limit)
        {
            yield return child;
            count++;
            var current = child;
            child = Safe(() => walker.GetNextSibling(current, request));
        }
    }
}
