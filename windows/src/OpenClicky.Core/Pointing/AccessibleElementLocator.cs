//
//  AccessibleElementLocator.cs
//  OpenClicky (Windows port of the ranking half of AccessibleElementLocator.swift)
//
//  `point_at` for captions that appear more than once ("Edit" on every row of a list). OCR reads
//  pixels, so every one of those captions looks identical and only distance separates them, which
//  the model's own 30–100 px error is enough to get wrong. UI Automation knows more than the pixels
//  do: each control carries a control type, and it sits inside a row or group that has its own name,
//  so "the Edit button on the openclicky row" is answerable from the request itself.
//
//  The tree walk is OS code (OpenClicky.Windows/Platform, UI Automation over the foreground window);
//  it follows the limits below and hands the candidates here.
//

namespace OpenClicky.Core.Pointing;

/// <summary>One control from the UI Automation tree, already mapped into the screenshot the model saw.</summary>
/// <param name="Role">The control type: a UIA ControlType programmatic name without the "ControlType." prefix
/// ("Button", "Hyperlink", "Text"…); the Mac's AX role strings ("AXButton"…) are accepted too.</param>
/// <param name="Title">The control's own caption (UIA Name, or its HelpText / Value when it has no name).</param>
/// <param name="ContainerTitles">Names of the groups the control sits in, nearest ancestor first. This is where a list
/// row's name lives, and it is what tells two identical buttons apart.</param>
/// <param name="Center">Centre of the control in screenshot pixels, so it is comparable with the model's guess.</param>
public sealed record AccessibleElementCandidate(string Role, string Title, IReadOnlyList<string> ContainerTitles, ScreenPoint Center)
{
    public bool Equals(AccessibleElementCandidate? other) =>
        other is not null && Role == other.Role && Title == other.Title && Center == other.Center
        && ContainerTitles.SequenceEqual(other.ContainerTitles);

    public override int GetHashCode() => HashCode.Combine(Role, Title, Center, ContainerTitles.Count);
}

public static class AccessibleElementLocator
{
    /// <summary>The tree walk visits at most this many elements (the Mac's cap)…</summary>
    public const int MaximumVisitedElements = 600;
    /// <summary>…and goes at most this deep.</summary>
    public const int MaximumDepth = 12;
    /// <summary>How many of the nearest captioned containers ride along with a candidate.</summary>
    public const int MaximumContainerTitles = 3;

    /// <summary>Roles a user can actually point at and click. A static label with the same caption as a button is nearly
    /// always the button's own text, not the thing being asked about. UIA control types, plus the Mac's AX roles.</summary>
    public static readonly IReadOnlySet<string> ActionableRoles = new HashSet<string>(StringComparer.Ordinal)
    {
        // UI Automation control types
        "Button", "Hyperlink", "MenuItem", "SplitButton", "CheckBox", "RadioButton",
        "ComboBox", "Edit", "Document", "TreeItem", "TabItem",
        // macOS Accessibility roles (kept so Mac-shaped data still ranks the same)
        "AXButton", "AXLink", "AXMenuItem", "AXMenuButton", "AXCheckBox", "AXRadioButton",
        "AXPopUpButton", "AXTextField", "AXTextArea", "AXDisclosureTriangle", "AXTab",
    };

    private sealed record ScoredCandidate(AccessibleElementCandidate Candidate, int Score, double Distance);

    /// <summary>The control called <paramref name="hint"/> that the request is actually about.
    ///
    /// Candidates are the controls carrying that caption within <paramref name="maxDistance"/> of the model's guess. They
    /// are ranked by what the request says rather than by distance alone: a word of the request appearing in a
    /// candidate's container titles ("openclicky" naming the row) is strong evidence, and an actionable role beats a
    /// static label repeating the same caption. Distance only breaks ties. When the two best candidates score the same
    /// and are closer together than <paramref name="ambiguityMargin"/>, nothing here can tell them apart either, so null
    /// is returned and the caller falls through to the Claude grounding pass.</summary>
    public static AccessibleElementCandidate? BestMatch(
        string hint,
        string? userRequest,
        ScreenPoint guess,
        IReadOnlyList<AccessibleElementCandidate> candidates,
        double maxDistance,
        double ambiguityMargin)
    {
        var needle = Normalize(hint);
        if (needle.Length < 3 || ScreenTextLocator.GenericWords.Contains(needle)) return null;

        // Words of the request that could name a row or section: long enough to be distinctive, not a generic UI noun,
        // and not the caption itself (every candidate carries that).
        var distinguishingRequestWords = Words(userRequest ?? "").ToHashSet()
            .Where(word => word.Length >= 3 && !ScreenTextLocator.GenericWords.Contains(word) && word != needle)
            .ToList();

        var scoredCandidates = new List<ScoredCandidate>();
        foreach (var candidate in candidates)
        {
            if (!CandidateCarriesCaption(candidate, needle)) continue;
            var distance = candidate.Center.DistanceTo(guess);
            if (distance > maxDistance) continue;
            var containerWords = candidate.ContainerTitles.SelectMany(Words).ToHashSet();
            var matchingRequestWordCount = distinguishingRequestWords.Count(containerWords.Contains);
            var roleScore = ActionableRoles.Contains(candidate.Role) ? 2 : 0;
            scoredCandidates.Add(new ScoredCandidate(candidate, matchingRequestWordCount * 3 + roleScore, distance));
        }
        scoredCandidates.Sort((first, second) =>
            first.Score == second.Score ? first.Distance.CompareTo(second.Distance) : second.Score.CompareTo(first.Score));

        if (scoredCandidates.Count == 0) return null;
        var bestCandidate = scoredCandidates[0];
        if (scoredCandidates.Count > 1)
        {
            var runnerUpCandidate = scoredCandidates[1];
            if (runnerUpCandidate.Score == bestCandidate.Score && runnerUpCandidate.Distance - bestCandidate.Distance < ambiguityMargin) return null;
        }
        return bestCandidate.Candidate;
    }

    /// <summary>The caption hint for <see cref="BestMatch"/>: the caption the model gave is the better hint; its own
    /// description is the fallback.</summary>
    public static string CaptionHint(string label, string text) => text.Length == 0 ? label : text;

    /// <summary>The container titles a child of an element inherits during the tree walk: a captioned group is the row or
    /// section its children sit in; the three nearest are enough context to tell two identical buttons apart.</summary>
    public static IReadOnlyList<string> ChildContainerTitles(string role, string caption, IReadOnlyList<string> containerTitles) =>
        caption.Length == 0 || ActionableRoles.Contains(role)
            ? containerTitles
            : new[] { caption }.Concat(containerTitles).Take(MaximumContainerTitles).ToList();

    /// <summary>Whether the control's own caption is the one being asked for: the whole caption, or the caption as one of
    /// its words ("Edit profile" answers to "edit").</summary>
    private static bool CandidateCarriesCaption(AccessibleElementCandidate candidate, string needle)
    {
        var title = Normalize(candidate.Title);
        return title == needle || Words(title).Contains(needle);
    }

    private static string Normalize(string text) => string.Join(' ', Words(text));

    internal static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var character in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(character);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }
}
