using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using OpenClicky.Core.Platform;
using static OpenClicky.Windows.Platform.NativeMethods;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// FocusedFieldReader, SelectedTextReader, WriteInPlace's target capture and AccessibleElementLocator's
/// tree walk, over UI Automation. Read-only and bounded; "unknown" whenever UIA does not answer in time.
/// </summary>
public sealed class FocusedElementReader : IFocusedElementReader
{
    private const int MaximumValueCharacters = 20_000;
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(0.6);
    private static readonly TimeSpan TargetTimeout = TimeSpan.FromSeconds(0.3);
    private static readonly TimeSpan TreeWalkBudget = TimeSpan.FromSeconds(0.25);
    private static readonly TimeSpan TreeWalkTimeout = TimeSpan.FromSeconds(1);

    private static readonly HashSet<string> ActionableRoles = new(StringComparer.Ordinal)
    {
        "Button", "Hyperlink", "MenuItem", "SplitButton", "CheckBox", "RadioButton", "ComboBox", "Edit", "TabItem", "TreeItem", "ListItem",
    };

    public Task<FocusedFieldSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) =>
        UiaSupport.RunAsync(ReadSnapshot, SnapshotTimeout, ForegroundAppOnlySnapshot(), cancellationToken);

    public Task<FocusedTextTarget?> FocusedTextTargetAsync(CancellationToken cancellationToken = default) =>
        UiaSupport.RunAsync<FocusedTextTarget?>(ReadFocusedTextTarget, TargetTimeout, null, cancellationToken);

    public Task<bool> StillHasFocusAsync(FocusedTextTarget target, CancellationToken cancellationToken = default) =>
        UiaSupport.RunAsync(() =>
        {
            var current = ReadFocusedTextTarget();
            return current is not null && current.IsSameElement(target);
        }, TargetTimeout, false, cancellationToken);

    public Task<IReadOnlyList<string>> NearbyTermsAsync(int limit = 40, CancellationToken cancellationToken = default) =>
        UiaSupport.RunAsync<IReadOnlyList<string>>(() => ReadNearbyTerms(limit), TreeWalkTimeout, [], cancellationToken);

    public Task<string?> SelectedTextAsync(CancellationToken cancellationToken = default) =>
        UiaSupport.RunAsync(ReadSelectedText, SnapshotTimeout, null, cancellationToken);

    public async Task<string?> FocusedBoxTextAsync(int maximumCharacters = 6_000, CancellationToken cancellationToken = default)
    {
        var snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.IsEditable || snapshot.IsSecure || snapshot.Value is not { } value) return null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumCharacters) return null;
        return value;
    }

    public Task<IReadOnlyList<AccessibleControl>> CaptionedControlsInForegroundWindowAsync(CancellationToken cancellationToken = default) =>
        UiaSupport.RunAsync<IReadOnlyList<AccessibleControl>>(ReadCaptionedControls, TreeWalkTimeout, [], cancellationToken);

    // ------------------------------------------------------------------ snapshot

    private static FocusedFieldSnapshot ForegroundAppOnlySnapshot()
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0) return FocusedFieldSnapshot.Unknown;
        var description = ProcessDescriber.Describe(ProcessIdOf(foreground));
        return new FocusedFieldSnapshot { AppProcessName = description.ImageName, AppName = description.DisplayName, ProcessId = description.ProcessId };
    }

    private static FocusedFieldSnapshot ReadSnapshot()
    {
        var snapshot = ForegroundAppOnlySnapshot();
        var focused = UiaSupport.Safe(() => AutomationElement.FocusedElement);
        if (focused is null) return snapshot;

        var processId = UiaSupport.Safe(() => focused.Current.ProcessId);
        if (processId != 0 && processId != snapshot.ProcessId)
        {
            var description = ProcessDescriber.Describe(processId);
            snapshot = snapshot with { AppProcessName = description.ImageName, AppName = description.DisplayName, ProcessId = processId };
        }

        var controlType = UiaSupport.Safe(() => focused.Current.ControlType);
        var role = UiaSupport.ControlTypeName(controlType);
        var isSecure = UiaSupport.Safe(() => focused.Current.IsPassword);
        var valuePattern = UiaSupport.ValuePatternOf(focused);
        var textPattern = UiaSupport.TextPatternOf(focused);
        var isEditable = IsEditable(focused, controlType, valuePattern, textPattern);

        snapshot = snapshot with
        {
            Role = role,
            IsSecure = isSecure,
            IsEditable = isEditable && !isSecure,
            Name = UiaSupport.Safe(() => focused.Current.Name),
            RuntimeId = UiaSupport.RuntimeIdString(focused),
        };
        if (!snapshot.IsEditable) return snapshot;

        string? value = null;
        if (valuePattern is not null) value = UiaSupport.Safe(() => valuePattern.Current.Value);
        if (value is null && textPattern is not null) value = UiaSupport.Safe(() => textPattern.DocumentRange.GetText(MaximumValueCharacters));
        if (value is not null && value.Length > MaximumValueCharacters) value = value[..MaximumValueCharacters];

        string? selectedText = null;
        int? selectionStart = null, selectionLength = null;
        if (textPattern is not null)
        {
            var selection = UiaSupport.Safe(() => textPattern.GetSelection());
            if (selection is { Length: > 0 })
            {
                var range = selection[0];
                var text = UiaSupport.Safe(() => range.GetText(MaximumValueCharacters));
                if (!string.IsNullOrEmpty(text)) selectedText = text;
                selectionLength = text?.Length ?? 0;
                selectionStart = UiaSupport.Safe<int?>(() =>
                {
                    var before = textPattern.DocumentRange.Clone();
                    before.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                    return before.GetText(MaximumValueCharacters).Length;
                });
            }
        }
        return snapshot with { Value = value, SelectedText = selectedText, SelectionStart = selectionStart, SelectionLength = selectionLength };
    }

    /// <summary>
    /// Edit (not read-only), Document (a focused page or rich document: the Mac counts AXWebArea as a
    /// paste target), an editable ComboBox, and — for Electron and web apps that focus a generic
    /// element — a writable value or an editable ancestor within six levels.
    /// </summary>
    private static bool IsEditable(AutomationElement element, ControlType? controlType, ValuePattern? valuePattern, TextPattern? textPattern)
    {
        var valueIsWritable = valuePattern is not null && UiaSupport.Safe(() => !valuePattern.Current.IsReadOnly);
        if (controlType == ControlType.Edit) return valuePattern is null || valueIsWritable;
        if (controlType == ControlType.Document) return true;
        if (controlType == ControlType.ComboBox) return valueIsWritable;
        if (controlType == ControlType.Group || controlType == ControlType.Text || controlType == ControlType.Custom
            || controlType == ControlType.Pane || controlType is null)
        {
            return valueIsWritable || HasEditableAncestor(element);
        }
        return false;
    }

    private static bool HasEditableAncestor(AutomationElement element)
    {
        var walker = TreeWalker.ControlViewWalker;
        var current = element;
        for (var level = 0; level < 6; level++)
        {
            var parent = UiaSupport.Safe(() => walker.GetParent(current));
            if (parent is null || parent == AutomationElement.RootElement) return false;
            var parentType = UiaSupport.Safe(() => parent.Current.ControlType);
            if (parentType == ControlType.Edit || parentType == ControlType.Document) return true;
            current = parent;
        }
        return false;
    }

    // ------------------------------------------------------------------ live typing target

    private static FocusedTextTarget? ReadFocusedTextTarget()
    {
        var focused = UiaSupport.Safe(() => AutomationElement.FocusedElement);
        if (focused is null) return null;
        var processId = UiaSupport.Safe(() => focused.Current.ProcessId);
        if (processId == 0) return null;
        return new FocusedTextTarget
        {
            ProcessId = processId,
            RuntimeId = UiaSupport.RuntimeIdString(focused),
            Role = UiaSupport.ControlTypeName(UiaSupport.Safe(() => focused.Current.ControlType)),
            IsSecure = UiaSupport.Safe(() => focused.Current.IsPassword),
            AppProcessName = ProcessDescriber.ImageName(processId),
            NativeElement = focused,
        };
    }

    // ------------------------------------------------------------------ selection

    private static string? ReadSelectedText()
    {
        var focused = UiaSupport.Safe(() => AutomationElement.FocusedElement);
        if (focused is null) return null;
        if (UiaSupport.Safe(() => focused.Current.IsPassword)) return null;
        if (UiaSupport.Safe(() => focused.Current.ProcessId) == Environment.ProcessId) return null;
        var textPattern = UiaSupport.TextPatternOf(focused);
        if (textPattern is null) return null;
        var selection = UiaSupport.Safe(() => textPattern.GetSelection());
        if (selection is not { Length: > 0 }) return null;
        // A little more than the cap so the "…" is honest.
        var text = UiaSupport.Safe(() => selection[0].GetText(SelectedTextLimits.MaximumCharacters * 4));
        return SelectedTextLimits.Tidied(text);
    }

    // ------------------------------------------------------------------ tree walks

    private static AutomationElement? ForegroundWindowElement()
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0) return null;
        return UiaSupport.Safe(() => AutomationElement.FromHandle(foreground));
    }

    private static IReadOnlyList<string> ReadNearbyTerms(int limit)
    {
        var window = ForegroundWindowElement();
        if (window is null) return [];
        var request = UiaSupport.WalkCacheRequest();
        var root = UiaSupport.Safe(() => window.GetUpdatedCache(request));
        if (root is null) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = new List<string>();
        var visited = 0;
        var stopwatch = Stopwatch.StartNew();
        void Walk(AutomationElement node, int depth)
        {
            if (depth >= 10 || visited >= 400 || terms.Count >= limit || stopwatch.Elapsed > TreeWalkBudget) return;
            visited++;
            foreach (var property in new[] { AutomationElement.NameProperty, AutomationElement.HelpTextProperty })
            {
                var text = UiaSupport.Cached<string>(node, property)?.Trim();
                if (text is null || text.Length < 2 || text.Length > 60 || !text.Any(char.IsLetter)) continue;
                if (seen.Add(text)) terms.Add(text);
            }
            foreach (var child in UiaSupport.CachedChildren(node, request, limit: 60))
            {
                Walk(child, depth + 1);
                if (visited >= 400 || terms.Count >= limit) return;
            }
        }
        Walk(root, 0);
        return terms;
    }

    private static IReadOnlyList<AccessibleControl> ReadCaptionedControls()
    {
        var window = ForegroundWindowElement();
        if (window is null) return [];
        var request = UiaSupport.WalkCacheRequest();
        var root = UiaSupport.Safe(() => window.GetUpdatedCache(request));
        if (root is null) return [];

        var candidates = new List<AccessibleControl>();
        var queue = new Queue<(AutomationElement Element, int Depth, IReadOnlyList<string> ContainerTitles)>();
        queue.Enqueue((root, 0, []));
        var visited = 0;
        var stopwatch = Stopwatch.StartNew();
        while (queue.Count > 0 && visited < 600 && stopwatch.Elapsed < TreeWalkBudget)
        {
            var (element, depth, containerTitles) = queue.Dequeue();
            visited++;
            var role = UiaSupport.ControlTypeName(UiaSupport.Cached<ControlType>(element, AutomationElement.ControlTypeProperty));
            var caption = FirstNonEmpty(
                UiaSupport.Cached<string>(element, AutomationElement.NameProperty),
                UiaSupport.Cached<string>(element, AutomationElement.HelpTextProperty),
                UiaSupport.Cached<string>(element, ValuePattern.ValueProperty));

            if (caption.Length > 0 && !UiaSupport.Cached<bool>(element, AutomationElement.IsOffscreenProperty))
            {
                var bounds = UiaSupport.ToPixelRect(UiaSupport.Cached<System.Windows.Rect>(element, AutomationElement.BoundingRectangleProperty));
                if (!bounds.IsEmpty) candidates.Add(new AccessibleControl(role, caption, containerTitles, bounds));
            }

            if (depth >= 12) continue;
            // A captioned group is the row or section its children sit in; the three nearest tell two identical buttons apart.
            IReadOnlyList<string> childContainerTitles = caption.Length == 0 || ActionableRoles.Contains(role)
                ? containerTitles
                : new[] { caption }.Concat(containerTitles).Take(3).ToArray();
            foreach (var child in UiaSupport.CachedChildren(element, request))
            {
                queue.Enqueue((child, depth + 1, childContainerTitles));
                if (stopwatch.Elapsed >= TreeWalkBudget) break;
            }
        }
        return candidates;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return string.Empty;
    }
}
