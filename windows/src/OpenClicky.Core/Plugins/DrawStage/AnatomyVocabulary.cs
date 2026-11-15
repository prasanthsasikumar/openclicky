//
//  AnatomyVocabulary.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  The names a question about the body may use, from the atlas's own index: every English structure name and every
//  topic group ("Brachial plexus", "Heart", "Cranium"), plus the everyday and clinical words people say instead
//  ("skull", "kneecap", "LAD"). A question that names one of them is about the body even when it is phrased like
//  pointing ("where is the brachial plexus"), so it is drawn from the atlas rather than answered with a pointer.
//
//  Loaded once at startup from Assets/DrawStage/library/anatomy/index.json (the Windows app passes its own path, so
//  Core never reaches into OpenClicky.Windows). Without the file the vocabulary is empty and nothing is routed.
//

using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClicky.Core.Plugins.DrawStage;

public sealed class AnatomyVocabulary
{
    /// <summary>The vocabulary questions are checked against: loaded at startup, empty until then (and when the atlas is
    /// not shipped).</summary>
    public static AnatomyVocabulary Current { get; set; } = new();

    // MARK: - What is not a name on its own

    /// <summary>Names too everyday to mean the body by themselves: "where is the head of the page", "show me the root
    /// folder", "click the arm icon" are about the screen. They still count beside a body's own words ("show me the human
    /// eye"): see <see cref="BodyContext"/>.</summary>
    private static readonly HashSet<string> GenericAlone = new(StringComparer.Ordinal)
    {
        "head", "neck", "back", "hand", "arm", "leg", "foot", "face", "eye", "ear", "nose", "mouth", "lip", "skin",
        "body", "trunk", "chest", "side", "column", "crown", "root", "base", "bridge", "tip", "apex",
    };

    /// <summary>The body's own words beside a generic one, which make it anatomy after all ("show me the human eye").</summary>
    private static readonly string[] BodyContext = [" human ", " my body ", " anatomy ", " in the body "];

    /// <summary>The short clinical abbreviations a question may name on its own, unlike other names of three letters or
    /// fewer ("RIB", "pia"), which are too easily something else.</summary>
    private static readonly HashSet<string> ClinicalAbbreviations = new(StringComparer.Ordinal)
    {
        "lad", "rca", "lca", "acl", "pcl", "mcl", "lcl",
    };

    /// <summary>Where a name's head noun starts when a question leaves the qualifier out ("cruciate ligaments" for
    /// "anterior cruciate ligament"): a suffix beginning with one of these is not a name of its own ("of femur").</summary>
    private static readonly HashSet<string> SuffixStopWords = new(StringComparer.Ordinal)
    {
        "the", "of", "a", "an", "and", "to", "for", "with", "in", "on",
    };

    /// <summary>What people say instead of the atlas's systematic names: the keys of anatomy-index.js's SYNONYMS table,
    /// which the page's own finder uses.</summary>
    private static readonly string[] Synonyms =
    [
        "skull", "brain", "encephalon", "lad", "left anterior descending artery", "rca", "lca", "acl", "pcl", "mcl", "lcl",
        "achilles tendon", "achilles", "funny bone", "collarbone", "kneecap", "shin bone", "shinbone", "windpipe",
        "voice box", "voicebox", "gullet", "aortic valve", "mitral valve", "left atrioventricular valve",
        "bicuspid valve", "tricuspid valve", "right atrioventricular valve", "pulmonary valve", "shoulder blade",
        // The joints people name by the body part ("where is the knee"); the atlas only says "Knee joint".
        "knee", "shoulder", "shoulder joint", "hip", "elbow", "wrist", "wrist joint", "ankle", "jaw joint",
        "breastbone", "tailbone", "cheekbone", "jawbone", "thigh bone", "calf bone", "heel bone", "hip bone",
        "belly button", "adam's apple", "backbone", "spine",
    ];

    /// <summary>The names the atlas gave, lowercase, longest first so the longest name a question uses is the one found.</summary>
    private readonly string[] names;

    /// <summary>An empty vocabulary: nothing is anatomy (the atlas is missing, or the tests that check the school of
    /// questions the atlas's presence changes).</summary>
    public AnatomyVocabulary() : this([])
    {
    }

    private AnatomyVocabulary(string[] names) => this.names = names;

    // MARK: - Loading

