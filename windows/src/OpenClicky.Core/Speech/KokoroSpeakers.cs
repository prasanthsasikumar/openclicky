// The static half of KokoroVoice.swift: Kokoro's speakers and language hints. The rest (download,
// helper, speaking) is in KokoroVoice.cs.

namespace OpenClicky.Core.Speech;

public sealed partial class KokoroVoice
{
    /// <summary>One of Kokoro's voices offered in Settings.</summary>
    /// <param name="Id">The speaker's index in the model's voice table.</param>
    public sealed record Speaker(int Id, string Name, string LanguageCode, string Accent)
    {
        public string Label => $"{Name}, {Accent}";
    }

    /// <summary>The speakers offered in Settings: the model has 54, these are the clearest per language.</summary>
    public static IReadOnlyList<Speaker> Speakers { get; } =
    [
        new(3, "Heart", "en", "American"),
        new(2, "Bella", "en", "American"),
        new(6, "Nicole", "en", "American"),
        new(9, "Sarah", "en", "American"),
        new(16, "Michael", "en", "American"),
        new(11, "Adam", "en", "American"),
        new(21, "Emma", "en", "British"),
        new(26, "George", "en", "British"),
        new(31, "Alpha", "hi", "Hindi"),
        new(33, "Omega", "hi", "Hindi"),
        new(28, "Dora", "es", "Spanish"),
        new(30, "Siwis", "fr", "French"),
        new(35, "Sara", "it", "Italian"),
        new(37, "Alpha", "ja", "Japanese"),
        new(42, "Dora", "pt", "Portuguese"),
        new(45, "Xiaobei", "zh", "Mandarin"),
    ];

    /// <summary>The speaker for the reply language: the one picked if it speaks it, else the language's
    /// first; null when Kokoro doesn't speak the language.</summary>
    public static Speaker? SpeakerFor(string languageCode, int? picked)
    {
        var forLanguage = Speakers.Where(speaker => speaker.LanguageCode == languageCode).ToList();
        return forLanguage.FirstOrDefault(speaker => speaker.Id == picked) ?? forLanguage.FirstOrDefault();
    }

    /// <summary>Kokoro's language hint for espeak (sherpa-onnx's `lang`); English and Mandarin use the
    /// model's own lexicons instead.</summary>
    public static string LanguageHint(string languageCode) => languageCode switch
    {
        "en" => "en-us",
        "zh" => "zh",
        "pt" => "pt-br",
        _ => languageCode,
    };
}