    /// <summary>Reads the atlas's index.json at <paramref name="path"/> into <see cref="Current"/>. A missing or unreadable
    /// file leaves it empty: a question about the body is then routed as it was before.</summary>
    public static AnatomyVocabulary Load(string path)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path))
            {
                using var index = JsonDocument.Parse(File.ReadAllText(path));
                if (index.RootElement.ValueKind == JsonValueKind.Object
                    && index.RootElement.TryGetProperty("structures", out var structures)
                    && structures.ValueKind == JsonValueKind.Array)
                {
                    foreach (var structure in structures.EnumerateArray())
                    {
                        if (structure.ValueKind != JsonValueKind.Object) continue;
                        if (structure.TryGetProperty("en", out var name) && name.ValueKind == JsonValueKind.String) Add(found, name.GetString());
                        if (!structure.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array) continue;
                        foreach (var group in groups.EnumerateArray())
                        {
                            if (group.ValueKind == JsonValueKind.String) Add(found, group.GetString());
                        }
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // An unreadable atlas means no anatomy routing, not a startup crash.
            found.Clear();
        }
        // The synonyms name what the index names, so without the index there is nothing for them to find.
        if (found.Count > 0)
        {
            foreach (var synonym in Synonyms) Add(found, synonym);
        }
        Current = new AnatomyVocabulary([.. found.OrderByDescending(name => name.Count(character => character == ' ')).ThenByDescending(name => name.Length)]);
        return Current;
    }

    /// <summary>Adds a name, and the phrase its head noun makes on its own ("cruciate ligament" for "anterior cruciate
    /// ligament"): people leave the qualifier out.</summary>
    private static void Add(HashSet<string> names, string? raw)
    {
        var name = Normalise(raw);
        if (Dropped(name)) return;
        names.Add(name);
        var words = name.Split(' ');
        for (var start = 1; start + 1 < words.Length; start++)
        {
            if (SuffixStopWords.Contains(words[start])) break;
            var head = string.Join(' ', words[start..]);
            if (!Dropped(head)) names.Add(head);
        }
    }

    /// <summary>Whether a name is too generic or too short to mean the body on its own.</summary>
    private static bool Dropped(string name) =>
        name.Length == 0
        || GenericAlone.Contains(name)
        || name.StartsWith("body of ", StringComparison.Ordinal)
        || (name.Count(char.IsLetter) <= 3 && !ClinicalAbbreviations.Contains(name));

    // MARK: - Matching

    /// <summary>Whether <paramref name="question"/> names a body structure, and which name it used ("brachial plexus").</summary>
    public bool Mentions(string question, out string term)
    {
        term = "";
        if (names.Length == 0 || string.IsNullOrWhiteSpace(question)) return false;
        var text = " " + Normalise(question) + " ";
        foreach (var name in names)
        {
            if (!HasPhrase(text, name)) continue;
            term = name;
            return true;
        }
        // A generic word is anatomy only beside the body's own words ("show me the human eye").
        if (!BodyContext.Any(context => text.Contains(context, StringComparison.Ordinal))) return false;
        foreach (var word in GenericAlone)
        {
            if (!text.Contains(" " + word + " ", StringComparison.Ordinal)) continue;
            term = word;
            return true;
        }
        return false;
    }

    /// <summary>Whether the question says <paramref name="phrase"/>, singular or in the plural people ask it in ("where are
    /// the cruciate ligaments").</summary>
    private static bool HasPhrase(string text, string phrase)
    {
        if (text.Contains(" " + phrase + " ", StringComparison.Ordinal)) return true;
        var words = phrase.Split(' ');
        words[^1] = Plural(words[^1]);
        return text.Contains(" " + string.Join(' ', words) + " ", StringComparison.Ordinal);
    }

    private static string Plural(string word) => word switch
    {
        _ when word.Length > 1 && word.EndsWith('y') && !"aeiou".Contains(word[^2]) => word[..^1] + "ies",
        _ when word.EndsWith('s') || word.EndsWith('x') || word.EndsWith("ch", StringComparison.Ordinal) || word.EndsWith("sh", StringComparison.Ordinal) => word + "es",
        _ => word + "s",
    };

    /// <summary>The words the way DrawStageLesson reads them: lowercase, whole words, single spaces.</summary>
    private static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = NotANameCharacter.Replace(value.ToLowerInvariant().Replace('’', '\''), " ");
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // Marks (\p{M}) are kept: a Latin name's accents must not split a word.
    private static readonly Regex NotANameCharacter = new(@"[^\p{L}\p{M}\p{N}' ]+", RegexOptions.Compiled);
}
